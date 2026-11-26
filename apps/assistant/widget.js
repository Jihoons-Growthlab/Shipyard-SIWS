/*
 * SIWS assistant widget: one chat panel shared by the Portal, the ERP (/erp/) and the digital twin WebGL page (/twin/).
 *
 *   <script src="/assistant/widget.js" data-context="erp|twin|portal" [data-launcher="none"]></script>
 *
 * - Shows itself only when the page is a secure context (HTTPS or localhost), the tab holds a login
 *   (sessionStorage 'shipyard-token') and GET /assistant/health answers {ready:true}. Elsewhere (a friend's demo, the
 *   ERP opened directly without the assistant service) it stays hidden and SiwsAssistant.open() returns false.
 * - On the ERP it hides while the page is signed out (body.signed-out).
 * - POST /assistant/chat, Bearer token, JSON {message, history:[{role, content}], context}; answer
 *   {answer, sources:[{label, url}], model}; errors carry a `detail` string. All text goes in with textContent;
 *   source links are kept only for same-origin paths ("/...").
 * - The conversation lives in this page's memory only and is cleared when the login token changes.
 * - Unity WebGL captures the keyboard on the window; a window capture listener registered before the Unity loader
 *   keeps keys typed in the panel away from Unity (and handles the panel's own keys there).
 *
 * Per screen (one shared UI): the ERP gets a floating launcher and, on wide screens, a panel docked beside the work
 * area; the twin opens the panel from its left chat button, in the drawer slot next to the rail, with a note that
 * answers follow the ERP data and the shared plan replay rather than this browser's own playback.
 * Source links: an ERP view on the ERP page opens in place (hash deep link); other pages open in a new tab so the
 * current screen and the conversation stay.
 *
 * Twin mode (/twin/ only): a yard guide character in a dark panel matching the dashboard, and camera moves in the open
 * scene. A move runs only for an explicit request typed by the user ("J안벽으로 이동해줘", "SN2051-B101 블록 보여줘") or a
 * move button the user pressed; the target is a name/ID from the user's own text, never anything from a model answer.
 * The scene side is TwinAssistantBridge.cs (Unity) through ShipyardWeb.jslib: window.SiwsTwinScene.queue holds JSON
 * requests {id, op:'find'|'open'|'state', query|pick}, onReply receives the answers. Finding and framing reuse the
 * unified search (YardObjectSearch.Find/Open); success is shown only after the scene reports the camera arrived.
 * On the twin screen the guide also stands pinned at the bottom right, above the scene's cards, as a companion that
 * says something in a speech bubble (what the goliath is lifting, the playback pausing, a tip). It keeps quiet while
 * the user is working the scene, waits 20 s after a remark before a live lift, 45 s before other news and 2 min before a
 * tip, mentions a lift only while it is still going on, and "그만 말하기" silences it (kept per
 * browser). Pressing it opens the conversation; the rail's chat button still does too.
 *
 * API: window.SiwsAssistant = { open() -> bool, close(), toggle() -> bool, isOpen() -> bool, available() -> bool,
 *                               status() -> 'ready'|'login'|'checking'|'offline'|'unsupported' }.
 */
(function () {
  'use strict';
  if (window.SiwsAssistant) return;

  var script = document.currentScript;
  var context = (script && script.getAttribute('data-context')) || guessContext();
  var launcherWanted = !(script && script.getAttribute('data-launcher') === 'none');
  var HISTORY_LIMIT = 12;          // past messages sent with a question (server: at most 12)
  var MESSAGE_LIMIT = 1600;        // server: a question of at most 1600 characters
  var REQUEST_TIMEOUT_MS = 65000;  // the server gives up after 52 s, the Portal proxy after 56 s
  var RETRY_HEALTH_MS = 30000;
  var DOCK_MIN_WIDTH = 1180;       // ERP: docked beside the work area from this width, an overlay below it
  var SN_HULL = /^SN\d{3,8}$/i, SN_BLOCK = /^SN\d{3,8}-[A-Z0-9][A-Z0-9_-]*$/i, CRANE_ID = /^(LC|JC)-[A-Z0-9-]{2,32}$/i;

  // Example questions follow what the assistant can look up (hull progress, block position and erection, dock and
  // quay plans, jib crane plan simulation, the shared twin replay). IDs come from this account's ERP data when the
  // account may read them; without an ID a click puts the question in the input for the user to complete.
  var TOPICS = {
    hull: { tag: '호선 진행', ask: function (s) { return s.hull ? s.hull + ' 호선 진행 상황과 지연 여부 알려줘' : '지연 위험이 있는 호선과 진행 상황 알려줘'; } },
    block: { tag: '블록 실적', ask: function (s) { return s.block ? s.block + ' 블록 위치와 탑재 실적 알려줘' : null; },
             template: '블록 위치와 탑재 실적 알려줘: SN' },
    blockTwin: { tag: '블록 위치', ask: function (s) { return s.block ? s.block + ' 블록 지금 어디 있고 탑재됐어?' : null; },
                 template: '블록 위치와 탑재 여부 알려줘: SN' },
    erection: { tag: '탑재 계획', ask: function (s) { return s.hull ? s.hull + ' 호선 탑재 예정과 합격 실적 알려줘' : '이번 주 탑재 계획과 합격 실적 알려줘'; } },
    yard: { tag: '도크·안벽', ask: function () { return '오늘 도크 예약과 안벽 선석 배정 알려줘'; } },
    crane: { tag: '크레인 모의', ask: function (s) { return (s.crane ? s.crane + ' ' : '') + '지브 크레인 지금 계획 모의 상태 알려줘'; } },
    replay: { tag: '공용 재생', ask: function () { return '공용 트윈 재생은 지금 몇 시 어떤 작업 중이야?'; } }
  };
  var SCREENS = {
    erp: { label: '생산관리 ERP', topics: ['hull', 'block', 'erection', 'yard'],
           intro: '이 계정의 ERP 조회 권한으로 호선 진행, 블록 위치·탑재 실적, 탑재 계획, 도크·안벽 계획을 찾아 답합니다. 조회만 하며 입력·수정은 하지 않습니다.' },
    twin: { label: '디지털 트윈', topics: [],
            intro: '장면의 시설·크레인·블록으로 카메라를 옮기고, 탑재 계획·도크·안벽 배치·공용 트윈 재생을 ERP에서 조회합니다.',
            basis: '조회 답변은 ERP 계획·실적과 공용 계획 재생 기준이며, 이 화면에서 돌리는 재생과는 별개입니다.' },
    portal: { label: 'SIWS 포털', topics: ['hull', 'erection', 'yard', 'replay'],
              intro: '이 계정의 ERP 조회 권한으로 호선 진행, 블록 위치·탑재, 도크·안벽 계획, 크레인 계획 모의, 공용 트윈 재생을 찾아 답합니다.' }
  };
  var screen = SCREENS[context] || SCREENS.portal;
  var twin = context === 'twin';

  // Twin examples: camera moves run in the open scene (no server call); questions go to the assistant.
  var TWIN_EXAMPLES = [
    { group: '카메라 이동', tag: '시설', text: 'J안벽으로 이동해줘', move: true },
    { group: '카메라 이동', tag: '크레인', text: '골리앗 크레인에 줌인해줘', move: true },
    { group: '카메라 이동', tag: function (s) { return s.block ? '블록' : '지브 크레인'; },
      text: function (s) { return s.block ? s.block + ' 블록 보여줘' : '1도크 지브 크레인 1호 보여줘'; }, move: true },
    { group: '계획 조회', tag: '탑재 계획', text: '이번 주 탑재 계획과 합격 실적 알려줘' },
    { group: '계획 조회', tag: '도크·안벽', text: '오늘 도크 예약과 안벽 선석 배정 알려줘' },
    { group: '계획 조회', tag: '공용 재생', text: '공용 트윈 재생은 지금 몇 시 어떤 작업 중이야?' }
  ];

  var state = {
    token: readToken(),
    healthy: false,
    checking: false,
    checked: false,     // a health answer arrived for this token
    model: '',
    open: false,
    busy: false,
    generation: 0,      // bumps on a token change or reset: late answers of an old conversation are dropped
    history: [],        // [{role:'user'|'assistant', content}]
    returnFocus: null,
    retryTimer: 0,
    pressing: false,    // a press is held on one of the widget's buttons (no rebuild under it)
    samples: {},        // {hull, block, crane} from this account's ERP data
    samplesFor: null
  };

  function guessContext() {
    var path = location.pathname;
    if (path.indexOf('/erp') === 0) return 'erp';
    if (path.indexOf('/twin') === 0) return 'twin';
    return 'portal';
  }

  function readToken() {
    try { return sessionStorage.getItem('shipyard-token') || ''; } catch (e) { return ''; }
  }

  function signedOut() {
    return !document.body || document.body.classList.contains('signed-out');
  }

  // ------------------------------------------------------------------ styles
  // Every rule is scoped under .siws-ai so the host page's element rules (the ERP styles section, form and
  // button:hover) cannot override it, and the reset below neutralises what they set.

  var css = [
    '.siws-ai{--ai-accent:#0f7f86;--ai-accent-dark:#075c67;--ai-ink:#18374c;--ai-dim:#5d7384;--ai-line:#dce5ed;--ai-soft:#eef6f7;',
    'font:14px/1.55 -apple-system,BlinkMacSystemFont,"Apple SD Gothic Neo","Malgun Gothic","Noto Sans KR",sans-serif;color:var(--ai-ink);',
    '-webkit-user-select:text;user-select:text;-webkit-touch-callout:default}',
    '.siws-ai *,.siws-ai *::before,.siws-ai *::after{box-sizing:border-box}',
    '.siws-ai :is(div,form,p,b,span,small,em,a,button,textarea){margin:0;letter-spacing:normal;text-transform:none}',
    '.siws-ai button{font-family:inherit;transition:none;opacity:1;min-height:0;width:auto;box-shadow:none}',
    '.siws-ai .siws-ai-form{margin:0!important}.siws-ai .siws-ai-form button{align-self:auto!important;float:none!important}',
    '.siws-ai[hidden],.siws-ai [hidden]{display:none!important}',
    'body.signed-out .siws-ai{display:none!important}',
    'html.siws-ai-pad body{padding-bottom:88px}',
    '@media (min-width:' + DOCK_MIN_WIDTH + 'px){html.siws-ai-docked body{padding-right:calc(min(400px,100vw - 32px) + 24px)}}',
    '.siws-ai .siws-ai-launch{position:fixed;z-index:2147483000;right:calc(env(safe-area-inset-right) + 20px);bottom:calc(env(safe-area-inset-bottom) + 20px);',
    'display:inline-flex;align-items:center;gap:8px;height:48px;padding:0 18px 0 14px;border:0;border-radius:24px;cursor:pointer;',
    'background:var(--ai-accent);color:#fff;font:650 14px/1 inherit;font-family:inherit;box-shadow:0 8px 24px #0d3d4a40}',
    '.siws-ai .siws-ai-launch:hover{background:var(--ai-accent-dark);color:#fff}',
    '.siws-ai .siws-ai-launch svg{width:20px;height:20px}',
    '.siws-ai :is(button,a,textarea):focus-visible{outline:3px solid #58aab7;outline-offset:2px}',
    '.siws-ai-open .siws-ai-launch{display:none}',
    '.siws-ai .siws-ai-panel{position:fixed;z-index:2147483002;top:calc(var(--siws-ai-top,72px) + 12px);right:calc(env(safe-area-inset-right) + 16px);',
    'bottom:calc(env(safe-area-inset-bottom) + 16px);width:min(400px,calc(100vw - 32px));display:flex;flex-direction:column;padding:0;',
    'background:#fff;border:1px solid var(--ai-line);border-radius:14px;box-shadow:0 18px 48px #0b263a40;overflow:hidden;outline:none}',
    // Twin: the drawer slot next to the left rail, clear of the top-right tools and the detail popup.
    '.siws-ai--twin .siws-ai-panel{left:calc(env(safe-area-inset-left) + 86px);right:auto;width:min(380px,calc(100vw - 102px));box-shadow:0 18px 48px #00000066}',
    '.siws-ai .siws-ai-head{display:flex;align-items:center;gap:4px;padding:10px 8px 10px 16px;border-bottom:1px solid var(--ai-line);flex:none}',
    '.siws-ai .siws-ai-title{flex:1;min-width:0}',
    '.siws-ai .siws-ai-title b{display:block;font-size:15px;font-weight:700}',
    '.siws-ai .siws-ai-title span{display:block;font-size:11px;color:var(--ai-dim);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
    '.siws-ai .siws-ai-icon{display:inline-flex;align-items:center;justify-content:center;min-width:40px;height:40px;padding:0 8px;border:0;border-radius:8px;',
    'background:transparent;color:var(--ai-dim);cursor:pointer;font:600 12px/1 inherit;font-family:inherit}',
    '.siws-ai .siws-ai-icon:hover{background:var(--ai-soft);color:var(--ai-accent-dark)}',
    '.siws-ai .siws-ai-icon:disabled{opacity:.45;cursor:default;background:transparent}',
    '.siws-ai .siws-ai-icon svg{width:18px;height:18px}',
    '.siws-ai .siws-ai-basis{flex:none;padding:8px 16px;background:#fff8e8;border-bottom:1px solid #f0e0b8;color:#6d5310;font-size:12px;line-height:1.45}',
    '.siws-ai .siws-ai-log{flex:1;min-height:0;overflow-y:auto;padding:14px 14px 6px;overscroll-behavior:contain;-webkit-overflow-scrolling:touch}',
    '.siws-ai .siws-ai-welcome{padding:4px 2px 10px;color:var(--ai-dim);font-size:13px}',
    '.siws-ai .siws-ai-welcome b{display:block;color:var(--ai-ink);font-size:14px;margin-bottom:4px}',
    '.siws-ai .siws-ai-welcome p{margin-top:6px}',
    '.siws-ai .siws-ai-examples{display:flex;flex-direction:column;gap:6px;margin-top:12px}',
    '.siws-ai .siws-ai-example{display:flex;flex-direction:column;align-items:flex-start;gap:2px;text-align:left;padding:8px 12px;border:1px solid var(--ai-line);',
    'border-radius:10px;background:#fff;color:var(--ai-ink);cursor:pointer;font:500 13px/1.4 inherit;font-family:inherit}',
    '.siws-ai .siws-ai-example small{font-size:11px;font-weight:650;color:var(--ai-accent-dark)}',
    '.siws-ai .siws-ai-example:hover{border-color:var(--ai-accent);background:var(--ai-soft);color:var(--ai-ink)}',
    '.siws-ai .siws-ai-example:disabled{opacity:.55;cursor:default}',
    '.siws-ai .siws-ai-msg{max-width:88%;margin:0 0 10px;padding:9px 12px;border-radius:12px;white-space:pre-wrap;word-break:break-word;overflow-wrap:anywhere}',
    '.siws-ai .siws-ai-msg--user{margin-left:auto;background:var(--ai-accent);color:#fff;border-bottom-right-radius:4px}',
    '.siws-ai .siws-ai-msg--assistant{background:#f3f6f8;border-bottom-left-radius:4px}',
    '.siws-ai .siws-ai-msg--error{background:#fff1ef;color:#96352d;border:1px solid #f3c9c2}',
    '.siws-ai .siws-ai-sources{display:flex;flex-wrap:wrap;align-items:center;gap:4px 6px;margin-top:8px;padding-top:6px;border-top:1px solid #dfe6ea;font-size:12px;white-space:normal}',
    '.siws-ai .siws-ai-sources > span{color:var(--ai-dim)}',
    '.siws-ai .siws-ai-sources a,.siws-ai .siws-ai-sources em{display:inline-block;max-width:100%;padding:3px 9px;border-radius:10px;background:#fff;',
    'border:1px solid var(--ai-line);color:var(--ai-accent-dark);text-decoration:none;font-style:normal;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}',
    '.siws-ai .siws-ai-sources a:hover{border-color:var(--ai-accent);background:var(--ai-soft)}',
    '.siws-ai .siws-ai-sources em{color:var(--ai-dim)}',
    '.siws-ai .siws-ai-source-move{padding:3px 10px;border-radius:10px;border:1px solid #14b8b0;background:#0e8f89;color:#fff;cursor:pointer;font:600 12px/1.4 inherit;font-family:inherit}',
    '.siws-ai .siws-ai-source-move:hover{background:#14b8b0;color:#fff}',
    '.siws-ai .siws-ai-retry{display:inline-block;margin-top:8px;padding:6px 12px;border:1px solid #e2a79e;border-radius:8px;background:#fff;color:#96352d;cursor:pointer;',
    'font:600 12px/1.2 inherit;font-family:inherit}',
    '.siws-ai .siws-ai-retry:hover{background:#fff6f4;color:#96352d}',
    '.siws-ai .siws-ai-pending{display:inline-flex;gap:4px;align-items:center;color:var(--ai-dim);font-size:13px}',
    '.siws-ai .siws-ai-pending i{flex:none;width:6px;height:6px;border-radius:3px;background:var(--ai-accent);opacity:.3;animation:siws-ai-dot 1.2s infinite}',
    '.siws-ai .siws-ai-pending i:nth-child(2){animation-delay:.2s}.siws-ai .siws-ai-pending i:nth-child(3){animation-delay:.4s}',
    '@keyframes siws-ai-dot{0%,80%,100%{opacity:.25}40%{opacity:1}}',
    '.siws-ai .siws-ai-form{display:flex;gap:8px;align-items:flex-end;padding:10px 12px 8px;border-top:1px solid var(--ai-line);flex:none}',
    '.siws-ai .siws-ai-form textarea{flex:1;min-width:0;min-height:42px;max-height:140px;resize:none;padding:10px 12px;border:1px solid #c9d6df;',
    'border-radius:10px;font:15px/1.4 inherit;font-family:inherit;color:var(--ai-ink);background:#fff;outline:none}',
    '.siws-ai .siws-ai-form textarea:focus{border-color:var(--ai-accent);box-shadow:0 0 0 3px #0f7f8622}',
    '.siws-ai .siws-ai-send{height:42px;min-width:64px;padding:0 14px;border:0;border-radius:10px;background:var(--ai-accent);color:#fff;cursor:pointer;',
    'font:650 14px/1 inherit;font-family:inherit;white-space:nowrap}',
    '.siws-ai .siws-ai-send:hover{background:var(--ai-accent-dark);color:#fff}',
    '.siws-ai .siws-ai-send:disabled{background:#9fbcc2;color:#fff;cursor:default}',
    '.siws-ai .siws-ai-foot{display:flex;justify-content:space-between;gap:8px;padding:0 14px 10px;font-size:11px;color:var(--ai-dim);flex:none}',
    '.siws-ai .siws-ai-count--near{color:#96352d;font-weight:650}',
    // Phones and short landscape screens: the whole viewport, sized to the visual viewport so the on-screen keyboard
    // never covers the input (--siws-ai-vh / --siws-ai-vt follow window.visualViewport).
    '@media (max-width:640px),(max-height:500px){.siws-ai .siws-ai-panel,.siws-ai--twin .siws-ai-panel{left:0;right:0;top:var(--siws-ai-vt,0px);bottom:auto;',
    'width:auto;height:var(--siws-ai-vh,100dvh);border-radius:0;border:0;box-shadow:none;',
    'padding:env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left)}',
    '.siws-ai .siws-ai-form textarea{font-size:16px}.siws-ai .siws-ai-keys{display:none}',
    '.siws-ai .siws-ai-icon{min-width:44px;height:44px}',
    '.siws-ai .siws-ai-launch{height:52px;right:calc(env(safe-area-inset-right) + 14px);bottom:calc(env(safe-area-inset-bottom) + 14px)}}',
    // ---- twin: dark panel in the dashboard's colours, yard guide character, move cards
    '.siws-ai--twin{--ai-accent:#14b8b0;--ai-accent-dark:#0e8f89;--ai-ink:#e6ebf3;--ai-dim:#9fb2c3;--ai-line:#2a3a4c;--ai-soft:#1f3040}',
    '.siws-ai--twin .siws-ai-panel{background:#121a25;border-color:#2a3a4c;color:var(--ai-ink)}',
    '.siws-ai--twin .siws-ai-head{align-items:flex-start;gap:10px;padding:12px 8px 12px 12px;background:linear-gradient(180deg,#17324099,#121a2500)}',
    '.siws-ai--twin .siws-ai-title b{color:#fff}',
    '.siws-ai--twin .siws-ai-speech{margin-top:5px;padding:6px 9px;border-radius:9px;border-top-left-radius:2px;background:#1e2e3d;color:#d7e5ee;font-size:12px;line-height:1.45;',
    'white-space:normal;overflow:visible;transition:background-color .25s}',
    '.siws-ai--twin [data-guide=fail] .siws-ai-speech{background:#3a2326;color:#ffc9c0}',
    '.siws-ai--twin [data-guide=arrived] .siws-ai-speech{background:#16393a;color:#bff3e9}',
    '.siws-ai .siws-ai-guide{flex:none;width:58px;height:58px;border-radius:16px;background:#0f2a36;overflow:hidden}',
    '.siws-ai .siws-ai-guide svg{display:block;width:58px;height:58px}',
    // ---- the guide figure (2D): moods show faces and poses; short one-off motions (wave, talk, hop), a slow blink
    '.siws-ai-char .st{display:none}',
    '.siws-ai-char :is(.arm,.leg,.head,.body,.mouth,.eyes){transform-box:view-box}',
    '.siws-ai-char .arm-l{transform-origin:28px 67px}.siws-ai-char .arm-r{transform-origin:72px 67px}',
    '.siws-ai-char .leg-l{transform-origin:43px 110px}.siws-ai-char .leg-r{transform-origin:57px 110px}',
    '.siws-ai-char .head{transform-origin:50px 58px}.siws-ai-char .mouth{transform-origin:50px 48px}.siws-ai-char .eyes{transform-origin:50px 40px}',
    '.siws-ai-char .body{transform-origin:50px 160px}',
    '.siws-ai-char .arm,.siws-ai-char .head{transition:transform .35s ease}',
    '.siws-ai :is([data-guide=idle],[data-guide=answer],[data-guide=choose],[data-guide=move],[data-guide=fail]) .siws-ai-char .st-open{display:inline}',
    '.siws-ai :is([data-guide=idle],[data-guide=answer],[data-guide=choose]) .siws-ai-char .st-smile{display:inline}',
    '.siws-ai :is([data-guide=think],[data-guide=search]) .siws-ai-char :is(.st-up,.st-flat,.st-think){display:inline}',
    '.siws-ai [data-guide=move] .siws-ai-char .st-grin{display:inline}',
    '.siws-ai [data-guide=arrived] .siws-ai-char :is(.st-happy,.st-grin){display:inline}',
    '.siws-ai [data-guide=fail] .siws-ai-char :is(.st-sad,.st-frown){display:inline}',
    '.siws-ai :is([data-guide=think],[data-guide=search]) .siws-ai-char .arm-l{transform:rotate(-40deg)}',
    '.siws-ai :is([data-guide=think],[data-guide=search]) .siws-ai-char .arm-r{transform:rotate(40deg)}',
    '.siws-ai [data-guide=move] .siws-ai-char .arm-r{transform:rotate(-128deg)}',
    '.siws-ai [data-guide=fail] .siws-ai-char .head{transform:rotate(7deg)}',
    '.siws-ai-char .eyes{animation:siws-ai-blink 5.2s infinite}',
    '@keyframes siws-ai-blink{0%,94%,100%{transform:scaleY(1)}96%{transform:scaleY(.12)}}',
    '.siws-ai-talk .siws-ai-char .mouth{animation:siws-ai-talk .18s ease-in-out 8 alternate}',
    '@keyframes siws-ai-talk{to{transform:scaleY(.3)}}',
    '.siws-ai-wave .siws-ai-char .arm-r{animation:siws-ai-wave .42s ease-in-out 5 alternate}',
    '@keyframes siws-ai-wave{from{transform:rotate(-155deg)}to{transform:rotate(-118deg)}}',
    '.siws-ai-hop .siws-ai-char .body{animation:siws-ai-hop .5s ease-out 2}',
    '@keyframes siws-ai-hop{40%{transform:translateY(-10px)}}',

    '.siws-ai--twin .siws-ai-basis{background:#1a2230;border-bottom-color:#2a3a4c;color:#c9d6e2}',
    '.siws-ai--twin .siws-ai-basis b{color:#f3d38b;font-weight:650}',
    '.siws-ai--twin .siws-ai-basis span{display:block;color:#93a6b8;margin-top:2px}',
    '.siws-ai--twin .siws-ai-welcome b{color:#fff}',
    '.siws-ai--twin .siws-ai-group{margin-top:12px;font-size:11px;font-weight:650;letter-spacing:.04em;color:#7fd6cf}',
    '.siws-ai--twin .siws-ai-examples{margin-top:6px}',
    '.siws-ai--twin .siws-ai-example{background:#18232f;border-color:#2a3a4c;color:#e6ebf3}',
    '.siws-ai--twin .siws-ai-example small{color:#7fd6cf}',
    '.siws-ai--twin .siws-ai-example:hover{background:#1f3040;border-color:#14b8b0;color:#fff}',
    '.siws-ai--twin .siws-ai-msg--assistant{background:#1c2836;color:#e6ebf3}',
    '.siws-ai--twin .siws-ai-msg--user{background:#0e8f89}',
    '.siws-ai--twin .siws-ai-msg--error{background:#35201f;color:#ffc9c0;border-color:#6b3a35}',
    '.siws-ai--twin .siws-ai-sources{border-top-color:#2a3a4c}',
    '.siws-ai--twin .siws-ai-sources a,.siws-ai--twin .siws-ai-sources em{background:#121a25;border-color:#33465a;color:#8fe3da}',
    '.siws-ai--twin .siws-ai-sources a:hover{background:#1f3040;border-color:#14b8b0}',
    '.siws-ai--twin .siws-ai-retry{background:#121a25;border-color:#6b3a35;color:#ffc9c0}',
    '.siws-ai--twin .siws-ai-form textarea{background:#0e151e;border-color:#33465a;color:#e6ebf3}',
    '.siws-ai--twin .siws-ai-form textarea::placeholder{color:#7f93a6}',
    '.siws-ai--twin .siws-ai-send:disabled{background:#2c4a50;color:#9fb2c3}',
    '.siws-ai--twin .siws-ai-icon:hover{background:#1f3040;color:#fff}',
    '.siws-ai .siws-ai-scene{display:block;white-space:normal}',
    '.siws-ai .siws-ai-scene-line{display:flex;gap:8px;align-items:flex-start}',
    '.siws-ai .siws-ai-scene-line i{flex:none;width:8px;height:8px;margin-top:7px;border-radius:4px;background:#7f93a6}',
    '.siws-ai .siws-ai-scene--moving .siws-ai-scene-line i{background:#f3d38b}',
    '.siws-ai .siws-ai-scene--arrived .siws-ai-scene-line i{background:#3ddc97}',
    '.siws-ai .siws-ai-scene--failed .siws-ai-scene-line i{background:#ff8a7a}',
    '.siws-ai .siws-ai-scene small{display:block;margin-top:4px;color:var(--ai-dim);font-size:11px}',
    '.siws-ai .siws-ai-choices{display:flex;flex-direction:column;gap:6px;margin-top:8px}',
    '.siws-ai .siws-ai-choice{display:flex;flex-direction:column;align-items:flex-start;gap:1px;width:100%;text-align:left;padding:8px 10px;border-radius:9px;cursor:pointer;',
    'border:1px solid #33465a;background:#121a25;color:#e6ebf3;font:500 13px/1.35 inherit;font-family:inherit}',
    '.siws-ai .siws-ai-choice:hover{border-color:#14b8b0;background:#1f3040;color:#fff}',
    '.siws-ai .siws-ai-choice small{color:#93a6b8;font-size:11px}',
    '.siws-ai .siws-ai-choice[aria-disabled=true]{opacity:.6;cursor:default;border-style:dashed}',
    '.siws-ai .siws-ai-choice[aria-current=true]{border-color:#3ddc97}',
    '@media (max-width:640px),(max-height:500px){.siws-ai .siws-ai-guide,.siws-ai .siws-ai-guide svg{width:48px;height:48px}}',
    // ---- twin companion: the guide pinned at the bottom right, above everything (Unity cards included), with a speech
    // bubble on its left. It never moves, so a press always lands on it.
    '.siws-ai .siws-ai-buddy{position:fixed;z-index:2147483001;right:calc(env(safe-area-inset-right) + var(--buddy-right,16px));bottom:calc(env(safe-area-inset-bottom) + var(--buddy-bottom,18px));',
    'display:flex;flex-direction:row-reverse;align-items:flex-start;gap:4px;pointer-events:none}',
    '.siws-ai-open .siws-ai-buddy{display:none}',
    '.siws-ai .siws-ai-buddy-me{pointer-events:auto;flex:none;width:76px;height:160px;padding:0;border:0;border-radius:12px;background:transparent;cursor:pointer;',
    'overflow:visible;filter:drop-shadow(0 6px 10px #00000066)}',
    '.siws-ai .siws-ai-buddy-me:hover{background:transparent;filter:drop-shadow(0 0 6px #73dec2aa) drop-shadow(0 6px 10px #00000066)}',
    '.siws-ai .siws-ai-buddy-me svg{display:block;width:100%;height:100%;overflow:visible}',
    '.siws-ai .siws-ai-bubble{pointer-events:auto;position:relative;max-width:270px;margin:2px 18px 0 0;padding:9px 30px 9px 12px;border-radius:12px;border-top-right-radius:3px;',
    'background:#121a25;border:1px solid #2a3a4c;color:#e6ebf3;font-size:13px;line-height:1.45;box-shadow:0 10px 26px #0000008c;opacity:0;transform:translateY(6px);',
    'transition:opacity .25s,transform .25s}',
    '.siws-ai .siws-ai-bubble--on{opacity:1;transform:none}',
    '.siws-ai .siws-ai-bubble-x{position:absolute;top:4px;right:4px;width:24px;height:24px;padding:0;border:0;border-radius:6px;background:transparent;color:#93a6b8;cursor:pointer;font:600 15px/1 inherit}',
    '.siws-ai .siws-ai-bubble-x:hover{background:#1f3040;color:#fff}',
    '.siws-ai .siws-ai-bubble-actions{display:flex;flex-wrap:wrap;gap:6px;margin-top:7px}',
    '.siws-ai .siws-ai-bubble-actions button{padding:4px 10px;border-radius:8px;border:1px solid #33465a;background:#18232f;color:#e6ebf3;cursor:pointer;font:600 12px/1.3 inherit;font-family:inherit}',
    '.siws-ai .siws-ai-bubble-actions button:first-child:not(.siws-ai-bubble-quiet){border-color:#14b8b0;background:#0e8f89;color:#fff}',
    '.siws-ai .siws-ai-bubble-actions button:hover{border-color:#73dec2}',
    '.siws-ai .siws-ai-quiet-toggle{margin-top:10px;padding:0;border:0;background:none;color:#7fd6cf;cursor:pointer;text-decoration:underline;font:500 12px/1.4 inherit;font-family:inherit}',
    '@media (max-width:640px),(max-height:500px){.siws-ai .siws-ai-buddy{--buddy-right:8px;--buddy-bottom:12px}',
    '.siws-ai .siws-ai-buddy-me{width:56px;height:118px}.siws-ai .siws-ai-bubble{max-width:calc(100vw - 140px);font-size:12px}}',
    '@media (prefers-reduced-motion:reduce){.siws-ai .siws-ai-pending i{animation:none;opacity:.7}',
    '.siws-ai--twin .siws-ai-speech,.siws-ai .siws-ai-bubble,.siws-ai-char .arm,.siws-ai-char .head{transition:none}',
    '.siws-ai-char *{animation:none!important}}'
  ].join('');

  // ------------------------------------------------------------------ DOM

  var CHAT_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 18l-1 3 4-2h9a3 3 0 0 0 3-3V7a3 3 0 0 0-3-3H7a3 3 0 0 0-3 3v9a3 3 0 0 0 1 2z"/><path d="M8 10h8M8 14h5"/></svg>';
  var CLOSE_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>';

  var root, launch, panel, log, subtitle, input, send, resetButton, counter, speech, replayLine;

  // Yard guide (original 2D vector): hard hat, high-visibility vest over navy coveralls, gloves and boots. Head, arms and
  // legs are separate groups so the figure can wave, point, blink and talk; faces and the tablet per mood (st-*).
  // The panel header shows the head and chest (viewBox crop), the screen companion the whole figure.
  function guideSvg(viewBox) {
    return '<svg class="siws-ai-char" viewBox="' + viewBox + '" aria-hidden="true">' +
      '<ellipse cx="50" cy="164" rx="21" ry="3.6" fill="#00000059"/>' +
      '<g class="body">' +
      '<g class="leg leg-l"><rect x="38" y="106" width="10" height="46" rx="4.5" fill="#1d3a5f"/>' +
      '<path d="M37 150h12v9H33q-2.4 0-1.8-2.6l.9-3.4q.6-3 4.9-3z" fill="#2b2f36"/></g>' +
      '<g class="leg leg-r"><rect x="52" y="106" width="10" height="46" rx="4.5" fill="#1d3a5f"/>' +
      '<path d="M51 150h12q4.3 0 4.9 3l.9 3.4q.6 2.6-1.8 2.6H51z" fill="#2b2f36"/></g>' +
      '<g class="arm arm-l"><rect x="23" y="63" width="10" height="38" rx="5" fill="#1d3a5f"/><circle cx="28" cy="102" r="5.4" fill="#e3b341"/></g>' +
      '<g class="torso"><rect x="30" y="58" width="40" height="56" rx="13" fill="#1d3a5f"/>' +
      '<path d="M33 63q3-4 9-5l8 9 8-9q6 1 9 5v38q0 4-4 4H37q-4 0-4-4z" fill="#ff8a1f"/>' +
      '<path d="M33.5 83h33M33.5 94h33" stroke="#e8f0f2" stroke-width="3"/>' +
      '<rect x="54" y="69" width="9" height="5" rx="1" fill="#14b8b0"/>' +
      '<g class="st st-think"><rect x="35" y="72" width="30" height="22" rx="2.5" fill="#203040" stroke="#7fd6cf" stroke-width="1.4"/>' +
      '<path d="M39 78h18M39 83h13M39 88h16" stroke="#7fd6cf" stroke-width="1.3"/></g></g>' +
      '<g class="arm arm-r"><rect x="67" y="63" width="10" height="38" rx="5" fill="#1d3a5f"/><circle cx="72" cy="102" r="5.4" fill="#e3b341"/></g>' +
      '<g class="head">' +
      '<rect x="45" y="50" width="10" height="11" rx="3" fill="#e9b98f"/>' +
      '<circle cx="33" cy="40" r="3.8" fill="#e9b98f"/><circle cx="67" cy="40" r="3.8" fill="#e9b98f"/>' +
      '<ellipse cx="50" cy="39" rx="17" ry="18" fill="#f2c9a0"/>' +
      '<circle cx="40.5" cy="46" r="3.1" fill="#f29a8a" opacity=".45"/><circle cx="59.5" cy="46" r="3.1" fill="#f29a8a" opacity=".45"/>' +
      '<g class="eyes st st-open"><ellipse cx="43" cy="40" rx="2.4" ry="2.8" fill="#2b2b2b"/><ellipse cx="57" cy="40" rx="2.4" ry="2.8" fill="#2b2b2b"/>' +
      '<circle cx="43.9" cy="38.9" r=".85" fill="#fff"/><circle cx="57.9" cy="38.9" r=".85" fill="#fff"/></g>' +
      '<g class="st st-up"><ellipse cx="43.6" cy="37.6" rx="2.3" ry="2.6" fill="#2b2b2b"/><ellipse cx="57.6" cy="37.6" rx="2.3" ry="2.6" fill="#2b2b2b"/></g>' +
      '<path class="st st-happy" d="M39.8 41q3.2-3.8 6.4 0M53.8 41q3.2-3.8 6.4 0" stroke="#2b2b2b" stroke-width="1.9" fill="none" stroke-linecap="round"/>' +
      '<path class="st st-sad" d="M38.5 34.5l6.5-1.8M61.5 34.5l-6.5-1.8" stroke="#5a3a28" stroke-width="1.7" stroke-linecap="round"/>' +
      '<g class="mouth">' +
      '<path class="st st-smile" d="M44.5 47.5q5.5 4.6 11 0" stroke="#8a4b2a" stroke-width="1.9" fill="none" stroke-linecap="round"/>' +
      '<path class="st st-grin" d="M43.5 46.5q6.5 7.5 13 0z" fill="#fff" stroke="#8a4b2a" stroke-width="1.6" stroke-linejoin="round"/>' +
      '<path class="st st-flat" d="M46.5 48.5h7" stroke="#8a4b2a" stroke-width="1.9" stroke-linecap="round"/>' +
      '<path class="st st-frown" d="M45 50.5q5-3.6 10 0" stroke="#8a4b2a" stroke-width="1.9" fill="none" stroke-linecap="round"/></g>' +
      '<path d="M31 32C31 14.5 69 14.5 69 32z" fill="#f5c400"/><rect x="27" y="30" width="46" height="5.2" rx="2.6" fill="#d9a900"/>' +
      '<rect x="48" y="15.5" width="4" height="15" fill="#d9a900"/><rect x="42.5" y="21.5" width="15" height="5" rx="1.5" fill="#14b8b0"/>' +
      '</g></g></svg>';
  }

  var GUIDE_LINES = {
    idle: '어디로 안내할까요? 시설·크레인·블록 이름을 말씀하시면 카메라를 옮겨 드려요.',
    think: 'ERP에서 계획과 실적을 확인하고 있어요.',
    answer: '조회 결과예요. 출처에서 원본을 확인할 수 있어요.',
    choose: '비슷한 대상이 여러 개예요. 이동할 곳을 골라 주세요.',
    fail: '이번 요청은 처리하지 못했어요. 아래 이유를 확인해 주세요.'
  };

  // A short one-off motion on the panel guide or the companion (talk, wave, hop); none with reduced motion (CSS).
  function act(target, name, ms) {
    if (!target) return;
    target.classList.remove(name);
    void target.offsetWidth;   // restart
    target.classList.add(name);
    clearTimeout(target['_' + name]);
    target['_' + name] = setTimeout(function () { target.classList.remove(name); }, ms);
  }

  function setGuide(mode, line) {
    if (!twin || !panel) return;
    panel.setAttribute('data-guide', mode);
    speech.textContent = line || GUIDE_LINES[mode] || GUIDE_LINES.idle;
    if (state.open) act(panel, mode === 'arrived' ? 'siws-ai-hop' : 'siws-ai-talk', 1500);
    if (buddy) buddy.setAttribute('data-guide', mode === 'answer' || mode === 'choose' ? 'idle' : mode);
  }

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }

  function button(className, text) {
    var node = el('button', className, text);
    node.type = 'button';
    return node;
  }

  // One press, one action. A plain click listener misses a press when the button moves or is rebuilt between
  // pointerdown and pointerup (the click then needs a second try). The pointer is captured on the button and the
  // action runs on release; the click that follows is ignored. Keyboard (Enter/Space) still comes in as a click.
  function press(node, run) {
    var down = null, handledAt = 0;
    node.addEventListener('pointerdown', function (event) {
      if (event.button !== 0 || !event.isPrimary) return;
      down = { id: event.pointerId, x: event.clientX, y: event.clientY };
      state.pressing = true;
      try { node.setPointerCapture(event.pointerId); } catch (e) { /* capture is a help, not a need */ }
    });
    function release(event, cancelled) {
      if (!down || event.pointerId !== down.id) return;
      var moved = Math.abs(event.clientX - down.x) + Math.abs(event.clientY - down.y);
      down = null;
      state.pressing = false;
      if (cancelled || moved > 24 || node.disabled) return;
      handledAt = Date.now();
      run(event);
    }
    node.addEventListener('pointerup', function (event) { release(event, false); });
    node.addEventListener('pointercancel', function (event) { release(event, true); });
    node.addEventListener('click', function (event) {
      if (Date.now() - handledAt < 700) { event.preventDefault(); return; }
      if (!node.disabled) run(event);
    });
  }

  function build() {
    var style = el('style');
    style.textContent = css;
    document.head.appendChild(style);

    root = el('div', 'siws-ai siws-ai--' + context);
    root.hidden = true;

    launch = button('siws-ai-launch');
    launch.innerHTML = CHAT_ICON;   // static markup only
    launch.appendChild(el('span', null, '도우미'));
    launch.setAttribute('aria-label', 'SIWS 도우미 열기');
    launch.setAttribute('aria-expanded', 'false');
    launch.title = 'ERP·디지털 트윈 조회 도우미';
    launch.hidden = !launcherWanted;
    press(launch, function () { open(); });

    panel = el('div', 'siws-ai-panel');
    panel.hidden = true;
    panel.id = 'siws-ai-panel';
    panel.tabIndex = -1;
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', 'SIWS 도우미');
    launch.setAttribute('aria-controls', panel.id);

    var head = el('div', 'siws-ai-head');
    var title = el('div', 'siws-ai-title');
    if (twin) {
      var guide = el('div', 'siws-ai-guide');
      guide.innerHTML = guideSvg('24 12 52 60');   // static markup only: head and chest
      guide.setAttribute('aria-hidden', 'true');
      head.appendChild(guide);
      panel.setAttribute('aria-label', 'SIWS 야드 안내');
    }
    title.appendChild(el('b', null, twin ? 'SIWS 야드 안내' : 'SIWS 도우미'));
    subtitle = el('span', null, screen.label);
    title.appendChild(subtitle);
    if (twin) {
      speech = el('div', 'siws-ai-speech');
      speech.setAttribute('role', 'status');
      title.appendChild(speech);
    }
    resetButton = button('siws-ai-icon', '새 대화');
    resetButton.title = '대화 기록 지우기';
    press(resetButton, function () { reset(); if (!coarsePointer()) input.focus(); });
    var close = button('siws-ai-icon');
    close.innerHTML = CLOSE_ICON;
    close.setAttribute('aria-label', '도우미 닫기');
    close.title = '닫기 (Esc)';
    press(close, function () { closePanel(); });
    head.appendChild(title);
    head.appendChild(resetButton);
    head.appendChild(close);
    panel.appendChild(head);

    if (twin) {
      var basis = el('div', 'siws-ai-basis');
      replayLine = el('b', null, '이 화면 재생: 확인 중');
      basis.appendChild(replayLine);
      basis.appendChild(el('span', null, screen.basis));
      panel.appendChild(basis);
    } else if (screen.basis) panel.appendChild(el('div', 'siws-ai-basis', screen.basis));

    log = el('div', 'siws-ai-log');
    log.setAttribute('role', 'log');
    log.setAttribute('aria-live', 'polite');
    log.setAttribute('aria-relevant', 'additions');
    panel.appendChild(log);

    var form = el('form', 'siws-ai-form');
    form.noValidate = true;
    input = el('textarea');
    input.rows = 1;
    input.maxLength = MESSAGE_LIMIT;
    input.placeholder = '질문을 입력하세요';
    input.setAttribute('aria-label', '도우미에게 질문');
    input.setAttribute('enterkeyhint', 'send');
    input.addEventListener('input', onInput);
    send = el('button', 'siws-ai-send', '보내기');
    send.type = 'submit';
    form.appendChild(input);
    form.appendChild(send);
    form.addEventListener('submit', function (event) { event.preventDefault(); submit(); });
    panel.appendChild(form);

    var foot = el('div', 'siws-ai-foot');
    foot.appendChild(el('span', 'siws-ai-keys', 'Enter 보내기 · Shift+Enter 줄바꿈 · Esc 닫기'));
    counter = el('span', 'siws-ai-count');
    counter.setAttribute('aria-live', 'polite');
    foot.appendChild(counter);
    panel.appendChild(foot);

    root.appendChild(launch);
    root.appendChild(panel);
    if (twin) buildBuddy();
    document.body.appendChild(root);
    renderWelcome();
    setGuide('idle');
  }

  function onInput() {
    autosize();
    var n = input.value.length;
    counter.textContent = n > MESSAGE_LIMIT - 300 ? n + ' / ' + MESSAGE_LIMIT : '';
    counter.className = 'siws-ai-count' + (n >= MESSAGE_LIMIT - 50 ? ' siws-ai-count--near' : '');
  }

  function autosize() {
    input.style.height = 'auto';
    input.style.height = Math.min(input.scrollHeight + 2, 140) + 'px';
  }

  function renderWelcome() {
    if (state.pressing) { setTimeout(renderWelcome, 250); return; }
    if (twin) { renderTwinWelcome(); return; }
    log.textContent = '';
    var welcome = el('div', 'siws-ai-welcome');
    welcome.appendChild(el('b', null, '무엇을 찾아볼까요?'));
    welcome.appendChild(el('span', null, screen.intro));
    welcome.appendChild(el('p', null, '호선번호(SN…)나 블록번호를 넣으면 더 정확합니다. 대화는 이 페이지에만 남고 새로고침·로그아웃하면 지워집니다.'));
    var examples = el('div', 'siws-ai-examples');
    screen.topics.forEach(function (key) {
      var topic = TOPICS[key];
      var question = topic.ask(state.samples);
      var item = button('siws-ai-example');
      item.appendChild(el('small', null, topic.tag));
      item.appendChild(el('span', null, question || topic.template + '…'));
      item.disabled = state.busy;
      press(item, function () {
        if (question) { ask(question); return; }
        input.value = topic.template;   // no ID known: the user completes the number
        onInput();
        input.focus();
        input.setSelectionRange(input.value.length, input.value.length);
      });
      examples.appendChild(item);
    });
    welcome.appendChild(examples);
    log.appendChild(welcome);
  }

  function renderTwinWelcome() {
    log.textContent = '';
    var welcome = el('div', 'siws-ai-welcome');
    welcome.appendChild(el('span', null, screen.intro));
    welcome.appendChild(el('p', null, '이동은 "…으로 이동해줘", "…보여줘"처럼 요청할 때만 하고, 재생 일자는 바꾸지 않습니다. 대화는 이 페이지에만 남습니다.'));
    var group = null, list = null;
    TWIN_EXAMPLES.forEach(function (example) {
      if (example.group !== group) {
        group = example.group;
        welcome.appendChild(el('div', 'siws-ai-group', group));
        list = el('div', 'siws-ai-examples');
        welcome.appendChild(list);
      }
      var text = typeof example.text === 'function' ? example.text(state.samples) : example.text;
      var item = button('siws-ai-example');
      item.appendChild(el('small', null, typeof example.tag === 'function' ? example.tag(state.samples) : example.tag));
      item.appendChild(el('span', null, text));
      item.disabled = state.busy;
      press(item, function () { ask(text); });
      list.appendChild(item);
    });
    if (buddyQuiet()) {
      var talk = button('siws-ai-quiet-toggle', '안내원 말풍선 다시 켜기');
      press(talk, function () { setBuddyQuiet(false); talk.remove(); });
      welcome.appendChild(talk);
    }
    log.appendChild(welcome);
  }

  function addMessage(role, text) {
    var welcome = log.querySelector('.siws-ai-welcome');
    if (welcome) welcome.remove();
    var bubble = el('div', 'siws-ai-msg siws-ai-msg--' + role, text);
    log.appendChild(bubble);
    log.scrollTop = log.scrollHeight;
    return bubble;
  }

  // A long answer is read from its start: bring the top of the bubble into view rather than the end of the log.
  function reveal(bubble) {
    log.scrollTop = bubble.offsetHeight > log.clientHeight - 24 ? bubble.offsetTop - log.offsetTop - 8 : log.scrollHeight;
  }

  // Where a source opens: 'view' = an ERP view on this ERP page (hash deep link, handled in place),
  // 'here' = this very page, 'tab' = another page, opened in a new tab so this screen and the conversation stay.
  function linkKind(url) {
    var target;
    try { target = new URL(url, location.href); } catch (e) { return null; }
    if (target.origin !== location.origin) return null;
    var samePage = target.pathname === location.pathname && target.search === location.search;
    if (samePage && context === 'erp' && target.hash.length > 1) return 'view';
    if (samePage && !target.hash) return 'here';
    return 'tab';
  }

  function addSources(bubble, sources) {
    if (!Array.isArray(sources) || !sources.length) return;
    var box = el('div', 'siws-ai-sources');
    box.appendChild(el('span', null, '출처'));
    sources.slice(0, 8).forEach(function (source) {
      if (!source) return;
      var label = String(source.label || source.url || '출처').slice(0, 80);
      var url = typeof source.url === 'string' ? source.url : '';
      var kind = url.charAt(0) === '/' && url.charAt(1) !== '/' && url.charAt(1) !== '\\' ? linkKind(url) : null;
      var block = twin && kind ? sceneBlock(url) : '';
      if (block) {
        // Twin: a twin link to a block becomes a move in this scene (a button the user presses), not a new page.
        var show = button('siws-ai-source-move', '장면에서 ' + block + ' 보기');
        show.title = '이 화면의 카메라를 블록으로 옮깁니다 (재생 일자는 그대로)';
        press(show, function () { if (!state.busy) moveRequest(null, block); });
        box.appendChild(show);
        return;
      }
      if (!kind) {
        box.appendChild(el('em', null, label));
        return;
      }
      var link = el('a', null, kind === 'tab' ? label + ' ↗' : kind === 'here' ? label + ' (현재 화면)' : label);
      link.href = url;   // same-origin path only
      link.title = kind === 'tab' ? '새 탭에서 열기' : kind === 'view' ? 'ERP에서 이 화면 열기' : '도우미를 닫고 현재 화면 보기';
      link.addEventListener('click', function (event) { followSource(event, url, kind); });
      box.appendChild(link);
    });
    bubble.appendChild(box);
  }

  function sceneBlock(url) {
    try {
      var target = new URL(url, location.href);
      var block = target.pathname === location.pathname ? target.searchParams.get('block') || '' : '';
      return SN_BLOCK.test(block) || /^[A-Z0-9][A-Z0-9_-]{1,40}$/i.test(block) ? block.toUpperCase() : '';
    } catch (e) { return ''; }
  }

  function followSource(event, url, kind) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;   // browser's own new tab/window
    event.preventDefault();
    if (kind === 'tab') {
      // window.open keeps the opener, so the new tab starts with this tab's login (sessionStorage is copied).
      if (!window.open(url, '_blank')) location.href = url;
      return;
    }
    if (kind === 'here' || !docked()) closePanel(true);
    if (kind === 'view') location.hash = new URL(url, location.href).hash;   // the ERP opens the view on hashchange
  }

  function setBusy(busy) {
    state.busy = busy;
    send.disabled = busy;
    resetButton.disabled = busy;
    send.textContent = busy ? '답변 중' : '보내기';
    Array.prototype.forEach.call(log.querySelectorAll('.siws-ai-example'), function (item) { item.disabled = busy; });
  }

  // ------------------------------------------------------------------ requests

  function headers(json) {
    var h = { Accept: 'application/json' };
    if (json) h['Content-Type'] = 'application/json';
    if (state.token) h.Authorization = 'Bearer ' + state.token;
    return h;
  }

  function checkHealth() {
    clearTimeout(state.retryTimer);
    if (!window.isSecureContext || !state.token || state.checking) {
      applyAvailability();
      return;
    }

    state.checking = true;
    var token = state.token;
    fetch('/assistant/health', { headers: headers(false), credentials: 'same-origin', cache: 'no-store' })
      .then(function (response) {
        if (!response.ok) throw new Error('health ' + response.status);
        return response.json();
      })
      .then(function (body) {
        if (token !== state.token) return;
        state.healthy = !!(body && body.ready === true);
        if (body && typeof body.model === 'string') state.model = body.model;
      })
      .catch(function () {
        if (token === state.token) state.healthy = false;
      })
      .then(function () {
        state.checking = false;
        if (token !== state.token) { checkHealth(); return; }
        state.checked = true;
        applyAvailability();
        if (!state.healthy && state.token) state.retryTimer = setTimeout(checkHealth, RETRY_HEALTH_MS);
      });
  }

  // Real IDs for the example questions, read with this login (the ERP read permission applies; a refusal keeps the
  // examples without an ID). Once per login, when the panel first opens.
  function loadSamples() {
    if (state.samplesFor === state.token) return;
    state.samplesFor = state.token;
    var token = state.token;
    function get(route) {
      return fetch('/api/' + route, { headers: headers(false), credentials: 'same-origin', cache: 'no-store' })
        .then(function (response) { return response.ok ? response.json() : null; })
        .catch(function () { return null; });
    }
    function pick(rows, key, pattern) {
      if (!Array.isArray(rows)) return '';
      for (var i = 0; i < rows.length; i++) {
        var value = rows[i] && rows[i][key];
        if (typeof value === 'string' && pattern.test(value)) return value.toUpperCase();
      }
      return '';
    }
    var wanted = screen.topics.join(' ');
    Promise.all([
      /hull/.test(wanted) ? get('hull_overview?size=30') : null,
      /block/i.test(wanted) ? get('erection_timeline?size=30') : null,
      /crane/.test(wanted) ? get('crane_activity') : null
    ]).then(function (replies) {
      if (token !== state.token) return;
      state.samples = {
        hull: pick(replies[0] && replies[0].rows, 'hull', SN_HULL),
        block: pick(replies[1] && replies[1].rows, 'block', SN_BLOCK),
        crane: pick(replies[2] && replies[2].cranes, 'id', CRANE_ID)
      };
      if (!state.busy && log.querySelector('.siws-ai-welcome')) renderWelcome();
    });
  }

  function status() {
    if (!window.isSecureContext) return 'unsupported';
    if (!state.token || (context === 'erp' && signedOut())) return 'login';
    if (state.healthy) return 'ready';
    return state.checking || !state.checked ? 'checking' : 'offline';
  }

  function available() {
    return status() === 'ready';
  }

  function applyAvailability() {
    if (!root) return;
    var on = available();
    root.hidden = !on;
    document.documentElement.classList.toggle('siws-ai-pad', on && context === 'erp' && !launch.hidden);
    if (!on && state.open) closePanel(true);
    subtitle.textContent = screen.label + (state.model ? ' · ' + state.model : '');
  }

  function ask(text) {
    if (state.busy) return;
    input.value = text;
    onInput();
    submit();
  }

  function submit() {
    var message = input.value.trim();
    if (!message || state.busy || !available()) return;
    input.value = '';
    onInput();
    var target = twin ? moveTarget(message) : null;
    if (target !== null) moveRequest(message, target);
    else request(message);
  }

  function request(message) {
    setBusy(true);
    var generation = state.generation;
    var history = state.history.slice(-HISTORY_LIMIT);
    addMessage('user', message);
    var pending = addMessage('assistant', '');
    var dots = el('span', 'siws-ai-pending');
    dots.appendChild(el('i'));
    dots.appendChild(el('i'));
    dots.appendChild(el('i'));
    var waitText = document.createTextNode(' ERP 조회 후 답변 작성 중');
    dots.appendChild(waitText);
    pending.appendChild(dots);
    pending.setAttribute('aria-busy', 'true');
    setGuide('think');

    var started = Date.now();
    var ticker = setInterval(function () {
      var seconds = Math.round((Date.now() - started) / 1000);
      waitText.nodeValue = ' ERP 조회 후 답변 작성 중 · ' + seconds + '초' + (seconds >= 15 ? ' (로컬 모델은 30초 넘게 걸릴 수 있습니다)' : '');
    }, 1000);
    var controller = window.AbortController ? new AbortController() : null;
    var timer = setTimeout(function () { if (controller) controller.abort(); }, REQUEST_TIMEOUT_MS);
    fetch('/assistant/chat', {
      method: 'POST',
      headers: headers(true),
      credentials: 'same-origin',
      body: JSON.stringify({ message: message, history: history, context: context }),
      signal: controller ? controller.signal : undefined
    })
      .then(function (response) {
        return response.json().catch(function () { return null; }).then(function (body) {
          if (!response.ok) {
            var failure = new Error(errorText(response.status, body));
            failure.status = response.status;
            throw failure;
          }
          if (!body || typeof body.answer !== 'string') throw new Error('응답 형식이 올바르지 않습니다.');
          return body;
        });
      })
      .then(function (body) {
        if (generation !== state.generation) return;
        pending.removeAttribute('aria-busy');
        pending.textContent = body.answer || '(빈 답변)';
        addSources(pending, body.sources);
        if (typeof body.model === 'string' && body.model) {
          state.model = body.model;
          subtitle.textContent = screen.label + ' · ' + state.model;
        }
        setGuide('answer');
        state.history.push({ role: 'user', content: message });
        state.history.push({ role: 'assistant', content: (body.answer || '(빈 답변)').slice(0, 2500) });
        if (state.history.length > HISTORY_LIMIT * 2) state.history = state.history.slice(-HISTORY_LIMIT * 2);
        reveal(pending);
      })
      .catch(function (error) {
        if (generation !== state.generation) return;
        var code = error && error.status;
        var text = error && error.name === 'AbortError' ? '답변이 ' + Math.round(REQUEST_TIMEOUT_MS / 1000) + '초 안에 오지 않아 중단했습니다. 질문을 짧게 해서 다시 시도해 주세요.'
          : error instanceof TypeError ? '도우미 서버에 연결할 수 없습니다. 네트워크를 확인해 주세요.'
          : (error && error.message) || '알 수 없는 오류가 발생했습니다.';
        pending.removeAttribute('aria-busy');
        pending.className = 'siws-ai-msg siws-ai-msg--error';
        pending.textContent = text;
        setGuide('fail', '조회하지 못했어요. ' + text);
        if (code === 401) {
          pending.appendChild(document.createTextNode('\n' + (context === 'erp' ? '로그아웃한 뒤 다시 로그인해 주세요.' : '포털에서 다시 로그인해 주세요.')));
        } else if (code !== 403 && code !== 422) {
          // Retrying helps after a timeout, a busy model (429), a network or server error; not after a refusal.
          var retry = button('siws-ai-retry', '다시 시도');
          press(retry, function () {
            if (state.busy || generation !== state.generation) return;
            var user = pending.previousElementSibling;
            pending.remove();
            if (user && user.classList.contains('siws-ai-msg--user')) user.remove();
            request(message);
          });
          pending.appendChild(document.createElement('br'));
          pending.appendChild(retry);
        }
        if (code === 422 && !input.value) input.value = message;   // too long or malformed: hand the text back to edit
        reveal(pending);
      })
      .then(function () {
        clearTimeout(timer);
        clearInterval(ticker);
        if (generation !== state.generation) return;
        setBusy(false);
        onInput();
        if (state.open && !coarsePointer()) input.focus();   // phones: do not pop the keyboard over the answer
      });
  }

  function errorText(status, body) {
    var detail = body && body.detail;
    if (typeof detail === 'string' && detail) return detail;
    if (Array.isArray(detail) && detail.length) {
      var loc = detail[0] && Array.isArray(detail[0].loc) ? detail[0].loc : [];
      if (loc[loc.length - 1] === 'message') return '질문은 ' + MESSAGE_LIMIT + '자까지 입력할 수 있습니다.';
      return '요청 형식이 맞지 않습니다. "새 대화"로 다시 시도해 주세요.';
    }
    if (status === 401) return '로그인이 만료되었습니다.';
    if (status === 403) return '이 내용을 조회할 권한이 없습니다.';
    if (status === 429) return '다른 질문에 답변하는 중입니다. 잠시 후 다시 시도해 주세요.';
    if (status >= 500) return '도우미 서버 오류입니다 (' + status + '). 잠시 후 다시 시도해 주세요.';
    return '요청을 처리하지 못했습니다 (' + status + ').';
  }

  function reset() {
    state.generation++;
    state.history = [];
    if (log) renderWelcome();
    if (send) setBusy(false);
    setGuide('idle');
  }

  // ------------------------------------------------------------------ twin: scene bridge and camera moves

  // Explicit move requests only: a move verb, and no question about plans or status unless the verb is unmistakable.
  var MOVE_VERB = /(이동|옮겨|가\s*줘|가\s*주세요|가\s*자|보여\s*줘|보여\s*주세요|보여\s*줄래|줌\s*인|확대|클로즈업|비춰|비추어|포커스|찾아\s*가|데려\s*가)/;
  var STRONG_MOVE = /(이동|옮겨|가\s*줘|가\s*주세요|줌\s*인|확대|클로즈업|비춰|비추어|포커스|찾아\s*가|데려\s*가)/;
  var QUESTION_WORD = /(계획|일정|예정|실적|합격|현황|상태|몇|언제|재생|배치|목록|알려|요약|정보|어디|있어|됐어|\?)/;
  var ID_IN_TEXT = /SN\d{3,8}-[A-Z0-9][A-Z0-9_-]*|\b(?:LC|JC)-[A-Z0-9-]{2,32}\b|SN\d{3,8}/i;

  /** The name/ID to move to for an explicit move request, '' when the target is missing, null when not a move. */
  function moveTarget(text) {
    if (!MOVE_VERB.test(text) || (QUESTION_WORD.test(text) && !STRONG_MOVE.test(text))) return null;
    var id = text.match(ID_IN_TEXT);
    if (id) return id[0].toUpperCase();
    var head = text.split(MOVE_VERB)[0].replace(/(카메라|화면|시점)\s*(를|을|로|으로)?/g, ' ');
    for (var i = 0; i < 4; i++) {
      head = head.replace(/\s+$/, '').replace(/(으로|로|에게|에서|에|을|를|좀|쪽|까지|위치|근처|가까이|한번|한 번)$/, '');
    }
    return head.replace(/^(지금|바로|저기|여기)\s+/, '').trim().slice(0, 80);
  }

  var scene = window.SiwsTwinScene || (window.SiwsTwinScene = { queue: [] });
  var sceneSeq = 0, sceneWaiting = {};
  if (twin) {
    scene.onReply = function (json) {
      var reply;
      try { reply = JSON.parse(json); } catch (e) { return; }
      var waiter = reply && sceneWaiting[reply.id];
      if (waiter) waiter(reply);
    };
    scene.onReady = function () { if (state.open) refreshReplay(); };
  }

  // One request to the scene; onReply may be called more than once (open: moving, then arrived/failed) until done().
  function sceneCall(request, onReply, timeoutMs) {
    if (!scene.ready) { onReply({ state: 'notready' }); return; }
    var id = ++sceneSeq;
    var timer = 0;
    function done() { clearTimeout(timer); delete sceneWaiting[id]; }
    function arm(ms) { clearTimeout(timer); timer = setTimeout(function () { done(); onReply({ state: 'timeout' }, done); }, ms); }
    sceneWaiting[id] = function (reply) { onReply(reply, done, arm); };
    arm(timeoutMs || 4000);
    request.id = id;
    if (scene.queue.length > 8) scene.queue.splice(0, scene.queue.length - 8);
    scene.queue.push(JSON.stringify(request));
  }

  function refreshReplay() {
    if (!twin || !replayLine) return;
    sceneCall({ op: 'state' }, function (reply, done) {
      if (done) done();
      buddyObserve(reply);
      if (!reply || reply.state !== 'ok') {
        replayLine.textContent = reply && reply.state === 'notready' ? '이 화면 재생: 장면 준비 중' : '이 화면 재생: 확인할 수 없음';
        return;
      }
      var when = reply.date + (reply.playing ? ' · 재생 중' : ' · 일시정지');
      replayLine.textContent = reply.synced ? '이 화면: 공용 계획 재생을 따르는 중 (' + when + ')'
        : '이 화면 재생(이 브라우저): ' + (reply.mode === 'hull' && reply.hull ? reply.hull + ' 탑재 · ' : '야드 일정 · ') + when;
    }, 2500);
  }

  // Korean particle after a name: 을/를, 이/가, (으)로; names ending in a digit or letter get the safe double form.
  function josa(word, kind) {
    var code = word.charCodeAt(word.length - 1) - 0xac00;
    if (code < 0 || code > 11171) return kind === 'ro' ? '(으)로' : kind === 'eul' ? '을(를)' : '이(가)';
    var jong = code % 28;
    if (kind === 'ro') return jong === 0 || jong === 8 ? '로' : '으로';
    if (kind === 'eul') return jong ? '을' : '를';
    return jong ? '이' : '가';
  }

  function compact(value) { return String(value || '').replace(/[\s\-_·]/g, '').toLowerCase(); }

  function sceneCard(bubble, kind, text, note) {
    bubble.className = 'siws-ai-msg siws-ai-msg--assistant siws-ai-scene siws-ai-scene--' + kind;
    var line = bubble.querySelector('.siws-ai-scene-line');
    if (!line) {
      line = el('div', 'siws-ai-scene-line');
      line.appendChild(el('i'));
      line.appendChild(el('span'));
      bubble.insertBefore(line, bubble.firstChild);
    }
    line.lastChild.textContent = text;
    var small = bubble.querySelector('.siws-ai-scene > small');
    if (note) {
      if (!small) { small = el('small'); bubble.insertBefore(small, line.nextSibling); }
      small.textContent = note;
    } else if (small) small.remove();
    reveal(bubble);
  }

  function moveRequest(message, target) {
    var generation = state.generation;
    if (message) addMessage('user', message);   // null: a move button the user pressed
    var bubble = addMessage('assistant', '');
    if (!target) {
      sceneCard(bubble, 'failed', '어디로 이동할지 알려 주세요.', '예: "J안벽으로 이동해줘", "SN2051-B101 블록 보여줘", "골리앗 크레인에 줌인해줘"');
      setGuide('fail', '이동할 시설·크레인·블록 이름이나 ID를 함께 말씀해 주세요.');
      return;
    }
    setBusy(true);
    sceneCard(bubble, 'moving', '장면에서 ‘' + target + '’' + josa(target, 'eul') + ' 찾는 중…');
    setGuide('search', '장면에서 ‘' + target + '’' + josa(target, 'eul') + ' 찾고 있어요.');
    sceneCall({ op: 'find', query: target }, function (reply, done) {
      if (done) done();
      if (generation !== state.generation) return;
      setBusy(false);
      if (!reply || reply.state !== 'found') {
        var why = !reply || reply.state === 'none' ? '‘' + target + '’' + josa(target, 'eul') + ' 지금 장면에서 찾지 못했습니다.'
          : reply.state === 'notready' ? '야드 장면이 아직 준비되지 않았습니다. 장면이 뜬 뒤 다시 요청해 주세요.'
          : '장면이 응답하지 않습니다. 잠시 후 다시 요청해 주세요.';
        sceneCard(bubble, 'failed', why, reply && reply.state === 'none' ? '명칭이나 ID 일부로 다시 말해 보세요. 왼쪽 통합검색에서도 찾을 수 있습니다.' : reply && reply.reason);
        setGuide('fail', why);
        buddyNote(why);
        return;
      }
      var results = reply.results || [];
      var exact = results.filter(function (r) { return compact(r.title) === compact(target) || compact(r.id) === compact(target); });
      var only = results.length === 1 ? results[0] : exact.length === 1 ? exact[0] : null;
      if (only) {
        if (only.available) openCandidate(bubble, target, only, generation);
        else {
          sceneCard(bubble, 'failed', '‘' + (only.title || only.id) + '’' + josa(only.title || only.id, 'eul') + ' 지금 보여드릴 수 없습니다.', only.reason);
          setGuide('fail', '지금 화면에는 없는 대상이에요. 재생 일자는 그대로 둘게요.');
          buddyNote('‘' + (only.title || only.id) + '’' + josa(only.title || only.id, 'eul') + ' 지금 화면에서 보여드릴 수 없어요. ' + (only.reason || ''));
        }
        return;
      }
      renderChoices(bubble, target, exact.length > 1 ? exact : results, generation);
    }, 4000);
  }

  function renderChoices(bubble, target, results, generation) {
    sceneCard(bubble, 'choose', '‘' + target + '’ 후보가 ' + results.length + '개 있습니다. 이동할 대상을 고르세요.');
    setGuide('choose');
    if (!state.open) buddySay('‘' + target + '’ 후보가 ' + results.length + '개예요. 어느 곳으로 갈까요?', [{ label: '고르기', run: function () { open(); } }], true);
    var list = el('div', 'siws-ai-choices');
    results.forEach(function (candidate) {
      var item = button('siws-ai-choice');
      item.appendChild(el('span', null, candidate.title || candidate.id));
      item.appendChild(el('small', null, [candidate.kind, candidate.id !== candidate.title ? candidate.id : '', candidate.available ? '' : '화면에 없음']
        .filter(Boolean).join(' · ')));
      if (!candidate.available) item.setAttribute('aria-disabled', 'true');
      press(item, function () {
        if (generation !== state.generation || state.busy) return;
        if (!candidate.available) {
          sceneCard(bubble, 'choose', '‘' + (candidate.title || candidate.id) + '’' + josa(candidate.title || candidate.id, 'eul') + ' 지금 보여드릴 수 없습니다.', candidate.reason);
          setGuide('fail', '지금 화면에는 없는 대상이에요. 다른 후보를 골라 주세요.');
          return;
        }
        Array.prototype.forEach.call(list.children, function (other) { other.removeAttribute('aria-current'); });
        item.setAttribute('aria-current', 'true');
        openCandidate(bubble, target, candidate, generation);   // a move the user pressed
      });
      list.appendChild(item);
    });
    bubble.appendChild(list);
    reveal(bubble);
  }

  // The scene's last candidate list may have changed (another search, replay moved on): look the target up again and
  // open the same object by its category and ID.
  function openCandidate(bubble, target, candidate, generation) {
    var name = candidate.title || candidate.id;
    setBusy(true);
    sceneCard(bubble, bubble.querySelector('.siws-ai-choices') ? 'choose' : 'moving', '‘' + name + '’' + josa(name, 'ro') + ' 카메라를 옮기는 중…');
    setGuide('move', '‘' + name + '’' + josa(name, 'ro') + ' 카메라를 옮기고 있어요.');
    sceneCall({ op: 'find', query: candidate.id || target }, function (found, done) {
      if (done) done();
      var match = found && found.results ? found.results.filter(function (r) { return r.id === candidate.id && r.category === candidate.category; })[0] : null;
      if (generation !== state.generation) return;
      if (!match) { finishMove(bubble, false, name, found && found.state === 'notready' ? '야드 장면이 아직 준비되지 않았습니다.' : '대상을 다시 찾지 못했습니다. 재생이 진행되면서 사라졌을 수 있습니다.'); return; }
      if (!match.available) { finishMove(bubble, false, name, match.reason); return; }
      if (fullScreen()) closePanel(true);   // phones: the panel covers the scene, so step aside while the camera moves
      sceneCall({ op: 'open', pick: match.pick }, function (reply, finish, arm) {
        if (generation !== state.generation) { if (finish) finish(); return; }
        if (reply.state === 'moving') { if (arm) arm(9000); return; }
        if (finish) finish();
        finishMove(bubble, reply.state === 'arrived', name, reply.state === 'timeout' ? '카메라 이동이 확인되지 않았습니다.' : reply.reason);
      }, 4000);
    }, 4000);
  }

  // ------------------------------------------------------------------ twin: the companion guide

  var buddy = null, buddyBubble = null, buddyText = null, buddyActions = null;
  var buddyState = {
    lastSpoke: 0, lastInteract: 0, hideAt: 0, hover: false, tips: 0, tipIndex: 0,
    greeted: false, last: null, pending: null
  };
  var BUDDY_TIPS = [
    { text: '‘J안벽으로 이동해줘’처럼 말씀하시면 카메라를 그곳으로 옮겨 드려요.', try: 'J안벽으로 이동해줘' },
    { text: '이번 주 탑재 계획이 궁금하세요? 계획과 확정 실적을 나눠서 알려드려요.', ask: '이번 주 탑재 계획과 합격 실적 알려줘' },
    { text: '골리앗 크레인을 가까이 보고 싶으면 말씀만 하세요.', try: '골리앗 크레인에 줌인해줘' },
    { text: '이 화면 재생과 공용 계획 재생은 따로 움직여요. 공용 재생 상황도 물어볼 수 있어요.', ask: '공용 트윈 재생은 지금 몇 시 어떤 작업 중이야?' },
    { text: '오늘 도크와 안벽이 어떻게 배정됐는지 ERP에서 찾아 드릴게요.', ask: '오늘 도크 예약과 안벽 선석 배정 알려줘' }
  ];

  function reducedMotion() {
    return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  }

  function buddyQuiet() {
    try { return localStorage.getItem('siws-guide-quiet') === '1'; } catch (e) { return false; }
  }

  function setBuddyQuiet(quiet) {
    try { quiet ? localStorage.setItem('siws-guide-quiet', '1') : localStorage.removeItem('siws-guide-quiet'); } catch (e) { /* per-viewer only */ }
    if (quiet) buddyHush();
    else buddySay('다시 말을 걸게요. 언제든 저를 누르면 대화를 열 수 있어요.', null, true);
  }

  function buildBuddy() {
    buddy = el('div', 'siws-ai-buddy');
    var me = button('siws-ai-buddy-me');
    me.innerHTML = guideSvg('12 8 76 160');   // static markup only: the whole figure
    me.setAttribute('aria-label', 'SIWS 야드 안내원: 눌러서 대화 열기');
    me.title = '야드 안내원 (눌러서 대화)';
    press(me, function () { buddyHush(); open(); });
    buddyBubble = el('div', 'siws-ai-bubble');
    buddyBubble.hidden = true;
    buddyText = el('div');
    buddyActions = el('div', 'siws-ai-bubble-actions');
    var x = button('siws-ai-bubble-x', '×');
    x.setAttribute('aria-label', '말풍선 닫기');
    press(x, buddyHush);
    buddyBubble.appendChild(buddyText);
    buddyBubble.appendChild(buddyActions);
    buddyBubble.appendChild(x);
    buddyBubble.addEventListener('pointerenter', function () { buddyState.hover = true; });
    buddyBubble.addEventListener('pointerleave', function () { buddyState.hover = false; });
    buddy.appendChild(me);
    buddy.appendChild(buddyBubble);
    buddy.setAttribute('data-guide', 'idle');
    root.appendChild(buddy);
  }

  // A line in the bubble. reaction: the answer to something the user did (shown even when quiet, right away).
  function buddySay(text, actions, reaction) {
    if (!buddy || (!reaction && buddyQuiet())) return false;
    buddyText.textContent = text;
    buddyActions.textContent = '';
    (actions || []).concat(reaction ? [] : [{ label: '그만 말하기', quiet: true, run: function () { setBuddyQuiet(true); } }]).forEach(function (action) {
      var item = button(action.quiet ? 'siws-ai-bubble-quiet' : null, action.label);
      press(item, function () { buddyHush(); action.run(); });
      buddyActions.appendChild(item);
    });
    buddyBubble.hidden = false;
    act(buddy, 'siws-ai-talk', 1500);
    var mood = buddy.getAttribute('data-guide');
    if (mood === 'arrived') act(buddy, 'siws-ai-hop', 1000);
    // next frame: fade in (the hidden attribute removes it from layout first)
    requestAnimationFrame(function () { buddyBubble.classList.add('siws-ai-bubble--on'); });
    buddyState.lastSpoke = Date.now();
    buddyState.hideAt = Date.now() + (actions && actions.length ? 11000 : 7500);
    return true;
  }

  function buddyHush() {
    if (!buddyBubble) return;
    buddyBubble.classList.remove('siws-ai-bubble--on');
    buddyBubble.hidden = true;
    buddyState.hideAt = 0;
    var mood = buddy.getAttribute('data-guide');
    if (mood === 'arrived' || mood === 'fail') buddy.setAttribute('data-guide', 'idle');
  }

  // Scene state (every 4 s): note what changed, to be said when the user is not busy.
  function buddyObserve(reply) {
    if (!buddy) return;
    if (!reply || reply.state !== 'ok') return;
    var last = buddyState.last;
    buddyState.last = reply;
    if (!buddyState.greeted) {
      buddyState.greeted = true;
      buddyState.pending = { wave: true, text: '안녕하세요, 야드 안내원이에요! 저를 누르면 카메라 이동이나 ERP 계획 조회를 도와드려요.' };
      return;
    }
    if (!last) return;
    if (reply.lift && reply.lift !== last.lift) {
      var block = reply.lift;
      buddyState.pending = {
        lift: block,
        text: '골리앗 크레인이 ' + block + ' 블록을 ' + (reply.liftHooked ? '들어 올리고' : '인양하러 가고') + ' 있어요' + (reply.liftDock ? ' (' + reply.liftDock + ')' : '') + '.',
        actions: [{ label: '보러 가기', run: function () { liftRequest(block); } }]
      };
    } else if (reply.synced && !last.synced) {
      buddyState.pending = { text: '지금은 이 화면이 공용 계획 재생을 따라가고 있어요.' };
    } else if (reply.mode === 'hull' && reply.hull && reply.hull !== last.hull) {
      buddyState.pending = { text: reply.hull + ' 탑재 재생을 보고 계시네요. 블록 번호를 말씀하시면 그 블록으로 안내할게요.' };
    } else if (last.playing && !reply.playing) {
      buddyState.pending = { text: '재생을 멈추셨네요. 궁금한 곳 이름을 말씀하시면 바로 보여드릴게요.', actions: [{ label: '말 걸기', run: function () { open(); } }] };
    }
  }

  // Once a second: hide an expired bubble; when the user has been idle a while, say what is pending or a tip.
  function buddyTick() {
    if (!buddy) return;
    var loading = document.getElementById('loading');
    var ready = !loading || loading.classList.contains('done') || loading.classList.contains('failed');
    buddy.hidden = !ready;   // not over the loading screen
    if (!ready || !available() || state.open || document.hidden) return;
    var now = Date.now();
    if (buddyState.hideAt && now > buddyState.hideAt && !buddyState.hover && !buddy.contains(document.activeElement)) buddyHush();
    var talking = !buddyBubble.hidden;
    var calm = now - buddyState.lastInteract > 10000;   // the user is not working the scene
    if (talking || !calm || !buddyState.greeted) return;   // the greeting (first scene state) comes first
    var since = now - buddyState.lastSpoke;
    var pause = !buddyState.lastSpoke ? 0 : buddyState.pending && buddyState.pending.lift ? 20000 : 45000;   // a live lift is worth saying sooner
    if (buddyState.pending && since > pause) {
      var line = buddyState.pending;
      buddyState.pending = null;
      if (line.lift && (!buddyState.last || buddyState.last.lift !== line.lift)) return;   // that lift is over: stay quiet
      if (buddySay(line.text, line.actions) && line.wave) act(buddy, 'siws-ai-wave', 2200);
      return;
    }
    if (since > 120000 && buddyState.tips < 6) {
      var tip = BUDDY_TIPS[buddyState.tipIndex++ % BUDDY_TIPS.length];
      buddyState.tips++;
      buddySay(tip.text, [tip.try ? { label: '해 보기', run: function () { open(); ask(tip.try); } }
        : { label: '물어보기', run: function () { open(); ask(tip.ask); } }]);
      return;
    }
  }

  function buddyNote(text) {
    if (!state.open) buddySay(text, [{ label: '대화 열기', run: function () { open(); } }], true);
  }

  // The companion's "보러 가기": frame the lift it mentioned (pressed by the user; the scene picks the current lift).
  function liftRequest(block) {
    if (state.busy) return;
    var generation = state.generation;
    var bubble = addMessage('assistant', '');
    setBusy(true);
    sceneCard(bubble, 'moving', block + ' 인양 현장으로 카메라를 옮기는 중…');
    setGuide('move', block + ' 인양 현장으로 가고 있어요.');
    sceneCall({ op: 'lift', query: block }, function (reply, finish, arm) {
      if (generation !== state.generation) { if (finish) finish(); return; }
      if (reply.state === 'moving') { if (arm) arm(9000); return; }
      if (finish) finish();
      finishMove(bubble, reply.state === 'arrived', (reply.title || block) + ' 인양 현장',
        reply.state === 'timeout' ? '카메라 이동이 확인되지 않았습니다.' : reply.state === 'notready' ? '야드 장면이 아직 준비되지 않았습니다.' : reply.reason, true);
    }, 4000);
  }

  function finishMove(bubble, arrived, name, reason, noDetail) {
    setBusy(false);
    if (arrived) {
      sceneCard(bubble, 'arrived', '‘' + name + '’에 도착했습니다.' + (noDetail ? '' : ' 하이라이트와 상세 정보를 열었습니다.'), '카메라만 옮겼습니다. 재생 일자와 재생 상태는 그대로입니다.');
      setGuide('arrived', '‘' + name + '’에 도착했어요.' + (noDetail ? '' : ' 하이라이트와 상세 정보를 확인해 보세요.'));
      if (!state.open) buddySay('‘' + name + '’에 도착했어요.' + (noDetail ? '' : ' 상세 정보를 열어 두었어요.'), null, true);
    } else {
      sceneCard(bubble, 'failed', '‘' + name + '’' + josa(name, 'ro') + ' 이동하지 못했습니다.', reason || '');
      setGuide('fail', '이동하지 못했어요. ' + (reason || ''));
      if (!state.open) buddySay('‘' + name + '’' + josa(name, 'ro') + ' 이동하지 못했어요. 저를 누르면 이유를 볼 수 있어요.', null, true);
    }
  }

  // ------------------------------------------------------------------ open / close / layout / keys

  function coarsePointer() {
    return !!(window.matchMedia && window.matchMedia('(pointer: coarse)').matches);
  }

  function fullScreen() {
    return !!(window.matchMedia && window.matchMedia('(max-width:640px),(max-height:500px)').matches);
  }

  // ERP on a wide screen: the panel sits beside the work area (the page makes room for it), so a view opened from a
  // source stays visible next to the conversation.
  function docked() {
    return context === 'erp' && state.open && window.innerWidth >= DOCK_MIN_WIDTH && !fullScreen();
  }

  function layout() {
    var header = document.querySelector('body > header');
    var top = 0;
    if (header && /fixed|sticky/.test(getComputedStyle(header).position)) top = Math.max(0, header.getBoundingClientRect().bottom);
    root.style.setProperty('--siws-ai-top', top + 'px');
    var view = window.visualViewport;
    if (view) {
      root.style.setProperty('--siws-ai-vh', Math.round(view.height) + 'px');
      root.style.setProperty('--siws-ai-vt', Math.round(view.offsetTop) + 'px');
    }
    document.documentElement.classList.toggle('siws-ai-docked', docked());
  }

  function open() {
    if (!root) return false;
    if (!available()) {
      if (state.token && !state.healthy) checkHealth();
      return false;
    }

    if (!state.open) {
      state.returnFocus = document.activeElement;
      state.open = true;
      panel.hidden = false;
      root.classList.add('siws-ai-open');
      launch.setAttribute('aria-expanded', 'true');
      layout();
      loadSamples();
      if (twin && !state.busy && log.querySelector('.siws-ai-welcome')) renderWelcome();   // quiet toggle, samples
      if (twin) {
        refreshReplay();
        clearInterval(state.replayTimer);
        state.replayTimer = setInterval(refreshReplay, 4000);
      }
    }

    setTimeout(function () {
      // Touch screens: focus the panel, not the input, so the keyboard comes up only when the input is tapped.
      if (coarsePointer()) panel.focus({ preventScroll: true });
      else input.focus();
      log.scrollTop = log.scrollHeight;
    }, 0);
    return true;
  }

  function closePanel(silent) {
    if (!root || !state.open) return;
    state.open = false;
    clearInterval(state.replayTimer);
    if (buddy) buddy.setAttribute('data-guide', 'idle');
    panel.hidden = true;
    root.classList.remove('siws-ai-open');
    launch.setAttribute('aria-expanded', 'false');
    layout();
    var back = state.returnFocus;
    state.returnFocus = null;
    if (panel.contains(document.activeElement)) document.activeElement.blur();
    if (silent) return;
    var canvas = document.getElementById('unity-canvas');
    if (context === 'twin' && canvas) canvas.focus();
    else if (back && back.focus && back !== document.body && document.contains(back)) back.focus();
    else if (launch && !launch.hidden) launch.focus();
  }

  // Keys typed in the panel never reach Unity (it listens on the window and swallows them) or the host page's own
  // shortcuts. This listener is registered before the Unity loader runs, so it comes first; the panel's own keys are
  // handled here.
  function onKey(event) {
    if (buddy && buddy.contains(event.target)) {
      event.stopImmediatePropagation();
      if (event.type !== 'keydown') return;
      if (event.key === 'Escape') { buddyHush(); var canvas = document.getElementById('unity-canvas'); if (canvas) canvas.focus(); }
      else if ((event.key === 'Enter' || event.key === ' ') && event.target.tagName === 'BUTTON') { event.preventDefault(); event.target.click(); }
      return;
    }
    if (!panel || !state.open || !panel.contains(event.target)) return;
    event.stopImmediatePropagation();
    if (event.type !== 'keydown') return;
    if (event.key === 'Escape') {
      if (event.isComposing) return;   // Esc that cancels an IME composition leaves the panel open
      event.preventDefault();
      closePanel();
    } else if (event.key === 'Enter' && event.target === input && !event.shiftKey && !event.isComposing && event.keyCode !== 229) {
      event.preventDefault();   // Korean IME: the Enter that ends a composition does not send
      submit();
    } else if ((event.key === 'Enter' || event.key === ' ') && event.target.tagName === 'BUTTON') {
      event.preventDefault();   // the button's own key handling is skipped with the propagation
      event.target.click();
    } else if (event.key === 'Tab' && !docked()) {
      trapTab(event);   // an overlay keeps focus inside; the docked ERP panel lets Tab move on to the page
    }
  }

  function trapTab(event) {
    var items = Array.prototype.filter.call(panel.querySelectorAll('button, a[href], textarea'), function (node) {
      return !node.disabled && node.offsetParent !== null;
    });
    if (!items.length) return;
    var first = items[0], last = items[items.length - 1];
    if (event.shiftKey && (document.activeElement === first || document.activeElement === panel)) { event.preventDefault(); last.focus(); }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
  }

  ['keydown', 'keyup', 'keypress'].forEach(function (type) { window.addEventListener(type, onKey, true); });

  // The login can change in this tab without a storage event (same tab): poll the token cheaply.
  function watch() {
    var token = readToken();
    if (token !== state.token) {
      state.token = token;
      state.healthy = false;
      state.checked = false;
      state.model = '';
      state.samples = {};
      state.samplesFor = null;
      reset();
      checkHealth();
    } else if (root) {
      var shown = !root.hidden;
      if (shown !== available()) applyAvailability();
    }
  }

  window.SiwsAssistant = {
    open: open,
    close: function () { closePanel(); },
    toggle: function () { if (state.open) { closePanel(); return false; } return open(); },
    isOpen: function () { return state.open; },
    available: available,
    status: status
  };

  // A modal <dialog> (the ERP's record and trace views) sits in the browser's top layer and makes the rest of the page
  // inert, whatever its z-index: the launcher and the panel would be covered and ignore clicks. While one is open the
  // widget moves into it (fixed positioning still pins it to the viewport's bottom right) and back to <body> after.
  function followTopLayer() {
    if (!root) return;
    var modals = Array.prototype.filter.call(document.querySelectorAll('dialog[open]'), function (dialog) {
      try { return dialog.matches(':modal'); } catch (e) { return false; }
    });
    var host = modals.length ? modals[modals.length - 1] : document.body;
    if (root.parentNode === host || root.contains(host)) return;
    var focused = root.contains(document.activeElement) ? document.activeElement : null;
    host.appendChild(root);
    if (focused) focused.focus({ preventScroll: true });
  }

  function start() {
    build();
    if (window.MutationObserver) {
      new MutationObserver(followTopLayer).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['open'] });
    }
    checkHealth();
    setInterval(watch, 1500);
    if (twin) {
      setInterval(function () { if (available() && !document.hidden && !state.open) refreshReplay(); }, 2000);
      setInterval(buddyTick, 1000);
      ['pointerdown', 'wheel', 'keydown', 'touchstart'].forEach(function (type) {
        window.addEventListener(type, function (event) {
          if (!buddy || !buddy.contains(event.target)) buddyState.lastInteract = Date.now();
        }, { capture: true, passive: true });
      });
    }
    window.addEventListener('storage', function (event) { if (event.key === 'shipyard-token') watch(); });
    window.addEventListener('resize', function () { if (state.open) layout(); });
    if (window.visualViewport) {
      window.visualViewport.addEventListener('resize', function () { if (state.open) layout(); });
      window.visualViewport.addEventListener('scroll', function () { if (state.open) layout(); });
    }
  }

  if (document.body) start();
  else document.addEventListener('DOMContentLoaded', start);
})();

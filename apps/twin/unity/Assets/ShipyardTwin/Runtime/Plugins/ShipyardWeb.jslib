mergeInto(LibraryManager.library, {
  ShipyardWeb_Origin: function () {
    var value = window.location.origin || '';
    var size = lengthBytesUTF8(value) + 1;
    var buffer = _malloc(size);
    stringToUTF8(value, buffer, size);
    return buffer;
  },
  // Canvas pixels per CSS pixel (the template caps devicePixelRatio, so read it back from the canvas).
  ShipyardWeb_PixelRatio: function () {
    var canvas = Module['canvas'];
    if (!canvas || !canvas.clientWidth) return window.devicePixelRatio || 1;
    return canvas.width / canvas.clientWidth;
  },
  // One query parameter of the page address (/twin/?hull=SN2052&block=...), handed over by the Portal.
  ShipyardWeb_Query: function (namePtr) {
    var value = '';
    try { value = new URLSearchParams(window.location.search).get(UTF8ToString(namePtr)) || ''; } catch (e) {}
    value = value.slice(0, 100);
    var size = lengthBytesUTF8(value) + 1;
    var buffer = _malloc(size);
    stringToUTF8(value, buffer, size);
    return buffer;
  },
  ShipyardWeb_Leave: function () {
    window.location.href = '/';
  },
  // Another drawer opens in the same slot: the assistant panel steps aside (the conversation stays).
  ShipyardWeb_CloseAssistant: function () {
    try { if (window.SiwsAssistant && window.SiwsAssistant.isOpen && window.SiwsAssistant.isOpen()) window.SiwsAssistant.close(); } catch (e) {}
  },
  // Twin assistant <-> scene (TwinAssistantBridge.cs). The page (widget.js) queues JSON requests on
  // window.SiwsTwinScene.queue; the scene takes one at a time and answers through onReply. Requests hold a name/ID the
  // user typed or the index of a candidate the user pressed, never coordinates or commands.
  ShipyardWeb_AssistantRegister: function () {
    var scene = window.SiwsTwinScene || (window.SiwsTwinScene = { queue: [] });
    scene.ready = true;
    try { if (typeof scene.onReady === 'function') scene.onReady(); } catch (e) {}
  },
  ShipyardWeb_AssistantTake: function () {
    var scene = window.SiwsTwinScene;
    if (!scene || !scene.queue || !scene.queue.length) return 0;
    var value = String(scene.queue.shift() || '').slice(0, 2000);
    var size = lengthBytesUTF8(value) + 1;
    var buffer = _malloc(size);
    stringToUTF8(value, buffer, size);
    return buffer;
  },
  ShipyardWeb_AssistantReply: function (jsonPtr) {
    var scene = window.SiwsTwinScene;
    var json = UTF8ToString(jsonPtr);
    try { if (scene && typeof scene.onReply === 'function') scene.onReply(json); } catch (e) {}
  },
  ShipyardWeb_Token: function () {
    var value = '';
    try { value = window.sessionStorage.getItem('shipyard-token') || ''; } catch (e) {}
    var size = lengthBytesUTF8(value) + 1;
    var buffer = _malloc(size);
    stringToUTF8(value, buffer, size);
    return buffer;
  }
});

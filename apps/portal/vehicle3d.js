// 360° equipment viewer for the equipment monitor: a turntable you spin by dragging sideways (like a car
// configurator), with inertia, a little tilt on vertical mouse drags, wheel/pinch zoom and arrow keys.
// Models are built from simple solids per ERP equipment type, so no model files are downloaded.
import * as THREE from 'three';
import {RoomEnvironment} from 'three/addons/environments/RoomEnvironment.js';

const PAINT = {
  yellow: {color: 0xf2b01e, roughness: .45, metalness: .25},
  orange: {color: 0xe8772e, roughness: .45, metalness: .2},
  white: {color: 0xf3f4f1, roughness: .5, metalness: .15},
  blue: {color: 0x1d5f9e, roughness: .45, metalness: .3},
  red: {color: 0xc8352b, roughness: .5, metalness: .2},
  steel: {color: 0x7c8a95, roughness: .5, metalness: .6},
  dark: {color: 0x2a3138, roughness: .7, metalness: .3},
  tyre: {color: 0x1b1d20, roughness: .9, metalness: 0},
  rim: {color: 0xb9c0c6, roughness: .35, metalness: .8},
  glass: {color: 0x8fb4cc, roughness: .08, metalness: .1, transparent: true, opacity: .55},
  black: {color: 0x111316, roughness: .6, metalness: .2},
  amber: {color: 0xffa31a, roughness: .3, metalness: 0, emissive: 0xff8a00, emissiveIntensity: .6},
  siren_r: {color: 0xff3b30, roughness: .3, emissive: 0xff2010, emissiveIntensity: .7},
  siren_b: {color: 0x2f7bff, roughness: .3, emissive: 0x1050ff, emissiveIntensity: .7},
  block: {color: 0x8796a3, roughness: .65, metalness: .35},
};
const materials = {};
const paint = name => materials[name] ||= new THREE.MeshStandardMaterial(PAINT[name]);

function part(group, geometry, material, x = 0, y = 0, z = 0, rotation = null) {
  const mesh = new THREE.Mesh(geometry, paint(material));
  mesh.position.set(x, y, z);
  if (rotation) mesh.rotation.set(...rotation);
  mesh.castShadow = material !== 'glass';
  mesh.receiveShadow = true;
  group.add(mesh);
  return mesh;
}
const box = (g, w, h, d, m, x, y, z) => part(g, new THREE.BoxGeometry(w, h, d), m, x, y, z);
const post = (g, r, h, m, x, y, z) => part(g, new THREE.CylinderGeometry(r, r, h, 14), m, x, y, z);

// A wheel lying across x (axle along z).
function wheel(g, r, width, x, y, z) {
  part(g, new THREE.CylinderGeometry(r, r, width, 24), 'tyre', x, y, z, [Math.PI / 2, 0, 0]);
  part(g, new THREE.CylinderGeometry(r * .55, r * .55, width + .02, 16), 'rim', x, y, z, [Math.PI / 2, 0, 0]);
}

function transporter({loaded}) {
  const g = new THREE.Group();
  const L = 24, W = 7, deckY = 1.55;
  box(g, L, .5, W, 'yellow', 0, deckY, 0);                       // deck plate
  box(g, L - .4, .7, W - 1.2, 'dark', 0, deckY - .55, 0);        // frame
  for (let i = 0; i < 8; i++) {                                   // 8 axle lines of dual wheels each side
    const x = -L / 2 + 1.8 + i * (L - 3.6) / 7;
    for (const side of [-1, 1]) {
      box(g, .5, .7, .5, 'dark', x, .95, side * (W / 2 - 1.1));   // swing arm
      wheel(g, .48, .34, x, .48, side * (W / 2 - .78));
      wheel(g, .48, .34, x, .48, side * (W / 2 - 1.42));
    }
  }
  // Low cab on the front left corner and the power pack on the right.
  box(g, 2.2, 1.7, 2.1, 'yellow', L / 2 - 1.2, deckY + 1.1, -W / 2 + 1.1);
  box(g, .06, 1.0, 1.7, 'glass', L / 2 - .07, deckY + 1.35, -W / 2 + 1.1);
  box(g, 1.6, 1.0, .06, 'glass', L / 2 - 1.2, deckY + 1.35, -W / 2 + .02);
  box(g, 2.6, 1.2, 2.2, 'dark', L / 2 - 1.4, deckY + .85, W / 2 - 1.2);
  post(g, .12, .9, 'dark', L / 2 - 2.2, deckY + 1.9, W / 2 - .7);
  post(g, .16, .22, 'amber', L / 2 - .6, deckY + 2.08, -W / 2 + 1.6);
  post(g, .16, .22, 'amber', -L / 2 + .6, deckY + .4, W / 2 - .6);
  for (let i = 0; i < 6; i++) {                                   // hazard stripes on both ends
    for (const end of [-1, 1]) box(g, .08, .45, .5, i % 2 ? 'black' : 'yellow', end * (L / 2 + .02), deckY - .1, -W / 2 + .6 + i * 1.2);
  }
  if (loaded) {                                                   // a hull block on supports
    for (const x of [-6, 0, 6]) box(g, .8, .6, W - 1.6, 'dark', x, deckY + .55, 0);
    box(g, 15, 4.2, W - 1, 'block', 0, deckY + 2.95, 0);
    box(g, 15.05, .15, W - .95, 'red', 0, deckY + 1.0, 0);
  }
  return g;
}

// Gantry on rails along z: two leg frames at x = ±span/2, box girder(s) on top, trolley and hook block.
function gantry({span, height, twin, legs, girder, trolley}) {
  const g = new THREE.Group();
  const depth = span * .28, top = height;
  for (const side of [-1, 1]) {
    const x = side * span / 2;
    box(g, 2.4, 1.4, depth + 4, 'dark', x, .9, 0);                 // sill beam
    for (const z of [-1, 1]) {
      for (const k of [-1, 1]) wheel(g, .55, .5, x + k * .7, .55, z * (depth / 2 + 1.2));
      const leg = box(g, 1.6, top, 1.6, legs, x, top / 2, z * depth / 2 * .82);
      leg.rotation.x = -z * Math.atan((depth / 2 * .18) / top);     // legs splay out towards the sill
    }
    box(g, 2.2, 1.6, depth * .9, legs, x, top - .8, 0);              // leg head
    for (let i = 1; i <= 3; i++) box(g, .5, .5, depth * (.82 + .18 * (1 - i / 4)), legs, x, top * i / 4, 0);
  }
  const length = span + (twin ? span * .35 : 4);
  const girders = twin ? [-depth * .28, depth * .28] : [0];
  for (const z of girders) {
    box(g, length, 3.2, 2, girder, 0, top + 1.6, z);
    box(g, length, .35, 2.05, 'blue', 0, top + .3, z);
  }
  box(g, length, .3, Math.abs(girders[0]) * 2 + 2.6, 'steel', 0, top + 3.35, 0);    // walkway
  for (const x of [-length / 2 + 1, length / 2 - 1]) box(g, .3, 1.1, Math.abs(girders[0]) * 2 + 2.6, 'steel', x, top + 3.9, 0);
  const tx = span * .12;
  box(g, 6, 2.6, Math.abs(girders[0]) * 2 + 3.2, trolley, tx, top + 4.8, 0);       // trolley house
  box(g, 2.2, 2, 2.2, trolley, tx - 2.4, top + 6.8, 0);
  const hookY = top * .42;
  for (const z of [-.6, .6]) post(g, .06, top + 4 - hookY, 'black', tx, (top + 4 + hookY) / 2, z);
  box(g, 2, 1.6, 1.8, 'yellow', tx, hookY - .4, 0);                // hook block
  part(g, new THREE.TorusGeometry(.6, .18, 10, 20, Math.PI * 1.4), 'steel', tx, hookY - 1.9, 0, [0, Math.PI / 2, Math.PI * .8]);
  box(g, 3, 2.6, 2.6, 'white', -span / 2 + 3.2, top - 1.4, depth * .2);           // operator cab
  box(g, 3.05, 1.3, 2.65, 'glass', -span / 2 + 3.2, top - 1.0, depth * .2);
  for (const x of [-span / 2, span / 2]) box(g, .7, .3, depth + 16, 'steel', x, .15, 0);  // rails
  return g;
}

function forklift() {
  const g = new THREE.Group();
  box(g, 2.3, .9, 1.25, 'orange', 0, .95, 0);
  box(g, .7, 1.0, 1.25, 'dark', -1.25, 1.0, 0);                      // counterweight
  box(g, .7, .25, .6, 'dark', -.1, 1.55, 0);                          // seat
  box(g, .6, .7, .1, 'dark', -.35, 1.85, 0);
  for (const [x, z] of [[.7, .55], [.7, -.55], [-.9, .55], [-.9, -.55]]) post(g, .05, 1.35, 'dark', x, 2.05, z);
  box(g, 1.75, .08, 1.2, 'dark', -.1, 2.72, 0);                       // overhead guard
  for (const z of [-.4, .4]) box(g, .14, 3.4, .14, 'steel', 1.3, 1.9, z);  // mast
  box(g, .12, .9, 1.05, 'dark', 1.42, .85, 0);                       // carriage
  for (const z of [-.3, .3]) box(g, 1.25, .07, .14, 'steel', 2.05, .35, z);  // forks
  for (const x of [.75, -.75]) for (const z of [-.62, .62]) wheel(g, x > 0 ? .34 : .28, .26, x, x > 0 ? .34 : .28, z);
  post(g, .09, .14, 'amber', -.6, 2.83, 0);
  return g;
}

function pickup() {
  const g = new THREE.Group();
  box(g, 5.3, .75, 1.9, 'white', 0, .8, 0);
  box(g, 2.3, .85, 1.8, 'white', .35, 1.6, 0);                        // cab
  box(g, 2.35, .55, 1.82, 'glass', .35, 1.68, 0);
  box(g, .06, .5, 1.6, 'glass', 1.5, 1.63, 0);
  box(g, 1.95, .4, 1.8, 'white', -1.65, 1.35, 0);                     // bed walls
  box(g, 1.85, .05, 1.65, 'dark', -1.65, 1.2, 0);
  box(g, 5.32, .12, 1.92, 'orange', 0, 1.05, 0);                      // stripe
  box(g, .5, .14, 1.1, 'dark', .35, 2.1, 0);                          // light bar
  box(g, .4, .14, .45, 'siren_r', .35, 2.2, -.27);
  box(g, .4, .14, .45, 'siren_b', .35, 2.2, .27);
  for (const x of [1.7, -1.6]) for (const z of [-.86, .86]) wheel(g, .4, .28, x, .4, z);
  box(g, .1, .2, 1.5, 'amber', 2.66, 1.0, 0);
  return g;
}

function truck() {
  const g = new THREE.Group();
  box(g, 6.6, .4, 1.9, 'dark', -.3, .75, 0);                          // chassis
  box(g, 1.8, 2.0, 2.2, 'white', 2.3, 1.95, 0);                       // cab
  box(g, .06, .9, 1.9, 'glass', 3.21, 2.35, 0);
  box(g, 1.2, .7, 2.21, 'glass', 2.4, 2.35, 0);
  box(g, 4.4, 2.3, 2.3, 'blue', -1.0, 2.15, 0);                       // cargo box
  box(g, 4.42, .25, 2.32, 'white', -1.0, 1.15, 0);
  box(g, .5, .14, 1.2, 'amber', 2.3, 3.02, 0);
  for (const x of [2.4, -1.6, -2.6]) for (const z of [-.95, .95]) wheel(g, .45, .32, x, .45, z);
  return g;
}

// Rail-mounted slewing crane. The dock version has a straight lattice jib; the quay
// version has an articulated outreach link above the hook for work over the berth.
function jibCrane({quay = false, gauge = 10}) {
  const g = new THREE.Group();
  const half = gauge / 2, deck = quay ? 9 : 7;
  const color = quay ? 'orange' : 'yellow';
  const beam = (a, b, width, paintName) => {
    const start = new THREE.Vector3(...a), end = new THREE.Vector3(...b);
    const delta = end.clone().sub(start), mid = start.clone().lerp(end, .5);
    const mesh = box(g, width, delta.length(), width, paintName, mid.x, mid.y, mid.z);
    mesh.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), delta.normalize());
  };
  for (const x of [-half, half]) {
    box(g, .55, .25, 20, 'steel', x, .13, 0);
    box(g, 1.6, 1.1, 11, 'dark', x, 1, 0);
    for (const z of [-4, 4]) {
      beam([x, 1.5, z], [x * .68, deck, z * .68], 1.2, color);
      part(g, new THREE.CylinderGeometry(.55, .55, .65, 16), 'dark', x, .55, z, [0, 0, Math.PI / 2]);
    }
  }
  box(g, gauge * .85, 1.4, 7, color, 0, deck, 0);
  post(g, 2.35, .85, 'dark', 0, deck + 1.1, 0);
  box(g, 7.3, 2, 4, color, -.8, deck + 2.2, 0);
  box(g, 2, 2.1, 2, 'dark', -5.1, deck + 2.2, 0);
  box(g, 2.2, 1.7, 1.55, 'white', 2, deck + 3.3, 1.9);
  box(g, 2.12, .92, .08, 'glass', 2, deck + 3.5, 2.7);
  const start = new THREE.Vector3(1, deck + 2.7, 0);
  const knee = new THREE.Vector3(quay ? 15 : 22, deck + (quay ? 18 : 13), 0);
  const tip = quay ? [27, deck + 15, 0] : knee.toArray();
  const node = (t, side) => {
    const v = start.clone().lerp(knee, t);
    v.z = side * (1 - .6 * t);
    return v.toArray();
  };
  for (const side of [-1, 1]) beam(node(0, side), node(1, side), .62, color);
  for (let i = 0; i < 6; i++) {
    beam(node(i / 6, -1), node((i + 1) / 6, 1), .24, 'dark');
    beam(node(i / 6, -1), node(i / 6, 1), .25, color);
  }
  if (quay) {
    beam(knee.toArray(), tip, .9, color);
    beam([-3, deck + 3, 0], [6, deck + 17, 0], .7, color);
    beam([6, deck + 17, 0], knee.toArray(), .2, 'steel');
  } else {
    beam([-3, deck + 3, 0], [0, deck + 9, 0], .65, color);
    beam([0, deck + 9, 0], tip, .14, 'steel');
  }
  beam(tip, [tip[0], deck + 4, 0], .1, 'black');
  box(g, 1.25, .8, .85, 'yellow', tip[0], deck + 3.8, 0);
  part(g, new THREE.TorusGeometry(.39, .11, 8, 16, Math.PI * 1.5), 'steel', tip[0], deck + 3.05, 0);
  return g;
}

const BUILDERS = {
  transporter, forklift, pickup, truck,
  dockJib: ({gauge}) => jibCrane({gauge}),
  quayJib: ({gauge}) => jibCrane({quay: true, gauge}),
  goliath: () => gantry({span: 56, height: 40, twin: true, legs: 'white', girder: 'white', trolley: 'yellow'}),
  gantry: () => gantry({span: 30, height: 22, twin: false, legs: 'yellow', girder: 'yellow', trolley: 'orange'}),
};

export function createVehicleViewer(host) {
  const canvas = document.createElement('canvas');
  host.prepend(canvas);
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({canvas, antialias: true, alpha: true});
  } catch {
    canvas.remove();
    throw new Error('이 브라우저에서 3D 미리보기를 표시할 수 없습니다.');
  }
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.05;

  const scene = new THREE.Scene();
  const pmrem = new THREE.PMREMGenerator(renderer);
  scene.environment = pmrem.fromScene(new RoomEnvironment(), .04).texture;
  scene.add(new THREE.HemisphereLight(0xffffff, 0xb8c4cc, .6));
  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  sun.castShadow = true;
  sun.shadow.mapSize.set(2048, 2048);
  scene.add(sun, sun.target);

  const floor = new THREE.Mesh(new THREE.CircleGeometry(1, 96), new THREE.ShadowMaterial({opacity: .22}));
  floor.rotation.x = -Math.PI / 2;
  floor.receiveShadow = true;
  const ring = new THREE.Mesh(new THREE.RingGeometry(.985, 1, 128),
    new THREE.MeshBasicMaterial({color: 0x9fb3c2, transparent: true, opacity: .55}));
  ring.rotation.x = -Math.PI / 2;
  ring.position.y = .01;
  const turntable = new THREE.Group();
  scene.add(floor, ring, turntable);

  const camera = new THREE.PerspectiveCamera(30, 1, .1, 2000);
  const view = {yaw: -.7, pitch: .32, zoom: 1, radius: 10, centre: new THREE.Vector3()};
  let velocity = 0, frame = 0, intro = 0, touched = false;

  function render() {
    turntable.rotation.y = view.yaw;
    const r = view.radius * view.zoom;
    camera.position.set(view.centre.x + r * Math.cos(view.pitch), view.centre.y + r * Math.sin(view.pitch), view.centre.z);
    camera.lookAt(view.centre);
    renderer.render(scene, camera);
    host.style.setProperty('--angle', ((((view.yaw * 180 / Math.PI) % 360) + 360) % 360).toFixed(0));
    host.dispatchEvent(new CustomEvent('viewer-angle', {detail: Math.round(((view.yaw * 180 / Math.PI) % 360 + 360) % 360)}));
  }
  function tick() {
    frame = 0;
    if (intro > 0) { view.yaw += intro; intro *= .965; if (intro < .0004) intro = 0; }
    if (Math.abs(velocity) > .0002) { view.yaw += velocity; velocity *= .93; } else velocity = 0;
    render();
    if (intro || velocity) frame = requestAnimationFrame(tick);
  }
  const kick = () => { if (!frame) frame = requestAnimationFrame(tick); };

  function resize() {
    const {width, height} = host.getBoundingClientRect();
    if (!width || !height) return;
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
    kick();
  }
  new ResizeObserver(resize).observe(host);

  // Pointer: horizontal drag spins (page keeps vertical touch scrolling: touch-action pan-y), mouse also tilts.
  const pointers = new Map();
  let last = null, pinch = null;
  canvas.addEventListener('pointerdown', event => {
    canvas.setPointerCapture(event.pointerId);
    pointers.set(event.pointerId, event);
    last = {x: event.clientX, y: event.clientY, t: performance.now()};
    velocity = 0; intro = 0;
    if (!touched) { touched = true; host.classList.add('viewer-touched'); }
    host.classList.add('viewer-dragging');
  });
  canvas.addEventListener('pointermove', event => {
    if (!pointers.has(event.pointerId)) return;
    pointers.set(event.pointerId, event);
    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      const d = Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
      if (pinch) view.zoom = THREE.MathUtils.clamp(view.zoom * pinch / d, .55, 1.6);
      pinch = d;
      kick();
      return;
    }
    const now = performance.now(), dx = event.clientX - last.x, dy = event.clientY - last.y;
    const step = dx / Math.max(canvas.clientWidth, 1) * Math.PI * 1.6;
    view.yaw += step;
    if (event.pointerType === 'mouse') view.pitch = THREE.MathUtils.clamp(view.pitch + dy * .006, .05, 1.1);
    velocity = step / Math.max(now - last.t, 8) * 16;
    last = {x: event.clientX, y: event.clientY, t: now};
    render();
  });
  const release = event => {
    pointers.delete(event.pointerId);
    pinch = null;
    if (!pointers.size) host.classList.remove('viewer-dragging');
    if (performance.now() - (last?.t ?? 0) > 80) velocity = 0;
    kick();
  };
  canvas.addEventListener('pointerup', release);
  canvas.addEventListener('pointercancel', release);
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    view.zoom = THREE.MathUtils.clamp(view.zoom * Math.exp(event.deltaY * .0012), .55, 1.6);
    kick();
  }, {passive: false});
  host.addEventListener('keydown', event => {
    const turn = {ArrowLeft: -1, ArrowRight: 1}[event.key];
    if (!turn) return;
    event.preventDefault();
    velocity = turn * .06;
    kick();
  });

  return {
    show({model, loaded = false, gauge}) {
      const build = BUILDERS[model] || BUILDERS.truck;
      turntable.traverse(item => item.geometry?.dispose());
      turntable.clear();
      const object = build({loaded, gauge});
      turntable.add(object);
      const bounds = new THREE.Box3().setFromObject(object);
      const size = bounds.getSize(new THREE.Vector3()), centre = bounds.getCenter(new THREE.Vector3());
      object.position.x -= centre.x;
      object.position.z -= centre.z;
      const reach = Math.hypot(size.x, size.z) / 2;
      floor.scale.setScalar(reach * 1.08);
      ring.scale.setScalar(reach * 1.08);
      view.centre.set(0, size.y * .42, 0);
      view.radius = Math.max(reach, size.y * .75) / Math.tan(camera.fov * Math.PI / 360) * 1.12;
      camera.near = view.radius / 50; camera.far = view.radius * 10; camera.updateProjectionMatrix();
      sun.position.set(reach * 1.2, size.y + reach * 2, reach * .9);
      const s = sun.shadow.camera;
      s.left = s.bottom = -reach * 1.3; s.right = s.top = reach * 1.3; s.near = 1; s.far = (size.y + reach * 4) * 2;
      s.updateProjectionMatrix();
      view.yaw = -.7; view.zoom = 1; velocity = 0;
      intro = touched ? 0 : .045;   // a short spin hints that it turns, until the first drag
      kick();
    },
    reset() { view.yaw = -.7; view.pitch = .32; view.zoom = 1; velocity = 0; kick(); },
  };
}

// In-app GLB viewer for cluster-map-downloader: three.js (MIT) scene renderer
// with a WASD + Space fly camera. Loaded inside WebView2 from
// https://cmdviewer.local/ ; the GLB is fetched from https://cmdmodels.local/
// (both are per-window virtual hosts mapped by the GUI to local folders).
// Model data stays on disk; three.js itself is imported from a CDN below.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const infoBox = document.getElementById('info');
const overlay = document.getElementById('overlay');
const ovmsg = document.getElementById('ovmsg');
const progwrap = document.getElementById('progwrap');
const progfill = document.getElementById('progfill');

const params = new URLSearchParams(location.search);
const modelFile = params.get('model');
const displayName = params.get('name') || modelFile || 'model';

function postReport(kind, text) {
  // best-effort host reporting; only works when running inside WebView2
  if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
    window.chrome.webview.postMessage({ kind: kind, text: text || '' });
  }
}

function reportError(message) {
  overlay.classList.add('error');
  ovmsg.textContent = message;
  progwrap.style.display = 'none';
  postReport('error', 'viewer: ' + message);
}

function formatBytes(bytes) {
  if (!bytes) { return 'unknown size'; }
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return value.toFixed(value >= 10 || unit === 0 ? 0 : 1) + ' ' + units[unit];
}

if (!modelFile) {
  reportError('No model was specified.');
}

const renderer = new THREE.WebGLRenderer({ antialias: true });
document.body.appendChild(renderer.domElement);
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.setSize(window.innerWidth, window.innerHeight);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x14161a);

const camera = new THREE.PerspectiveCamera(70, window.innerWidth / window.innerHeight, 0.05, 8000);
camera.rotation.order = 'YXZ';
camera.position.set(0, 4, 14);
camera.rotation.set(-0.15, 0, 0);

scene.add(new THREE.HemisphereLight(0xbfd4e8, 0x3a3f46, 1.0));
const sun = new THREE.DirectionalLight(0xffffff, 1.6);
sun.position.set(60, 120, 80);
scene.add(sun);
const back = new THREE.DirectionalLight(0x88a0b8, 0.5);
back.position.set(-50, 40, -70);
scene.add(back);

const grid = new THREE.GridHelper(200, 100, 0x2f353d, 0x22262c);
scene.add(grid);

const clayMaterial = new THREE.MeshStandardMaterial({
  color: 0x9aa2a8, roughness: 0.85, side: THREE.DoubleSide,
});

function applyViewMode() {
  if (!currentModel) { return; }
  currentModel.traverse((node) => {
    if (!node.isMesh) { return; }
    const materials = Array.isArray(node.material) ? node.material.filter(Boolean)
      : (node.material ? [node.material] : []);
    for (const material of materials) {
      if (clayMode) {
        if (!savedMaps.has(material)) {
          const saved = {};
          for (const slot of mapSlots) {
            saved[slot] = material[slot];
            material[slot] = null;
          }
          savedMaps.set(material, saved);
        }
      } else {
        const saved = savedMaps.get(material);
        if (saved) {
          for (const slot of mapSlots) { material[slot] = saved[slot]; }
          savedMaps.delete(material);
        }
      }
      material.wireframe = wireMode;
      material.needsUpdate = true;
    }
  });
}

// ---- fly state -------------------------------------------------------------
const keys = new Set();
let fast = false;
let yaw = 0;      // camera looks along -Z rotated by yaw (YXZ order)
let pitch = -0.15;
let velocity = new THREE.Vector3();
const speedBase = { value: 3.0 };
let lastSize = null;
let currentModel = null;
let clayMode = false;
let wireMode = false;
const savedMaps = new Map();
const mapSlots = ['map', 'normalMap', 'roughnessMap', 'metalnessMap', 'emissiveMap', 'aoMap', 'alphaMap'];

function resetPose(size) {
  lastSize = size || lastSize;
  if (!lastSize) { return; }
  const fit = Math.max(lastSize.x, Math.max(lastSize.y, lastSize.z), 1);
  speedBase.value = Math.max(1.0, fit * 0.35);
  const distance = fit * 0.9;              // from the model center at origin
  camera.position.set(fit * 0.1, lastSize.y * 0.4 + distance * 0.25, distance);
  yaw = Math.atan2(camera.position.x, camera.position.z); // face the center
  pitch = -0.35;
  velocity.set(0, 0, 0);
  camera.near = Math.max(fit / 500, 0.02);
  camera.far = Math.max(fit * 60, 200);
  camera.updateProjectionMatrix();
  camera.rotation.set(pitch, yaw, 0);
}

// yaw/pitch conventions (YXZ): forward = (-sin yaw, pitch, -cos yaw) local,
// so yaw = atan2(x, z) points the camera at the origin when it stands at (x, y, z).

// ---- pointer lock + keys ---------------------------------------------------
const canvas = renderer.domElement;
canvas.style.cursor = 'crosshair';
canvas.addEventListener('click', () => {
  if (!overlay.classList.contains('error') && document.pointerLockElement !== canvas) {
    canvas.requestPointerLock();
  }
});
document.addEventListener('pointerlockchange', () => {
  // the hint stays the same; locking state is visible through the mouse
});
document.addEventListener('mousemove', (event) => {
  if (document.pointerLockElement !== canvas) { return; }
  yaw -= event.movementX * 0.0024;
  pitch = Math.max(-1.55, Math.min(1.55, pitch - event.movementY * 0.0024));
});
document.addEventListener('keydown', (event) => {
  if (event.code === 'Space') { event.preventDefault(); }
  if (event.code === 'ShiftLeft' || event.code === 'ShiftRight') { fast = true; }
  if (event.code === 'KeyR') { resetPose(null); }
  if (event.code === 'KeyG') { grid.visible = !grid.visible; }
  if (event.code === 'KeyT') { clayMode = !clayMode; applyViewMode(); }
  if (event.code === 'KeyX') { wireMode = !wireMode; applyViewMode(); }
  keys.add(event.code);
});
document.addEventListener('keyup', (event) => {
  if (event.code === 'ShiftLeft' || event.code === 'ShiftRight') { fast = false; }
  keys.delete(event.code);
});
window.addEventListener('blur', () => { keys.clear(); fast = false; });
window.addEventListener('resize', () => {
  camera.aspect = window.innerWidth / window.innerHeight;
  camera.updateProjectionMatrix();
  renderer.setSize(window.innerWidth, window.innerHeight);
});

// ---- animation loop --------------------------------------------------------
let lastTime = performance.now();
function tick(now) {
  const dt = Math.min((now - lastTime) / 1000, 0.1);
  lastTime = now;

  if (document.pointerLockElement === canvas) {
    const forward = new THREE.Vector3(-Math.sin(yaw), 0, -Math.cos(yaw));
    const right = new THREE.Vector3(Math.cos(yaw), 0, -Math.sin(yaw));
    const wish = new THREE.Vector3();
    if (keys.has('KeyW')) { wish.add(forward); }
    if (keys.has('KeyS')) { wish.sub(forward); }
    if (keys.has('KeyD')) { wish.add(right); }
    if (keys.has('KeyA')) { wish.sub(right); }
    if (keys.has('Space')) { wish.y += 1; }
    if (keys.has('KeyC')) { wish.y -= 1; }
    if (wish.lengthSq() > 0) { wish.normalize(); }
    const speed = speedBase.value * (fast ? 3 : 1);
    // exponential smoothing keeps the motion smooth and FPS independent
    velocity.lerp(wish.multiplyScalar(speed), 1 - Math.exp(-dt * 9));
    camera.position.addScaledVector(velocity, dt);
    camera.rotation.set(pitch, yaw, 0);
  }

  renderer.render(scene, camera);
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

// ---- model loading ---------------------------------------------------------
async function loadModel() {
  if (!modelFile) { return; }
  const modelUrl = 'https://cmdmodels.local/' + encodeURIComponent(modelFile);
  let sizeBytes = 0;
  try {
    const probe = await fetch(modelUrl, { method: 'HEAD' });
    sizeBytes = Number(probe.headers.get('content-length')) || 0;
  } catch { /* size shown as unknown */ }

  new GLTFLoader().load(modelUrl, (gltf) => {
    const model = gltf.scene;
    model.updateMatrixWorld(true);
    currentModel = model;
    savedMaps.clear();

    let vertices = 0;
    let meshCount = 0;
    let fixedSlots = 0;
    const materials = new Set();
    model.traverse((node) => {
      if (!node.isMesh) { return; }
      meshCount += 1;
      const position = node.geometry && node.geometry.attributes && node.geometry.attributes.position;
      vertices += position ? position.count : 0;
      const nodeMaterials = Array.isArray(node.material) ? node.material.filter(Boolean)
        : (node.material ? [node.material] : []);
      if (nodeMaterials.length === 0) {
        node.material = clayMaterial;
      }
      for (const material of nodeMaterials) {
        materials.add(material);
        // Unity scenes frequently contain mirrored (negative-scale) meshes:
        // with front-face culling they render inside-out / black. DoubleSide
        // is the pragmatic generic-model-data fix.
        material.side = THREE.DoubleSide;
        for (const slot of mapSlots) {
          const texture = material[slot];
          // Degraded texture slots would render as black holes; drop them so
          // the geometry stays visible (same handling as the browser viewer).
          if (texture && !texture.image) {
            material[slot] = null;
            material.needsUpdate = true;
            fixedSlots += 1;
          }
        }
      }
    });
    applyViewMode();

    const box = new THREE.Box3().setFromObject(model);
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    grid.visible = false; // off by default: avoids the checker overlay on terrain
    grid.position.y = box.min.y - Math.max(size.y * 0.002, 0.002);
    model.position.sub(center);
    scene.add(model);
    resetPose(size);

    overlay.style.display = 'none';
    infoBox.textContent =
      displayName + ' | ' + formatBytes(sizeBytes) +
      ' | ' + meshCount + ' meshes | ' + vertices.toLocaleString('en-US') + ' vertices' +
      ' | ' + materials.size + ' materials' +
      (fixedSlots ? ' | ' + fixedSlots + ' degraded texture slot(s) neutralized' : '') +
      ' | personal use only';
    postReport('loaded', 'loaded: ' + displayName + ' (' + formatBytes(sizeBytes) + ', ' +
      meshCount + ' meshes' + (fixedSlots ? ', ' + fixedSlots + ' broken texture slot(s) neutralized' : '') + ')');
  }, (event) => {
    if (event.total) {
      const pct = Math.round(100 * event.loaded / event.total);
      progfill.style.width = pct + '%';
      ovmsg.textContent = 'Loading model... ' + pct + '%';
      postReport('status', 'loading ' + pct + '%');
    } else {
      postReport('status', 'loading...');
    }
  }, () => {
    reportError('Failed to load the model (missing file or unreadable GLB).');
  });
}

loadModel();

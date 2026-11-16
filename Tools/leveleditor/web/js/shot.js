/**
 * Headless render support for `Tools/leveleditor/shot.py`.
 *
 * This module is loaded by index.html next to main.js.  It adds nothing to the human
 * UI: it waits for the editor to finish loading, then exposes two globals:
 *
 *   window.__editorReady   a promise that resolves {ok:true} after boot + all GLB
 *                          loads + one rendered frame, or {ok:false, error}
 *   window.__editorShot    {version, readiness(), render(spec)}
 *
 * `render(spec)` is the whole picture: it applies a camera planned in Python, applies
 * the visibility layers, waits for no sleeps (readiness is signalled by the editor's
 * own load promises), renders exactly once per frame, composes the contact sheet,
 * labels / watermark / scale bar in 2D, and returns PNG data URLs plus the metadata an
 * agent needs to reason about the image without decoding it.
 *
 * The point of the split: Python owns *what* to look at (camera math, allowed paths,
 * reference-level rules); this file only executes it on the same scene the human sees.
 */

import * as THREE from "three";

const state = {
  ready: false,
  error: null,
  bootMs: null,
};

const FOCUS_COLOR = 0xffe14d;

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, Math.max(0, ms)));
}

function readiness() {
  const editor = window.__leveleditor;
  return {
    editor: Boolean(editor),
    booted: Boolean(editor?.ready),
    loading: editor?.view ? Boolean(editor.view.loading) : null,
    level: editor?.view?.doc?.level?.id || null,
    ready: state.ready,
    error: state.error,
  };
}

async function waitForEditorReady(timeoutMs = 120000) {
  const started = performance.now();
  while (performance.now() - started < timeoutMs) {
    if (window.__leveleditor?.ready && window.__leveleditor.view
        && !window.__leveleditor.view.loading) {
      break;
    }
    await sleep(100);
  }
  const editor = window.__leveleditor;
  if (!editor?.ready || !editor.view) {
    return { ok: false, error: "editor boot did not finish; see the page console" };
  }
  const view = editor.view;
  try {
    await Promise.all([...view.defPromises.values()]);
  } catch (error) {
    // A missing model is already reported by the page; the remaining props still draw.
    console.warn("shot: a model failed to load", error);
  }
  // Do not wait on requestAnimationFrame here: with software WebGL a full-scene frame
  // can take seconds, and two rAFs would add ~15 s to every cold start.  One
  // synchronous render is all the caller needs; the page's own loop continues after.
  // Shadows are off for this first frame too, or a 500-prop reference level spends a
  // minute compiling/drawing shadow passes before the first shot even starts.
  const renderer = view.renderer;
  const shadowsBefore = renderer.shadowMap.enabled;
  renderer.shadowMap.enabled = false;
  view.renderNow();
  renderer.shadowMap.enabled = shadowsBefore;
  state.ready = true;
  state.bootMs = Math.round(performance.now() - started);
  return {
    ok: true,
    bootMs: state.bootMs,
    loadTiming: view.lastLoadTiming || null,
    bootTiming: window.__leveleditorBootTiming || null,
  };
}

// --------------------------------------------------------------------------- //
// scene helpers
// --------------------------------------------------------------------------- //
function visibleBox(view) {
  const box = new THREE.Box3();
  // Recurse, do not Box3.setFromObject a group: the reference stage keeps its sky
  // shell and distant coast loaded but individually hidden, and a group-level box
  // would report a 240 m bbox for a 60 m level.
  const add = (object) => {
    if (!object || !object.visible) return;
    if (object.isMesh || object.isLine || object.isPoints || object.isSprite) {
      box.expandByObject(object);
      return;
    }
    for (const child of object.children || []) add(child);
  };
  for (const group of view.propObjects.values()) add(group);
  for (const group of view.structureObjects.values()) add(group);
  add(view.terrainGroup);
  add(view.collisionGroup);
  add(view.parentLines);
  add(view.ballGhost);
  for (const highlight of view.highlightHelpers?.values?.() || []) add(highlight);
  return box;
}

function framedBox(view, camera) {
  const frustum = new THREE.Frustum().setFromProjectionMatrix(
    new THREE.Matrix4().multiplyMatrices(camera.projectionMatrix,
      camera.matrixWorldInverse));
  const box = new THREE.Box3();
  const add = (object) => {
    if (!object || !object.visible) return;
    if (object.isMesh || object.isLine || object.isPoints || object.isSprite) {
      if (frustum.intersectsObject(object)) box.expandByObject(object);
      return;
    }
    for (const child of object.children || []) add(child);
  };
  for (const group of view.propObjects.values()) add(group);
  for (const group of view.structureObjects.values()) add(group);
  add(view.terrainGroup);
  add(view.collisionGroup);
  add(view.parentLines);
  add(view.ballGhost);
  return box;
}

function groundStartOnTerrain(view, plan) {
  if (plan.kind !== "start" || !view.refMode || !view.terrainGroup?.visible) return plan;
  const x = plan.position[0];
  const z = plan.position[2];
  const eyeAbove = Math.max(0.35, Number(plan.eyeHeight ?? 1.0)
    - Number(view.doc?.level?.groundY ?? 0));
  const meshes = [];
  view.terrainGroup.traverse((object) => {
    if (!object.isMesh || object.userData.refBackdrop) return;
    let parent = object;
    while (parent) {
      if (!parent.visible) return;
      parent = parent.parent;
    }
    meshes.push(object);
  });
  if (!meshes.length) return plan;
  const ray = new THREE.Raycaster(new THREE.Vector3(x, 400, z),
    new THREE.Vector3(0, -1, 0), 0, 1000);
  const hits = ray.intersectObjects(meshes, false);
  if (!hits.length) return plan;
  const surface = hits[0].point.y;
  const delta = surface + eyeAbove - plan.position[1];
  if (Math.abs(delta) < 0.05) return plan;
  return {
    ...plan,
    position: [x, plan.position[1] + delta, z],
    target: [plan.target[0], plan.target[1] + delta, plan.target[2]],
    startSurfaceY: round(surface, 4),
    startLiftedM: round(delta, 4),
  };
}

function threeBoxToWorld(box) {
  if (!box || box.isEmpty()) return null;
  return {
    min: [round(box.min.x, 4), round(box.min.y, 4), round(-box.max.z, 4)],
    max: [round(box.max.x, 4), round(box.max.y, 4), round(-box.min.z, 4)],
    size: [round(box.max.x - box.min.x, 4), round(box.max.y - box.min.y, 4),
      round(box.max.z - box.min.z, 4)],
  };
}

function round(value, digits = 4) {
  const scale = 10 ** digits;
  return Math.round(Number(value) * scale) / scale;
}

function levelSpawn(view) {
  const level = view.doc?.level || {};
  const raw = Array.isArray(level.ballStart) ? level.ballStart
    : (Array.isArray(level.playerSpawn) ? level.playerSpawn : [0, 0.15, 0]);
  return [Number(raw[0]) || 0, Number(raw[1]) || 0, Number(raw[2]) || 0];
}

function groundY(view) {
  return Number(view.doc?.level?.groundY || 0);
}

function buildShotGrid(view, layers) {
  disposeShotGrid(view);
  const box = visibleBox(view);
  if (box.isEmpty()) return null;
  const center = box.getCenter(new THREE.Vector3());
  const size = box.getSize(new THREE.Vector3());
  const extent = Math.max(8, Math.ceil(Math.max(size.x, size.z) / 10) * 10);
  const y = groundY(view) + 0.02;
  const group = new THREE.Group();
  group.name = "shot-grid";
  const fine = new THREE.GridHelper(extent, Math.max(2, Math.round(extent)),
    0x6f8c63, 0x6f8c63);
  fine.position.set(center.x, y, center.z);
  fine.material.transparent = true;
  fine.material.opacity = 0.22;
  fine.material.depthWrite = false;
  group.add(fine);
  const coarse = new THREE.GridHelper(extent, Math.max(1, Math.round(extent / 10)),
    0x9fc48e, 0x9fc48e);
  coarse.position.set(center.x, y + 0.002, center.z);
  coarse.material.transparent = true;
  coarse.material.opacity = 0.55;
  coarse.material.depthWrite = false;
  group.add(coarse);
  view.scene.add(group);
  view.shotGrid = group;
  return { extent, meters: extent, center: [round(center.x, 3), round(y, 3), round(center.z, 3)] };
}

function disposeShotGrid(view) {
  if (view.shotGrid) {
    view.scene.remove(view.shotGrid);
    view.shotGrid.traverse((object) => {
      object.geometry?.dispose?.();
      object.material?.dispose?.();
    });
    view.shotGrid = null;
  }
}

function setWireframe(view, enabled) {
  if (!enabled) {
    for (const [material, previous] of view.shotWireMaterials || []) {
      material.wireframe = previous;
    }
    view.shotWireMaterials = null;
    return;
  }
  if (view.shotWireMaterials) return;
  const materials = new Map();
  const visit = (object) => {
    if (!object || !object.isMesh) return;
    const list = Array.isArray(object.material) ? object.material : [object.material];
    for (const material of list) {
      if (!material || materials.has(material)) continue;
      materials.set(material, Boolean(material.wireframe));
      material.wireframe = true;
    }
  };
  for (const group of view.propObjects.values()) group.traverse(visit);
  for (const group of view.structureObjects.values()) group.traverse(visit);
  view.terrainGroup?.traverse(visit);
  view.collisionGroup?.traverse(visit);
  view.shotWireMaterials = [...materials.entries()];
}

function applyBall(view, diameter) {
  if (!view.ballGhost) return null;
  const d = Math.max(0.02, Number(diameter) || 0.3);
  const radius = d / 2;
  const spawn = levelSpawn(view);
  const y = groundY(view) + radius;
  view.ballGhost.visible = true;
  view.ballGhost.scale.setScalar(radius / 0.5);
  view.ballGhost.position.set(spawn[0], y, -spawn[2]);
  return { diameter: round(d, 4), position: [round(spawn[0], 4), round(y, 4), round(spawn[2], 4)] };
}

function applyLayers(view, layers, warnings) {
  const applied = [];
  const unavailable = [];
  const has = (name) => layers[name] === true;
  for (const group of view.propObjects.values()) group.visible = has("props");
  for (const group of view.structureObjects.values()) group.visible = has("structures");
  if (has("props")) applied.push("props");
  if (has("structures")) applied.push("structures");
  for (const group of view.structureObjects.values()) {
    const role = group.userData.role;
    if (role === "ground" || role === "boundary") {
      group.visible = group.visible && !(view.refMode && view.terrainGroup?.visible
        && view.showTerrain && !view.showLegacyPlanes);
    }
  }

  if (has("terrain")) {
    if (view.refMode && typeof view.setTerrain === "function") {
      const ok = view.setTerrain(true);
      (ok ? applied : unavailable).push(ok ? "terrain" : "terrain (no stage extraction)");
    } else {
      unavailable.push("terrain (only _ref_* levels carry a stage extraction)");
    }
  } else if (view.refMode) {
    view.setTerrain(false);
  }

  if (has("collision")) {
    if (view.refMode && typeof view.setCollision === "function") {
      // setCollision is async (it fetches the collision payload the first time).
      applied.push("collision(pending)");
    } else {
      unavailable.push("collision (only _ref_* levels carry a stage extraction)");
    }
  }

  if (has("parents")) {
    if (view.refMode && typeof view.setParentLines === "function") {
      const count = view.setParentLines(true);
      applied.push(count ? `parents(${count})` : "parents(0)");
    } else {
      unavailable.push("parents (only _ref_* levels carry parent links)");
    }
  } else {
    view.setParentLines(false);
  }

  if (has("grid")) {
    const grid = buildShotGrid(view, layers);
    if (grid) applied.push("grid"); else unavailable.push("grid (nothing visible to size it)");
    view.shotGrid = view.shotGrid || null;
  } else {
    disposeShotGrid(view);
  }

  if (layers.tier !== null && layers.tier !== undefined) {
    view.setLadder(Number(layers.tier), true);
    applied.push(`tier(${round(Number(layers.tier), 4)})`);
  } else {
    view.setLadder(view.ladder?.diameter || 0.3, false);
  }

  let ball = null;
  if (layers.ball !== null && layers.ball !== undefined) {
    ball = applyBall(view, layers.ball);
    if (ball) applied.push("ball");
  } else if (view.ballGhost) {
    // The editor always shows the spawn ghost; a close-up of a 5 cm prop does not need a
    // 30 cm sphere in front of the lens, so a shot only shows it when asked (or when a
    // tier ball size implies a scale reference).
    view.ballGhost.visible = layers.tier !== null && layers.tier !== undefined;
  }

  setWireframe(view, has("wire"));
  if (has("wire")) applied.push("wire");

  if (layers.motions === true) {
    const colored = view.setMotionColors(true);
    const paths = view.setMotionPaths(true);
    applied.push(`motions(colors ${colored}, paths ${paths})`);
    if (!colored && !paths) unavailable.push("motions (this level has no motion data)");
  }
  view.clearHighlights();
  view.clearSelection();
  if (Array.isArray(layers.select) && layers.select.length) {
    view.highlightIds(layers.select);
    applied.push(`select(${layers.select.length})`);
  }

  if (view.regionGroup) view.regionGroup.visible = false;
  view.needsRender = true;
  return { applied, unavailable, warnings, ball };
}

// --------------------------------------------------------------------------- //
// cameras
// --------------------------------------------------------------------------- //
function applyCamera(view, plan) {
  const camera = view.camera;
  camera.fov = Number(plan.fov) || 45;
  camera.near = 0.03;
  camera.far = 5000;
  camera.aspect = view.shotAspect || camera.aspect;
  camera.updateProjectionMatrix();
  view.cam.camera = camera;
  view.cam.setFromPositionTarget(
    new THREE.Vector3(plan.position[0], plan.position[1], plan.position[2]),
    new THREE.Vector3(plan.target[0], plan.target[1], plan.target[2]),
  );
  view.camera = view.cam.camera;
  camera.aspect = view.shotAspect || camera.aspect;
  camera.updateProjectionMatrix();
  view.needsRender = true;
}

function makeFocusMarker(view, plan) {
  if (!plan.mark) return null;
  const group = view.propObjects.get(plan.mark);
  if (!group) return null;
  group.updateMatrixWorld(true);
  const box = new THREE.Box3().setFromObject(group);
  if (box.isEmpty()) return null;
  const helper = new THREE.Box3Helper(box, FOCUS_COLOR);
  helper.material.transparent = true;
  helper.material.opacity = 0.95;
  helper.material.depthTest = false;
  helper.renderOrder = 40;
  view.scene.add(helper);
  view.shotFocusHelper = helper;
  return helper;
}

function clearFocusMarker(view) {
  if (view.shotFocusHelper) {
    view.scene.remove(view.shotFocusHelper);
    view.shotFocusHelper.geometry?.dispose?.();
    view.shotFocusHelper.material?.dispose?.();
    view.shotFocusHelper = null;
  }
}

// --------------------------------------------------------------------------- //
// 2D overlay: labels, scale bar, watermark, frame title
// --------------------------------------------------------------------------- //
function projectPoint(point, camera, width, height) {
  const projected = point.clone().project(camera);
  if (projected.z > 1 || projected.z < -1) return null;
  return {
    x: (projected.x * 0.5 + 0.5) * width,
    y: (-projected.y * 0.5 + 0.5) * height,
  };
}

function drawLabels(ctx, view, plan, width, height, count) {
  if (!count) return { count: 0, ids: [] };
  const target = new THREE.Vector3(plan.target[0], plan.target[1], plan.target[2]);
  const rows = [];
  for (const [id, group] of view.propObjects) {
    if (!group.visible) continue;
    const distance = group.position.distanceTo(target);
    rows.push({ id, group, distance });
  }
  rows.sort((a, b) => (a.distance - b.distance) || a.id.localeCompare(b.id));
  const picked = rows.slice(0, count);
  ctx.save();
  ctx.font = "600 15px system-ui, sans-serif";
  ctx.textBaseline = "middle";
  const seen = [];
  for (const row of picked) {
    const point = row.group.position.clone();
    point.y += 0.08;
    const at = projectPoint(point, view.camera, width, height);
    if (!at || at.x < 0 || at.y < 0 || at.x > width || at.y > height) continue;
    // Deterministic de-overlap: once a label is placed, a second one within 90x18 px is
    // skipped rather than piled on top of it.
    if (seen.some((other) => Math.abs(other.x - at.x) < 90
      && Math.abs(other.y - at.y) < 18)) continue;
    const text = row.id;
    const metrics = ctx.measureText(text);
    const y = at.y;
    seen.push({ x: at.x, y });
    ctx.fillStyle = "rgba(10,16,22,0.72)";
    ctx.fillRect(at.x + 5, y - 8, metrics.width + 8, 16);
    ctx.fillStyle = row.id === plan.mark ? "#ffe14d" : "#dff0ff";
    ctx.fillText(text, at.x + 9, y);
    ctx.fillStyle = row.id === plan.mark ? "#ffe14d" : "#9fd2ff";
    ctx.beginPath();
    ctx.arc(at.x, y, row.id === plan.mark ? 3.5 : 2.2, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.restore();
  return { count: picked.length, ids: picked.map((row) => row.id) };
}

function niceMetres(value) {
  const steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500];
  for (const step of steps) if (step >= value) return step;
  return steps[steps.length - 1];
}

function drawScaleBar(ctx, view, plan, width, height) {
  const camera = view.camera;
  const distance = Math.max(0.1, camera.position.distanceTo(
    new THREE.Vector3(plan.target[0], plan.target[1], plan.target[2])));
  const metresPerPixel = (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * distance)
    / height;
  const metres = niceMetres(metresPerPixel * 120);
  const pixels = metres / metresPerPixel;
  const x = width - pixels - 28;
  const y = height - 30;
  ctx.save();
  ctx.strokeStyle = "rgba(255,255,255,0.92)";
  ctx.lineWidth = 3;
  ctx.beginPath();
  ctx.moveTo(x, y);
  ctx.lineTo(x + pixels, y);
  ctx.moveTo(x, y - 6);
  ctx.lineTo(x, y + 6);
  ctx.moveTo(x + pixels, y - 6);
  ctx.lineTo(x + pixels, y + 6);
  ctx.stroke();
  ctx.font = "600 15px system-ui, sans-serif";
  ctx.fillStyle = "rgba(255,255,255,0.94)";
  ctx.textAlign = "center";
  ctx.shadowColor = "rgba(0,0,0,0.8)";
  ctx.shadowBlur = 4;
  ctx.fillText(metres < 1 ? `${metres} m` : `${metres} m`, x + pixels / 2, y - 12);
  ctx.restore();
  return { metres, pixels: Math.round(pixels) };
}

function drawTileText(ctx, info, width, height, watermark) {
  const bar = 30;
  ctx.save();
  ctx.fillStyle = "rgba(8,12,16,0.62)";
  ctx.fillRect(0, 0, width, bar);
  ctx.font = "600 16px system-ui, sans-serif";
  ctx.textBaseline = "middle";
  ctx.fillStyle = "#eaf4ff";
  const title = `${info.level} · ${info.viewName}`;
  ctx.fillText(title, 12, bar / 2 + 1);
  ctx.font = "13px system-ui, sans-serif";
  ctx.fillStyle = "rgba(220,235,250,0.8)";
  const right = info.layers.join(", ");
  const metrics = ctx.measureText(right);
  ctx.fillText(right, Math.max(12, width - metrics.width - 12), bar / 2 + 1);
  if (watermark) {
    ctx.fillStyle = "rgba(180,20,20,0.92)";
    ctx.fillRect(0, bar, width, 30);
    ctx.fillStyle = "#ffffff";
    ctx.font = "700 17px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.fillText("UNLICENSED REFERENCE — LOCAL EVIDENCE ONLY — DO NOT DISTRIBUTE",
      width / 2, bar + 16);
    ctx.textAlign = "left";
  }
  ctx.restore();
}

function drawWatermark(ctx, width, height, text = "UNLICENSED REFERENCE") {
  ctx.save();
  ctx.globalAlpha = 0.16;
  ctx.fillStyle = "#ff3b30";
  ctx.font = `800 ${Math.max(46, Math.round(width / 22))}px system-ui, sans-serif`;
  ctx.textAlign = "center";
  ctx.translate(width / 2, height / 2);
  ctx.rotate(-Math.PI / 7);
  for (let row = -2; row <= 2; row += 1) {
    ctx.fillText(text, 0, row * Math.max(90, height / 7));
  }
  ctx.restore();
}

// --------------------------------------------------------------------------- //
// one render
// --------------------------------------------------------------------------- //
async function syncScene(view) {
  const api = window.__leveleditor?.api;
  if (!api) return { synced: false, reason: "no api" };
  try {
    const payload = await api.loadLevel();
    const incoming = payload.state;
    if (!incoming) return { synced: false, reason: "no state" };
    if (!view.state || incoming.version !== view.state.version
        || incoming.level !== view.state.level) {
      await view.applyState(incoming);
      return { synced: true, version: incoming.version };
    }
    return { synced: false, version: incoming.version };
  } catch (error) {
    console.warn("shot: state sync failed", error);
    return { synced: false, reason: String(error?.message || error) };
  }
}

function pngOf(canvas) {
  return canvas.toDataURL("image/png");
}

function canvasSize(canvas) {
  return { width: canvas.width, height: canvas.height };
}

async function render(spec) {
  const started = performance.now();
  const timing = {};
  const warnings = [];
  const errors = [];
  const editor = window.__leveleditor;
  if (!editor?.ready) return { ok: false, error: "the editor is not ready", stage: "boot" };
  const view = editor.view;
  state.ready = true;

  const tile = spec.tile || { w: 1280, h: 720 };
  const width = Math.max(64, Math.round(tile.w));
  const height = Math.max(64, Math.round(tile.h));
  view.shotAspect = width / height;
  const views = spec.views || [];
  if (!views.length) return { ok: false, error: "no views", stage: "arguments" };

  const sync = await syncScene(view);
  timing.syncMs = Math.round(performance.now() - started);
  // `--time`: put the world at that moment of the server's trajectory before any frame is
  // drawn.  The pose is the same one the page's play button shows, because both come from
  // GET /api/v2/motion/trajectory.
  let playback = null;
  if (spec.time !== null && spec.time !== undefined) {
    const absolute = Math.max(0, Number(spec.time) || 0);
    const seconds = Math.max(30, Math.ceil(absolute) + 1);
    const loaded = await window.__leveleditor.loadTrajectory?.(seconds);
    if (loaded) {
      playback = window.__leveleditor.setMotionTime(absolute);
      timing.timeMs = Math.round(performance.now() - started - timing.syncMs);
    } else {
      warnings.push("time: the motion trajectory was not available");
    }
  }
  const layersInfo = await applyLayersBefore(view, spec.layers || {}, warnings);
  const appliedCount = layersInfo.applied.length;

  const renderer = view.renderer;
  // Software WebGL plus 130-511 shadow-casting props makes the first frame take a
  // minute.  A shot is for reading geometry, not for beauty, so shadows stay off
  // unless explicitly requested; the editor page itself is unchanged.
  const shadowsBefore = renderer.shadowMap.enabled;
  renderer.shadowMap.enabled = Boolean(spec.shadows);
  renderer.setPixelRatio(1);
  renderer.setSize(width, height, false);
  view.cam.resize(width / height);
  view.camera.aspect = width / height;
  view.camera.updateProjectionMatrix();

  const layout = spec.layout || { cols: 1, rows: 1 };
  const cols = Math.max(1, Number(layout.cols) || 1);
  const rows = Math.max(1, Number(layout.rows) || 1);
  const sheet = document.createElement("canvas");
  sheet.width = cols * width;
  sheet.height = rows * height;
  const sheetCtx = sheet.getContext("2d", { willReadFrequently: true });
  sheetCtx.fillStyle = "#0b0f13";
  sheetCtx.fillRect(0, 0, sheet.width, sheet.height);

  const frames = Math.max(1, Math.min(32, Number(spec.frames) || 1));
  const interval = Math.max(0, Number(spec.interval) || 0);
  const images = [];
  const viewMeta = [];
  const labelInfo = [];
  const framed = new THREE.Box3();
  let diffCanvas = null;
  let firstFrameCanvas = null;
  let lastFrameCanvas = null;
  const renderStarted = performance.now();

  for (let frame = 0; frame < frames; frame += 1) {
    if (frame > 0 && interval > 0) await sleep(interval * 1000);
    const frameCanvas = document.createElement("canvas");
    frameCanvas.width = sheet.width;
    frameCanvas.height = sheet.height;
    const frameCtx = frameCanvas.getContext("2d", { willReadFrequently: true });
    frameCtx.fillStyle = "#0b0f13";
    frameCtx.fillRect(0, 0, frameCanvas.width, frameCanvas.height);
    for (let index = 0; index < views.length; index += 1) {
      const plan = groundStartOnTerrain(view, views[index]);
      applyCamera(view, plan);
      clearFocusMarker(view);
      makeFocusMarker(view, plan);
      const before = performance.now();
      renderer.render(view.scene, view.camera);
      if (frame === 0) framed.union(framedBox(view, view.camera));
      const tileCanvas = document.createElement("canvas");
      tileCanvas.width = width;
      tileCanvas.height = height;
      const ctx = tileCanvas.getContext("2d", { willReadFrequently: true });
      ctx.drawImage(renderer.domElement, 0, 0, width, height);
      const labels = drawLabels(ctx, view, plan, width, height, spec.labels || 0);
      if (frame === 0) labelInfo.push({ view: plan.name, ...labels });
      drawTileText(ctx, {
        level: spec.level || view.doc?.level?.id || "level",
        viewName: plan.name,
        layers: layersInfo.applied,
      }, width, height, Boolean(spec.watermark));
      drawScaleBar(ctx, view, plan, width, height);
      const col = index % cols;
      const row = Math.floor(index / cols);
      frameCtx.drawImage(tileCanvas, col * width, row * height);
      if (frame === 0) {
        viewMeta.push({
          name: plan.name,
          kind: plan.kind,
          fov: round(view.camera.fov, 3),
          position: plan.position.map((value) => round(value, 4)),
          target: plan.target.map((value) => round(value, 4)),
          box: plan.box || null,
          mark: plan.mark || null,
          startSurfaceY: plan.startSurfaceY ?? null,
          startLiftedM: plan.startLiftedM ?? null,
          renderMs: Math.round(performance.now() - before),
        });
      }
    }
    if (spec.watermark) drawWatermark(frameCtx, frameCanvas.width, frameCanvas.height);
    if (frame === 0) firstFrameCanvas = frameCanvas;
    lastFrameCanvas = frameCanvas;
    images.push({ kind: frames > 1 ? "frame" : (views.length > 1 ? "sheet" : "view"),
      frame: frame + 1, canvas: frameCanvas, ...canvasSize(frameCanvas) });
  }
  clearFocusMarker(view);
  for (const image of images) image.dataUrl = pngOf(image.canvas);
  const compositeMs = Math.round(performance.now() - renderStarted);

  if (spec.diff && frames > 1) {
    diffCanvas = makeDiff(firstFrameCanvas, lastFrameCanvas, width, height);
    const diffUrl = pngOf(diffCanvas);
    images.push({ kind: "diff", frame: null, width: diffCanvas.width,
      height: diffCanvas.height, canvas: diffCanvas, dataUrl: diffUrl });
  }

  const sceneBox = visibleBox(view);
  const finalBox = framed.isEmpty() ? sceneBox : framed.clone();
  renderer.shadowMap.enabled = shadowsBefore;
  setWireframe(view, false);
  disposeShotGrid(view);
  view.clearHighlights();
  const totalMs = Math.round(performance.now() - started);
  timing.renderMs = compositeMs;
  timing.totalMs = totalMs;

  const cleanImages = images.map((image) => ({
    kind: image.kind, frame: image.frame, width: image.width, height: image.height,
    dataUrl: image.dataUrl,
  }));
  // The big strings are the only thing that leaves the page; drop the canvases.
  return {
    ok: true,
    level: spec.level || view.doc?.level?.id || null,
    levelHash: view.state?.file?.sha256 || null,
    propCount: view.state?.counts?.props ?? (view.doc?.props || []).length,
    defCount: view.doc?.defs?.length ?? null,
    images: cleanImages,
    views: viewMeta,
    sheet: { cols, rows, tile: { w: width, h: height },
      image: { w: sheet.width, h: sheet.height } },
    layers: { requested: spec.layers?.requested || [], applied: layersInfo.applied,
      unavailable: layersInfo.unavailable, warnings: layersInfo.warnings,
      ball: layersInfo.ball, appliedCount, shadows: Boolean(spec.shadows) },
    labels: labelInfo,
    visibleBox: threeBoxToWorld(finalBox),
    sceneBox: threeBoxToWorld(sceneBox),
    sync,
    playback,
    timings: timing,
    warnings,
    errors,
    readyMs: state.bootMs,
    ready: state.readyResult || null,
  };
}

async function applyLayersBefore(view, layers, warnings) {
  const result = applyLayers(view, layers, warnings);
  if (layers.collision === true && view.refMode
      && typeof view.setCollision === "function") {
    // Report what actually got built, not just that it was requested.
    await view.setCollision(true);
    if (view.collisionGroup) {
      result.applied = result.applied.map((entry) =>
        (entry === "collision(pending)" ? "collision" : entry));
    } else {
      result.unavailable.push("collision (no stage extraction)");
      result.applied = result.applied.filter((entry) => entry !== "collision(pending)");
    }
  }
  return result;
}

function makeDiff(beforeCanvas, afterCanvas, width, height) {
  const canvas = document.createElement("canvas");
  canvas.width = beforeCanvas.width;
  canvas.height = beforeCanvas.height;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  const before = beforeCanvas.getContext("2d", { willReadFrequently: true })
    .getImageData(0, 0, beforeCanvas.width, beforeCanvas.height);
  const after = afterCanvas.getContext("2d", { willReadFrequently: true })
    .getImageData(0, 0, afterCanvas.width, afterCanvas.height);
  const out = ctx.createImageData(before.width, before.height);
  for (let index = 0; index < before.data.length; index += 4) {
    const a = before.data;
    const b = after.data;
    const diff = Math.abs(a[index] - b[index]) + Math.abs(a[index + 1] - b[index + 1])
      + Math.abs(a[index + 2] - b[index + 2]);
    const luminance = (a[index] + a[index + 1] + a[index + 2]) / 3;
    const base = Math.round(luminance * 0.28);
    const boost = Math.min(255, diff * 2.2);
    out.data[index] = Math.min(255, base + boost);
    out.data[index + 1] = Math.min(255, base + boost * 0.55);
    out.data[index + 2] = Math.min(255, base + boost * 0.2);
    out.data[index + 3] = 255;
  }
  ctx.putImageData(out, 0, 0);
  ctx.save();
  ctx.fillStyle = "rgba(8,12,16,0.72)";
  ctx.fillRect(0, 0, Math.min(canvas.width, 460), 32);
  ctx.fillStyle = "#ffffff";
  ctx.font = "700 16px system-ui, sans-serif";
  ctx.fillText("DIFF first → last frame (red = changed)", 12, 22);
  ctx.restore();
  return canvas;
}

// --------------------------------------------------------------------------- //
// globals
// --------------------------------------------------------------------------- //
window.__editorShot = {
  version: 1,
  readiness,
  render,
  get ready() { return state.ready; },
};
window.__editorReady = waitForEditorReady().then((result) => {
  state.readyResult = result;
  if (!result.ok) state.error = result.error || "not ready";
  return result;
});

// A tiny console marker helps a person confirm the render hook is loaded.
if (new URLSearchParams(window.location.search).get("shot")) {
  console.info("shot: ready hook installed", readiness());
}

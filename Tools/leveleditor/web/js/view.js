/**
 * The three.js side of the level editor, with Unity scene-view interaction.
 *
 * Coordinates (the one conversion, involutive):
 *   Unity position  (x, y, z)     -> three (x, y, -z)
 *   Unity quaternion(x, y, z, w)  -> three (-x, -y, z, w)
 *
 * Interaction:
 *   left click / shift-click / box-drag   select, add/remove, rectangle select
 *   right hold                            fly (pointer lock, WASD/QE, Shift x3, wheel = speed)
 *   middle drag, Space+left, hand tool    pan
 *   Alt+left drag                          orbit around the pivot
 *   Alt+right drag / wheel (not flying)   dolly
 *   Q/W/E                                  hand / move / rotate tool
 *
 * Multi-selection shares one gizmo at the union pivot.  A drag is applied live to
 * every selected object and committed as ONE batch op (the server records one undo
 * step and exports once).  Props and structures can be mixed; props are never scaled.
 */

import * as THREE from "three";
import { TransformControls } from "three/addons/controls/TransformControls.js";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { SceneCamera } from "./camera.js";

const TIER_COLORS = {
  green: 0x38d66b,
  yellow: 0xe6c14a,
  red: 0xd95f52,
  grey: 0x8d99a6,
};

const ROLE_COLORS = {
  ground: 0x83a26c,
  box: 0xb08d6c,
  ramp: 0xd39a52,
  boundary: 0x79b6d6,
};

const SELECTION_COLOR = 0xffdf6a;
// The motion layer's own colour, used when a path is not the selected prop's path.
const MOTION_PATH_COLOR = 0x7fd4ff;
// A path that belongs to the selected prop is drawn in the selection colour so the
// highlighted polyline matches the selection box.
const MOTION_PATH_SELECTED_COLOR = 0xffdf6a;
const CLICK_THRESHOLD_PX = 4;

export function toThreePosition(position) {
  return new THREE.Vector3(position[0], position[1], -position[2]);
}

export function fromThreePosition(vector) {
  return [round(vector.x), round(vector.y), round(-vector.z)];
}

export function toThreeQuaternion(quat) {
  return new THREE.Quaternion(-quat[0], -quat[1], quat[2], quat[3]).normalize();
}

export function fromThreeQuaternion(quaternion) {
  return [
    round(quaternion.x, 6),
    round(quaternion.y, 6),
    round(-quaternion.z, 6),
    round(quaternion.w, 6),
  ];
}

function round(value, digits = 4) {
  return Number(value.toFixed(digits));
}

function unityAabbToThree(box) {
  return new THREE.Box3(
    new THREE.Vector3(box[0][0], box[0][1], -box[1][2]),
    new THREE.Vector3(box[1][0], box[1][1], -box[0][2])
  );
}

function threeBoxToUnity(box) {
  return [
    [round(box.min.x), round(box.min.y), round(-box.max.z)],
    [round(box.max.x), round(box.max.y), round(-box.min.z)],
  ];
}

export class EditorView {
  /**
   * @param {HTMLCanvasElement} canvas
   * @param {{onSelect: Function, onBatch: Function, onStatus: Function,
   *          onError: Function, onProgress: Function}} callbacks
   */
  constructor(canvas, callbacks = {}) {
    this.canvas = canvas;
    this.callbacks = callbacks;
    this.state = null;
    this.doc = null;
    this.derived = { props: {}, structures: {} };
    this.manifests = {};
    this.apiToken = "";

    this.renderer = new THREE.WebGLRenderer({
      canvas,
      antialias: true,
      preserveDrawingBuffer: true,
      powerPreference: "high-performance",
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 1.05;
    this.renderer.shadowMap.enabled = !window.location.search.includes("noshadow");
    this.renderer.shadowMap.type = THREE.PCFShadowMap;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0xc9dcec);
    this.scene.fog = new THREE.Fog(0xc9dcec, 55, 190);

    this.cam = new SceneCamera(1);
    this.cam.onChange = () => { this.needsRender = true; };
    this.camera = this.cam.camera;

    this.gizmo = new TransformControls(this.camera, canvas);
    this.gizmo.setSize(0.6);
    this.gizmo.setSpace("world");
    this.gizmoHelper = this.gizmo.getHelper();
    this.scene.add(this.gizmoHelper);
    this.gizmo.addEventListener("dragging-changed", (event) => {
      if (event.value) this._startGroupTransform();
      else this._commitGroupTransform();
    });
    this.gizmo.addEventListener("objectChange", () => this._onGizmoMove());

    this.proxy = new THREE.Object3D();
    this.scene.add(this.proxy);

    this.loader = new GLTFLoader();
    this.defPromises = new Map();
    this.defBoxes = new Map();
    this.propObjects = new Map();
    this.structureObjects = new Map();
    this.pickTargets = [];
    this.selectionIds = new Set();
    this.selectionObbHelpers = new Map();
    this.selectionWorldHelpers = new Map();
    this.highlightHelpers = new Map();
    //: UNLICENSED reference levels: bottom-pivoted ripped meshes and parent lines.
    this.refMode = false;
    this.readOnly = false;
    this.showParentLines = false;
    //: The motion layer (motions[]/paths[], from /api/v2/motion): one row per moving or
    //: parented prop, the class palette, and the paths[] polylines.  This client never
    //: computes motion: rows come resolved from the server and are only drawn/applied.
    this.v2Motions = null;
    this.v2MotionRows = new Map();
    this.v2MotionPaths = [];
    this.motionClassColors = new Map();
    this.motionClassMaterials = new Map();
    this.showMotionColors = false;
    this.showMotionPaths = false;
    this.showMotionRadius = true;
    this.motionPathGroup = null;
    this.motionPathLines = new Map();
    this.motionPathCount = 0;
    this.motionOverlay = null;
    //: Playback: the world-pose trajectory came from the server (motion_sim), and this
    //: client only interpolates between samples.  Times are seconds of game time.
    this.trajectory = null;
    this.trajectoryRows = new Map();
    this.trajectoryRate = 30;
    this.trajectoryDurationS = 0;
    this.playback = { playing: false, time: 0, speed: 1, scope: "all", loop: true };
    this.playbackBase = new Map();
    this.playbackPropCount = 0;
    this._playbackReportedAt = 0;
    this._playbackPosition = new THREE.Vector3();
    this._playbackQuaternionA = new THREE.Quaternion();
    this._playbackQuaternionB = new THREE.Quaternion();
    this.parentLines = null;
    this.parentLineCount = 0;
    //: The original stage of a reference level: terrain (stage.glb), the collision
    //: volumes, the true instance transforms, and whether each is currently drawn.
    this.refStage = null;            // state.ref.stage: counts + extraction location
    this.refTransforms = null;       // {itemId: {quat, scale, position, parent, zone, ...}}
    this.terrainGroup = null;
    this.terrainMaterial = null;
    this.collisionGroup = null;
    this.collisionPayload = null;
    this.showTerrain = true;
    this.terrainWireframe = false;
    this.showCollision = false;
    this.showLegacyPlanes = false;
    this.useOriginalHeight = true;
    this.refLoadMs = null;
    //: v2 (katamari.placement/2): terrain sidecar mesh, solids, activity walls/kill
    //: plane and the original prop rotation/scale.  All layers come from /api/v2/*.
    this.v2Mode = false;
    this.v2Summary = null;
    this.v2View = null;
    this.v2WallsGroup = null;
    this.v2KillGroup = null;
    this.v2WallCount = 0;
    this.v2ParentLinks = [];
    this.v2Rotations = true;
    this.v2Scale = true;
    this.v2ShowSolids = true;
    this.v2ShowWalls = true;
    this.v2ShowKill = true;
    this.multiFrame = null;
    this.showWorldBoxes = false;
    this.boxStyle = "obb";
    this.selectionBox = new THREE.Box3();
    this.groupStart = null;
    this.tool = "move";
    this.spaceMode = "world";
    this.snap = { translate: 0.05, rotate: 15 };
    this._ctrl = false;
    this.panModifier = false;
    this.pending = null;
    this.navigation = null;
    this.flyPointerId = null;
    this.flyFallback = false;
    this.boxElement = document.getElementById("box-select");
    this.loading = true;
    this.needsRender = true;
    this.ladder = { diameter: 0.3, enabled: false };
    this.raycaster = new THREE.Raycaster();
    this.pointer = new THREE.Vector2();
    this.currentCamera = "overview";
    this.regions = [];
    this.activeRegion = null;
    this.regionMode = false;
    this.regionGroup = new THREE.Group();
    this.scene.add(this.regionGroup);
    this.regionHandles = [];
    this.regionInteraction = null;
    this.regionPreview = null;
    this.statsScopeIds = null;
    this.fadedMaterial = new THREE.MeshStandardMaterial({
      color: 0x8f99a3, transparent: true, opacity: 0.12, depthWrite: false,
      roughness: 1, metalness: 0,
    });

    this._bindPointer();
    this._bindKeyboard();
    this._resizeObserver = new ResizeObserver(() => this.resize());
    this._resizeObserver.observe(canvas.parentElement || canvas);
    this.resize();
    this._animate = this._animate.bind(this);
    this._frame = requestAnimationFrame(this._animate);
  }

  get isFlying() {
    return this.cam.fly.active;
  }

  get selectedIds() {
    return [...this.selectionIds];
  }

  // ------------------------------------------------------------------ #
  // input
  // ------------------------------------------------------------------ #
  _bindPointer() {
    this.canvas.addEventListener("contextmenu", (event) => event.preventDefault());
    this.canvas.addEventListener("pointerdown", (event) => this._onPointerDown(event));
    window.addEventListener("pointermove", (event) => this._onPointerMove(event));
    window.addEventListener("pointerup", (event) => this._onPointerUp(event));
    this.canvas.addEventListener("wheel", (event) => this._onWheel(event), { passive: false });
    document.addEventListener("pointerlockchange", () => {
      if (this.isFlying && document.pointerLockElement !== this.canvas) {
        this.flyFallback = true;
        this.callbacks.onStatus?.("pointer-fallback");
      }
    });
    document.addEventListener("pointerlockerror", () => {
      this.flyFallback = true;
      this.callbacks.onStatus?.("pointer-fallback");
    });
  }

  _bindKeyboard() {
    window.addEventListener("keydown", (event) => {
      if (event.code === "ControlLeft" || event.code === "ControlRight"
          || event.code === "MetaLeft" || event.code === "MetaRight") {
        this._ctrl = true;
        if (this.gizmo.dragging) this._applyGizmoSnap();
      }
      if (!this.isFlying) return;
      if (["KeyW", "KeyA", "KeyS", "KeyD", "KeyQ", "KeyE",
        "ShiftLeft", "ShiftRight"].includes(event.code)) {
        this.cam.keyDown(event.code);
        event.preventDefault();
      }
    });
    window.addEventListener("keyup", (event) => {
      if (event.code === "ControlLeft" || event.code === "ControlRight"
          || event.code === "MetaLeft" || event.code === "MetaRight") {
        this._ctrl = false;
        if (this.gizmo.dragging) this._applyGizmoSnap();
      }
      this.cam.keyUp(event.code);
    });
    window.addEventListener("blur", () => this._releaseEverything());
  }

  _releaseEverything() {
    this.navigation = null;
    this.pending = null;
    this.regionInteraction = null;
    this.hideBoxSelection();
    this.panModifier = false;
    this._ctrl = false;
    this.cam.clearKeys();
    if (this.isFlying) this._endFly();
    if (document.pointerLockElement === this.canvas) document.exitPointerLock();
  }

  _onPointerDown(event) {
    if (event.target !== this.canvas) return;
    this.canvas.focus?.();
    const button = event.button;
    this.pending = null;
    if (button === 2) {
      event.preventDefault();
      if (event.altKey) {
        this.navigation = { mode: "zoom", lastY: event.clientY };
        this.canvas.setPointerCapture?.(event.pointerId);
        return;
      }
      this._beginFly(event);
      return;
    }
    if (button === 1) {
      event.preventDefault();
      this.navigation = { mode: "pan", lastX: event.clientX, lastY: event.clientY };
      this.canvas.setPointerCapture?.(event.pointerId);
      return;
    }
    if (button !== 0 || this.isFlying) return;
    if (this.regionMode && this._beginRegionInteraction(event)) return;
    if (this.gizmo.dragging) return;
    if (event.altKey) {
      event.preventDefault();
      this.navigation = { mode: "orbit", lastX: event.clientX, lastY: event.clientY };
      this.canvas.setPointerCapture?.(event.pointerId);
      return;
    }
    if (this.panModifier || this.tool === "hand") {
      event.preventDefault();
      this.navigation = { mode: "pan", lastX: event.clientX, lastY: event.clientY };
      this.canvas.setPointerCapture?.(event.pointerId);
      return;
    }
    const additive = event.shiftKey || event.ctrlKey || event.metaKey;
    this.pending = {
      x: event.clientX,
      y: event.clientY,
      additive,
      moved: false,
      box: false,
      empty: this._pickId(event.clientX, event.clientY) === null,
      pointerId: event.pointerId,
    };
    this.canvas.setPointerCapture?.(event.pointerId);
  }

  _onPointerMove(event) {
    if (this.regionInteraction) {
      this._moveRegionInteraction(event);
      return;
    }
    if (this.navigation?.mode === "fly") {
      if (document.pointerLockElement === this.canvas || this.flyFallback) {
        this.cam.look(event.movementX || 0, event.movementY || 0);
        this.needsRender = true;
      }
      return;
    }
    if (this.navigation?.mode === "pan") {
      const dx = event.clientX - this.navigation.lastX;
      const dy = event.clientY - this.navigation.lastY;
      this.navigation.lastX = event.clientX;
      this.navigation.lastY = event.clientY;
      this.cam.pan(dx, dy, this.canvas.clientHeight);
      this.needsRender = true;
      return;
    }
    if (this.navigation?.mode === "orbit") {
      const dx = event.clientX - this.navigation.lastX;
      const dy = event.clientY - this.navigation.lastY;
      this.navigation.lastX = event.clientX;
      this.navigation.lastY = event.clientY;
      this.cam.orbit(dx, dy);
      this.needsRender = true;
      return;
    }
    if (this.navigation?.mode === "zoom") {
      const dy = event.clientY - this.navigation.lastY;
      this.navigation.lastY = event.clientY;
      this.cam.dollyForward(-dy * Math.max(0.5, this.cam.position.distanceTo(this.cam.pivot)) * 0.008);
      this.needsRender = true;
      return;
    }
    if (!this.pending || this.isFlying) return;
    const dx = event.clientX - this.pending.x;
    const dy = event.clientY - this.pending.y;
    if (!this.pending.moved && Math.hypot(dx, dy) <= CLICK_THRESHOLD_PX) return;
    this.pending.moved = true;
    if (this.pending.empty) {
      this.pending.box = true;
      this._showBoxSelection(this.pending.x, this.pending.y, event.clientX, event.clientY);
    }
  }

  _onPointerUp(event) {
    if (this.regionInteraction) {
      this._endRegionInteraction(event);
      return;
    }
    if (this.navigation?.mode === "fly") {
      this._endFly();
      return;
    }
    if (this.navigation) {
      this.navigation = null;
      try {
        this.canvas.releasePointerCapture?.(event.pointerId);
      } catch (error) {
        /* already released */
      }
      return;
    }
    if (!this.pending) return;
    const pending = this.pending;
    this.pending = null;
    this.hideBoxSelection();
    try {
      this.canvas.releasePointerCapture?.(pending.pointerId);
    } catch (error) {
      /* already released */
    }
    if (pending.box) {
      const rect = this._boxRect(pending.x, pending.y, event.clientX, event.clientY);
      const ids = this._idsInRect(rect);
      if (ids.length || !pending.additive) {
        this._setSelection(pending.additive ? [...this.selectionIds, ...ids] : ids);
      }
      return;
    }
    if (pending.moved) return;
    const id = this._pickId(event.clientX, event.clientY);
    if (id === null) {
      if (!pending.additive) this.clearSelection();
      return;
    }
    if (pending.additive) this.toggleSelection(id);
    else this._setSelection([id]);
  }

  _onWheel(event) {
    event.preventDefault();
    if (this.isFlying) {
      const speed = this.cam.setFlySpeed(Math.sign(event.deltaY));
      this.callbacks.onStatus?.("fly-speed", speed);
      return;
    }
    if (event.altKey) {
      const distance = Math.max(0.5, this.cam.position.distanceTo(this.cam.pivot));
      this.cam.dollyForward(-Math.sign(event.deltaY) * distance * 0.08);
      this.needsRender = true;
      return;
    }
    const point = this._cursorPoint(event.clientX, event.clientY);
    const direction = Math.sign(event.deltaY) || 1;
    if (point) this.cam.dollyToward(point, direction * 0.16);
    else this.cam.dollyForward(-direction * Math.max(0.3, this.cam.position.distanceTo(this.cam.pivot)) * 0.08);
    this.needsRender = true;
  }

  _beginFly() {
    this.navigation = { mode: "fly" };
    this.cam.beginFly();
    this.flyFallback = false;
    this.gizmoHelper.visible = false;
    this.needsRender = true;
    this.callbacks.onStatus?.("fly-start", this.cam.fly.speed);
    try {
      const request = this.canvas.requestPointerLock?.();
      if (request && typeof request.catch === "function") {
        request.catch(() => {
          this.flyFallback = true;
          this.callbacks.onStatus?.("pointer-fallback");
        });
      }
    } catch (error) {
      this.flyFallback = true;
      this.callbacks.onStatus?.("pointer-fallback");
    }
  }

  _endFly() {
    if (!this.isFlying) {
      this.navigation = null;
      return;
    }
    this.navigation = null;
    this.cam.endFly();
    if (document.pointerLockElement === this.canvas) document.exitPointerLock();
    this.flyFallback = false;
    this.gizmoHelper.visible = this.tool !== "hand" && this.selectionIds.size > 0;
    this.needsRender = true;
    this.callbacks.onStatus?.("fly-end", this.cam.fly.speed);
  }

  // ------------------------------------------------------------------ #
  // selection
  // ------------------------------------------------------------------ #
  objectGroup(id) {
    return this.propObjects.get(id) || this.structureObjects.get(id) || null;
  }

  _selectableGroups() {
    const groups = [];
    for (const group of this.propObjects.values()) groups.push(group);
    for (const group of this.structureObjects.values()) {
      if (group.userData.role !== "ground") groups.push(group);
    }
    return groups;
  }

  _pickId(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.set(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    );
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const hits = this.raycaster.intersectObjects(this._selectableGroups(), true);
    for (const hit of hits) {
      let object = hit.object;
      while (object && !object.userData?.id) object = object.parent;
      if (object) return object.userData.id;
    }
    return null;
  }

  dropPoint(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.set(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    );
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const targets = [...this.propObjects.values(), ...this.structureObjects.values()];
    const hits = this.raycaster.intersectObjects(targets, true);
    if (!hits.length) return null;
    const hit = hits[0];
    let object = hit.object;
    while (object && !object.userData?.type) object = object.parent;
    return {
      point: fromThreePosition(hit.point),
      world: hit.point.clone(),
      target: object ? {
        type: object.userData.type,
        id: object.userData.id,
        role: object.userData.role,
      } : null,
    };
  }

  _idsInRect(rect) {
    const ids = [];
    const width = this.canvas.clientWidth;
    const height = this.canvas.clientHeight;
    const canvasRect = this.canvas.getBoundingClientRect();
    for (const group of this._selectableGroups()) {
      const point = group.position.clone().project(this.camera);
      if (point.z < -1 || point.z > 1) continue;
      const x = (point.x + 1) / 2 * width;
      const y = (1 - point.y) / 2 * height;
      void canvasRect;
      if (x >= rect.x && x <= rect.x + rect.width && y >= rect.y && y <= rect.y + rect.height) {
        ids.push(group.userData.id);
      }
    }
    return ids;
  }

  _setSelection(ids) {
    const valid = [];
    for (const id of ids || []) {
      if (this.objectGroup(id) && !valid.includes(id)) valid.push(id);
    }
    this.selectionIds = new Set(valid);
    this._rebuildSelection();
  }

  select(ids, options = {}) {
    this._setSelection(Array.isArray(ids) ? ids : [ids]);
    if (options.focus) this.focusSelection();
  }

  toggleSelection(id) {
    if (!this.objectGroup(id)) return;
    if (this.selectionIds.has(id)) this.selectionIds.delete(id);
    else this.selectionIds.add(id);
    this._rebuildSelection();
  }

  selectAll() {
    this._setSelection(this._selectableGroups().map((group) => group.userData.id));
  }

  clearSelection() {
    this._setSelection([]);
  }

  _disposeLine(object) {
    this.scene.remove(object);
    object.geometry?.dispose?.();
    object.material?.dispose?.();
  }

  _clearSelectionVisuals() {
    for (const helper of this.selectionObbHelpers.values()) this._disposeLine(helper);
    for (const helper of this.selectionWorldHelpers.values()) this._disposeLine(helper);
    this.selectionObbHelpers.clear();
    this.selectionWorldHelpers.clear();
    if (this.multiFrame) {
      this._disposeLine(this.multiFrame);
      this.multiFrame = null;
    }
  }

  _obbGeometry(size) {
    const hx = Math.max(1e-4, size[0] / 2);
    const hy = Math.max(1e-4, size[1] / 2);
    const hz = Math.max(1e-4, size[2] / 2);
    const corners = [];
    for (const x of [-hx, hx]) {
      for (const y of [-hy, hy]) {
        for (const z of [-hz, hz]) corners.push(new THREE.Vector3(x, y, z));
      }
    }
    const edges = [[0, 1], [0, 2], [0, 4], [1, 3], [1, 5], [2, 3], [2, 6],
      [3, 7], [4, 5], [4, 6], [5, 7], [6, 7]];
    const positions = [];
    for (const [a, b] of edges) {
      positions.push(corners[a].x, corners[a].y, corners[a].z);
      positions.push(corners[b].x, corners[b].y, corners[b].z);
    }
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
    return geometry;
  }

  _makeObb(group, color, dashed = false) {
    const data = group.userData.data || {};
    const size = data.aabb || data.size
      || (() => {
        const box = new THREE.Box3().setFromObject(group);
        return box.getSize(new THREE.Vector3()).toArray();
      })();
    const material = new THREE.LineBasicMaterial({
      color, transparent: true, opacity: 0.95, depthTest: false,
    });
    const object = new THREE.LineSegments(this._obbGeometry(size), material);
    object.position.copy(group.position);
    object.quaternion.copy(group.quaternion);
    object.renderOrder = 20;
    object.frustumCulled = false;
    this.scene.add(object);
    return object;
  }

  _makeWorldBoxHelper(box, color, opacity) {
    const helper = new THREE.Box3Helper(box.clone(), color);
    helper.material.transparent = true;
    helper.material.opacity = opacity;
    helper.material.depthTest = false;
    helper.renderOrder = 10;
    this.scene.add(helper);
    return helper;
  }

  _rebuildSelection() {
    this._clearSelectionVisuals();
    const groups = [];
    for (const id of this.selectionIds) {
      const group = this.objectGroup(id);
      if (!group) continue;
      groups.push(group);
      const obb = this._makeObb(group, SELECTION_COLOR);
      this.selectionObbHelpers.set(id, obb);
      const derived = group.userData.type === "prop"
        ? this.derived.props[id] : this.derived.structures[id];
      const box = new THREE.Box3().setFromObject(group);
      if (derived?.worldAabb) {
        box.copy(unityAabbToThree(derived.worldAabb));
      }
      const worldHelper = this._makeWorldBoxHelper(box, 0x9aa3ad, 0.55);
      worldHelper.visible = this.showWorldBoxes;
      this.selectionWorldHelpers.set(id, worldHelper);
    }
    if (!groups.length) {
      this.selectionIds.clear();
      this._updateMotionOverlay();
      this.selectionBox.makeEmpty();
      this.gizmo.detach();
      this.gizmoHelper.visible = false;
      this.callbacks.onSelect?.(null);
      this.callbacks.onStatus?.("selection", 0);
      this.needsRender = true;
      return;
    }
    this.selectionBox.makeEmpty();
    for (const group of groups) this.selectionBox.expandByObject(group);
    if (groups.length > 1) {
      this.multiFrame = this._makeWorldBoxHelper(this.selectionBox, 0x9ec7e0, 0.40);
    }
    const pivot = this.selectionBox.getCenter(new THREE.Vector3());
    this.proxy.position.copy(pivot);
    if (groups.length === 1) this.proxy.quaternion.copy(groups[0].quaternion);
    else this.proxy.quaternion.identity();
    this.proxy.scale.set(1, 1, 1);
    this._applyBoxStyle();
    this._updateMotionOverlay();
    this._refreshGizmo();
    this.callbacks.onSelect?.(this.selectionInfo());
    this.callbacks.onStatus?.("selection", groups.length);
    this.needsRender = true;
  }

  _applyBoxStyle() {
    const showObb = this.boxStyle === "obb";
    for (const helper of this.selectionObbHelpers.values()) helper.visible = showObb;
    for (const [id, helper] of this.selectionWorldHelpers) {
      helper.visible = this.boxStyle === "world" || this.showWorldBoxes;
      helper.material.color.set(this.boxStyle === "world" ? SELECTION_COLOR : 0x9aa3ad);
      helper.material.opacity = this.boxStyle === "world" ? 0.95 : 0.55;
      void id;
    }
    if (this.multiFrame) this.multiFrame.visible = showObb;
    this.needsRender = true;
  }

  _updateSelectionVisuals() {
    if (!this.selectionIds.size) return;
    const union = new THREE.Box3();
    for (const id of this.selectionIds) {
      const group = this.objectGroup(id);
      if (!group) continue;
      const obb = this.selectionObbHelpers.get(id);
      if (obb) {
        obb.position.copy(group.position);
        obb.quaternion.copy(group.quaternion);
      }
      const worldHelper = this.selectionWorldHelpers.get(id);
      if (worldHelper) {
        worldHelper.box.setFromObject(group);
        worldHelper.updateMatrixWorld(true);
      }
      union.expandByObject(group);
    }
    if (!union.isEmpty()) {
      this.selectionBox.copy(union);
      if (this.multiFrame) {
        this.multiFrame.box.copy(union);
        this.multiFrame.updateMatrixWorld(true);
      }
      if (!this.gizmo.dragging) {
        this.proxy.position.copy(union.getCenter(new THREE.Vector3()));
      }
    }
    this._applyBoxStyle();
  }

  setShowWorldBoxes(enabled) {
    this.showWorldBoxes = Boolean(enabled);
    this._applyBoxStyle();
  }

  setBoxStyle(style) {
    this.boxStyle = style === "world" ? "world" : "obb";
    this._applyBoxStyle();
  }

  highlightIds(ids) {
    this.clearHighlights();
    for (const id of ids || []) {
      const group = this.objectGroup(id);
      if (!group) continue;
      const obb = this._makeObb(group, 0xff5a3c);
      obb.renderOrder = 30;
      this.highlightHelpers.set(id, obb);
    }
    this.needsRender = true;
  }

  clearHighlights() {
    for (const helper of this.highlightHelpers.values()) this._disposeLine(helper);
    this.highlightHelpers.clear();
    this.needsRender = true;
  }

  _refreshGizmo() {
    if (!this.selectionIds.size || this.tool === "hand" || this.isFlying || this.regionMode) {
      this.gizmo.detach();
      this.gizmoHelper.visible = false;
      return;
    }
    this.gizmo.attach(this.proxy);
    this.gizmo.setMode(this.tool === "rotate" ? "rotate" : "translate");
    this.gizmo.setSpace(this.spaceMode);
    this.gizmoHelper.visible = true;
    this._applyGizmoSnap();
  }

  setTool(tool) {
    if (!["hand", "move", "rotate"].includes(tool)) return false;
    this.tool = tool;
    this._refreshGizmo();
    this.callbacks.onStatus?.("tool", tool);
    this.needsRender = true;
    return true;
  }

  toggleSpace() {
    this.spaceMode = this.spaceMode === "world" ? "local" : "world";
    this.gizmo.setSpace(this.spaceMode);
    this.callbacks.onStatus?.("space", this.spaceMode);
    this.needsRender = true;
    return this.spaceMode;
  }

  setSelectionMode(mode) {
    return this.setTool(mode === "rotate" ? "rotate" : "move");
  }

  setSnapping({ translate, rotate } = {}) {
    if (translate !== undefined) this.snap.translate = Number(translate) || 0;
    if (rotate !== undefined) this.snap.rotate = Number(rotate) || 0;
    this._applyGizmoSnap();
  }

  _applyGizmoSnap() {
    if (this.tool === "rotate") {
      this.gizmo.rotationSnap = this._ctrl && this.snap.rotate
        ? THREE.MathUtils.degToRad(this.snap.rotate) : null;
      this.gizmo.translationSnap = null;
    } else {
      this.gizmo.translationSnap = this._ctrl && this.snap.translate ? this.snap.translate : null;
      this.gizmo.rotationSnap = null;
    }
  }

  selectionInfo() {
    const ids = [...this.selectionIds];
    if (!ids.length) return null;
    const groups = ids.map((id) => this.objectGroup(id)).filter(Boolean);
    if (!groups.length) return null;
    const box = new THREE.Box3();
    for (const group of groups) box.expandByObject(group);
    const union = threeBoxToUnity(box);
    const pivot = fromThreePosition(box.getCenter(new THREE.Vector3()));
    if (ids.length === 1) {
      const id = ids[0];
      const group = groups[0];
      const type = group.userData.type;
      const data = group.userData.data;
      const derived = type === "prop" ? this.derived.props[id] : this.derived.structures[id];
      return {
        count: 1,
        ids,
        type,
        id,
        def: data.def,
        kind: derived?.kind || data.kind,
        zone: data.zone,
        group: data.group,
        groupId: data.groupId,
        parent: data.parent,
        scale: data.scale,
        behaviour: data.behaviour,
        absorbable: data.absorbable,
        growthMm3: derived?.growthMm3 ?? data.growthMm3,
        refModel: Boolean(this.refMode && (this.manifests[data.def] || {}).refModel),
        support: data.support,
        stackedOn: data.stackedOn,
        valueUnits: derived?.valueUnits ?? data.valueUnits,
        dReqMm: derived?.dReqMm,
        volumeK: derived?.volumeK,
        aabb: data.aabb,
        size: data.size,
        role: data.role,
        dReq: derived?.dReq,
        worldAabb: derived?.worldAabb || union,
        position: pivot,
        positions: { placed: data.position, instance: group.userData.refInstance?.position || null },
        rotationQuat: fromThreeQuaternion(group.quaternion),
        refInstance: group.userData.refInstance || null,
        refApplied: Boolean(group.userData.refApplied),
      };
    }
    const propIds = ids.filter((id) => this.propObjects.has(id));
    const structureIds = ids.filter((id) => this.structureObjects.has(id));
    let valueUnits = 0;
    for (const id of propIds) valueUnits += this.derived.props[id]?.valueUnits || 0;
    return {
      count: ids.length,
      ids,
      types: [...new Set(ids.map((id) => this.objectGroup(id)?.userData.type))],
      props: propIds.length,
      structures: structureIds.length,
      valueUnits,
      worldAabb: union,
      position: pivot,
    };
  }

  setSelectionWorldBox() {
    return this.selectionBox.isEmpty() ? null : threeBoxToUnity(this.selectionBox);
  }

  focusSelection() {
    if (!this.selectionIds.size) {
      this.fitLevelView();
      return;
    }
    this.cam.focus(this.selectionBox);
    this.needsRender = true;
    this.callbacks.onStatus?.("camera", "focus");
  }

  // ------------------------------------------------------------------ #
  // group transform (one drag -> one batch op)
  // ------------------------------------------------------------------ #
  _startGroupTransform() {
    const items = [];
    for (const id of this.selectionIds) {
      const group = this.objectGroup(id);
      if (!group) continue;
      items.push({
        id,
        type: group.userData.type,
        group,
        position: group.position.clone(),
        quaternion: group.quaternion.clone(),
      });
    }
    if (!items.length) return;
    this.groupStart = {
      pivot: this.proxy.position.clone(),
      proxyQuaternion: this.proxy.quaternion.clone(),
      items,
    };
    this._applyGizmoSnap();
  }

  _onGizmoMove() {
    if (!this.groupStart) return;
    const deltaPosition = this.proxy.position.clone().sub(this.groupStart.pivot);
    const deltaQuaternion = this.proxy.quaternion.clone()
      .multiply(this.groupStart.proxyQuaternion.clone().invert()).normalize();
    for (const item of this.groupStart.items) {
      const offset = item.position.clone().sub(this.groupStart.pivot);
      item.group.position.copy(this.groupStart.pivot)
        .add(deltaPosition).add(offset.applyQuaternion(deltaQuaternion));
      item.group.quaternion.copy(deltaQuaternion.clone().multiply(item.quaternion)).normalize();
    }
    this._updateSelectionVisuals();
    this.needsRender = true;
  }

  _supportHeight(propDoc, x, z) {
    const ground = Number((this.doc.level || {}).groundY || 0);
    if (propDoc.stackedOn) {
      const other = this.objectGroup(propDoc.stackedOn);
      if (other) return new THREE.Box3().setFromObject(other).max.y;
      const info = this.derived.props[propDoc.stackedOn];
      if (info?.worldAabb) return info.worldAabb[1][1];
      return ground;
    }
    if (propDoc.support) {
      const group = this.objectGroup(propDoc.support);
      const structure = (this.doc.structures || []).find((entry) => entry.id === propDoc.support);
      let normal = null;
      let point = null;
      if (group && ["box", "ramp"].includes(group.userData.role)) {
        const size = group.userData.data.size;
        normal = new THREE.Vector3(0, 1, 0).applyQuaternion(group.quaternion).normalize();
        point = group.position.clone().addScaledVector(normal, size[1] / 2);
      } else if (structure) {
        const info = this.derived.structures[propDoc.support];
        if (info?.topPlane) {
          normal = new THREE.Vector3(...info.topPlane.normal);
          point = new THREE.Vector3(...info.topPlane.point);
        }
      }
      if (normal && point && Math.abs(normal.y) > 1e-6) {
        return point.y - (normal.x * (x - point.x) + normal.z * (z - point.z)) / normal.y;
      }
      if (point) return point.y;
    }
    return ground;
  }

  /** Keep rotated/moved props on their declared support by adjusting only Y. */
  _keepOnSupport(start, changedProps, changedStructures) {
    const affected = new Set(changedProps);
    for (const prop of this.doc.props || []) {
      if (prop.support && changedStructures.has(prop.support)) affected.add(prop.itemId);
    }
    let grew = true;
    while (grew) {
      grew = false;
      for (const prop of this.doc.props || []) {
        if (prop.stackedOn && affected.has(prop.stackedOn) && !affected.has(prop.itemId)) {
          affected.add(prop.itemId);
          grew = true;
        }
      }
    }
    let adjusted = false;
    for (let pass = 0; pass < 4; pass += 1) {
      for (const id of affected) {
        const group = this.objectGroup(id);
        const docProp = (this.doc.props || []).find((entry) => entry.itemId === id);
        if (!group || !docProp) continue;
        const top = this._supportHeight(docProp, group.position.x, group.position.z);
        const bottom = new THREE.Box3().setFromObject(group).min.y;
        const delta = top - bottom;
        if (Math.abs(delta) > 0.0005) {
          group.position.y += delta;
          adjusted = true;
          this.selectionObbHelpers.get(id)?.position.copy(group.position);
          this.selectionWorldHelpers.get(id)?.box.setFromObject(group);
          this.selectionWorldHelpers.get(id)?.updateMatrixWorld(true);
        }
      }
    }
    if (adjusted) this._updateSelectionVisuals();
    return affected;
  }

  async _commitGroupTransform() {
    const start = this.groupStart;
    this.groupStart = null;
    if (!start) return true;
    if (this.readOnly) {
      for (const item of start.items) {
        item.group.position.copy(item.position);
        item.group.quaternion.copy(item.quaternion);
      }
      this.proxy.position.copy(start.pivot);
      this.proxy.quaternion.copy(start.proxyQuaternion);
      this._updateSelectionVisuals();
      this.needsRender = true;
      this.callbacks.onError?.("UNLICENSED REFERENCE level is read-only; the transform was not sent.");
      return false;
    }

    const changedProps = new Set();
    const changedStructures = new Set();
    for (const item of start.items) {
      const moved = Math.abs(item.group.position.x - item.position.x) > 1e-5
        || Math.abs(item.group.position.y - item.position.y) > 1e-5
        || Math.abs(item.group.position.z - item.position.z) > 1e-5;
      const rotated = Math.abs(item.group.quaternion.dot(item.quaternion)) < 0.999999;
      if (!moved && !rotated) continue;
      if (item.type === "prop") changedProps.add(item.id);
      else changedStructures.add(item.id);
    }
    // A rotation must not turn "rotate it a bit" into a gate rejection: adjust the
    // rotated prop's Y (and everything sitting on a transformed support) so the
    // world AABB still rests on the declared support.  The orientation is kept.
    this._lastSupportAdjusted = new Set();
    if (changedProps.size || changedStructures.size) {
      this._lastSupportAdjusted = this._keepOnSupport(start, changedProps, changedStructures);
    }

    const operations = [];
    for (const item of start.items) {
      const position = fromThreePosition(item.group.position);
      const quaternion = fromThreeQuaternion(item.group.quaternion);
      const moved = Math.abs(item.group.position.x - item.position.x) > 1e-5
        || Math.abs(item.group.position.y - item.position.y) > 1e-5
        || Math.abs(item.group.position.z - item.position.z) > 1e-5;
      const rotated = Math.abs(item.group.quaternion.dot(item.quaternion)) < 0.999999;
      if (moved) operations.push({ op: "move", args: { id: item.id, to: position } });
      if (rotated) {
        operations.push({ op: "set", args: { id: item.id, rotationQuat: quaternion } });
      }
    }
    // Unselected props that were lifted with a support need their own move op.
    const selected = new Set(start.items.map((item) => item.id));
    for (const id of this._lastSupportAdjusted || []) {
      if (selected.has(id)) continue;
      const group = this.objectGroup(id);
      if (!group) continue;
      const docProp = (this.doc.props || []).find((entry) => entry.itemId === id);
      if (!docProp) continue;
      if (Math.abs(group.position.y - docProp.position[1]) > 1e-5
          || Math.abs(group.position.x - docProp.position[0]) > 1e-5
          || Math.abs(group.position.z - docProp.position[2]) > 1e-5) {
        operations.push({ op: "move", args: { id, to: fromThreePosition(group.position) } });
      }
    }
    if (!operations.length) return true;
    const ok = await this.callbacks.onBatch?.(operations, {
      label: `transform ${start.items.length} object(s)`,
    });
    if (!ok) {
      for (const item of start.items) {
        item.group.position.copy(item.position);
        item.group.quaternion.copy(item.quaternion);
      }
      for (const id of this._lastSupportAdjusted || []) {
        const docProp = (this.doc.props || []).find((entry) => entry.itemId === id);
        const group = this.objectGroup(id);
        if (docProp && group && group.userData.type === "prop") {
          group.position.copy(toThreePosition(docProp.position));
          group.quaternion.copy(toThreeQuaternion(docProp.rotationQuat));
        }
      }
      this.selectionBox.makeEmpty();
      for (const item of start.items) this.selectionBox.expandByObject(item.group);
      this._updateSelectionVisuals();
      this.proxy.position.copy(start.pivot);
      this.proxy.quaternion.copy(start.proxyQuaternion);
      this.needsRender = true;
      return false;
    }
    return true;
  }

  async debugRotateSelection(degrees = 30, axis = "y") {
    if (!this.selectionIds.size) return false;
    this._startGroupTransform();
    if (!this.groupStart) return false;
    const vector = axis === "x" ? new THREE.Vector3(1, 0, 0)
      : axis === "z" ? new THREE.Vector3(0, 0, 1) : new THREE.Vector3(0, 1, 0);
    const delta = new THREE.Quaternion().setFromAxisAngle(
      vector, THREE.MathUtils.degToRad(Number(degrees) || 0)
    );
    this.proxy.position.copy(this.groupStart.pivot);
    this.proxy.quaternion.copy(delta.multiply(this.groupStart.proxyQuaternion)).normalize();
    this._onGizmoMove();
    return this._commitGroupTransform();
  }

  async debugTranslateSelection(dx = 0.1, dy = 0, dz = 0.1) {
    if (!this.selectionIds.size) return false;
    this._startGroupTransform();
    if (!this.groupStart) return false;
    this.proxy.position.copy(this.groupStart.pivot).add(new THREE.Vector3(dx, dy, dz));
    this.proxy.quaternion.copy(this.groupStart.proxyQuaternion);
    this._onGizmoMove();
    return this._commitGroupTransform();
  }

  async nudgeSelection(dx, dy, dz) {
    if (this.readOnly) {
      this.callbacks.onError?.("UNLICENSED REFERENCE level is read-only; nothing was moved.");
      return false;
    }
    const operations = [];
    for (const id of this.selectionIds) {
      const group = this.objectGroup(id);
      if (!group) continue;
      const position = fromThreePosition(group.position.clone().add(new THREE.Vector3(dx, dy, dz)));
      operations.push({ op: "move", args: { id, to: position } });
    }
    if (!operations.length) return false;
    return Boolean(await this.callbacks.onBatch?.(operations, {
      label: `nudge ${operations.length} object(s)`,
    }));
  }

  async restSelection() {
    const operations = [];
    for (const id of this.selectionIds) {
      const group = this.objectGroup(id);
      if (!group) continue;
      const box = new THREE.Box3().setFromObject(group);
      const halfHeight = (box.max.y - box.min.y) / 2;
      const origin = new THREE.Vector3((box.min.x + box.max.x) / 2, box.min.y - 0.002,
        (box.min.z + box.max.z) / 2);
      const ray = new THREE.Raycaster(origin, new THREE.Vector3(0, -1, 0));
      const targets = [];
      for (const other of this.structureObjects.values()) {
        if (other.userData.role !== "ground") targets.push(other);
      }
      for (const other of this.propObjects.values()) {
        if (other !== group) targets.push(other);
      }
      const hits = ray.intersectObjects(targets, true);
      const ground = Number((this.doc.level || {}).groundY || 0);
      const surface = hits.length ? hits[0].point.y : ground;
      const position = fromThreePosition(
        new THREE.Vector3(group.position.x, surface + halfHeight, group.position.z)
      );
      operations.push({ op: "move", args: { id, to: position } });
    }
    if (!operations.length) return false;
    return Boolean(await this.callbacks.onBatch?.(operations, {
      label: `rest ${operations.length} object(s)`,
    }));
  }

  async dropSelectionToGround() {
    const ground = Number((this.doc.level || {}).groundY || 0);
    const operations = [];
    for (const id of this.selectionIds) {
      const group = this.objectGroup(id);
      if (!group) continue;
      const box = new THREE.Box3().setFromObject(group);
      const halfHeight = (box.max.y - box.min.y) / 2;
      const position = fromThreePosition(
        new THREE.Vector3(group.position.x, ground + halfHeight, group.position.z)
      );
      operations.push({ op: "move", args: { id, to: position } });
    }
    if (!operations.length) return false;
    return Boolean(await this.callbacks.onBatch?.(operations, {
      label: `snap ${operations.length} object(s) to ground`,
    }));
  }

  // ------------------------------------------------------------------ #
  // box-select overlay
  // ------------------------------------------------------------------ #
  _boxRect(x1, y1, x2, y2) {
    const canvasRect = this.canvas.getBoundingClientRect();
    return {
      x: Math.min(x1, x2) - canvasRect.left,
      y: Math.min(y1, y2) - canvasRect.top,
      width: Math.abs(x2 - x1),
      height: Math.abs(y2 - y1),
    };
  }

  _showBoxSelection(x1, y1, x2, y2) {
    if (!this.boxElement) return;
    const rect = this._boxRect(x1, y1, x2, y2);
    this.boxElement.style.left = `${rect.x}px`;
    this.boxElement.style.top = `${rect.y}px`;
    this.boxElement.style.width = `${rect.width}px`;
    this.boxElement.style.height = `${rect.height}px`;
    this.boxElement.classList.remove("hidden");
  }

  hideBoxSelection() {
    this.boxElement?.classList.add("hidden");
  }

  // ------------------------------------------------------------------ #
  // cursor world point for dolly
  // ------------------------------------------------------------------ #
  _cursorPoint(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.set(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    );
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const hits = this.raycaster.intersectObjects(this._selectableGroups(), true);
    if (hits.length) return hits[0].point.clone();
    const forward = this.cam.forward();
    const plane = new THREE.Plane().setFromNormalAndCoplanarPoint(forward, this.cam.pivot);
    return this.raycaster.ray.intersectPlane(plane, new THREE.Vector3());
  }

  // ------------------------------------------------------------------ #
  // scene construction
  // ------------------------------------------------------------------ #
  async load(state) {
    const loadStarted = performance.now();
    this.loading = true;
    this.state = state;
    this.doc = state.doc;
    this.derived = state.derived || { props: {}, structures: {} };
    this.manifests = state.manifests || {};
    this.refMode = Boolean(state.ref?.unlicensed);
    this.readOnly = Boolean(state.ref?.readOnly);
    this.v2Mode = Boolean(state.v2);
    this.v2Summary = state.v2 || null;
    if (this.v2Mode) {
      this.v2Rotations = true;
      this.v2Scale = true;
      this.v2ShowSolids = true;
      this.v2ShowWalls = true;
      this.v2ShowKill = true;
      this.showTerrain = true;
      this.terrainWireframe = false;
    }
    this.defBoxes.clear();
    this._clearLevel();
    this._buildWorld();
    this._buildStructures();
    const stageStarted = performance.now();
    if (this.v2Mode) {
      await this._loadV2View(state);
    } else {
      await this._loadRefStage(state);
    }
    this.refLoadMs = Math.round(performance.now() - stageStarted);
    const assetsStarted = performance.now();
    await this._buildProps();
    const modelsDone = performance.now();
    this._buildParentLines();
    this.selectionIds.clear();
    this._rebuildSelection();
    this._applyLadderToAll();
    this.setCameraPreset("overview");
    this._updateBall();
    this.loading = false;
    this.needsRender = true;
    this.lastLoadTiming = {
      assetsMs: Math.round(assetsStarted - loadStarted),
      modelsMs: Math.round(modelsDone - assetsStarted),
      modelFetchMs: this.lastModelLoadMs,
      placementMs: this.lastPlacementMs,
      refStageMs: this.refLoadMs,
      finishMs: Math.round(performance.now() - modelsDone),
      totalMs: Math.round(performance.now() - loadStarted),
    };
    return this;
  }

  _clearLevel() {
    for (const group of this.propObjects.values()) this.scene.remove(group);
    for (const group of this.structureObjects.values()) this.scene.remove(group);
    this.propObjects.clear();
    this.structureObjects.clear();
    this.pickTargets = [];
    if (this.parentLines) {
      this.scene.remove(this.parentLines);
      this.parentLines.traverse((object) => {
        object.geometry?.dispose?.();
        object.material?.dispose?.();
      });
      this.parentLines = null;
      this.parentLineCount = 0;
    }
    if (this.worldGroup) {
      this.scene.remove(this.worldGroup);
      this.worldGroup = null;
    }
    for (const name of ["terrainGroup", "collisionGroup", "v2WallsGroup", "v2KillGroup"]) {
      const group = this[name];
      if (!group) continue;
      this.scene.remove(group);
      const materials = new Set();
      group.traverse((object) => {
        object.geometry?.dispose?.();
        if (Array.isArray(object.material)) object.material.forEach((m) => materials.add(m));
        else if (object.material) materials.add(object.material);
      });
      for (const material of materials) material.dispose?.();
      this[name] = null;
    }
    this.terrainMaterial = null;
    this.collisionPayload = null;
    this.v2View = null;
    this.v2WallCount = 0;
    this.v2ParentLinks = [];
  }

  _levelCenter() {
    const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
    return new THREE.Vector3(
      (bounds[0][0] + bounds[1][0]) / 2,
      0.3,
      -(bounds[0][1] + bounds[1][1]) / 2
    );
  }

  _ballStart() {
    const level = this.doc?.level || {};
    if (Array.isArray(level.ballStart)) return level.ballStart;
    const ground = (this.doc?.structures || []).find((entry) => entry.role === "ground");
    if (ground && Array.isArray(ground.center)) {
      return [ground.center[0], 0.15, ground.center[2]];
    }
    return [0, 0.15, 0];
  }

  _spawn() {
    const level = this.doc?.level || {};
    if (Array.isArray(level.playerSpawn)) return level.playerSpawn;
    return this._ballStart();
  }

  _groundBox() {
    // v2's collision mesh includes high walls/ceilings; frame the walkable core, not
    // the 14 m outlier, or the camera ends up outside a green box looking at its side.
    if (this.v2Mode) {
      // The v2 collision mesh covers only the walkable core (20x15 m), while props
      // fill the whole 59x80 m level.  Frame the level, using the terrain's low band
      // for height so the 14 m collision-wall outlier does not zoom the camera out.
      const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
      let low = -1.6;
      if (this.terrainGroup) {
        const probe = new THREE.Box3();
        this.terrainGroup.traverse((object) => {
          if (object.isMesh) probe.expandByObject(object);
        });
        if (!probe.isEmpty()) low = probe.min.y;
      }
      return new THREE.Box3(
        new THREE.Vector3(bounds[0][0], low, -bounds[1][1]),
        new THREE.Vector3(bounds[1][0], low + 4.0, -bounds[0][1]),
      );
    }
    // In reference mode the real stage replaces the placement's flat ground plane, so
    // the camera presets frame the extracted terrain (minus the sky shell) instead.
    if (this.refMode && this.terrainGroup?.visible) {
      const box = new THREE.Box3();
      this.terrainGroup.traverse((object) => {
        if (!object.isMesh || object.userData.refBackdrop) return;
        box.expandByObject(object);
      });
      // The stage also carries distant coast/walls far outside the prop field; clip to
      // the level so the presets frame the map the props live in, not the ocean.
      const bounds = this.state?.bounds;
      if (bounds) {
        box.intersect(new THREE.Box3(
          new THREE.Vector3(bounds[0][0], -100, -bounds[1][1]),
          new THREE.Vector3(bounds[1][0], 100, -bounds[0][1])
        ));
      }
      if (!box.isEmpty()) return box;
    }
    const groundGroup = [...this.structureObjects.values()]
      .find((group) => group.userData.role === "ground");
    if (groundGroup) {
      groundGroup.updateMatrixWorld(true);
      const box = new THREE.Box3().setFromObject(groundGroup);
      if (!box.isEmpty()) return box;
    }
    const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
    return new THREE.Box3(
      new THREE.Vector3(bounds[0][0], 0, -bounds[1][1]),
      new THREE.Vector3(bounds[1][0], 0.1, -bounds[0][1])
    );
  }

  _buildWorld() {
    const group = new THREE.Group();
    this.worldGroup = group;
    this.scene.add(group);
    const center = this._levelCenter();
    const bounds = this.state.bounds || [[-10, -10], [10, 10]];
    const sizeX = bounds[1][0] - bounds[0][0];
    const sizeZ = bounds[1][1] - bounds[0][1];
    // A 60x80 m reference field would sit entirely inside the default fog; scale the
    // fog with the level so the overview is legible (small levels keep 55/190).
    const diagonal = Math.hypot(sizeX, sizeZ) || 1;
    if (this.scene.fog) {
      this.scene.fog.near = Math.max(55, diagonal * 1.4);
      this.scene.fog.far = Math.max(190, diagonal * 4);
    }

    const hemi = new THREE.HemisphereLight(0xf4fbff, 0x5f6b4c, 1.55);
    hemi.position.set(0, 30, 0);
    group.add(hemi);

    const sun = new THREE.DirectionalLight(0xfff3d6, 2.4);
    sun.position.copy(center).add(new THREE.Vector3(9, 16, 9));
    sun.target.position.copy(center);
    sun.castShadow = true;
    sun.shadow.mapSize.set(1024, 1024);
    sun.shadow.camera.near = 0.5;
    sun.shadow.camera.far = 60;
    const extent = Math.max(8, Math.max(sizeX, sizeZ) * 0.75);
    sun.shadow.camera.left = -extent;
    sun.shadow.camera.right = extent;
    sun.shadow.camera.top = extent;
    sun.shadow.camera.bottom = -extent;
    sun.shadow.bias = -0.0006;
    group.add(sun);
    group.add(sun.target);

    const fill = new THREE.DirectionalLight(0xbcd6ff, 0.5);
    fill.position.copy(center).add(new THREE.Vector3(-8, 9, -6));
    group.add(fill);

    const lawn = new THREE.Mesh(
      new THREE.PlaneGeometry(240, 240),
      new THREE.MeshStandardMaterial({ color: 0x667c56, roughness: 1.0, metalness: 0 })
    );
    lawn.rotation.x = -Math.PI / 2;
    lawn.position.set(center.x, -0.035, center.z);
    lawn.receiveShadow = true;
    group.add(lawn);
    this.lawn = lawn;

    const grid = new THREE.GridHelper(Math.max(sizeX, sizeZ), Math.max(2, Math.round(Math.max(sizeX, sizeZ))), 0x5d7a52, 0x6f8c63);
    grid.position.set(center.x, 0.006, center.z);
    grid.material.transparent = true;
    grid.material.opacity = 0.28;
    group.add(grid);
    this.grid = grid;

    const points = [
      new THREE.Vector3(bounds[0][0], 0.02, -bounds[0][1]),
      new THREE.Vector3(bounds[1][0], 0.02, -bounds[0][1]),
      new THREE.Vector3(bounds[1][0], 0.02, -bounds[1][1]),
      new THREE.Vector3(bounds[0][0], 0.02, -bounds[1][1]),
    ];
    const outline = new THREE.LineLoop(
      new THREE.BufferGeometry().setFromPoints(points),
      new THREE.LineDashedMaterial({ color: 0x2e7d9c, dashSize: 0.25, gapSize: 0.14 })
    );
    outline.computeLineDistances();
    group.add(outline);

    const ballStart = toThreePosition(this._ballStart());
    const ring = new THREE.Mesh(
      new THREE.RingGeometry(0.22, 0.3, 32),
      new THREE.MeshBasicMaterial({ color: 0xffa53b, side: THREE.DoubleSide, transparent: true, opacity: 0.85 })
    );
    ring.rotation.x = -Math.PI / 2;
    ring.position.set(ballStart.x, ballStart.y, ballStart.z);
    group.add(ring);

    this.ballGhost = new THREE.Mesh(
      new THREE.SphereGeometry(0.5, 32, 20),
      new THREE.MeshStandardMaterial({
        color: 0xffa53b, transparent: true, opacity: 0.3, roughness: 0.45,
        metalness: 0, depthWrite: false,
      })
    );
    this.ballGhost.position.copy(ballStart);
    group.add(this.ballGhost);
  }

  _buildStructures() {
    for (const structure of this.doc.structures || []) {
      const group = this._makeStructure(structure);
      this.structureObjects.set(structure.id, group);
      this.scene.add(group);
      if (structure.role !== "ground") this.pickTargets.push(group);
    }
  }

  _makeStructure(structure) {
    const group = new THREE.Group();
    const size = structure.size;
    const role = structure.role || "box";
    const material = this.v2Mode ? this._v2SolidMaterial() : this._structureMaterial(role);
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), material);
    mesh.scale.set(size[0], size[1], size[2]);
    mesh.position.set(0, 0, 0);
    if (role === "ground" || role === "boundary") {
      mesh.position.set(0, -size[1] / 2, 0);
    }
    mesh.castShadow = role === "box" || role === "ramp";
    mesh.receiveShadow = true;
    group.add(mesh);
    const edges = new THREE.LineSegments(
      new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1)),
      new THREE.LineBasicMaterial({
        color: role === "boundary" ? 0xd9f3ff : 0x4a3626,
        transparent: true,
        opacity: role === "boundary" ? 0.5 : 0.28,
      })
    );
    mesh.add(edges);
    if (role === "boundary") {
      const arrow = new THREE.ArrowHelper(
        new THREE.Vector3(0, 1, 0), new THREE.Vector3(0, 0, 0),
        0.55, 0x2e7d9c, 0.16, 0.09
      );
      group.add(arrow);
    }
    group.position.copy(toThreePosition(structure.center));
    group.quaternion.copy(toThreeQuaternion(structure.rotationQuat));
    group.userData = { type: "structure", id: structure.id, role, data: structure, mesh };
    return group;
  }

  _v2SolidMaterial() {
    if (!this.v2SolidMaterial) {
      this.v2SolidMaterial = new THREE.MeshStandardMaterial({
        color: 0x8f7ae8, roughness: 0.8, metalness: 0,
        transparent: true, opacity: 0.55, depthWrite: false,
      });
    }
    return this.v2SolidMaterial;
  }

  _structureMaterial(role) {
    this.structureMaterials = this.structureMaterials || new Map();
    if (!this.structureMaterials.has(role)) {
      const color = ROLE_COLORS[role] || ROLE_COLORS.box;
      const material = new THREE.MeshStandardMaterial({
        color,
        roughness: role === "boundary" ? 0.9 : 0.95,
        metalness: 0,
        transparent: role === "boundary",
        opacity: role === "boundary" ? 0.24 : 1,
        side: role === "boundary" ? THREE.DoubleSide : THREE.FrontSide,
        depthWrite: role !== "boundary",
      });
      this.structureMaterials.set(role, material);
    }
    return this.structureMaterials.get(role);
  }

  async _buildProps() {
    const started = performance.now();
    const props = this.doc.props || [];
    const defs = [...new Set(props.map((prop) => prop.def))];
    let done = 0;
    this.callbacks.onProgress?.(`加载模型 0/${defs.length}`);
    await Promise.all(defs.map(async (def) => {
      try {
        await this.loadDef(def);
      } catch (error) {
        console.error("model failed", def, error);
        this.callbacks.onError?.(`模型 ${def}.glb 加载失败：${error.message}`);
      }
      done += 1;
      this.callbacks.onProgress?.(`加载模型 ${done}/${defs.length}`);
    }));
    const modelsDone = performance.now();
    this.callbacks.onProgress?.(`摆放 ${props.length} 件道具…`);
    for (const prop of props) {
      const group = await this._makeProp(prop);
      this.propObjects.set(prop.itemId, group);
      this.scene.add(group);
      this.pickTargets.push(group);
    }
    this.callbacks.onProgress?.("");
    this.lastModelLoadMs = Math.round(modelsDone - started);
    this.lastPlacementMs = Math.round(performance.now() - modelsDone);
  }

  async _makeProp(prop) {
    const group = new THREE.Group();
    let model;
    try {
      const prototype = await this.loadDef(prop.def);
      model = prototype.clone(true);
      model.traverse((object) => {
        if (object.isMesh) {
          object.castShadow = true;
          object.receiveShadow = true;
          object.userData.baseMaterial = object.material;
          object.userData.originalMaterial = object.material;
        }
      });
      // The reference GLBs are the ripped assets: bottom-pivoted at load time.  The
      // placement position is the prop AABB CENTRE, so lift by half the declared
      // height and centre the mesh's own bbox in x/z.  (Repo GLBs are centre-pivoted
      // and need no offset.)  The offset is applied after the instance scale, below.
      const viewerPivot = (this.manifests[prop.def] || {}).viewerPivot;
      const box = this.defBoxes.get(prop.def);
      const instance = this._refTransform(prop);
      if (this.v2Mode) {
        // v2's prop.aabb is already multiplied by prop.scale, and position is the
        // AABB centre; the shared helper is also used by the rotation/scale toggles.
        this._placeV2Model(group, model, prop);
      } else {
        const scale = instance ? instance.scale : null;
        const scaleX = scale ? scale[0] : 1;
        const scaleY = scale ? scale[1] : 1;
        const scaleZ = scale ? scale[2] : 1;
        if (this.refMode && viewerPivot === "bottom" && box) {
          const height = (prop.aabb || [0, 0, 0])[1] || 0;
          model.position.set(
            -(box.min.x + box.max.x) / 2 * scaleX,
            (-box.min.y - height / 2) * scaleY,
            -(box.min.z + box.max.z) / 2 * scaleZ
          );
        }
        if (scale) model.scale.set(scaleX, scaleY, scaleZ);
      }
    } catch (error) {
      const fallback = new THREE.Mesh(
        new THREE.BoxGeometry(1, 1, 1),
        new THREE.MeshStandardMaterial({ color: 0xcc4488, roughness: 0.8 })
      );
      const aabb = prop.aabb || [0.2, 0.2, 0.2];
      fallback.scale.set(aabb[0], aabb[1], aabb[2]);
      model = fallback;
    }
    group.add(model);
    group.position.copy(toThreePosition(prop.position));
    group.quaternion.copy(toThreeQuaternion(prop.rotationQuat));
    group.userData = { type: "prop", id: prop.itemId, def: prop.def, data: prop, model };
    this._applyRefTransform(group, prop);
    return group;
  }

  // ------------------------------------------------------------------ #
  // v2 layers (katamari.placement/2; one implementation in /api/v2/*)
  // ------------------------------------------------------------------ #
  async _loadV2View(state) {
    const token = this.apiToken ? `&token=${encodeURIComponent(this.apiToken)}` : "";
    const started = performance.now();
    try {
      const response = await fetch(
        `/api/v2/view?terrain=1&solids=1&walls=1&kill=1&parents=1`
        + `&rotation=${this.v2Rotations ? 1 : 0}&scale=${this.v2Scale ? 1 : 0}`
        + `&props=0${token}`,
        { headers: { Accept: "application/json" } },
      );
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const payload = await response.json();
      this.v2View = payload;
      this.v2Summary = state.v2 || this.v2Summary;
      this.v2ParentLinks = payload.parents || [];
      this._buildV2Terrain(payload.terrain);
      this._buildV2Walls(payload.activity);
      this._buildV2KillPlane(payload.activity);
      this._applyV2LayerVisibility();
      this.lastTerrainMs = Math.round(performance.now() - started);
      this.needsRender = true;
    } catch (error) {
      console.warn("v2 layers failed", error);
      this.callbacks.onError?.(`v2 图层加载失败：${error.message}`);
    }
  }

  _buildV2Terrain(terrain) {
    if (!terrain || !Array.isArray(terrain.vertices) || !Array.isArray(terrain.indices)) return;
    const zoneColors = {
      0: 0x6f8c63, 1: 0x9a7b4f, 3: 0x7d8a96, 5: 0xb08968,
      8: 0x8aa6a0, 24: 0xc2a878,
    };
    const fallback = new THREE.Color(0x8a8f83);
    const positions = [];
    const colors = [];
    const vertices = terrain.vertices;
    const indices = terrain.indices;
    const zones = terrain.zoneIds || [];
    const classes = terrain.classes || [];
    for (let triangle = 0; triangle < indices.length / 3; triangle += 1) {
      // class 3 is the collision "wall" band; the activity-wall layer already shows
      // the rule walls, and drawing every collision wall here turns the core into a
      // green box that hides the floor.  Ride/climb surfaces are the visible terrain.
      if (classes[triangle] === 3) continue;
      const color = zoneColors[zones[triangle]] !== undefined
        ? new THREE.Color(zoneColors[zones[triangle]]) : fallback;
      for (let corner = 0; corner < 3; corner += 1) {
        const point = vertices[indices[triangle * 3 + corner]];
        if (!point) continue;
        positions.push(point[0], point[1], -point[2]);
        colors.push(color.r, color.g, color.b);
      }
    }
    if (!positions.length) return;
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
    geometry.setAttribute("color", new THREE.Float32BufferAttribute(colors, 3));
    geometry.computeVertexNormals();
    this.terrainMaterial = new THREE.MeshStandardMaterial({
      vertexColors: true, roughness: 0.96, metalness: 0,
      // The sidecar is CCW; FrontSide is the correct culling mode.  DoubleSide would
      // show the inside of every collision wall and turn the core into a green box.
      side: THREE.FrontSide, wireframe: this.terrainWireframe,
    });
    const mesh = new THREE.Mesh(geometry, this.terrainMaterial);
    mesh.name = "v2-terrain";
    mesh.receiveShadow = true;
    mesh.castShadow = false;
    const group = new THREE.Group();
    group.name = "v2-terrain";
    group.add(mesh);
    group.visible = this.showTerrain;
    this.terrainGroup = group;
    this.scene.add(group);
    this._applyLegacyPlaneVisibility();
  }

  _buildV2Walls(activity) {
    if (!activity || !Array.isArray(activity.walls) || !activity.walls.length) return;
    const meta = this.v2Summary?.meta || {};
    const terrainLow = this.v2Summary?.terrain?.bounds?.[0]?.[1];
    const groundY = Number(meta.groundY ?? -3.0);
    const baseY = Number.isFinite(terrainLow) ? Math.min(terrainLow, groundY) : groundY;
    const positions = [];
    const edges = [];
    for (const wall of activity.walls) {
      const points = wall.points || [];
      if (points.length < 2) continue;
      const pairs = [];
      for (let index = 0; index + 1 < points.length; index += 1) {
        pairs.push([points[index], points[index + 1]]);
      }
      if (wall.closed) pairs.push([points[points.length - 1], points[0]]);
      const top = baseY + Number(wall.heightM || 1.0);
      for (const [start, end] of pairs) {
        const a = [start[0], baseY, -start[1]];
        const b = [end[0], baseY, -end[1]];
        const c = [end[0], top, -end[1]];
        const d = [start[0], top, -start[1]];
        positions.push(...a, ...b, ...c, ...a, ...c, ...d);
        edges.push(...a, ...b, ...b, ...c, ...c, ...d, ...d, ...a);
      }
    }
    if (!positions.length) return;
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
    geometry.computeVertexNormals();
    const material = new THREE.MeshBasicMaterial({
      color: 0x49b8e8, transparent: true, opacity: 0.22,
      side: THREE.DoubleSide, depthWrite: false,
    });
    const mesh = new THREE.Mesh(geometry, material);
    mesh.renderOrder = 4;
    const line = new THREE.LineSegments(
      new THREE.BufferGeometry().setFromPoints(
        Array.from({ length: edges.length / 3 }, (_, index) =>
          new THREE.Vector3(edges[index * 3], edges[index * 3 + 1], edges[index * 3 + 2])),
      ),
      new THREE.LineBasicMaterial({ color: 0x7dd3fc, transparent: true, opacity: 0.85 }),
    );
    line.renderOrder = 21;
    const group = new THREE.Group();
    group.name = "v2-activity-walls";
    group.add(mesh, line);
    group.visible = this.v2ShowWalls;
    this.v2WallsGroup = group;
    this.v2WallCount = activity.walls.length;
    this.scene.add(group);
  }

  _buildV2KillPlane(activity) {
    const kill = activity?.killPlane;
    if (!kill || !Number.isFinite(Number(kill.y))) return;
    const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
    const width = Math.max(10, (bounds[1][0] - bounds[0][0]) * 1.25);
    const depth = Math.max(10, (bounds[1][1] - bounds[0][1]) * 1.25);
    // A filled plane at y=-4 reads as a pink false floor from above; a red grid +
    // border at the same height says "kill plane" without competing with the terrain.
    const centerX = (bounds[0][0] + bounds[1][0]) / 2;
    const centerZ = -(bounds[0][1] + bounds[1][1]) / 2;
    const grid = new THREE.GridHelper(Math.max(width, depth), 12, 0xff3b30, 0xff3b30);
    grid.position.set(centerX, Number(kill.y), centerZ);
    grid.material.transparent = true;
    grid.material.opacity = 0.3;
    grid.material.depthWrite = false;
    grid.renderOrder = 3;
    const halfW = width / 2;
    const halfD = depth / 2;
    const border = new THREE.LineLoop(
      new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(centerX - halfW, Number(kill.y), centerZ - halfD),
        new THREE.Vector3(centerX + halfW, Number(kill.y), centerZ - halfD),
        new THREE.Vector3(centerX + halfW, Number(kill.y), centerZ + halfD),
        new THREE.Vector3(centerX - halfW, Number(kill.y), centerZ + halfD),
      ]),
      new THREE.LineBasicMaterial({ color: 0xff3b30, transparent: true, opacity: 0.8 }),
    );
    border.renderOrder = 3;
    const group = new THREE.Group();
    group.name = "v2-kill-plane";
    group.add(grid, border);
    group.visible = this.v2ShowKill;
    this.v2KillGroup = group;
    this.scene.add(group);
  }

  _applyV2LayerVisibility() {
    if (!this.v2Mode) return;
    if (this.terrainGroup) this.terrainGroup.visible = this.showTerrain;
    if (this.v2WallsGroup) this.v2WallsGroup.visible = this.v2ShowWalls;
    if (this.v2KillGroup) this.v2KillGroup.visible = this.v2ShowKill;
    for (const group of this.structureObjects.values()) {
      group.visible = this.v2ShowSolids;
    }
    this._applyLegacyPlaneVisibility();
    this.needsRender = true;
  }

  setV2Terrain(visible) {
    this.showTerrain = Boolean(visible);
    if (this.terrainGroup) this.terrainGroup.visible = this.showTerrain;
    this._applyLegacyPlaneVisibility();
    this.needsRender = true;
    return Boolean(this.terrainGroup);
  }

  setV2Solids(visible) {
    this.v2ShowSolids = Boolean(visible);
    for (const group of this.structureObjects.values()) {
      group.visible = this.v2ShowSolids;
    }
    this.needsRender = true;
    return this.structureObjects.size;
  }

  setV2Walls(visible) {
    this.v2ShowWalls = Boolean(visible);
    if (this.v2WallsGroup) this.v2WallsGroup.visible = this.v2ShowWalls;
    this.needsRender = true;
    return this.v2WallCount;
  }

  setV2KillPlane(visible) {
    this.v2ShowKill = Boolean(visible);
    if (this.v2KillGroup) this.v2KillGroup.visible = this.v2ShowKill;
    this.needsRender = true;
    return Boolean(this.v2KillGroup);
  }

  // ------------------------------------------------------------------ #
  // motion layer (motions[] / paths[]; LEVEL_FORMAT_V2 6.7)
  // ------------------------------------------------------------------ #
  /**
   * Install the payload from `GET /api/v2/motion`.
   *
   * Every value here was resolved server-side: the classes and paths are document data, the
   * speeds / phase seeds / trigger radii come from the `motion_sim` tool that compiles
   * MotionMath.cs.  This method stores them and draws them; it never derives a motion
   * number itself.
   */
  setMotionData(payload) {
    this.v2Motions = payload || null;
    this.v2MotionRows = new Map();
    this.motionClassColors = new Map();
    this.motionClassMaterials.clear();
    for (const entry of (payload?.classes || [])) {
      this.motionClassColors.set(entry.class, entry.color);
    }
    for (const row of (payload?.props || [])) {
      this.v2MotionRows.set(row.itemId, row);
    }
    this.v2MotionPaths = (payload?.paths || []).slice();
    this._buildMotionPaths();
    this._applyPropAppearance();
    this._updateMotionOverlay();
    this.needsRender = true;
    return this.v2MotionRows.size;
  }

  motionInfo(itemId) {
    return this.v2MotionRows.get(itemId) || null;
  }

  motionLegend() {
    return (this.v2Motions?.classes || []).slice();
  }

  motionSummary() {
    const payload = this.v2Motions;
    if (!payload) return null;
    return {
      available: payload.available !== false,
      reason: payload.reason || null,
      counts: payload.counts || null,
      approximations: payload.approximations || [],
      evaluator: payload.evaluator || null,
    };
  }

  /** Colour every prop that has a motion class by that class (a display layer only). */
  setMotionColors(visible) {
    this.showMotionColors = Boolean(visible);
    this._applyPropAppearance();
    this.needsRender = true;
    return this.showMotionColors ? this.v2MotionRows.size : 0;
  }

  setMotionPaths(visible) {
    this.showMotionPaths = Boolean(visible);
    if (this.motionPathGroup) this.motionPathGroup.visible = this.showMotionPaths;
    this._updateMotionOverlay();
    this.needsRender = true;
    return this.motionPathGroup ? this.motionPathCount : 0;
  }

  setMotionRadius(visible) {
    this.showMotionRadius = Boolean(visible);
    this._updateMotionOverlay();
    this.needsRender = true;
    return this.showMotionRadius;
  }

  _motionClassMaterial(name) {
    if (!this.motionClassMaterials.has(name)) {
      const color = this.motionClassColors.get(name) || "#cccccc";
      this.motionClassMaterials.set(name, new THREE.MeshStandardMaterial({
        color: new THREE.Color(color),
        roughness: 0.62,
        metalness: 0,
        emissive: new THREE.Color(color).multiplyScalar(0.16),
      }));
    }
    return this.motionClassMaterials.get(name);
  }

  _disposeMotionPaths() {
    if (this.motionPathGroup) {
      this.scene.remove(this.motionPathGroup);
      this.motionPathGroup.traverse((object) => {
        // The tubes and nodes share two geometries (see _buildMotionPaths); disposing them
        // once below must not be repeated per mesh, and the trail may still be using them.
        if (object.geometry && object.geometry !== this.motionSegmentGeometry
          && object.geometry !== this.motionNodeGeometry) {
          object.geometry.dispose?.();
        }
        object.material?.dispose?.();
      });
    }
    this.motionPathGroup = null;
    this.motionPathLines.clear();
    this.motionPathCount = 0;
    this.motionSegmentGeometry?.dispose?.();
    this.motionSegmentGeometry = null;
    this.motionNodeGeometry?.dispose?.();
    this.motionNodeGeometry = null;
  }

  /**
   * The `paths[]` polylines, in world metres.
   *
   * A path is drawn as a thin tube per segment rather than a `THREE.Line`, because a 1 px
   * line is invisible at level scale and `linewidth` is ignored by every browser.  Straight
   * segments are kept straight - the format says a path is a polyline and must not be
   * smoothed (LEVEL_FORMAT_V2 6.7.4) - so each segment is its own cylinder and the corners
   * are exact.  The first point gets a marker so the direction of travel is readable.
   */
  _buildMotionPaths() {
    this._disposeMotionPaths();
    if (!this.v2MotionPaths.length) return;
    const group = new THREE.Group();
    group.name = "v2-motion-paths";
    group.userData = { type: "motion-paths" };
    // One unit cylinder / sphere, scaled per segment, so 113 segments share two geometries.
    this.motionSegmentGeometry = new THREE.CylinderGeometry(1, 1, 1, 8, 1, true);
    this.motionNodeGeometry = new THREE.SphereGeometry(1, 8, 6);
    const up = new THREE.Vector3(0, 1, 0);
    for (const path of this.v2MotionPaths) {
      const raw = (path.points || []).map((point) => toThreePosition(point));
      if (raw.length < 2) continue;
      // A path sits on the surface the props walk on; lift it a hair so it is not z-fighting
      // with the terrain it lies on.
      const points = raw.map((point) => point.clone().setY(point.y + 0.03));
      const closed = path.closed !== false;
      const pathGroup = new THREE.Group();
      pathGroup.userData = { type: "motion-path", pathId: path.id, path };
      const materials = [];
      const segmentCount = closed ? points.length : points.length - 1;
      for (let index = 0; index < segmentCount; index += 1) {
        const from = points[index];
        const to = points[(index + 1) % points.length];
        const direction = to.clone().sub(from);
        const length = direction.length();
        if (length < 1e-4) continue;
        const material = new THREE.MeshBasicMaterial({
          color: MOTION_PATH_COLOR, transparent: true, opacity: 0.92, depthTest: false,
        });
        materials.push(material);
        const segment = new THREE.Mesh(this.motionSegmentGeometry, material);
        segment.position.copy(from).add(to).multiplyScalar(0.5);
        segment.scale.set(0.028, length, 0.028);
        segment.quaternion.setFromUnitVectors(up, direction.normalize());
        segment.renderOrder = 18;
        pathGroup.add(segment);
      }
      for (const point of points) {
        const material = new THREE.MeshBasicMaterial({
          color: MOTION_PATH_COLOR, transparent: true, opacity: 0.92, depthTest: false,
        });
        materials.push(material);
        const node = new THREE.Mesh(this.motionNodeGeometry, material);
        node.position.copy(point);
        node.scale.setScalar(0.035);
        node.renderOrder = 18;
        pathGroup.add(node);
      }
      this.motionPathLines.set(path.id, { group: pathGroup, materials });
      group.add(pathGroup);
    }
    this.motionPathCount = this.motionPathLines.size;
    group.visible = this.showMotionPaths;
    this.motionPathGroup = group;
    this.scene.add(group);
    this.needsRender = true;
  }

  /**
   * A trail along the selected prop's sampled world positions (from the trajectory the
   * server computed).  Returns ``null`` when no trajectory covers this prop, so a level
   * without playback still shows the class, the path and the trigger ring.
   */
  _motionTrailObject(itemId, color) {
    const row = this.trajectoryRows.get(itemId);
    if (!row || !row.pos || row.pos.length < 6) return null;
    const samples = row.pos.length / 3;
    const step = Math.max(1, Math.ceil(samples / 240));
    if (!this.motionSegmentGeometry) {
      this.motionSegmentGeometry = new THREE.CylinderGeometry(1, 1, 1, 8, 1, true);
    }
    const group = new THREE.Group();
    group.userData = { type: "motion-trail", itemId, samples, step };
    const material = new THREE.MeshBasicMaterial({
      color: new THREE.Color(color), transparent: true, opacity: 0.75, depthTest: false,
    });
    const up = new THREE.Vector3(0, 1, 0);
    let previous = null;
    for (let index = 0; index < samples; index += step) {
      const point = new THREE.Vector3(
        row.pos[index * 3], row.pos[index * 3 + 1], -row.pos[index * 3 + 2]);
      if (previous) {
        const direction = point.clone().sub(previous);
        const length = direction.length();
        if (length > 1e-4) {
          const segment = new THREE.Mesh(this.motionSegmentGeometry, material);
          segment.position.copy(previous).add(point).multiplyScalar(0.5);
          segment.scale.set(0.018, length, 0.018);
          segment.quaternion.setFromUnitVectors(up, direction.normalize());
          segment.renderOrder = 19;
          group.add(segment);
        }
      }
      previous = point;
    }
    if (!group.children.length) return null;
    return group;
  }

  _disposeMotionOverlay() {
    if (!this.motionOverlay) return;
    const overlay = this.motionOverlay;
    this.scene.remove(overlay);
    overlay.traverse((object) => {
      // The path tubes and the trail share one unit cylinder / sphere geometry; only the
      // per-object materials belong to the overlay.
      if (object.geometry && object.geometry !== this.motionSegmentGeometry
        && object.geometry !== this.motionNodeGeometry) {
        object.geometry.dispose?.();
      }
      object.material?.dispose?.();
    });
    this.motionOverlay = null;
    for (const entry of this.motionPathLines.values()) {
      for (const material of entry.materials) {
        material.color.set(MOTION_PATH_COLOR);
        material.opacity = 0.92;
      }
    }
  }

  /**
   * What the selection adds to the motion layer: the WaitForPlayer trigger ring on the
   * ground, the line to the parent prop, and the selected prop's path in the selection
   * colour.  Nothing here moves a prop; it is all annotation of the resolved data.
   */
  _updateMotionOverlay() {
    this._disposeMotionOverlay();
    if (this.selectionIds.size !== 1) return;
    const id = [...this.selectionIds][0];
    const row = this.v2MotionRows.get(id);
    const group = this.propObjects.get(id);
    if (!row || !group) return;
    const overlay = new THREE.Group();
    overlay.name = "v2-motion-overlay";
    overlay.userData = { type: "motion-overlay", itemId: id };
    const color = this.motionClassColors.get(row.motionClass) || "#f05a8e";
    if (this.showMotionRadius && row.triggerRadiusM) {
      const groundY = Number.isFinite(row.groundY) ? row.groundY : group.position.y;
      const ring = new THREE.Mesh(
        new THREE.RingGeometry(
          Math.max(0.01, row.triggerRadiusM - 0.03), row.triggerRadiusM, 96),
        new THREE.MeshBasicMaterial({
          color: new THREE.Color(color), transparent: true, opacity: 0.55,
          side: THREE.DoubleSide, depthTest: false, depthWrite: false,
        }),
      );
      ring.rotation.x = -Math.PI / 2;
      ring.position.set(group.position.x, groundY + 0.02, group.position.z);
      ring.renderOrder = 17;
      ring.userData = { type: "motion-radius", radiusM: row.triggerRadiusM };
      overlay.add(ring);
      const disc = new THREE.Mesh(
        new THREE.CircleGeometry(row.triggerRadiusM, 64),
        new THREE.MeshBasicMaterial({
          color: new THREE.Color(color), transparent: true, opacity: 0.10,
          side: THREE.DoubleSide, depthTest: false, depthWrite: false,
        }),
      );
      disc.rotation.x = -Math.PI / 2;
      disc.position.copy(ring.position);
      disc.renderOrder = 16;
      overlay.add(disc);
    }
    const parent = row.parent ? this.propObjects.get(row.parent) : null;
    if (parent) {
      const line = new THREE.Line(
        new THREE.BufferGeometry().setFromPoints([
          group.position.clone(), parent.position.clone(),
        ]),
        new THREE.LineBasicMaterial({
          color: 0xff5fd2, transparent: true, opacity: 0.95, depthTest: false,
        }),
      );
      line.renderOrder = 21;
      line.userData = { type: "motion-parent-link", parent: row.parent };
      overlay.add(line);
    }
    // The selected prop's own sampled trajectory: this is what "看到它的行为轨迹" means once a
    // trajectory is loaded - the世界 positions the server sampled, drawn as a thin trail.
    const trail = this._motionTrailObject(id, color);
    if (trail) overlay.add(trail);
    const pathEntry = row.path ? this.motionPathLines.get(row.path) : null;
    if (pathEntry) {
      for (const material of pathEntry.materials) {
        material.color.set(MOTION_PATH_SELECTED_COLOR);
        material.opacity = 1;
      }
      if (this.motionPathGroup) this.motionPathGroup.visible = true;
    }
    this.motionOverlay = overlay;
    this.scene.add(overlay);
    this.needsRender = true;
  }

  // ------------------------------------------------------------------ #
  // motion playback (world poses from the server; interpolation only here)
  // ------------------------------------------------------------------ #
  /**
   * Install a trajectory payload from `GET /api/v2/motion/trajectory`.
   *
   * `pos` is a flat `x, y, z` array per prop and `rot` a flat `x, y, z, w` array, both
   * sampled at `rate` Hz by `Tools/motion_sim`; the spawn transform of every prop is
   * remembered so stopping puts the scene back exactly as the document describes it.
   */
  setTrajectory(payload, scope = null) {
    if (this.playback.playing) this.pauseMotion();
    if (scope) this.playback.scope = scope;
    this.trajectory = payload || null;
    this.trajectoryRows = new Map();
    this.trajectoryRate = Number(payload?.rate) || 30;
    this.trajectoryDurationS = Number(payload?.seconds) || 0;
    for (const row of (payload?.props || [])) {
      if (!this.propObjects.has(row.itemId)) continue;
      this.trajectoryRows.set(row.itemId, row);
      if (!this.playbackBase.has(row.itemId)) {
        const group = this.propObjects.get(row.itemId);
        this.playbackBase.set(row.itemId, {
          position: group.position.clone(),
          quaternion: group.quaternion.clone(),
        });
      }
    }
    this.playbackPropCount = this.trajectoryRows.size;
    this.playback.time = 0;
    this._updateMotionOverlay();
    this.needsRender = true;
    this.callbacks.onPlayback?.(this.playbackState());
    return this.playbackPropCount;
  }

  /**
   * Merge more sampled rows into the installed trajectory (the selection trail asks for one
   * prop at a time).  Playback state and the clock are untouched.
   */
  addTrajectoryRows(payload) {
    if (!payload) return this.trajectoryRows.size;
    for (const row of (payload.props || [])) {
      if (!this.propObjects.has(row.itemId)) continue;
      this.trajectoryRows.set(row.itemId, row);
      if (!this.playbackBase.has(row.itemId)) {
        const group = this.propObjects.get(row.itemId);
        this.playbackBase.set(row.itemId, {
          position: group.position.clone(),
          quaternion: group.quaternion.clone(),
        });
      }
    }
    this.trajectoryRate = Number(payload.rate) || this.trajectoryRate;
    this.trajectoryDurationS = Math.max(this.trajectoryDurationS,
      Number(payload.seconds) || 0);
    this.playbackPropCount = this.trajectoryRows.size;
    this._updateMotionOverlay();
    this.needsRender = true;
    return this.playbackPropCount;
  }

  /** True when a trajectory covering at least this many seconds is installed. */
  hasTrajectory(seconds = 0) {
    return Boolean(this.trajectory) && this.trajectoryDurationS + 1e-6 >= Number(seconds);
  }

  playbackState() {
    return {
      playing: this.playback.playing,
      time: Number(this.playback.time.toFixed(3)),
      durationS: this.trajectoryDurationS,
      speed: this.playback.speed,
      scope: this.playback.scope,
      loop: this.playback.loop,
      rate: this.trajectoryRate,
      props: this.playbackPropCount,
      sampleCount: this.trajectory?.sampleCount ?? 0,
    };
  }

  /**
   * Put every animated prop at time `seconds` by linear interpolation of the position and
   * shortest-arc interpolation of the rotation - the whole of the client-side "motion
   * logic", which is why there is no motion rule in this file.
   */
  setPlaybackTime(seconds, { silent = false } = {}) {
    if (!this.trajectoryRows.size) return 0;
    const duration = this.trajectoryDurationS;
    let time = Number(seconds);
    if (!Number.isFinite(time)) time = 0;
    time = Math.min(Math.max(time, 0), duration);
    this.playback.time = time;
    const sample = time * this.trajectoryRate;
    const index = Math.min(Math.floor(sample), Math.max(0, this.trajectory?.sampleCount - 1 || 0));
    const next = Math.min(index + 1, Math.max(0, (this.trajectory?.sampleCount || 1) - 1));
    const alpha = sample - index;
    for (const [itemId, row] of this.trajectoryRows) {
      const group = this.propObjects.get(itemId);
      if (!group) continue;
      const slider = index * 3;
      const nextSlider = next * 3;
      group.position.set(
        row.pos[slider] + (row.pos[nextSlider] - row.pos[slider]) * alpha,
        row.pos[slider + 1] + (row.pos[nextSlider + 1] - row.pos[slider + 1]) * alpha,
        -row.pos[slider + 2] + (-row.pos[nextSlider + 2] + row.pos[slider + 2]) * alpha,
      );
      const rotIndex = index * 4;
      const nextRot = next * 4;
      this._playbackQuaternionA.copy(toThreeQuaternion(
        [row.rot[rotIndex], row.rot[rotIndex + 1], row.rot[rotIndex + 2],
          row.rot[rotIndex + 3]]));
      this._playbackQuaternionB.copy(toThreeQuaternion(
        [row.rot[nextRot], row.rot[nextRot + 1], row.rot[nextRot + 2],
          row.rot[nextRot + 3]]));
      group.quaternion.copy(this._playbackQuaternionA)
        .slerp(this._playbackQuaternionB, alpha);
    }
    for (const id of this.selectionIds) {
      const group = this.propObjects.get(id);
      const helper = this.selectionObbHelpers.get(id);
      if (group && helper) {
        helper.position.copy(group.position);
        helper.quaternion.copy(group.quaternion);
      }
    }
    this.needsRender = true;
    if (!silent) this.callbacks.onPlayback?.(this.playbackState());
    return this.trajectoryRows.size;
  }

  playMotion() {
    if (!this.trajectoryRows.size) return false;
    if (this.playback.time >= this.trajectoryDurationS - 1e-4) this.playback.time = 0;
    this.playback.playing = true;
    this.callbacks.onPlayback?.(this.playbackState());
    return true;
  }

  pauseMotion() {
    const wasPlaying = this.playback.playing;
    this.playback.playing = false;
    if (wasPlaying) this.callbacks.onPlayback?.(this.playbackState());
    return this.playback.time;
  }

  togglePlayback() {
    if (this.playback.playing) {
      this.pauseMotion();
      return false;
    }
    return this.playMotion();
  }

  setPlaybackSpeed(speed) {
    const value = Number(speed);
    this.playback.speed = Number.isFinite(value) && value > 0 ? value : 1;
    this.callbacks.onPlayback?.(this.playbackState());
    return this.playback.speed;
  }

  setPlaybackLoop(loop) {
    this.playback.loop = Boolean(loop);
    return this.playback.loop;
  }

  /** Stop and put every prop back exactly where the document placed it. */
  stopMotion({ keepTrajectory = true } = {}) {
    this.pauseMotion();
    for (const [itemId, base] of this.playbackBase) {
      const group = this.propObjects.get(itemId);
      if (!group) continue;
      group.position.copy(base.position);
      group.quaternion.copy(base.quaternion);
    }
    this.playback.time = 0;
    if (!keepTrajectory) {
      this.trajectory = null;
      this.trajectoryRows.clear();
      this.trajectoryDurationS = 0;
      this.playbackPropCount = 0;
    }
    this.needsRender = true;
    this.callbacks.onPlayback?.(this.playbackState());
    return true;
  }

  async setV2Transforms(rotation = this.v2Rotations, scale = this.v2Scale) {
    this.v2Rotations = Boolean(rotation);
    this.v2Scale = Boolean(scale);
    const token = this.apiToken ? `&token=${encodeURIComponent(this.apiToken)}` : "";
    try {
      const response = await fetch(
        `/api/v2/props?rotation=${this.v2Rotations ? 1 : 0}`
        + `&scale=${this.v2Scale ? 1 : 0}${token}`,
        { headers: { Accept: "application/json" } },
      );
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const payload = await response.json();
      const byId = new Map((payload.props || []).map((prop) => [prop.itemId, prop]));
      for (const [itemId, group] of this.propObjects) {
        const prop = byId.get(itemId);
        if (!prop) continue;
        group.userData.data = prop;
        group.quaternion.copy(toThreeQuaternion(prop.rotationQuat));
        const model = group.userData.model;
        if (model) this._placeV2Model(group, model, prop);
      }
      this._buildParentLines();
      this._updateSelectionVisuals();
      this.needsRender = true;
      return { rotation: this.v2Rotations, scale: this.v2Scale, count: byId.size };
    } catch (error) {
      this.callbacks.onError?.(`v2 旋转/缩放显示切换失败：${error.message}`);
      return null;
    }
  }

  _placeV2Model(group, model, prop) {
    const viewerPivot = (this.manifests[prop.def] || {}).viewerPivot;
    const box = this.defBoxes.get(prop.def);
    const scale = prop.scale || [1, 1, 1];
    if (this.refMode && viewerPivot === "bottom" && box) {
      model.position.set(
        -(box.min.x + box.max.x) / 2 * scale[0],
        -box.min.y * scale[1] - ((prop.aabb || [0, 0, 0])[1] || 0) / 2,
        -(box.min.z + box.max.z) / 2 * scale[2],
      );
    }
    model.scale.set(scale[0], scale[1], scale[2]);
  }

  // ------------------------------------------------------------------ #
  // the original stage of a reference level (UNLICENSED, local only)
  // ------------------------------------------------------------------ #
  _refTransform(prop) {
    if (!this.refMode || !this.refTransforms || !prop) return null;
    return this.refTransforms[prop.itemId] || null;
  }

  /**
   * Draw one prop with the *original* instance transform: rotation from the record's
   * axis/angle, scale from +0x30, and (optionally) its original height.
   *
   * The placement files are never modified - this is a display override, which is why
   * it only runs in reference mode (read-only) and why the height is a separate toggle:
   * ``placement.model.json`` put every prop's AABB bottom on y = 0, while the original
   * ground steps by ~0.8 m, so the x/z of the file with the y of the instance is what
   * makes props stand on the terrain.
   */
  _applyRefTransform(group, prop) {
    const instance = this._refTransform(prop);
    group.userData.refInstance = instance;
    if (!instance) {
      if (group.userData.refApplied) {
        group.quaternion.copy(toThreeQuaternion(prop.rotationQuat));
        group.userData.refApplied = false;
      }
      return;
    }
    group.quaternion.copy(toThreeQuaternion(instance.quat || prop.rotationQuat));
    const placed = toThreePosition(prop.position);
    const y = this.useOriginalHeight && Array.isArray(instance.position)
      ? Number(instance.position[1])
      : placed.y;
    group.position.set(placed.x, y, placed.z);
    group.userData.refApplied = true;
  }

  /**
   * Fetch the original stage for a reference level: the extractor's per-item transforms
   * and then the terrain GLB.  A missing extraction is not an error - the level still
   * opens, and ``refStage.reason`` says why there is no terrain.
   */
  async _loadRefStage(state) {
    this.refStage = state.ref?.stage || null;
    this.refTransforms = null;
    this.terrainOrder = null;
    if (!this.refMode || !this.refStage?.available) return;
    const token = this.apiToken ? `?token=${encodeURIComponent(this.apiToken)}` : "";
    try {
      const response = await fetch(`/api/ref-stage/transforms.json${token}`, {
        headers: { Accept: "application/json" },
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const payload = await response.json();
      this.refTransforms = payload.byItemId || null;
      this.refParentLinks = payload.parents || [];
      this.refAnchors = payload.anchors || [];
      this.refZones = payload.zones || this.refStage?.zones || [];
    } catch (error) {
      console.warn("reference transforms failed", error);
      this.callbacks.onError?.(`原作实例变换加载失败：${error.message}`);
      this.refTransforms = null;
    }
    await this._buildRefTerrain();
  }

  async _buildRefTerrain() {
    if (!this.refMode || !this.refStage?.available) return;
    const token = this.apiToken ? `?token=${encodeURIComponent(this.apiToken)}` : "";
    const started = performance.now();
    try {
      const gltf = await this.loader.loadAsync(`/api/ref-stage/stage.glb${token}`);
      const group = new THREE.Group();
      group.name = "ref-terrain";
      // The extraction has no textures (232 material segments, all white), so one matte
      // stone material reads the geometry better than 232 identical white ones.
      // Front faces only: the extractor's ``stage.glb`` keeps the PS2 index order, this
      // loader flips it once (the PS2 -> REROLL map has determinant -1), and the result
      // agrees with the stored normals on 14,672 of 14,734 triangles - so culling the
      // back faces is what the original renderer does, and it is also what hides the
      // 242 m sky shell (sector1) when the camera is inside it.
      this.terrainMaterial = new THREE.MeshStandardMaterial({
        color: 0xffffff, roughness: 0.96, metalness: 0,
        side: THREE.FrontSide, wireframe: this.terrainWireframe,
        vertexColors: true,
      });
      const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
      const levelDiagonal = Math.hypot(bounds[1][0] - bounds[0][0],
        bounds[1][1] - bounds[0][1]) || 100;
      gltf.scene.traverse((object) => {
        if (!object.isMesh) return;
        object.material = this.terrainMaterial;
        object.castShadow = false;
        object.receiveShadow = true;
        object.frustumCulled = true;
        this._tintTerrainByHeight(object.geometry);
        const box = new THREE.Box3().setFromObject(object);
        // A mesh far bigger than the level is scenery around it (the sky shell, the
        // distant coast); it must not drive the camera framing.
        object.userData.refBackdrop = !box.isEmpty()
          && box.getSize(new THREE.Vector3()).length() > 2 * levelDiagonal;
        // The sky shell has no texture (the extraction carries none), so drawing it
        // only puts a grey wall around the map; it stays loaded but hidden.
        if (object.userData.refBackdrop) object.visible = false;
      });
      group.add(gltf.scene);
      group.visible = this.showTerrain;
      this.terrainGroup = group;
      this.scene.add(group);
      this.lastTerrainMs = Math.round(performance.now() - started);
      this._applyLegacyPlaneVisibility();
      this.needsRender = true;
    } catch (error) {
      console.warn("terrain failed", error);
      this.callbacks.onError?.(`原地形加载失败：${error.message}`);
      this.terrainGroup = null;
    }
  }

  /** Build the collision overlay (68 AABBs + 3,456 triangles + 7 zone boxes). */
  async _ensureCollision() {
    if (this.collisionGroup) return this.collisionGroup;
    if (!this.refMode || !this.refStage?.available) return null;
    const token = this.apiToken ? `?token=${encodeURIComponent(this.apiToken)}` : "";
    try {
      if (!this.collisionPayload) {
        const response = await fetch(`/api/ref-stage/collision.json${token}`, {
          headers: { Accept: "application/json" },
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        this.collisionPayload = await response.json();
      }
    } catch (error) {
      console.warn("collision failed", error);
      this.callbacks.onError?.(`碰撞数据加载失败：${error.message}`);
      return null;
    }
    const payload = this.collisionPayload;
    const group = new THREE.Group();
    group.name = "ref-collision";
    const boxPoints = [];
    for (const box of payload.aabbs || []) {
      const [x0, y0, z0] = box.min;
      const [x1, y1, z1] = box.max;
      const corners = [
        [x0, y0, z0], [x1, y0, z0], [x1, y0, z1], [x0, y0, z1],
        [x0, y1, z0], [x1, y1, z0], [x1, y1, z1], [x0, y1, z1],
      ];
      for (const [a, b] of [[0, 1], [1, 2], [2, 3], [3, 0],
        [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]]) {
        boxPoints.push(new THREE.Vector3(...corners[a]), new THREE.Vector3(...corners[b]));
      }
    }
    if (boxPoints.length) {
      const boxes = new THREE.LineSegments(
        new THREE.BufferGeometry().setFromPoints(boxPoints),
        new THREE.LineBasicMaterial({ color: 0xff9f1c, transparent: true, opacity: 0.55 }),
      );
      boxes.renderOrder = 6;
      group.add(boxes);
    }
    const triangles = payload.triangles || [];
    if (triangles.length) {
      // A triangle soup of 3,456 triangles is a wireframe of its edges (the surface is
      // already drawn by the terrain); every triangle contributes three segments.
      const edges = [];
      for (let index = 0; index < triangles.length; index += 9) {
        const p = [0, 3, 6].map((offset) => new THREE.Vector3(
          triangles[index + offset], triangles[index + offset + 1],
          triangles[index + offset + 2]));
        edges.push(p[0], p[1], p[1], p[2], p[2], p[0]);
      }
      const mesh = new THREE.LineSegments(
        new THREE.BufferGeometry().setFromPoints(edges),
        new THREE.LineBasicMaterial({ color: 0x22d3ee, transparent: true, opacity: 0.75 }),
      );
      mesh.renderOrder = 7;
      group.add(mesh);
    }
    const zonePoints = [];
    for (const zone of payload.zones || this.refZones || []) {
      if (!zone.min || !zone.max) continue;
      const [x0, y0, z0] = zone.min;
      const [x1, y1, z1] = zone.max;
      const corners = [
        new THREE.Vector3(x0, y0, z0), new THREE.Vector3(x1, y0, z0),
        new THREE.Vector3(x1, y0, z1), new THREE.Vector3(x0, y0, z1),
        new THREE.Vector3(x0, y1, z0), new THREE.Vector3(x1, y1, z0),
        new THREE.Vector3(x1, y1, z1), new THREE.Vector3(x0, y1, z1),
      ];
      for (const [a, b] of [[0, 1], [1, 2], [2, 3], [3, 0],
        [4, 5], [5, 6], [6, 7], [7, 4], [0, 4], [1, 5], [2, 6], [3, 7]]) {
        zonePoints.push(corners[a], corners[b]);
      }
    }
    if (zonePoints.length) {
      const zones = new THREE.LineSegments(
        new THREE.BufferGeometry().setFromPoints(zonePoints),
        new THREE.LineBasicMaterial({ color: 0x9f7aea, transparent: true, opacity: 0.7 }),
      );
      zones.renderOrder = 6;
      group.add(zones);
    }
    group.visible = this.showCollision;
    this.collisionGroup = group;
    this.scene.add(group);
    this.needsRender = true;
    return group;
  }

  /**
   * A grey stage with no textures reads as one flat surface; tinting the vertices by
   * height (a viewer aid, not original data) makes the terraces, ramps and the low
   * channel at the start legible at a glance.
   */
  _tintTerrainByHeight(geometry) {
    const position = geometry?.getAttribute?.("position");
    if (!position) return;
    const low = new THREE.Color(0x6f7d7a);
    const high = new THREE.Color(0xc3b394);
    const colors = new Float32Array(position.count * 3);
    const mix = new THREE.Color();
    for (let index = 0; index < position.count; index += 1) {
      const y = position.getY(index);
      const t = Math.min(1, Math.max(0, (y + 1.6) / 2.2));
      mix.copy(low).lerp(high, t);
      colors[index * 3] = mix.r;
      colors[index * 3 + 1] = mix.g;
      colors[index * 3 + 2] = mix.b;
    }
    geometry.setAttribute("color", new THREE.BufferAttribute(colors, 3));
  }

  setTerrain(visible) {
    this.showTerrain = Boolean(visible);
    if (this.terrainGroup) this.terrainGroup.visible = this.showTerrain;
    this._applyLegacyPlaneVisibility();
    this.needsRender = true;
    return Boolean(this.terrainGroup);
  }

  setTerrainWireframe(enabled) {
    this.terrainWireframe = Boolean(enabled);
    if (this.terrainMaterial) {
      this.terrainMaterial.wireframe = this.terrainWireframe;
      // A wireframe has to show the far faces too, or half the stage disappears.
      this.terrainMaterial.side = this.terrainWireframe ? THREE.DoubleSide : THREE.FrontSide;
      this.terrainMaterial.needsUpdate = true;
    }
    this.needsRender = true;
    return this.terrainWireframe;
  }

  async setCollision(visible) {
    this.showCollision = Boolean(visible);
    if (this.showCollision) await this._ensureCollision();
    if (this.collisionGroup) this.collisionGroup.visible = this.showCollision;
    this.needsRender = true;
    return Boolean(this.collisionGroup);
  }

  /** Show the placement's own ground/boundary planes on top of (or instead of) the stage. */
  setLegacyPlanes(visible) {
    this.showLegacyPlanes = Boolean(visible);
    this._applyLegacyPlaneVisibility();
    this.needsRender = true;
    return this.showLegacyPlanes;
  }

  /** Put each prop at its original instance height (display only; the file is untouched). */
  setOriginalHeight(enabled) {
    this.useOriginalHeight = Boolean(enabled);
    for (const [id, group] of this.propObjects) {
      const prop = group.userData.data;
      this._applyRefTransform(group, prop || { itemId: id });
    }
    this._buildParentLines();
    this._updateSelectionVisuals();
    this.needsRender = true;
    return this.useOriginalHeight;
  }

  _applyLegacyPlaneVisibility() {
    // The reference placement carries a flat ground plane at y = 0 and four boundary
    // planes; the original stage replaces both when it is drawn.
    const hide = Boolean(this.refMode && this.terrainGroup && this.showTerrain
      && !this.showLegacyPlanes);
    for (const group of this.structureObjects.values()) {
      const role = group.userData.role;
      if (role !== "ground" && role !== "boundary") continue;
      group.visible = !hide;
    }
    // The editor's own lawn and grid are drawn at y = 0, which is ~1 m above the real
    // ground; they would both hide the low parts of the stage and float over it.
    if (this.lawn) this.lawn.visible = !hide;
    if (this.grid) this.grid.visible = !hide;
  }

  /**
   * Terrain contact of every prop, measured by the server (``/api/ref-stage/contact``).
   *
   * The analysis lives in ``Tools/levelbuild/ref_stage.py`` so a headless client can
   * ask the same question; the browser only displays the answer.
   */
  async terrainContact(tolerance = 0.1) {
    if (!this.refMode || !this.refStage?.available) return null;
    const token = this.apiToken ? `&token=${encodeURIComponent(this.apiToken)}` : "";
    const started = performance.now();
    try {
      const response = await fetch(`/api/ref-stage/contact?t=${tolerance}${token}`, {
        headers: { Accept: "application/json" },
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const payload = await response.json();
      payload.requestMs = Math.round(performance.now() - started);
      return payload;
    } catch (error) {
      this.callbacks.onError?.(`地形接触测量失败：${error.message}`);
      return null;
    }
  }

  refSummary() {
    const stage = this.refStage || {};
    return {
      available: Boolean(stage.available),
      reason: stage.reason || null,
      dir: stage.dir || null,
      counts: stage.counts || {},
      frame: stage.frame || null,
      terrain: Boolean(this.terrainGroup),
      terrainVisible: Boolean(this.terrainGroup?.visible),
      wireframe: this.terrainWireframe,
      collision: Boolean(this.collisionGroup),
      collisionVisible: Boolean(this.collisionGroup?.visible),
      legacyPlanes: this.showLegacyPlanes,
      originalHeight: this.useOriginalHeight,
      transforms: this.refTransforms ? Object.keys(this.refTransforms).length : 0,
      parentLinks: (this.refParentLinks || []).length,
      backdropMeshes: this._refBackdropCount(),
      v2: Boolean(this.v2Mode),
      v2Terrain: Boolean(this.terrainGroup && this.v2Mode),
      v2Solids: this.v2Mode ? this.structureObjects.size : 0,
      v2SolidsVisible: Boolean(this.v2ShowSolids),
      v2Walls: this.v2WallCount,
      v2WallsVisible: Boolean(this.v2WallsGroup?.visible),
      v2KillPlane: Boolean(this.v2KillGroup),
      v2KillVisible: Boolean(this.v2KillGroup?.visible),
      v2Parents: (this.v2ParentLinks || []).length,
      v2Rotations: Boolean(this.v2Rotations),
      v2Scale: Boolean(this.v2Scale),
    };
  }

  _refBackdropCount() {
    let count = 0;
    this.terrainGroup?.traverse((object) => {
      if (object.isMesh && object.userData.refBackdrop) count += 1;
    });
    return count;
  }

  /**
   * The reference data's ``group``/``groupId`` parent-control pointer, drawn as a
   * thin line from child to parent.  This is structure the original level carries
   * (83 children in Make Cancer) that the web editor would otherwise not show.
   */
  _buildParentLines() {
    this._disposeParentLines();
    const props = this.doc?.props || [];
    if (!this.refMode || !props.length) return;
    if (this.v2Mode) {
      this._buildV2ParentLines();
      return;
    }
    if (this.refStage?.available && (this.refParentLinks || []).length) {
      this._buildInstanceParentLines();
      return;
    }
    this._buildGroupParentLines(props);
  }

  _disposeParentLines() {
    if (this.parentLines) {
      this.scene.remove(this.parentLines);
      this.parentLines.traverse((object) => {
        object.geometry?.dispose?.();
        object.material?.dispose?.();
      });
      this.parentLines = null;
    }
    this.parentLineCount = 0;
  }

  /**
   * The placement's own ``group``/``groupId`` pointer, drawn as a thin line from child
   * to parent.  Used when the stage extraction has no parent table for this level.
   */
  _buildGroupParentLines(props) {
    const byId = new Map(props.map((prop) => [prop.itemId, prop]));
    const points = [];
    for (const prop of props) {
      const parentId = prop.group || prop.groupId;
      if (!parentId || parentId === prop.itemId) continue;
      const parent = byId.get(parentId);
      if (!parent) continue;
      points.push(toThreePosition(prop.position), toThreePosition(parent.position));
    }
    if (!points.length) return;
    this.parentLineCount = points.length / 2;
    this._addParentLines(points, 0xff5fd2);
  }

  _buildV2ParentLines() {
    const links = this.v2ParentLinks || [];
    const points = [];
    for (const link of links) {
      const child = this.propObjects.get(link.child);
      const parent = link.parent ? this.propObjects.get(link.parent) : null;
      if (!child || !parent) continue;
      points.push(child.position.clone(), parent.position.clone());
    }
    if (!points.length) return;
    this.parentLineCount = points.length / 2;
    this._addParentLines(points, 0xff5fd2);
  }

  /**
   * The original parent table (``parentBlock``), resolved through the instance table:
   * 83 child -> parent links in Make Cancer.  Links the placement's ``group`` pointer
   * also carries are drawn magenta, extractor-only ones orange, so the two sources can
   * be compared on sight.
   */
  _buildInstanceParentLines() {
    const links = this.refParentLinks || [];
    const known = new Set();
    for (const prop of this.doc?.props || []) {
      const parentId = prop.group || prop.groupId;
      if (parentId) known.add(`${prop.itemId}>${parentId}`);
    }
    const corroborated = [];
    const extra = [];
    for (const link of links) {
      const child = this.propObjects.get(link.child);
      const parent = link.parent ? this.propObjects.get(link.parent) : null;
      if (!child || !parent) continue;
      const pair = [child.position.clone(), parent.position.clone()];
      if (known.has(`${link.child}>${link.parent}`)) corroborated.push(...pair);
      else extra.push(...pair);
    }
    if (!corroborated.length && !extra.length) return;
    this.parentLineCount = (corroborated.length + extra.length) / 2;
    this.parentLines = new THREE.Group();
    this.parentLines.userData = { type: "parent-lines" };
    if (corroborated.length) {
      this.parentLines.add(this._parentLineObject(corroborated, 0xff5fd2, 0.9));
    }
    if (extra.length) {
      this.parentLines.add(this._parentLineObject(extra, 0xffa53b, 0.9));
    }
    this.parentLines.visible = this.showParentLines;
    this.scene.add(this.parentLines);
    this.needsRender = true;
  }

  _parentLineObject(points, color, opacity) {
    const lines = new THREE.LineSegments(
      new THREE.BufferGeometry().setFromPoints(points),
      new THREE.LineBasicMaterial({ color, transparent: true, opacity, depthTest: false }),
    );
    lines.renderOrder = 20;
    return lines;
  }

  _addParentLines(points, color) {
    this.parentLines = new THREE.Group();
    this.parentLines.userData = { type: "parent-lines" };
    this.parentLines.add(this._parentLineObject(points, color, 0.85));
    this.parentLines.visible = this.showParentLines;
    this.scene.add(this.parentLines);
    this.needsRender = true;
  }

  setParentLines(visible) {
    this.showParentLines = Boolean(visible);
    if (this.parentLines) this.parentLines.visible = this.showParentLines;
    this.needsRender = true;
    return this.parentLineCount;
  }

  setReadOnly(value) {
    this.readOnly = Boolean(value);
  }

  loadDef(def) {
    if (!this.defPromises.has(def)) {
      const token = this.apiToken ? `?token=${encodeURIComponent(this.apiToken)}` : "";
      const promise = this.loader
        .loadAsync(`/api/models/${encodeURIComponent(def)}.glb${token}`)
        .then((gltf) => {
          const scene = gltf.scene;
          const cache = new Map();
          scene.traverse((object) => {
            if (!object.isMesh) return;
            object.castShadow = true;
            object.receiveShadow = true;
            object.material = this._propMaterial(def, object.material, cache);
          });
          scene.updateMatrixWorld(true);
          this.defBoxes.set(def, new THREE.Box3().setFromObject(scene));
          return scene;
        });
      this.defPromises.set(def, promise);
    }
    return this.defPromises.get(def);
  }

  _propMaterial(def, glbMaterial, cache) {
    if (glbMaterial && glbMaterial.map) return glbMaterial;
    const manifest = this.manifests[def] || {};
    const colors = manifest.colors || {};
    const slot = (Array.isArray(glbMaterial) ? glbMaterial[0]?.name : glbMaterial?.name)
      || manifest.materials?.[0];
    const rgb = colors[slot];
    if (!rgb) return glbMaterial;
    const key = `${def}:${slot}`;
    if (!cache.has(key)) {
      cache.set(key, new THREE.MeshStandardMaterial({
        name: slot,
        color: new THREE.Color(rgb[0], rgb[1], rgb[2]),
        roughness: (manifest.roughness || {})[slot] ?? 0.65,
        metalness: 0,
      }));
    }
    return cache.get(key);
  }

  // ------------------------------------------------------------------ #
  // cameras
  // ------------------------------------------------------------------ #
  fitLevelView() {
    this.cam.fit(this._groundBox());
    this.currentCamera = "overview";
    this.needsRender = true;
    this.callbacks.onStatus?.("camera", "overview");
    this.renderNow();
  }

  setCameraPreset(id) {
    if (this.isFlying) this._endFly();
    const box = this._groundBox();
    if (id === "overview") {
      this.cam.fit(box);
    } else if (id === "top") {
      this.cam.setTop(box);
    } else if (id === "ball_eye" || id === "human_eye") {
      const spawn = this._spawn();
      const base = toThreePosition([spawn[0], 0, spawn[2]]);
      const target = toThreePosition(this._ballStart());
      this.cam.setEye(base, id === "ball_eye" ? 0.6 : 1.6, target);
    } else {
      return false;
    }
    this.currentCamera = id;
    this.needsRender = true;
    this.callbacks.onStatus?.("camera", id);
    this.renderNow();
    return true;
  }

  cameraButtonNames() {
    return [
      { id: "overview", label: "3/4" },
      { id: "top", label: "俯视" },
      { id: "ball_eye", label: "球眼 0.6" },
      { id: "human_eye", label: "人眼 1.6" },
    ];
  }

  // ------------------------------------------------------------------ #
  // regions (sidecar; independent of placement undo)
  // ------------------------------------------------------------------ #
  _regionBox(region) {
    const [ax, ay, az] = region.min;
    const [bx, by, bz] = region.max;
    return new THREE.Box3(
      new THREE.Vector3(Math.min(ax, bx), Math.min(ay, by), -Math.max(az, bz)),
      new THREE.Vector3(Math.max(ax, bx), Math.max(ay, by), -Math.min(az, bz))
    );
  }

  _groundPoint(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.set(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    );
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const ground = Number((this.doc?.level || {}).groundY || 0);
    const plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -ground);
    const hit = this.raycaster.ray.intersectPlane(plane, new THREE.Vector3());
    return hit ? fromThreePosition(hit) : null;
  }

  setRegions(regions, activeName = null) {
    this.regions = Array.isArray(regions) ? regions : [];
    this.activeRegion = activeName
      || (this.activeRegion && this.regions.some((entry) => entry.name === this.activeRegion)
        ? this.activeRegion : (this.regions[0]?.name || null));
    this._rebuildRegionVisuals();
  }

  setRegionMode(enabled) {
    this.regionMode = Boolean(enabled);
    this._rebuildRegionVisuals();
    this._refreshGizmo();
    this.callbacks.onStatus?.("region-mode", this.regionMode);
    this.needsRender = true;
  }

  setActiveRegion(name) {
    this.activeRegion = name || null;
    this._rebuildRegionVisuals();
    this.needsRender = true;
  }

  _rebuildRegionVisuals() {
    for (const child of [...this.regionGroup.children]) {
      this.regionGroup.remove(child);
      child.geometry?.dispose?.();
      child.material?.dispose?.();
    }
    this.regionHandles = [];
    for (const region of this.regions) {
      if (region.visible === false) continue;
      const box = this._regionBox(region);
      const size = box.getSize(new THREE.Vector3());
      const center = box.getCenter(new THREE.Vector3());
      const active = region.name === this.activeRegion;
      const color = new THREE.Color(region.color || (active ? 0x8fd4ff : 0x6aa5c8));
      const fill = new THREE.Mesh(
        new THREE.BoxGeometry(Math.max(0.01, size.x), Math.max(0.01, size.y), Math.max(0.01, size.z)),
        new THREE.MeshBasicMaterial({ color, transparent: true,
          opacity: active ? 0.10 : 0.05, depthWrite: false, side: THREE.DoubleSide })
      );
      fill.position.copy(center);
      fill.renderOrder = 4;
      this.regionGroup.add(fill);
      const edges = new THREE.LineSegments(
        new THREE.EdgesGeometry(new THREE.BoxGeometry(Math.max(0.01, size.x),
          Math.max(0.01, size.y), Math.max(0.01, size.z))),
        new THREE.LineBasicMaterial({ color, transparent: true,
          opacity: active ? 0.95 : 0.5, depthTest: false })
      );
      edges.position.copy(center);
      edges.renderOrder = 6;
      this.regionGroup.add(edges);
    }
    if (this.regionMode && this.activeRegion) {
      const region = this.regions.find((entry) => entry.name === this.activeRegion);
      if (region) this._buildRegionHandles(region);
    }
    this.needsRender = true;
  }

  _buildRegionHandles(region) {
    const box = this._regionBox(region);
    const center = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    const axes = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
    const colors = [0xff5a4a, 0x5ad46a, 0x4a8cff];
    for (let axis = 0; axis < 3; axis += 1) {
      for (const side of ["min", "max"]) {
        const position = center.clone();
        const sign = side === "min" ? -1 : 1;
        position.addScaledVector(new THREE.Vector3(...axes[axis]), sign * size.getComponent(axis) / 2);
        const handle = new THREE.Mesh(
          new THREE.BoxGeometry(0.09, 0.09, 0.09),
          new THREE.MeshBasicMaterial({ color: colors[axis], depthTest: false })
        );
        handle.position.copy(position);
        handle.renderOrder = 30;
        handle.userData = { regionHandle: true, axis, side, regionName: region.name };
        this.regionGroup.add(handle);
        this.regionHandles.push(handle);
      }
    }
  }

  _beginRegionInteraction(event) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.set(
      ((event.clientX - rect.left) / rect.width) * 2 - 1,
      -((event.clientY - rect.top) / rect.height) * 2 + 1
    );
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const handleHits = this.raycaster.intersectObjects(this.regionHandles, false);
    if (handleHits.length) {
      const handle = handleHits[0].object;
      const region = this.regions.find((entry) => entry.name === handle.userData.regionName);
      if (region) {
        const axis = handle.userData.axis;
        const side = handle.userData.side;
        const world = handle.getWorldPosition(this.camera.position.clone());
        const axisPoint = world.clone();
        axisPoint.setComponent(axis, axisPoint.getComponent(axis) + 1);
        this.regionInteraction = {
          type: "handle",
          region,
          axis,
          side,
          startValue: region[side][axis],
          startX: event.clientX,
          startY: event.clientY,
          screen: this._axisScreen(handle.getWorldPosition(new THREE.Vector3()), axis),
        };
        this.canvas.setPointerCapture?.(event.pointerId);
        return true;
      }
    }
    const point = this._groundPoint(event.clientX, event.clientY);
    if (point) {
      this.regionInteraction = {
        type: "draw",
        start: point,
        current: point,
        pointerId: event.pointerId,
      };
      this.canvas.setPointerCapture?.(event.pointerId);
      return true;
    }
    return false;
  }

  _axisScreen(world, axis) {
    const rect = this.canvas.getBoundingClientRect();
    const a = world.clone();
    const b = world.clone();
    b.setComponent(axis, b.getComponent(axis) + 1);
    a.project(this.camera);
    b.project(this.camera);
    const ax = rect.left + (a.x + 1) / 2 * rect.width;
    const ay = rect.top + (1 - a.y) / 2 * rect.height;
    const bx = rect.left + (b.x + 1) / 2 * rect.width;
    const by = rect.top + (1 - b.y) / 2 * rect.height;
    return { ax, ay, bx, by, length: Math.hypot(bx - ax, by - ay) || 1 };
  }

  _moveRegionInteraction(event) {
    const interaction = this.regionInteraction;
    if (!interaction) return;
    if (interaction.type === "handle") {
      const region = interaction.region;
      const screen = interaction.screen;
      const dx = event.clientX - interaction.startX;
      const dy = event.clientY - interaction.startY;
      const along = (dx * (screen.bx - screen.ax) + dy * (screen.by - screen.ay)) / screen.length;
      const worldDelta = along / screen.length;
      let value = interaction.startValue + (interaction.side === "min" ? worldDelta : worldDelta);
      const other = interaction.side === "min" ? region.max[interaction.axis] : region.min[interaction.axis];
      value = interaction.side === "min"
        ? Math.min(value, other - 0.05)
        : Math.max(value, other + 0.05);
      region[interaction.side][interaction.axis] = round(value, 4);
      this._rebuildRegionVisuals();
      this.callbacks.onRegionChange?.(region);
      this.needsRender = true;
      return;
    }
    const point = this._groundPoint(event.clientX, event.clientY);
    if (!point) return;
    interaction.current = point;
    this._drawRegionPreview(interaction.start, point);
  }

  _drawRegionPreview(start, current) {
    if (this.regionPreview) {
      this.regionGroup.remove(this.regionPreview);
      this.regionPreview.geometry?.dispose?.();
      this.regionPreview.material?.dispose?.();
      this.regionPreview = null;
    }
    const ground = Number((this.doc?.level || {}).groundY || 0);
    const low = [Math.min(start[0], current[0]), ground, Math.min(start[2], current[2])];
    const high = [Math.max(start[0], current[0]), ground + 2.5, Math.max(start[2], current[2])];
    const box = new THREE.Box3(
      new THREE.Vector3(low[0], low[1], -high[2]),
      new THREE.Vector3(high[0], high[1], -low[2])
    );
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    const preview = new THREE.LineSegments(
      new THREE.EdgesGeometry(new THREE.BoxGeometry(Math.max(0.01, size.x),
        Math.max(0.01, size.y), Math.max(0.01, size.z))),
      new THREE.LineBasicMaterial({ color: 0xa8e6ff, transparent: true, opacity: 1,
        depthTest: false })
    );
    preview.position.copy(center);
    preview.renderOrder = 31;
    this.regionGroup.add(preview);
    this.regionPreview = preview;
    this.needsRender = true;
  }

  _endRegionInteraction(event) {
    const interaction = this.regionInteraction;
    this.regionInteraction = null;
    try {
      this.canvas.releasePointerCapture?.(interaction?.pointerId || event.pointerId);
    } catch (error) {
      /* released */
    }
    if (!interaction) return;
    if (interaction.type === "handle") {
      this.callbacks.onRegionChange?.(interaction.region);
      return;
    }
    if (this.regionPreview) {
      this.regionGroup.remove(this.regionPreview);
      this.regionPreview.geometry?.dispose?.();
      this.regionPreview.material?.dispose?.();
      this.regionPreview = null;
    }
    const start = interaction.start;
    const end = interaction.current;
    const ground = Number((this.doc?.level || {}).groundY || 0);
    let low = [Math.min(start[0], end[0]), ground, Math.min(start[2], end[2])];
    let high = [Math.max(start[0], end[0]), ground + 2.5, Math.max(start[2], end[2])];
    if (high[0] - low[0] < 0.1 || high[2] - low[2] < 0.1) return;
    this.callbacks.onRegionCreated?.({
      name: `区域 ${this.regions.length + 1}`,
      min: low.map((value) => round(value, 4)),
      max: high.map((value) => round(value, 4)),
      visible: true,
    });
  }

  setStatsScope(ids, active) {
    this.statsScopeIds = active ? new Set(ids || []) : null;
    this._applyPropAppearance();
  }

  // ------------------------------------------------------------------ #
  // ladder
  // ------------------------------------------------------------------ #
  setLadder(diameter, enabled = true) {
    this.ladder = { diameter, enabled };
    this._applyLadderToAll();
    this._updateBall();
    this.needsRender = true;
  }

  // The original's gate, in whole millimetres: an item is food iff
  // floor(1000*D) >= Req_mm (Req = floor(1000*cbrt(60*k*a*b*c/pi))).  Derived rows carry
  // dReqMm from the server because that is the integer the data gate used; dReq is the
  // same quantity in metres and is only a fallback for an older payload.
  _tierFor(dReqMm, dReq) {
    const floorMm = (meters) => Math.floor(Number(meters) * 1000 + 1e-6);
    const maxMm = floorMm((this.doc.level || {}).ballDiameterMax || 2.2);
    const ballMm = floorMm(this.ladder.diameter);
    const reqMm = Number.isFinite(dReqMm) ? dReqMm : floorMm(dReq);
    if (reqMm > maxMm) return "grey";
    if (reqMm <= ballMm) return "green";
    if (reqMm <= floorMm(this.ladder.diameter * 1.2)) return "yellow";
    return "red";
  }

  _tierMaterial(tier) {
    this.tierMaterials = this.tierMaterials || new Map();
    if (!this.tierMaterials.has(tier)) {
      this.tierMaterials.set(tier, new THREE.MeshStandardMaterial({
        color: TIER_COLORS[tier],
        roughness: 0.62,
        metalness: 0,
        emissive: new THREE.Color(TIER_COLORS[tier]).multiplyScalar(0.12),
      }));
    }
    return this.tierMaterials.get(tier);
  }

  _applyPropAppearance() {
    for (const [id, group] of this.propObjects) {
      const derived = this.derived.props[id];
      const tier = derived ? this._tierFor(derived.dReqMm, derived.dReq) : "grey";
      group.userData.tier = tier;
      const faded = this.statsScopeIds && !this.statsScopeIds.has(id);
      const motionRow = this.showMotionColors ? this.v2MotionRows.get(id) : null;
      group.traverse((object) => {
        if (!object.isMesh) return;
        if (faded) {
          object.material = this.fadedMaterial;
        } else if (motionRow) {
          object.material = this._motionClassMaterial(motionRow.motionClass);
        } else if (this.ladder.enabled) {
          object.material = this._tierMaterial(tier);
        } else if (object.userData.baseMaterial) {
          object.material = object.userData.baseMaterial;
        }
      });
    }
    this._updateSelectionVisuals();
  }

  _applyLadderToAll() {
    this._applyPropAppearance();
  }

  _updateBall() {
    if (!this.ballGhost) return;
    const radius = Math.max(0.02, this.ladder.diameter / 2);
    const start = toThreePosition(this._ballStart());
    const ground = Number((this.doc.level || {}).groundY || 0);
    this.ballGhost.scale.setScalar(radius / 0.5);
    this.ballGhost.position.set(start.x, this.v2Mode ? start.y : ground + radius, start.z);
  }

  // ------------------------------------------------------------------ #
  // state refresh
  // ------------------------------------------------------------------ #
  async applyState(state) {
    const oldLevel = this.doc?.level?.id;
    const newLevel = state.doc?.level?.id;
    // Same id is not the same document: v1 <-> v2 (or placement.model.json <->
    // placement.v2.json) must rebuild the scene, not just refresh the panels.
    const modeChanged = Boolean(this.state?.v2) !== Boolean(state.v2)
      || this.state?.file?.path !== state.file?.path;
    if (!this.doc || oldLevel !== newLevel || modeChanged) {
      await this.load(state);
      return;
    }
    this.state = state;
    this.doc = state.doc;
    this.derived = state.derived || { props: {}, structures: {} };
    this.manifests = state.manifests || this.manifests;
    this.refMode = Boolean(state.ref?.unlicensed);
    this.readOnly = Boolean(state.ref?.readOnly);
    if (this.v2Mode) {
      this.v2Summary = state.v2 || this.v2Summary;
      this.v2ParentLinks = state.v2?.parents || this.v2ParentLinks;
    }

    const seen = new Set();
    for (const prop of this.doc.props || []) {
      seen.add(prop.itemId);
      let group = this.propObjects.get(prop.itemId);
      if (!group) {
        group = await this._makeProp(prop);
        this.propObjects.set(prop.itemId, group);
        this.scene.add(group);
        this.pickTargets.push(group);
      } else {
        group.userData.data = prop;
        group.position.copy(toThreePosition(prop.position));
        group.quaternion.copy(toThreeQuaternion(prop.rotationQuat));
        this._applyRefTransform(group, prop);
      }
    }
    for (const [id, group] of [...this.propObjects]) {
      if (seen.has(id)) continue;
      this.scene.remove(group);
      this.propObjects.delete(id);
      this.pickTargets = this.pickTargets.filter((entry) => entry !== group);
    }

    const seenStructures = new Set();
    for (const structure of this.doc.structures || []) {
      seenStructures.add(structure.id);
      let group = this.structureObjects.get(structure.id);
      if (!group) {
        group = this._makeStructure(structure);
        this.structureObjects.set(structure.id, group);
        this.scene.add(group);
        if (structure.role !== "ground") this.pickTargets.push(group);
      } else {
        group.userData.data = structure;
        group.position.copy(toThreePosition(structure.center));
        group.quaternion.copy(toThreeQuaternion(structure.rotationQuat));
        const mesh = group.userData.mesh;
        mesh.scale.set(structure.size[0], structure.size[1], structure.size[2]);
        mesh.position.set(0, (structure.role === "ground" || structure.role === "boundary")
          ? -structure.size[1] / 2 : 0, 0);
      }
    }
    for (const [id, group] of [...this.structureObjects]) {
      if (seenStructures.has(id)) continue;
      this.scene.remove(group);
      this.structureObjects.delete(id);
      this.pickTargets = this.pickTargets.filter((entry) => entry !== group);
    }

    for (const group of this.structureObjects.values()) {
      const role = group.userData.role;
      if (role === "ground" || role === "boundary") this._applyLegacyPlaneVisibility();
    }
    const surviving = [...this.selectionIds].filter((id) => this.objectGroup(id));
    this._setSelection(surviving);
    this._applyLadderToAll();
    if (this.v2Mode && (!this.v2Rotations || !this.v2Scale)) {
      await this.setV2Transforms(this.v2Rotations, this.v2Scale);
    }
    this._buildParentLines();
    this._updateBall();
    this.needsRender = true;
  }

  // ------------------------------------------------------------------ #
  // thumbnails / screenshot / render loop
  // ------------------------------------------------------------------ #
  async renderThumbnail(def, size = 112) {
    if (!this.thumbnailRenderer) {
      const canvas = document.createElement("canvas");
      this.thumbnailRenderer = new THREE.WebGLRenderer({
        canvas, antialias: true, alpha: true, preserveDrawingBuffer: true,
      });
      this.thumbnailRenderer.setSize(size, size, false);
      this.thumbnailRenderer.outputColorSpace = THREE.SRGBColorSpace;
      this.thumbnailScene = new THREE.Scene();
      this.thumbnailScene.add(new THREE.HemisphereLight(0xffffff, 0x667755, 1.9));
      const light = new THREE.DirectionalLight(0xffffff, 2.4);
      light.position.set(3, 5, 4);
      this.thumbnailScene.add(light);
      this.thumbnailCamera = new THREE.PerspectiveCamera(32, 1, 0.001, 50);
    }
    const prototype = await this.loadDef(def);
    const model = prototype.clone(true);
    model.traverse((object) => {
      if (object.isMesh) object.userData.baseMaterial = object.material;
    });
    const box = new THREE.Box3().setFromObject(model);
    const center = box.getCenter(new THREE.Vector3());
    const extent = Math.max(0.001, box.getSize(new THREE.Vector3()).length());
    model.position.sub(center);
    this.thumbnailScene.add(model);
    const distance = extent * 1.45;
    this.thumbnailCamera.position.set(distance * 0.95, distance * 0.7, distance * 0.95);
    this.thumbnailCamera.lookAt(0, 0, 0);
    this.thumbnailCamera.updateProjectionMatrix();
    this.thumbnailRenderer.render(this.thumbnailScene, this.thumbnailCamera);
    const url = this.thumbnailRenderer.domElement.toDataURL("image/png");
    this.thumbnailScene.remove(model);
    return url;
  }

  renderNow() {
    if (this.loading || window.location.search.includes("norender")) return;
    this.renderer.render(this.scene, this.camera);
    this.needsRender = false;
  }

  screenshot() {
    this.renderer.render(this.scene, this.camera);
    const level = this.doc?.level?.id || "level";
    const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    const name = `${level}_${this.currentCamera || "view"}_${stamp}.png`;
    const dataUrl = this.canvas.toDataURL("image/png");
    this.lastScreenshot = dataUrl;
    const link = document.createElement("a");
    link.href = dataUrl;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    return { name, dataUrl };
  }

  resize() {
    const parent = this.canvas.parentElement;
    if (!parent) return;
    const width = Math.max(1, parent.clientWidth);
    const height = Math.max(1, parent.clientHeight);
    this.renderer.setSize(width, height, false);
    this.cam.resize(width / height);
    this.needsRender = true;
  }

  _animate(now) {
    this._frame = requestAnimationFrame(this._animate);
    const delta = Math.min(0.1, (now - (this._last || now)) / 1000);
    this._last = now;
    if (this.playback.playing && this.trajectoryRows.size) {
      let time = this.playback.time + delta * this.playback.speed;
      const duration = this.trajectoryDurationS;
      if (time >= duration) {
        if (this.playback.loop) time = duration > 0 ? time % duration : 0;
        else { time = duration; this.playback.playing = false; }
      }
      this.setPlaybackTime(time, { silent: true });
      // The time readout is for a human; ten updates a second is plenty and keeps the DOM
      // out of the render path.
      if (now - this._playbackReportedAt > 100) {
        this._playbackReportedAt = now;
        this.callbacks.onPlayback?.(this.playbackState());
      }
    }
    if (this.cam.update(delta)) this.needsRender = true;
    if (!this.loading && (this.needsRender || this.isFlying)) {
      const started = performance.now();
      this.renderer.render(this.scene, this.camera);
      this.needsRender = false;
      // Frame cost of the last drawn frame (software GL makes this the load-bearing
      // number, and the headless verification reads it instead of guessing).
      this.lastFrameMs = Math.round((performance.now() - started) * 10) / 10;
      this.frames = (this.frames || 0) + 1;
    }
  }

  /** Draw ``count`` frames and time them (used by the headless verification). */
  async benchmarkFrames(count = 60) {
    const times = [];
    for (let index = 0; index < count; index += 1) {
      const started = performance.now();
      this.renderer.render(this.scene, this.camera);
      times.push(performance.now() - started);
      await new Promise((resolve) => requestAnimationFrame(() => resolve()));
    }
    times.sort((a, b) => a - b);
    const median = times[Math.floor(times.length / 2)];
    return {
      frames: count,
      medianMs: Math.round(median * 10) / 10,
      meanMs: Math.round((times.reduce((sum, value) => sum + value, 0) / times.length) * 10) / 10,
      p95Ms: Math.round(times[Math.floor(times.length * 0.95)] * 10) / 10,
      maxMs: Math.round(times[times.length - 1] * 10) / 10,
      fps: Math.round(1000 / median * 10) / 10,
    };
  }

  // ------------------------------------------------------------------ #
  // coordinate audit
  // ------------------------------------------------------------------ #
  audit(tolerance = 0.01) {
    const problems = [];
    let worst = { id: null, error: 0 };
    const check = (id, group, expected) => {
      if (!expected) return;
      group.updateMatrixWorld(true);
      const box = new THREE.Box3().setFromObject(group);
      const target = unityAabbToThree(expected);
      const error = Math.max(
        Math.abs(box.min.x - target.min.x), Math.abs(box.min.y - target.min.y),
        Math.abs(box.min.z - target.min.z), Math.abs(box.max.x - target.max.x),
        Math.abs(box.max.y - target.max.y), Math.abs(box.max.z - target.max.z)
      );
      if (error > worst.error) worst = { id, error };
      if (error > tolerance) {
        problems.push({ id, error: Number(error.toFixed(4)),
          rendered: threeBoxToUnity(box), expected });
      }
    };
    for (const [id, group] of this.propObjects) {
      check(id, group, this.derived.props[id]?.worldAabb);
    }
    for (const [id, group] of this.structureObjects) {
      const role = group.userData.role;
      if (role === "ground" || role === "boundary") continue;
      check(id, group, this.derived.structures[id]?.worldAabb);
    }
    const chair = this.propObjects.get("struct_lounge_chair_01");
    let chairBox = null;
    if (chair) {
      chair.updateMatrixWorld(true);
      chairBox = threeBoxToUnity(new THREE.Box3().setFromObject(chair));
    }
    return {
      ok: problems.length === 0,
      props: this.propObjects.size,
      structures: this.structureObjects.size,
      maxError: Number(worst.error.toFixed(5)),
      worst: worst.id,
      problems: problems.slice(0, 25),
      chair: chairBox,
      ladder: this.ladder,
    };
  }

  selectionWorldBox() {
    return this.selectionBox.isEmpty() ? null : threeBoxToUnity(this.selectionBox);
  }

  dispose() {
    cancelAnimationFrame(this._frame);
    this._resizeObserver.disconnect();
  }
}

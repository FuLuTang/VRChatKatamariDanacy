/**
 * The three.js side of the level editor.
 *
 * Coordinates
 * -----------
 * placement.json is Unity world space: left-handed, Y up, quaternion ``(x, y, z, w)``.
 * three.js is right-handed with -Z forward.  The one conversion (and its inverse) is
 * at the top of this file:
 *
 *     position:  (x, y, z)  ->  (x, y, -z)
 *     rotation:  (x, y, z, w) -> (-x, -y, z, w)          (both directions)
 *
 * Negating z is a reflection, so the vector part of the quaternion flips x and y:
 * that keeps a Unity +30 deg yaw a +30 deg clockwise yaw from above, which is what
 * the Unity importer and the Blender exporter mean.  The inverse map is the same
 * map, so one pair of functions covers both directions.
 *
 * The prop GLBs are the Blender exports (``export_yup``), whose local axes already
 * match the JSON ``aabb`` (x width, y height, z depth).  The coordinate audit at the
 * bottom of this file compares each rendered Box3 against the server-computed world
 * AABB, so an axis mistake cannot hide.
 */

import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { TransformControls } from "three/addons/controls/TransformControls.js";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";

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
   * @param {{onSelect: Function, onTransform: Function, onStatus: Function,
   *          onError: Function, onProgress: Function}} callbacks
   */
  constructor(canvas, callbacks = {}) {
    this.canvas = canvas;
    this.callbacks = callbacks;
    this.state = null;
    this.doc = null;
    this.derived = { props: {}, structures: {} };
    this.manifests = {};

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
    this.noRender = window.location.search.includes("norender");
    this.renderer.shadowMap.enabled = !window.location.search.includes("noshadow");
    this.renderer.shadowMap.type = THREE.PCFShadowMap;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0xc9dcec);
    this.scene.fog = new THREE.Fog(0xc9dcec, 55, 190);

    this.persp = new THREE.PerspectiveCamera(45, 1, 0.03, 300);
    this.ortho = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.03, 300);
    this.camera = this.persp;

    this.controls = new OrbitControls(this.persp, canvas);
    this.controls.enableDamping = true;
    this.controls.dampingFactor = 0.08;
    this.controls.screenSpacePanning = true;
    this.controls.minDistance = 0.35;
    this.controls.maxDistance = 120;
    this.controls.maxPolarAngle = Math.PI * 0.495;
    this.controls.target.set(0, 0.3, 0);
    this.controls.addEventListener("change", () => { this.needsRender = true; });

    this.gizmo = new TransformControls(this.persp, canvas);
    this.gizmo.setSize(0.7);
    this.gizmo.translationSnap = 0.05;
    this.gizmo.rotationSnap = THREE.MathUtils.degToRad(15);
    this.gizmo.addEventListener("dragging-changed", (event) => {
      this.controls.enabled = !event.value;
      if (!event.value) this._commitTransform();
    });
    this.gizmo.addEventListener("objectChange", () => this._onGizmoMove());
    this.gizmoHelper = this.gizmo.getHelper();
    this.scene.add(this.gizmoHelper);

    this.proxy = new THREE.Object3D();
    this.scene.add(this.proxy);

    this.selectionBox = new THREE.BoxHelper(new THREE.Object3D(), 0xffdf6a);
    this.selectionBox.visible = false;
    this.selectionBox.material.depthTest = false;
    this.selectionBox.material.transparent = true;
    this.scene.add(this.selectionBox);

    this.loader = new GLTFLoader();
    this.defPromises = new Map();
    this.defMaterials = new Map();
    this.thumbMaterials = new Map();
    this.propObjects = new Map();
    this.structureObjects = new Map();
    this.pickTargets = [];
    this.selection = null;
    this.translateSnap = 0.05;
    this.rotateSnap = THREE.MathUtils.degToRad(15);
    this.selectionMode = "translate";
    this.ladder = { diameter: 0.5, enabled: true };
    this.mode = "orbit";
    this.eyeHeight = 0.6;
    this.walk = {
      yaw: 0,
      pitch: 0,
      keys: new Set(),
      fallback: false,
      looking: false,
      last: performance.now(),
    };
    this.loading = true;
    this.needsRender = true;
    this.raycaster = new THREE.Raycaster();
    this.pointer = new THREE.Vector2();
    this.dragStart = null;
    this.lastScreenshot = null;

    this._bindPointer();
    this._bindWalkKeys();
    this._resizeObserver = new ResizeObserver(() => this.resize());
    this._resizeObserver.observe(canvas.parentElement || canvas);
    this.resize();
    this._animate = this._animate.bind(this);
    this._frame = requestAnimationFrame(this._animate);
  }

  // ------------------------------------------------------------------ #
  // setup helpers
  // ------------------------------------------------------------------ #
  _bindPointer() {
    this.canvas.addEventListener("contextmenu", (event) => {
      if (this.mode === "walk") event.preventDefault();
    });
    this.canvas.addEventListener("pointerdown", (event) => {
      if (this.mode === "walk") {
        if (event.button === 2) {
          this.walk.looking = true;
          this.canvas.setPointerCapture(event.pointerId);
          event.preventDefault();
        }
        return;
      }
      if (this.gizmo.dragging) return;
      if (event.button !== 0) return;
      this.dragStart = { x: event.clientX, y: event.clientY };
    });
    this.canvas.addEventListener("pointerup", (event) => {
      if (this.mode === "walk") {
        if (event.button === 2) {
          this.walk.looking = false;
          try {
            this.canvas.releasePointerCapture(event.pointerId);
          } catch (error) {
            /* pointer capture may already be gone */
          }
        }
        return;
      }
      if (this.gizmo.dragging) return;
      if (event.button !== 0 || !this.dragStart) return;
      const moved = Math.hypot(event.clientX - this.dragStart.x, event.clientY - this.dragStart.y);
      this.dragStart = null;
      if (moved > 5) return;
      this._pickAt(event.clientX, event.clientY);
    });
    this.canvas.addEventListener("pointermove", (event) => {
      if (this.mode === "walk") {
        if (document.pointerLockElement === this.canvas) {
          this._look(event.movementX, event.movementY);
        } else if (this.walk.looking) {
          this._look(event.movementX, event.movementY);
        }
      }
    });
    document.addEventListener("pointerlockchange", () => {
      const locked = document.pointerLockElement === this.canvas;
      this.callbacks.onStatus?.(locked ? "pointer-locked" : "pointer-unlocked");
    });
    document.addEventListener("pointerlockerror", () => {
      this.walk.fallback = true;
      this.callbacks.onStatus?.("pointer-fallback");
      this.callbacks.onError?.(
        "浏览器拒绝了指针锁定（非 localhost 的 http 页面可能受限）。已退化为「按住右键拖动看方向 + WASD」，功能不受影响。"
      );
    });
  }

  _bindWalkKeys() {
    window.addEventListener("keydown", (event) => {
      if (this.mode !== "walk") return;
      if (event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement) {
        return;
      }
      this.walk.keys.add(event.code);
      if (["KeyW", "KeyA", "KeyS", "KeyD", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Space"].includes(event.code)) {
        event.preventDefault();
      }
    });
    window.addEventListener("keyup", (event) => {
      this.walk.keys.delete(event.code);
    });
    window.addEventListener("blur", () => this.walk.keys.clear());
  }

  _look(dx, dy) {
    const sensitivity = 0.0022;
    this.walk.yaw -= dx * sensitivity;
    this.walk.pitch -= dy * sensitivity;
    this.walk.pitch = THREE.MathUtils.clamp(this.walk.pitch, -1.45, 1.45);
    this._applyWalkCamera();
  }

  _applyWalkCamera() {
    const ground = this.doc ? Number((this.doc.level || {}).groundY || 0) : 0;
    this.camera.position.y = ground + this.eyeHeight;
    this.camera.quaternion.setFromEuler(
      new THREE.Euler(this.walk.pitch, this.walk.yaw, 0, "YXZ")
    );
  }

  resize() {
    const parent = this.canvas.parentElement;
    if (!parent) return;
    const width = Math.max(1, parent.clientWidth);
    const height = Math.max(1, parent.clientHeight);
    this.renderer.setSize(width, height, false);
    this.needsRender = true;
    const aspect = width / height;
    this.persp.aspect = aspect;
    this.persp.updateProjectionMatrix();
    this.ortho.userData.aspect = aspect;
    this._updateOrthoFrustum();
  }

  _updateOrthoFrustum() {
    const aspect = this.ortho.userData.aspect || 1;
    const coverage = this.ortho.userData.coverage || 14;
    let width = coverage;
    let height = coverage;
    if (aspect >= 1) width = coverage * aspect;
    else height = coverage / aspect;
    this.ortho.left = -width / 2;
    this.ortho.right = width / 2;
    this.ortho.top = height / 2;
    this.ortho.bottom = -height / 2;
    this.ortho.updateProjectionMatrix();
  }

  // ------------------------------------------------------------------ #
  // loading a level
  // ------------------------------------------------------------------ #
  async load(state) {
    this.loading = true;
    this.state = state;
    this.doc = state.doc;
    this.derived = state.derived || { props: {}, structures: {} };
    this.manifests = state.manifests || {};
    this._clearLevel();
    this._buildWorld();
    this._buildStructures();
    await this._buildProps();
    this.selection = null;
    this.selectionBox.visible = false;
    this.gizmo.detach();
    this._applyLadderToAll();
    this._applyInitialCamera();
    this._updateBall();
    this.loading = false;
    this.needsRender = true;
    return this;
  }

  _clearLevel() {
    for (const group of this.propObjects.values()) this.scene.remove(group);
    for (const group of this.structureObjects.values()) this.scene.remove(group);
    this.propObjects.clear();
    this.structureObjects.clear();
    this.pickTargets = [];
    if (this.worldGroup) {
      this.scene.remove(this.worldGroup);
      this.worldGroup = null;
    }
  }

  _levelCenter() {
    const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
    return new THREE.Vector3(
      (bounds[0][0] + bounds[1][0]) / 2,
      0.3,
      -(bounds[0][1] + bounds[1][1]) / 2
    );
  }

  /** Unity-space ball start; a test-field cell falls back to its own pad centre. */
  _ballStart() {
    const level = this.doc?.level || {};
    if (Array.isArray(level.ballStart)) return level.ballStart;
    const ground = (this.doc?.structures || []).find((entry) => entry.role === "ground");
    if (ground && Array.isArray(ground.center)) {
      return [ground.center[0], 0.15, ground.center[2]];
    }
    return [0, 0.15, 0];
  }

  _buildWorld() {
    const group = new THREE.Group();
    this.worldGroup = group;
    this.scene.add(group);
    const center = this._levelCenter();
    const bounds = this.state.bounds || [[-10, -10], [10, 10]];
    const sizeX = bounds[1][0] - bounds[0][0];
    const sizeZ = bounds[1][1] - bounds[0][1];

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

    // A wide lawn outside the authored ground so the level does not float in space.
    const lawn = new THREE.Mesh(
      new THREE.PlaneGeometry(240, 240),
      new THREE.MeshStandardMaterial({ color: 0x667c56, roughness: 1.0, metalness: 0 })
    );
    lawn.rotation.x = -Math.PI / 2;
    lawn.position.set(center.x, -0.035, center.z);
    lawn.receiveShadow = true;
    group.add(lawn);

    const grid = new THREE.GridHelper(Math.max(sizeX, sizeZ), Math.max(2, Math.round(Math.max(sizeX, sizeZ))), 0x5d7a52, 0x6f8c63);
    grid.position.set(center.x, 0.006, center.z);
    grid.material.transparent = true;
    grid.material.opacity = 0.28;
    group.add(grid);

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

    // Ball start / player spawn markers. A test-field cell has no ballStart; its
    // playable pad is the meaningful centre, not the world origin.
    const ballStart = toThreePosition(this._ballStart());
    const ring = new THREE.Mesh(
      new THREE.RingGeometry(0.22, 0.3, 32),
      new THREE.MeshBasicMaterial({ color: 0xffa53b, side: THREE.DoubleSide, transparent: true, opacity: 0.85 })
    );
    ring.rotation.x = -Math.PI / 2;
    ring.position.set(ballStart.x, 0.012, ballStart.z);
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
    const material = this._structureMaterial(role);
    const mesh = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), material);
    mesh.scale.set(size[0], size[1], size[2]);
    mesh.position.set(0, 0, 0);
    if (role === "ground" || role === "boundary") {
      // The Unity importer's visible body is the authored box shifted half a
      // thickness along authored -Y, so the authored +Y face IS the plane.
      // (PlacementImporter.TryComputePlaneBodyPose.)
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
    this.callbacks.onProgress?.(`摆放 ${props.length} 件道具…`);
    for (const prop of props) {
      const group = await this._makeProp(prop);
      this.propObjects.set(prop.itemId, group);
      this.scene.add(group);
      this.pickTargets.push(group);
    }
    this.callbacks.onProgress?.("");
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
    return group;
  }

  loadDef(def) {
    if (!this.defPromises.has(def)) {
      const promise = this.loader
        .loadAsync(`/api/models/${encodeURIComponent(def)}.glb`)
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
          return scene;
        });
      this.defPromises.set(def, promise);
    }
    return this.defPromises.get(def);
  }

  /**
   * The Unity prefab's material colour is the manifest's linear RGB (PropArt reads
   * the same table).  Using the manifest instead of the GLB material keeps the web
   * view and Unity the same colour even if an exporter tweaks baseColorFactor.
   */
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
  // camera presets and modes
  // ------------------------------------------------------------------ #
  _applyInitialCamera() {
    // A fitted 3/4 view is the useful first look at a level; the level's own
    // cameras are one click away (they are authored for Unity screenshots, and
    // can be far away on purpose).
    this.fitLevelView();
  }

  fitLevelView() {
    if (this.mode === "walk") this.exitWalk();
    this._usePerspective();
    const bounds = this.state?.bounds || [[-10, -10], [10, 10]];
    let width = bounds[1][0] - bounds[0][0];
    let depth = bounds[1][1] - bounds[0][1];
    let center = this._levelCenter();
    // Fit the playable pad, not the registration envelope: a physics-test-field cell
    // sits in a 28 x 31 m field but is only 6 x 10 m.
    const groundGroup = [...this.structureObjects.values()]
      .find((group) => group.userData.role === "ground");
    if (groundGroup) {
      groundGroup.updateMatrixWorld(true);
      const box = new THREE.Box3().setFromObject(groundGroup);
      const size = box.getSize(new THREE.Vector3());
      if (size.x > 0.1 && size.z > 0.1) {
        width = size.x;
        depth = size.z;
        center = box.getCenter(new THREE.Vector3());
      }
    }
    center.y = 0.8;
    const radius = 0.5 * Math.hypot(width, depth);
    const fov = 40;
    this.persp.fov = fov;
    this.persp.updateProjectionMatrix();
    const distance = (radius / Math.tan(THREE.MathUtils.degToRad(fov / 2))) * 0.9;
    const direction = new THREE.Vector3(0.8, 0.62, 1.0).normalize();
    this.controls.target.copy(center);
    this.persp.position.copy(center).addScaledVector(direction, distance);
    this.persp.up.set(0, 1, 0);
    this.persp.lookAt(center);
    this.controls.update();
    this.currentCamera = "__fit";
    this.needsRender = true;
    this.callbacks.onStatus?.("camera:fit");
    this.renderNow();
    return true;
  }

  _usePerspective() {
    this.camera = this.persp;
    this.gizmo.camera = this.persp;
    this.controls.object = this.persp;
    this.controls.enableRotate = true;
  }

  _useOrtho() {
    this.camera = this.ortho;
    this.gizmo.camera = this.ortho;
    this.controls.object = this.ortho;
    this.controls.enableRotate = false;
  }

  setCameraPreset(id) {
    const camera = (this.doc.cameras || []).find((entry) => entry.id === id);
    if (!camera) return false;
    if (this.mode === "walk") this.exitWalk();
    if (camera.type === "ortho_top") {
      const aspect = this.persp.aspect || 1;
      this.ortho.userData.coverage = Number(camera.coverage_m || 12) * 1.08;
      this.ortho.userData.aspect = aspect;
      this.ortho.up.set(0, 0, -1);
      this._updateOrthoFrustum();
      this._useOrtho();
      this.ortho.position.copy(toThreePosition(camera.location));
      const target = toThreePosition(camera.target || [
        camera.location[0], 0, camera.location[2],
      ]);
      this.controls.target.copy(target);
      this.ortho.lookAt(target);
      this.ortho.zoom = 1;
      this.ortho.updateProjectionMatrix();
      this.controls.update();
    } else {
      this._usePerspective();
      const aspect = this.persp.aspect || 1;
      const lens = Number(camera.lens || 45);
      const horizontal = 2 * Math.atan(18 / lens);
      this.persp.fov = THREE.MathUtils.radToDeg(2 * Math.atan(Math.tan(horizontal / 2) / aspect));
      this.persp.updateProjectionMatrix();
      this.persp.position.copy(toThreePosition(camera.location));
      this.persp.up.set(0, 1, 0);
      const target = toThreePosition(camera.target || [0, 0, 0]);
      this.controls.target.copy(target);
      this.persp.lookAt(target);
      this.controls.update();
    }
    this.currentCamera = id;
    this.needsRender = true;
    this.callbacks.onStatus?.(`camera:${id}`);
    this.renderNow();
    return true;
  }

  cameraButtonNames() {
    const labels = {
      overview_3q: "3/4 视角",
      top: "俯视",
      ball_eye: "球眼视角",
    };
    return (this.doc?.cameras || []).map((camera) => ({
      id: camera.id,
      label: labels[camera.id] || camera.id,
    }));
  }

  enterWalk(options = {}) {
    if (this.mode === "walk") return;
    this.orbitSave = {
      position: this.camera.position.clone(),
      target: this.controls.target.clone(),
      quaternion: this.camera.quaternion.clone(),
    };
    const previousPosition = this.camera.position.clone();
    const previousTarget = this.controls.target.clone();
    this._usePerspective();
    this.persp.position.copy(previousPosition);
    this.persp.lookAt(previousTarget);
    this.mode = "walk";
    this.controls.enabled = false;
    this.gizmo.enabled = false;
    this.gizmoHelper.visible = false;
    this.selectionBox.visible = false;
    if (this.ballGhost) this.ballGhost.visible = false;
    this.walk.fallback = false;
    const direction = new THREE.Vector3();
    this.camera.getWorldDirection(direction);
    this.walk.pitch = Math.asin(THREE.MathUtils.clamp(direction.y, -1, 1));
    this.walk.yaw = Math.atan2(-direction.x, -direction.z);

    // Starting the walk from a far orbit camera would drop the player outside the
    // arena at eye height.  If the camera is not over the level, start from the
    // level's authored ball-eye camera (else the ball start) looking into the map.
    const bounds = this.state?.bounds || [[-1e9, -1e9], [1e9, 1e9]];
    const position = this.camera.position;
    const inside = position.x > bounds[0][0] - 0.5 && position.x < bounds[1][0] + 0.5
      && position.z > -bounds[1][1] - 0.5 && position.z < -bounds[0][1] + 0.5;
    if (options.start || !inside) {
      const level = this.doc.level || {};
      // The player spawn is where a person actually starts in this level; the ball
      // start is the fallback for a test-field cell that only has a pad.
      const spawn = Array.isArray(level.playerSpawn) ? level.playerSpawn : null;
      const ball = toThreePosition(this._ballStart());
      const anchor = spawn ? toThreePosition(spawn)
        : new THREE.Vector3(ball.x, 0, ball.z);
      this.camera.position.set(anchor.x, 0, anchor.z);
      const toward = new THREE.Vector3(ball.x - anchor.x, 0, ball.z - anchor.z);
      if (toward.lengthSq() < 1e-6) {
        const center = this._levelCenter();
        toward.set(center.x - anchor.x, 0, center.z - anchor.z);
      }
      if (toward.lengthSq() > 1e-6) {
        this.walk.yaw = Math.atan2(-toward.x, -toward.z);
        this.walk.pitch = -0.1;
      }
    }
    this._applyWalkCamera();
    this.needsRender = true;
    this.callbacks.onStatus?.("walk");
    try {
      const request = this.canvas.requestPointerLock?.();
      if (request && typeof request.catch === "function") {
        request.catch(() => {
          this.walk.fallback = true;
          this.callbacks.onStatus?.("pointer-fallback");
        });
      }
    } catch (error) {
      this.walk.fallback = true;
      this.callbacks.onStatus?.("pointer-fallback");
    }
  }

  exitWalk() {
    if (this.mode !== "walk") return;
    this.mode = "orbit";
    if (document.pointerLockElement === this.canvas) document.exitPointerLock();
    this.walk.keys.clear();
    this.walk.looking = false;
    this.controls.enabled = true;
    if (this.orbitSave) {
      this._usePerspective();
      this.persp.position.copy(this.orbitSave.position);
      this.persp.quaternion.copy(this.orbitSave.quaternion);
      this.controls.target.copy(this.orbitSave.target);
      this.controls.update();
    }
    if (this.selection) this.selectionBox.visible = true;
    if (this.ballGhost) this.ballGhost.visible = true;
    this.gizmo.enabled = true;
    this.gizmoHelper.visible = Boolean(this.selection);
    this.needsRender = true;
    this.callbacks.onStatus?.("orbit");
  }

  setMode(mode) {
    if (mode === "walk") this.enterWalk();
    else this.exitWalk();
  }

  setEyeHeight(height) {
    this.eyeHeight = Math.max(0.05, Number(height) || 0.6);
    if (this.mode === "walk") this._applyWalkCamera();
  }

  // ------------------------------------------------------------------ #
  // selection and gizmos
  // ------------------------------------------------------------------ #
  _pickAt(clientX, clientY) {
    const rect = this.canvas.getBoundingClientRect();
    this.pointer.set(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1
    );
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const hits = this.raycaster.intersectObjects(this.pickTargets, true);
    for (const hit of hits) {
      let object = hit.object;
      while (object && !object.userData?.type) object = object.parent;
      if (!object) continue;
      if (object.userData.type === "structure" && object.userData.role === "ground") continue;
      this.select(object.userData.id);
      return;
    }
    this.select(null);
  }

  objectGroup(id) {
    return this.propObjects.get(id) || this.structureObjects.get(id) || null;
  }

  /**
   * Where a palette drop would land: the first surface under the pointer, the
   * owning object (for `support`/`stackedOn`) and the Unity-space point.
   */
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
      target: object ? { type: object.userData.type, id: object.userData.id,
                         role: object.userData.role } : null,
    };
  }

  select(id, options = {}) {
    const group = id ? this.objectGroup(id) : null;
    if (!group) {
      this.selection = null;
      this.selectionBox.visible = false;
      this.gizmo.detach();
      this.callbacks.onSelect?.(null);
      return null;
    }
    const type = group.userData.type;
    this.selection = {
      type,
      id,
      group,
      base: {
        position: group.position.clone(),
        quaternion: group.quaternion.clone(),
      },
    };
    this.proxy.position.copy(group.position);
    this.proxy.quaternion.copy(group.quaternion);
    this.proxy.scale.set(1, 1, 1);
    this.gizmo.attach(this.proxy);
    this.gizmoHelper.visible = this.mode !== "walk";
    this.gizmo.setMode(this.selectionMode);
    this.selectionBox.setFromObject(group);
    this.selectionBox.visible = this.mode !== "walk";
    this.callbacks.onSelect?.(this.selectionInfo());
    if (options.focus) this.focusObject(id);
    this.renderNow();
    return this.selection;
  }

  selectionInfo() {
    if (!this.selection) return null;
    const { type, id, group } = this.selection;
    const data = group.userData.data;
    const derived = type === "prop"
      ? this.derived.props[id]
      : this.derived.structures[id];
    return {
      type,
      id,
      def: data.def,
      kind: derived?.kind || data.kind,
      zone: data.zone,
      support: data.support,
      stackedOn: data.stackedOn,
      valueUnits: derived?.valueUnits ?? data.valueUnits,
      aabb: data.aabb,
      size: data.size,
      center: data.center,
      role: data.role,
      dReq: derived?.dReq,
      worldAabb: derived?.worldAabb,
      position: [Number(group.position.x.toFixed(4)), Number(group.position.y.toFixed(4)), Number(-group.position.z.toFixed(4))],
      rotationQuat: fromThreeQuaternion(group.quaternion),
    };
  }

  focusObject(id) {
    const group = this.objectGroup(id);
    if (!group) return;
    this.needsRender = true;
    if (this.mode === "walk") this.exitWalk();
    const box = new THREE.Box3().setFromObject(group);
    const center = box.getCenter(new THREE.Vector3());
    const size = Math.max(0.25, box.getSize(new THREE.Vector3()).length());
    const direction = this.camera.position.clone().sub(this.controls.target).normalize();
    if (direction.lengthSq() < 1e-6) direction.set(1, 1, 1).normalize();
    if (this.camera === this.ortho) {
      this.controls.target.copy(center);
      this.ortho.position.copy(center).add(new THREE.Vector3(0, 8, 0));
      this.controls.update();
    } else {
      this.controls.target.copy(center);
      this.persp.position.copy(center).add(direction.multiplyScalar(size * 2.1));
      this.controls.update();
    }
  }

  setSelectionMode(mode) {
    this.selectionMode = mode === "rotate" ? "rotate" : "translate";
    this.gizmo.setMode(this.selectionMode);
  }

  setSnapping({ translate, rotate } = {}) {
    if (translate !== undefined) this.translateSnap = Number(translate) || 0;
    if (rotate !== undefined) this.rotateSnap = THREE.MathUtils.degToRad(Number(rotate) || 0);
    this.gizmo.translationSnap = this.translateSnap || null;
    this.gizmo.rotationSnap = this.rotateSnap || null;
  }

  _onGizmoMove() {
    if (!this.selection) return;
    this.needsRender = true;
    const { group } = this.selection;
    group.position.copy(this.proxy.position);
    group.quaternion.copy(this.proxy.quaternion);
    this.selectionBox.setFromObject(group);
  }

  async _commitTransform() {
    if (!this.selection) return;
    const { group, base } = this.selection;
    const position = fromThreePosition(group.position);
    const quaternion = fromThreeQuaternion(group.quaternion);
    const moved = group.position.distanceTo(base.position) > 1e-6;
    const rotated = Math.abs(group.quaternion.dot(base.quaternion)) < 0.999999;
    if (!moved && !rotated) {
      this.selectionBox.setFromObject(group);
      return;
    }
    const info = this.selectionInfo();
    const ok = await this.callbacks.onTransform?.(info, position, quaternion);
    if (ok) {
      this.selection.base = {
        position: group.position.clone(),
        quaternion: group.quaternion.clone(),
      };
    } else {
      group.position.copy(base.position);
      group.quaternion.copy(base.quaternion);
      this.proxy.position.copy(base.position);
      this.proxy.quaternion.copy(base.quaternion);
      this.selectionBox.setFromObject(group);
    }
  }

  /** Drop the selection onto the highest surface below its current bottom. */
  restOnSurface() {
    if (!this.selection) return null;
    this.needsRender = true;
    const { group } = this.selection;
    const box = new THREE.Box3().setFromObject(group);
    const halfHeight = (box.max.y - box.min.y) / 2;
    const centerY = (box.max.y + box.min.y) / 2;
    const origin = new THREE.Vector3((box.min.x + box.max.x) / 2, box.min.y - 0.002,
      (box.min.z + box.max.z) / 2);
    const ray = new THREE.Raycaster(origin, new THREE.Vector3(0, -1, 0));
    const targets = [];
    for (const structure of this.structureObjects.values()) {
      if (structure.userData.role !== "ground") targets.push(structure);
    }
    for (const prop of this.propObjects.values()) {
      if (prop !== group) targets.push(prop);
    }
    const hits = ray.intersectObjects(targets, true);
    const ground = Number((this.doc.level || {}).groundY || 0);
    const surface = hits.length ? hits[0].point.y : ground;
    group.position.y = surface + halfHeight;
    this.proxy.position.copy(group.position);
    this.selectionBox.setFromObject(group);
    this._commitTransform();
    return { surface, y: group.position.y };
  }

  dropToGround() {
    if (!this.selection) return;
    const { group } = this.selection;
    const box = new THREE.Box3().setFromObject(group);
    const halfHeight = (box.max.y - box.min.y) / 2;
    const ground = Number((this.doc.level || {}).groundY || 0);
    group.position.y = ground + halfHeight;
    this.proxy.position.copy(group.position);
    this.selectionBox.setFromObject(group);
    this._commitTransform();
  }

  nudge(dx, dy, dz) {
    if (!this.selection) return;
    const { group } = this.selection;
    group.position.x += dx;
    group.position.y += dy;
    group.position.z += dz;
    this.proxy.position.copy(group.position);
    this.selectionBox.setFromObject(group);
    this._commitTransform();
  }

  // ------------------------------------------------------------------ #
  // ladder colouring
  // ------------------------------------------------------------------ #
  setLadder(diameter, enabled = true) {
    this.ladder = { diameter, enabled };
    this._applyLadderToAll();
    this._updateBall();
    this.needsRender = true;
  }

  _tierFor(dReq) {
    const max = Number((this.doc.level || {}).ballDiameterMax || 2.2);
    if (dReq > max + 1e-9) return "grey";
    if (dReq <= this.ladder.diameter + 1e-9) return "green";
    if (dReq <= this.ladder.diameter * 1.2 + 1e-9) return "yellow";
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

  _applyLadderToAll() {
    for (const [id, group] of this.propObjects) {
      const derived = this.derived.props[id];
      const tier = derived ? this._tierFor(derived.dReq) : "grey";
      group.userData.tier = tier;
      group.traverse((object) => {
        if (!object.isMesh) return;
        if (this.ladder.enabled) {
          object.material = this._tierMaterial(tier);
        } else if (object.userData.baseMaterial) {
          object.material = object.userData.baseMaterial;
        }
      });
    }
  }

  _updateBall() {
    if (!this.ballGhost) return;
    const radius = Math.max(0.02, this.ladder.diameter / 2);
    const start = toThreePosition(this._ballStart());
    const ground = Number((this.doc.level || {}).groundY || 0);
    this.ballGhost.scale.setScalar(radius / 0.5);
    this.ballGhost.position.set(start.x, ground + radius, start.z);
  }

  // ------------------------------------------------------------------ #
  // state refresh (after saves, undo, another tab)
  // ------------------------------------------------------------------ #
  async applyState(state) {
    const oldLevel = this.doc?.level?.id;
    const newLevel = state.doc?.level?.id;
    if (!this.doc || oldLevel !== newLevel) {
      await this.load(state);
      return;
    }
    this.state = state;
    this.doc = state.doc;
    this.derived = state.derived || { props: {}, structures: {} };
    this.manifests = state.manifests || this.manifests;

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

    this._applyLadderToAll();
    this._updateBall();
    this.needsRender = true;
    if (this.selection && !this.objectGroup(this.selection.id)) {
      this.select(null);
    } else if (this.selection) {
      const group = this.objectGroup(this.selection.id);
      this.selection.group = group;
      this.selection.base = {
        position: group.position.clone(),
        quaternion: group.quaternion.clone(),
      };
      this.proxy.position.copy(group.position);
      this.proxy.quaternion.copy(group.quaternion);
      this.selectionBox.setFromObject(group);
      this.callbacks.onSelect?.(this.selectionInfo());
    }
  }

  // ------------------------------------------------------------------ #
  // thumbnails
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

  // ------------------------------------------------------------------ #
  // screenshot and run loop
  // ------------------------------------------------------------------ #
  renderNow() {
    if (this.loading || this.noRender) return;
    this.renderer.render(this.scene, this.camera);
    this.needsRender = false;
  }

  screenshot() {
    this.renderer.render(this.scene, this.camera);
    const level = this.doc?.level?.id || "level";
    const stamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    const name = `${level}_${this.currentCamera || this.mode}_${stamp}.png`;
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

  _animate(now) {
    this._frame = requestAnimationFrame(this._animate);
    const delta = Math.min(0.05, (now - (this.walk.last || now)) / 1000);
    this.walk.last = now;
    if (this.mode === "walk") {
      const speed = this.walk.keys.has("ShiftLeft") || this.walk.keys.has("ShiftRight") ? 5.5 : 1.7;
      let forward = 0;
      let right = 0;
      if (this.walk.keys.has("KeyW")) forward += 1;
      if (this.walk.keys.has("KeyS")) forward -= 1;
      if (this.walk.keys.has("KeyD")) right += 1;
      if (this.walk.keys.has("KeyA")) right -= 1;
      if (forward || right) {
        const length = Math.hypot(forward, right) || 1;
        const yaw = this.walk.yaw;
        const dx = (-Math.sin(yaw) * forward + Math.cos(yaw) * right) / length;
        const dz = (-Math.cos(yaw) * forward - Math.sin(yaw) * right) / length;
        this.camera.position.x += dx * speed * delta;
        this.camera.position.z += dz * speed * delta;
      }
      this._applyWalkCamera();
    } else {
      if (this.controls.update()) this.needsRender = true;
    }
    if (!this.loading && !this.noRender && (this.needsRender || this.mode === "walk")) {
      this.renderer.render(this.scene, this.camera);
      this.needsRender = false;
    }
  }

  /**
   * Coordinate audit: every rendered prop Box3 against the server's world AABB
   * (computed by placement_core.world_aabb, the same function the validator uses).
   * The discrepancy is in metres; anything over `tolerance` is listed.
   */
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
      if (error > tolerance) problems.push({ id, error: Number(error.toFixed(4)),
        rendered: threeBoxToUnity(box), expected });
    };
    for (const [id, group] of this.propObjects) {
      check(id, group, this.derived.props[id]?.worldAabb);
    }
    for (const [id, group] of this.structureObjects) {
      const role = group.userData.role;
      if (role === "ground" || role === "boundary") continue; // the visible body is offset by design
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
    if (!this.selection) return null;
    this.selection.group.updateMatrixWorld(true);
    return threeBoxToUnity(new THREE.Box3().setFromObject(this.selection.group));
  }

  dispose() {
    cancelAnimationFrame(this._frame);
    this._resizeObserver.disconnect();
  }
}

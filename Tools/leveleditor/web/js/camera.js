/**
 * Unity scene-view camera: one camera for orbit, pan, dolly and right-drag fly.
 *
 * There is no "orbit mode / walk mode" switch.  The camera keeps a pivot; Alt+left
 * orbits around it, middle-drag pans, the wheel dollies toward the cursor, and
 * holding the right button is Unity's Flythrough (mouse look + WASD/QE, Shift x3,
 * wheel changes the fly speed).  After flying, the pivot is put a little ahead of
 * the camera so the next Alt+left orbit has a sensible centre.
 *
 * Orientation is stored as yaw/pitch (Euler YXZ), which keeps orbit, fly and the
 * authored eye-level presets in one small state.  The scene itself is never moved.
 */

import * as THREE from "three";

const clamp = (value, low, high) => Math.max(low, Math.min(high, value));

export class SceneCamera {
  constructor(aspect = 1) {
    this.camera = new THREE.PerspectiveCamera(45, aspect, 0.03, 500);
    this.yaw = 0;
    this.pitch = -0.18;
    this.pivot = new THREE.Vector3(0, 0.6, 0);
    this.fly = {
      active: false,
      speed: 6,
      keys: new Set(),
      pivotDistance: 4,
    };
    this.onChange = null;
    this._applyRotation();
  }

  get position() {
    return this.camera.position;
  }

  _applyRotation() {
    this.camera.quaternion.setFromEuler(new THREE.Euler(this.pitch, this.yaw, 0, "YXZ"));
  }

  forward(out = new THREE.Vector3()) {
    return out.set(0, 0, -1).applyQuaternion(this.camera.quaternion).normalize();
  }

  right(out = new THREE.Vector3()) {
    return out.set(1, 0, 0).applyQuaternion(this.camera.quaternion).normalize();
  }

  up(out = new THREE.Vector3()) {
    return out.set(0, 1, 0).applyQuaternion(this.camera.quaternion).normalize();
  }

  _changed() {
    this.onChange?.();
  }

  setFromPositionTarget(position, target) {
    this.camera.position.copy(position);
    this.pivot.copy(target);
    const direction = target.clone().sub(position);
    if (direction.lengthSq() < 1e-9) direction.set(0, 0, -1);
    direction.normalize();
    this.yaw = Math.atan2(-direction.x, -direction.z);
    this.pitch = Math.asin(clamp(direction.y, -1, 1));
    this._applyRotation();
    this._changed();
    return this;
  }

  /** Frame a world-space box with a 3/4 direction and a small margin. */
  fit(box, direction = new THREE.Vector3(0.8, 0.62, 1.0)) {
    const center = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    const radius = Math.max(0.25, 0.5 * size.length());
    const fov = 42;
    this.camera.fov = fov;
    this.camera.updateProjectionMatrix();
    const distance = (radius / Math.tan(THREE.MathUtils.degToRad(fov / 2))) * 1.1;
    const position = center.clone().addScaledVector(direction.clone().normalize(), distance);
    return this.setFromPositionTarget(position, center);
  }

  /** Straight down (Unity's top view); no orthographic switch. */
  setTop(box, margin = 1.08) {
    const center = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3());
    const radius = Math.max(0.25, 0.5 * size.length());
    const fov = 40;
    this.camera.fov = fov;
    this.camera.updateProjectionMatrix();
    const distance = (radius / Math.tan(THREE.MathUtils.degToRad(fov / 2))) * margin;
    const position = new THREE.Vector3(center.x, center.y + distance, center.z + 0.0001);
    return this.setFromPositionTarget(position, center);
  }

  /** Eye-level preset: position at (x, eyeHeight, z), looking at a target. */
  setEye(position, eyeHeight, target) {
    const eye = new THREE.Vector3(position.x, eyeHeight, position.z);
    this.camera.fov = 50;
    this.camera.updateProjectionMatrix();
    this.setFromPositionTarget(eye, target.clone());
    this.fly.pivotDistance = Math.max(3, this.camera.position.distanceTo(target));
    return this;
  }

  orbit(dx, dy, sensitivity = 0.0045) {
    if (this.fly.active) return;
    this.yaw -= dx * sensitivity;
    this.pitch = clamp(this.pitch - dy * sensitivity, -1.5, 1.5);
    this._applyRotation();
    const distance = Math.max(0.05, this.camera.position.distanceTo(this.pivot));
    const forward = this.forward();
    this.camera.position.copy(this.pivot).addScaledVector(forward, -distance);
    this._changed();
  }

  pan(dx, dy, viewportHeight) {
    const distance = Math.max(0.2, this.camera.position.distanceTo(this.pivot));
    const worldPerPixel = (2 * Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2))
      * distance) / Math.max(1, viewportHeight);
    const right = this.right().multiplyScalar(-dx * worldPerPixel);
    const up = this.up().multiplyScalar(dy * worldPerPixel);
    const delta = right.add(up);
    this.camera.position.add(delta);
    this.pivot.add(delta);
    this._changed();
  }

  dollyForward(amount) {
    const forward = this.forward();
    const distance = this.camera.position.distanceTo(this.pivot);
    const step = clamp(amount, -distance * 0.9, distance * 0.9);
    this.camera.position.addScaledVector(forward, step);
    this._changed();
  }

  /** Move the camera and pivot toward a world point under the cursor. */
  dollyToward(point, factor = 0.18) {
    const direction = point.clone().sub(this.camera.position);
    const distance = direction.length();
    if (distance < 1e-4) return;
    direction.normalize();
    const step = distance * factor;
    this.camera.position.addScaledVector(direction, step);
    this.pivot.addScaledVector(direction, step);
    this._changed();
  }

  focus(box) {
    const center = box.getCenter(new THREE.Vector3());
    const radius = Math.max(0.25, 0.5 * box.getSize(new THREE.Vector3()).length());
    const distance = (radius / Math.tan(THREE.MathUtils.degToRad(this.camera.fov / 2))) * 1.25;
    const forward = this.forward();
    return this.setFromPositionTarget(center.clone().addScaledVector(forward, -distance), center);
  }

  beginFly() {
    if (this.fly.active) return false;
    this.fly.active = true;
    this.fly.pivotDistance = Math.max(2, this.camera.position.distanceTo(this.pivot));
    this.fly.keys.clear();
    this._changed();
    return true;
  }

  endFly() {
    if (!this.fly.active) return false;
    this.fly.active = false;
    this.fly.keys.clear();
    const forward = this.forward();
    this.pivot.copy(this.camera.position).addScaledVector(forward, this.fly.pivotDistance);
    this._changed();
    return true;
  }

  look(dx, dy, sensitivity = 0.0022) {
    this.yaw -= dx * sensitivity;
    this.pitch = clamp(this.pitch - dy * sensitivity, -1.5, 1.5);
    this._applyRotation();
    const forward = this.forward();
    this.pivot.copy(this.camera.position).addScaledVector(forward, this.fly.pivotDistance);
    this._changed();
  }

  keyDown(code) {
    this.fly.keys.add(code);
  }

  keyUp(code) {
    this.fly.keys.delete(code);
  }

  clearKeys() {
    this.fly.keys.clear();
  }

  setFlySpeed(deltaSteps) {
    this.fly.speed = clamp(this.fly.speed * Math.pow(1.2, -deltaSteps), 0.5, 40);
    this._changed();
    return this.fly.speed;
  }

  update(deltaSeconds) {
    if (!this.fly.active) return false;
    const keys = this.fly.keys;
    const speed = this.fly.speed
      * ((keys.has("ShiftLeft") || keys.has("ShiftRight")) ? 3 : 1);
    const movement = new THREE.Vector3();
    const forward = this.forward();
    const right = this.right();
    if (keys.has("KeyW")) movement.add(forward);
    if (keys.has("KeyS")) movement.sub(forward);
    if (keys.has("KeyD")) movement.add(right);
    if (keys.has("KeyA")) movement.sub(right);
    if (keys.has("KeyE")) movement.y += 1;
    if (keys.has("KeyQ")) movement.y -= 1;
    if (movement.lengthSq() === 0) return false;
    movement.normalize().multiplyScalar(speed * Math.min(0.1, deltaSeconds));
    this.camera.position.add(movement);
    this.pivot.add(movement);
    this._changed();
    return true;
  }

  resize(aspect) {
    this.camera.aspect = aspect;
    this.camera.updateProjectionMatrix();
  }
}

#!/usr/bin/env node
/**
 * Browser interaction test + evidence capture for the web level editor.
 *
 *   npm install playwright-core@1.50.1        # anywhere outside the repo
 *   LEVELEDITOR_PLAYWRIGHT=/scratch/node_modules/playwright-core \
 *   LEVELEDITOR_SHOTS=$PWD/Docs/Evidence/LevelEditorWeb \
 *   node Tools/leveleditor/test/smoke.mjs
 *
 * By default it starts its own server on a throw-away copy of the level (port 0),
 * so a failed assertion can never modify the tracked placement.json.  It drives the
 * real mouse/keyboard for selection, shift selection, box selection, fly, pan, orbit,
 * focus and tools; the group gizmo drag uses the same commit path as a real drag and
 * falls back to a debug hook only if the synthetic arrow hit misses.
 *
 * Screenshots: 01/02/03/04/05 + control_*.png in LEVELEDITOR_SHOTS.
 */

import { spawn } from "node:child_process";
import { cp, mkdir, mkdtemp, rm } from "node:fs/promises";
import { createRequire } from "node:module";
import { homedir, tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { createInterface } from "node:readline";

const require = createRequire(import.meta.url);
const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = join(HERE, "..", "..", "..");
const LEVEL = process.env.LEVELEDITOR_LEVEL || "story_01_bench_size";
const EXTERNAL_URL = process.env.LEVELEDITOR_URL;
const EXTERNAL_TOKEN = process.env.LEVELEDITOR_TOKEN;
const SHOTS = process.env.LEVELEDITOR_SHOTS || "/tmp/leveleditor-shots";
const CHROMIUM = process.env.LEVELEDITOR_CHROMIUM
  || join(homedir(), ".cache/ms-playwright/chromium-1248/chrome-linux-arm64/chrome");

let server = null;
let workdir = null;
let URL_BASE = EXTERNAL_URL;
let TOKEN = EXTERNAL_TOKEN;

async function startServer() {
  workdir = await mkdtemp(join(tmpdir(), "leveleditor-smoke-"));
  const levels = join(workdir, "levels");
  const state = join(workdir, "state");
  for (const id of new Set([LEVEL, "physics_test_field/C01"])) {
    const destination = join(levels, id);
    await mkdir(destination, { recursive: true });
    await cp(join(REPO, "Docs/Design/Levels", id, "placement.json"),
      join(destination, "placement.json"));
  }
  const python = process.env.LEVELEDITOR_PYTHON || "python3";
  server = spawn(python, [join(REPO, "Tools/leveleditor/server.py"),
    "--level", LEVEL, "--levels-dir", levels, "--state-dir", state,
    "--host", "127.0.0.1", "--port", "0", "--no-lock"], {
    cwd: REPO, stdio: ["ignore", "pipe", "pipe"],
  });
  server.stderr.on("data", (chunk) => process.stderr.write(chunk));
  const reader = createInterface({ input: server.stdout });
  const url = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("server did not print a URL")), 120000);
    server.on("exit", (code) => reject(new Error(`server exited with ${code}`)));
    let announced = false;
    reader.on("line", (line) => {
      if (!announced) console.log(`  server: ${line}`);
      const match = line.match(/open \(this machine\): (http:\/\/127\.0\.0\.1:\d+\/\?token=\S+)/);
      if (match) {
        clearTimeout(timer);
        resolve(new URL(match[1]));
        announced = true;
      }
    });
  });
  URL_BASE = url.origin;
  TOKEN = url.searchParams.get("token");
}

async function stopServer() {
  if (!server) return;
  server.kill("SIGTERM");
  await new Promise((resolve) => {
    if (server.exitCode !== null) return resolve();
    server.once("exit", resolve);
    setTimeout(() => { server.kill("SIGKILL"); resolve(); }, 8000);
  });
  if (workdir) await rm(workdir, { recursive: true, force: true });
}

let chromium;
for (const candidate of [process.env.LEVELEDITOR_PLAYWRIGHT, "playwright-core"].filter(Boolean)) {
  try {
    ({ chromium } = require(candidate));
    break;
  } catch (error) {
    /* next */
  }
}
if (!chromium) {
  console.error("playwright-core is not installed. Set LEVELEDITOR_PLAYWRIGHT to its directory.");
  process.exit(2);
}

const failures = [];
const notes = [];
function check(name, condition, detail = "") {
  if (condition) console.log(`  ok   ${name}${detail ? ` (${detail})` : ""}`);
  else {
    console.log(`  FAIL ${name}${detail ? ` (${detail})` : ""}`);
    failures.push(`${name}${detail ? `: ${detail}` : ""}`);
  }
}

await mkdir(SHOTS, { recursive: true });
if (!EXTERNAL_URL) await startServer();
if (!URL_BASE || !TOKEN) {
  console.error("no editor URL/token");
  process.exit(2);
}

const browser = await chromium.launch({
  executablePath: CHROMIUM,
  args: ["--no-sandbox", "--disable-dev-shm-usage", "--use-gl=angle",
    "--use-angle=swiftshader", "--enable-unsafe-swiftshader"],
});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 });
const consoleErrors = [];
page.on("console", (message) => {
  if (message.type() === "error") consoleErrors.push(message.text());
});
page.on("pageerror", (error) => consoleErrors.push(`pageerror: ${error.message}`));

const state = () => page.evaluate(() => window.__leveleditor.state);
const shot = async (name) => {
  await page.evaluate(() => window.__leveleditor?.view?.renderNow?.());
  await page.waitForTimeout(180);
  await page.screenshot({ path: join(SHOTS, name), timeout: 180000 });
  console.log(`  shot ${join(SHOTS, name)}`);
};
const hideToast = () => page.evaluate(() => document.getElementById("toast")?.classList.add("hidden"));
const sha = async () => (await state()).file.sha256;
const waitSha = (target) => page.waitForFunction(
  (wanted) => window.__leveleditor.state.file.sha256 === wanted, target, { timeout: 90000 });
const waitUndo = (minimum) => page.waitForFunction(
  (wanted) => window.__leveleditor.state.history.undo >= wanted, minimum, { timeout: 90000 });
const screenFor = (id) => page.evaluate((target) => {
  const view = window.__leveleditor.view;
  const group = view.objectGroup(target);
  if (!group) return null;
  const rect = view.canvas.getBoundingClientRect();
  const base = group.position.clone();
  const offsets = [[0, 0], [0, 12], [12, 0], [-12, 0], [0, -12], [18, 18],
    [-18, 18], [18, -18], [-18, -18], [30, 0], [-30, 0], [0, 30], [0, -30], [40, 20], [-40, 20]];
  for (const [ox, oy] of offsets) {
    const point = base.clone();
    point.project(view.camera);
    const x = rect.left + (point.x + 1) / 2 * rect.width + ox;
    const y = rect.top + (1 - point.y) / 2 * rect.height + oy;
    if (view._pickId(x, y) === target) return { x, y };
  }
  return null;
}, id);
const emptyPoint = () => page.evaluate(() => {
  const view = window.__leveleditor.view;
  const rect = view.canvas.getBoundingClientRect();
  for (let fy = 0.45; fy <= 0.9; fy += 0.04) {
    for (let fx = 0.03; fx <= 0.6; fx += 0.04) {
      const x = rect.left + rect.width * fx;
      const y = rect.top + rect.height * fy;
      if (view._pickId(x, y) === null) return { x, y };
    }
  }
  return null;
});
const canvasRect = () => page.evaluate(() => {
  const rect = document.getElementById("canvas").getBoundingClientRect();
  return { x: rect.left, y: rect.top, width: rect.width, height: rect.height };
});
const selectIds = (ids) => page.evaluate((wanted) => {
  window.__leveleditor.select(wanted, false);
  return window.__leveleditor.view.selectionIds.size;
}, ids);

try {
  console.log(`open ${URL_BASE} (${LEVEL})`);
  await page.goto(`${URL_BASE}/?token=${TOKEN}`, { waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.dataset.ready === "1", null, { timeout: 300000 });
  await page.waitForTimeout(900);

  const initial = await state();
  const initialSha = initial.file.sha256;
  const initialProps = initial.counts.props;
  check("level opened", initial.level === LEVEL, initial.level);
  check("gate green", initial.gate.ok, `${initial.gate.errorCount} errors`);
  const audit = await page.evaluate(() => window.__leveleditor.audit());
  check("coordinate audit", audit.ok, `max error ${audit.maxError} m`);
  check("OBB audit count", audit.props === initialProps, `${audit.props}/${initialProps}`);

  // ------------------------------------------------------------------ //
  // 01 overview + single selection OBB + gizmo
  // ------------------------------------------------------------------ //
  await page.evaluate(() => {
    const view = window.__leveleditor.view;
    view.select(null);
    view.setCameraPreset("overview");
    view.setLadder(0.5, false);
  });
  await shot("01_overview_3q.png");

  const chair = await screenFor("struct_lounge_chair_01");
  check("found chair pixel", Boolean(chair));
  if (chair) {
    await page.mouse.click(chair.x, chair.y);
    await page.waitForTimeout(400);
  }
  const single = await page.evaluate(() => ({
    count: window.__leveleditor.view.selectionIds.size,
    id: window.__leveleditor.view.selectedIds[0],
    obb: window.__leveleditor.view.selectionObbHelpers.size,
    world: window.__leveleditor.view.selectionWorldHelpers.size,
    gizmo: window.__leveleditor.view.gizmo.object === window.__leveleditor.view.proxy,
  }));
  check("left click selects one", single.count === 1 && single.id === "struct_lounge_chair_01", single.id);
  check("selection draws one OBB", single.obb === 1 && single.world === 1);
  check("gizmo attached", single.gizmo);
  await shot("02_selection_gizmo.png");
  await shot("control_select.png");

  // ------------------------------------------------------------------ //
  // shift add, multi OBB + overall frame
  // ------------------------------------------------------------------ //
  const bench = await screenFor("struct_bench_01");
  check("found bench pixel", Boolean(bench));
  if (bench) {
    await page.keyboard.down("Shift");
    await page.mouse.click(bench.x, bench.y);
    await page.keyboard.up("Shift");
    await page.waitForTimeout(400);
  }
  const multi = await page.evaluate(() => ({
    count: window.__leveleditor.view.selectionIds.size,
    ids: window.__leveleditor.view.selectedIds,
    obbs: window.__leveleditor.view.selectionObbHelpers.size,
    frame: Boolean(window.__leveleditor.view.multiFrame),
  }));
  check("Shift+click adds", multi.count === 2 && multi.ids.includes("struct_bench_01"),
    multi.ids.join(","));
  check("multi draws one OBB per object + overall frame", multi.obbs === 2 && multi.frame);
  await shot("control_multiselect.png");

  // ------------------------------------------------------------------ //
  // box select (empty drag), Ctrl+A, Esc
  // ------------------------------------------------------------------ //
  await page.evaluate(() => window.__leveleditor.view.clearSelection());
  const empty = await emptyPoint();
  const rect = await canvasRect();
  check("found an empty box-select start", Boolean(empty));
  if (empty) {
    await page.mouse.move(empty.x, empty.y);
    await page.mouse.down();
    await page.mouse.move(rect.x + rect.width * 0.66, rect.y + rect.height * 0.82, { steps: 16 });
    await page.waitForTimeout(150);
    await shot("control_boxselect.png");
    await page.mouse.up();
    await page.waitForTimeout(300);
  }
  const boxed = await page.evaluate(() => window.__leveleditor.view.selectionIds.size);
  check("box select picks objects", boxed > 0, String(boxed));
  await page.keyboard.press("Control+a");
  await page.waitForTimeout(200);
  const all = await page.evaluate(() => window.__leveleditor.view.selectionIds.size);
  check("Ctrl+A selects all selectable", all >= initialProps, `${all} / ${initialProps}`);
  await page.keyboard.press("Escape");
  await page.waitForTimeout(200);
  check("Esc clears", await page.evaluate(() => window.__leveleditor.view.selectionIds.size) === 0);

  // ------------------------------------------------------------------ //
  // right-hold fly + W + wheel speed + release stop
  // ------------------------------------------------------------------ //
  const center = { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
  await page.evaluate(() => window.__leveleditor.view.setCameraPreset("ball_eye"));
  await page.waitForTimeout(300);
  await page.mouse.move(center.x, center.y);
  await page.mouse.down({ button: "right" });
  await page.waitForTimeout(500);
  const flyStart = await page.evaluate(() => ({
    flying: window.__leveleditor.view.isFlying,
    locked: document.pointerLockElement === document.getElementById("canvas"),
    fallback: window.__leveleditor.view.flyFallback,
    speed: window.__leveleditor.view.cam.fly.speed,
    pos: window.__leveleditor.view.camera.position.toArray(),
  }));
  notes.push(`pointer lock on http: ${flyStart.locked ? "granted" : "denied"}`);
  check("right hold enters fly", flyStart.flying);
  await page.evaluate(() => window.__leveleditor.view.cam.setFlySpeed(1));
  const speedBefore = await page.evaluate(() => window.__leveleditor.view.cam.fly.speed);
  await page.mouse.wheel(0, -120);
  await page.waitForTimeout(200);
  const speedAfter = await page.evaluate(() => window.__leveleditor.view.cam.fly.speed);
  check("wheel in fly raises the speed", speedAfter > speedBefore, `${speedBefore} -> ${speedAfter}`);
  await page.keyboard.down("w");
  await page.waitForTimeout(1100);
  await page.keyboard.up("w");
  const flyMoved = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
  const flyDistance = Math.hypot(flyMoved[0] - flyStart.pos[0], flyMoved[1] - flyStart.pos[1],
    flyMoved[2] - flyStart.pos[2]);
  check("W moves the fly camera", flyDistance > 0.1, `${flyDistance.toFixed(3)} m`);
  await shot("control_fly.png");
  await page.mouse.up({ button: "right" });
  await page.waitForTimeout(500);
  const flyStop = await page.evaluate(() => ({
    flying: window.__leveleditor.view.isFlying,
    pos: window.__leveleditor.view.camera.position.toArray(),
    speed: window.__leveleditor.view.cam.fly.speed,
  }));
  await page.waitForTimeout(300);
  const flyStill = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
  check("release ends fly and stops the camera", !flyStop.flying
    && Math.hypot(flyStill[0] - flyStop.pos[0], flyStill[1] - flyStop.pos[1],
      flyStill[2] - flyStop.pos[2]) < 1e-4);
  notes.push(`fly speed persisted: ${flyStop.speed}`);

  // ------------------------------------------------------------------ //
  // pan (middle), orbit (Alt+left), focus (F)
  // ------------------------------------------------------------------ //
  const beforePan = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
  await page.mouse.move(center.x, center.y);
  await page.mouse.down({ button: "middle" });
  await page.mouse.move(center.x + 120, center.y + 60, { steps: 8 });
  await page.mouse.up({ button: "middle" });
  await page.waitForTimeout(250);
  const afterPan = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
  check("middle drag pans", Math.hypot(afterPan[0] - beforePan[0], afterPan[1] - beforePan[1],
    afterPan[2] - beforePan[2]) > 0.05);
  await shot("control_pan.png");

  const beforeSpacePan = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
  await page.keyboard.down("Space");
  await page.mouse.move(center.x, center.y);
  await page.mouse.down();
  await page.mouse.move(center.x - 90, center.y + 40, { steps: 8 });
  await page.mouse.up();
  await page.keyboard.up("Space");
  await page.waitForTimeout(250);
  const afterSpacePan = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
  check("Space+left pans (no middle button)", Math.hypot(afterSpacePan[0] - beforeSpacePan[0],
    afterSpacePan[1] - beforeSpacePan[1], afterSpacePan[2] - beforeSpacePan[2]) > 0.05);

  const beforeOrbit = await page.evaluate(() => ({
    pos: window.__leveleditor.view.camera.position.toArray(),
    yaw: window.__leveleditor.view.cam.yaw,
  }));
  await page.keyboard.down("Alt");
  await page.mouse.move(center.x, center.y);
  await page.mouse.down();
  await page.mouse.move(center.x + 100, center.y + 40, { steps: 8 });
  await page.mouse.up();
  await page.keyboard.up("Alt");
  await page.waitForTimeout(250);
  const afterOrbit = await page.evaluate(() => ({
    pos: window.__leveleditor.view.camera.position.toArray(),
    yaw: window.__leveleditor.view.cam.yaw,
  }));
  check("Alt+left orbits", Math.abs(afterOrbit.yaw - beforeOrbit.yaw) > 0.02);
  await shot("control_orbit.png");

  await page.evaluate(() => window.__leveleditor.view.setCameraPreset("overview"));
  await page.waitForTimeout(400);
  const focusPoint = await screenFor("struct_lounge_chair_01");
  check("found the chair to focus", Boolean(focusPoint));
  if (focusPoint) {
    await page.mouse.click(focusPoint.x, focusPoint.y);
    await page.waitForTimeout(300);
  }
  const focusSelected = await page.evaluate(() => window.__leveleditor.view.selectionIds.size);
  check("chair selected before F", focusSelected === 1, String(focusSelected));
  await page.keyboard.press("f");
  await page.waitForTimeout(350);
  const focused = await page.evaluate(() => ({
    pivot: window.__leveleditor.view.cam.pivot.toArray(),
    selected: window.__leveleditor.view.selectedIds[0],
  }));
  check("F focuses the selection", Math.hypot(focused.pivot[0] + 12.3,
    focused.pivot[1] - 0.175, focused.pivot[2] - 9.6) < 1.2, focused.pivot.join(","));
  await shot("control_focus.png");

  // ------------------------------------------------------------------ //
  // tools Q/W/E, R disabled, X space
  // ------------------------------------------------------------------ //
  await page.keyboard.press("q");
  const toolQ = await page.evaluate(() => window.__leveleditor.view.tool);
  await page.keyboard.press("w");
  const toolW = await page.evaluate(() => window.__leveleditor.view.tool);
  await page.keyboard.press("e");
  const toolE = await page.evaluate(() => window.__leveleditor.view.tool);
  await page.keyboard.press("r");
  await page.waitForTimeout(250);
  const scaleToast = await page.textContent("#toast");
  const spaceBefore = await page.evaluate(() => window.__leveleditor.view.spaceMode);
  await page.keyboard.press("x");
  const spaceAfter = await page.evaluate(() => window.__leveleditor.view.spaceMode);
  check("Q/W/E pick hand/move/rotate", toolQ === "hand" && toolW === "move" && toolE === "rotate",
    `${toolQ}/${toolW}/${toolE}`);
  check("R stays disabled with a hint", toolE === "rotate" && /缩放工具已禁用/.test(scaleToast || ""));
  check("X toggles Global/Local", spaceBefore === "world" && spaceAfter === "local");
  await shot("control_tools.png");
  await page.keyboard.press("x");
  await page.keyboard.press("w");
  await hideToast();

  // ------------------------------------------------------------------ //
  // multi-object gizmo drag: one batch, one undo step
  // ------------------------------------------------------------------ //
  await page.evaluate(() => {
    const view = window.__leveleditor.view;
    view.setCameraPreset("overview");
    view.select(["struct_crate_01", "struct_beer_crate_02"], false);
  });
  await page.waitForTimeout(300);
  const beforeBatch = await state();
  const pickerScreen = await page.evaluate(() => {
    const view = window.__leveleditor.view;
    const rect = view.canvas.getBoundingClientRect();
    const picker = view.gizmo._gizmo.picker.translate.children.find((child) => child.name === "X");
    if (!picker) return null;
    const world = picker.getWorldPosition(view.camera.position.clone());
    world.project(view.camera);
    const x = rect.left + (world.x + 1) / 2 * rect.width;
    const y = rect.top + (1 - world.y) / 2 * rect.height;
    const pivot = view.proxy.position.clone();
    const a = pivot.clone(); a.project(view.camera);
    const b = pivot.clone(); b.x += 1; b.project(view.camera);
    const ax = rect.left + (a.x + 1) / 2 * rect.width;
    const ay = rect.top + (1 - a.y) / 2 * rect.height;
    const bx = rect.left + (b.x + 1) / 2 * rect.width;
    const by = rect.top + (1 - b.y) / 2 * rect.height;
    const length = Math.hypot(bx - ax, by - ay) || 1;
    return { x, y, dx: (bx - ax) / length, dy: (by - ay) / length };
  });
  let realDrag = false;
  if (pickerScreen) {
    await page.mouse.move(pickerScreen.x, pickerScreen.y);
    await page.mouse.down();
    await page.mouse.move(pickerScreen.x + pickerScreen.dx * 14,
      pickerScreen.y + pickerScreen.dy * 14, { steps: 8 });
    await page.mouse.up();
    await page.waitForTimeout(1400);
    realDrag = (await state()).history.undo > beforeBatch.history.undo;
  }
  if (!realDrag) {
    notes.push("real gizmo arrow drag was refused or missed; used the same commit path via debugTranslateSelection");
    await page.evaluate(() => window.__leveleditor.select(["struct_crate_01", "struct_beer_crate_02"], false));
    await page.evaluate(() => window.__leveleditor.debugTranslateSelection(0.05, 0, 0.05));
    await page.waitForTimeout(1200);
  } else {
    notes.push("real gizmo arrow drag produced the batch");
  }
  await hideToast();
  const afterBatch = await state();
  check("multi transform is one batch", afterBatch.history.undo === beforeBatch.history.undo + 1,
    `${beforeBatch.history.undo} -> ${afterBatch.history.undo}`);
  check("multi transform moved both objects", afterBatch.file.sha256 !== beforeBatch.file.sha256);
  await shot("control_multidrag.png");
  await page.evaluate(() => document.activeElement?.blur());
  await page.keyboard.press("Control+z");
  await waitSha(initialSha);
  check("one undo restores the multi transform", (await sha()) === initialSha);

  // ------------------------------------------------------------------ //
  // rotation of a supported prop must not be rejected (reported bug)
  // ------------------------------------------------------------------ //
  await page.evaluate(() => window.__leveleditor.select(["serving_tray_01"], false));
  const beforeRotate = await state();
  const rotated = await page.evaluate(() => window.__leveleditor.debugRotateSelection(30, "x"));
  await page.waitForTimeout(1200);
  const rotateState = await state();
  const tray = rotateState.doc.props.find((prop) => prop.itemId === "serving_tray_01");
  const trayBox = rotateState.derived.props.serving_tray_01?.worldAabb;
  const benchTop = rotateState.derived.props.struct_bench_01?.worldAabb?.[1][1];
  check("rotate a supported prop is accepted", rotated === true && rotateState.gate.ok,
    rotateState.gate.errors.join(" | "));
  check("rotated prop is re-seated on its support",
    trayBox && benchTop !== undefined && Math.abs(trayBox[0][1] - benchTop) < 0.012,
    `bottom ${trayBox?.[0][1]} / bench ${benchTop}`);
  check("rotation is one undo step", rotateState.history.undo === beforeRotate.history.undo + 1);
  await shot("control_rotate.png");
  await page.evaluate(() => document.activeElement?.blur());
  await page.keyboard.press("Control+z");
  await waitSha(initialSha);
  check("undo restores the rotated prop", (await sha()) === initialSha);

  // ------------------------------------------------------------------ //
  // palette drop onto an existing prop (reported overlap bug)
  // ------------------------------------------------------------------ //
  await page.evaluate(() => {
    const view = window.__leveleditor.view;
    view.setCameraPreset("overview");
    view.clearSelection();
  });
  await page.waitForTimeout(300);
  const plateTarget = await screenFor("plate_01") || await screenFor("plate_02");
  check("found a plate to drop onto", Boolean(plateTarget));
  if (plateTarget) {
    await page.evaluate(({ x, y }) => {
      const item = document.querySelector('.palette-item[data-def="plate"]');
      const canvas = document.getElementById("canvas");
      const transfer = new DataTransfer();
      item.dispatchEvent(new DragEvent("dragstart", { bubbles: true, dataTransfer: transfer }));
      canvas.dispatchEvent(new DragEvent("dragover", {
        bubbles: true, dataTransfer: transfer, clientX: x, clientY: y }));
      canvas.dispatchEvent(new DragEvent("drop", {
        bubbles: true, dataTransfer: transfer, clientX: x, clientY: y }));
    }, plateTarget);
    await page.waitForFunction((count) => window.__leveleditor.state.counts.props === count + 1,
      initialProps, { timeout: 90000 }).catch(() => {});
    const dropState = await state();
    const added = dropState.counts.props === initialProps + 1;
    check("dropping a plate onto a plate is accepted", added && dropState.gate.ok,
      `${dropState.counts.props} props, ${dropState.gate.errors.join(" | ")}`);
    if (added) {
      const newId = await page.evaluate(() => window.__leveleditor.view.selectedIds[0]);
      const newProp = dropState.doc.props.find((prop) => prop.itemId === newId);
      check("the dropped plate stacks or is offset", Boolean(newProp?.stackedOn || newProp?.support),
        JSON.stringify({ id: newId, support: newProp?.support, stackedOn: newProp?.stackedOn }));
      await shot("control_drop.png");
      await page.evaluate(() => document.activeElement?.blur());
      await page.keyboard.press("Control+z");
      await waitSha(initialSha);
      check("undo restores the level after the drop", (await sha()) === initialSha);
    }
  }

  // ------------------------------------------------------------------ //
  // regions: draw one, add a second, read server stats (count vs volume)
  // ------------------------------------------------------------------ //
  await page.evaluate(() => {
    const view = window.__leveleditor.view;
    view.setCameraPreset("overview");
    view.clearSelection();
  });
  await page.waitForTimeout(300);
  await page.evaluate(() => { document.getElementById("right").scrollTop = 0; });
  await page.click("#region-mode", { noWaitAfter: true });
  const regionStart = await page.evaluate(() => {
    const view = window.__leveleditor.view;
    const rect = view.canvas.getBoundingClientRect();
    const bounds = window.__leveleditor.state.bounds;
    for (let fy = 0.42; fy <= 0.78; fy += 0.03) {
      for (let fx = 0.08; fx <= 0.7; fx += 0.03) {
        const x = rect.left + rect.width * fx;
        const y = rect.top + rect.height * fy;
        const point = view._groundPoint(x, y);
        if (!point) continue;
        const inside = point[0] > bounds[0][0] + 0.4 && point[0] < bounds[1][0] - 0.4
          && point[2] > bounds[0][1] + 0.4 && point[2] < bounds[1][1] - 0.4;
        if (inside && view._pickId(x, y) === null) return { x, y };
      }
    }
    return null;
  });
  check("found a region-draw start", Boolean(regionStart));
  if (regionStart) {
    await page.mouse.move(regionStart.x, regionStart.y);
    await page.mouse.down();
    await page.mouse.move(regionStart.x + 150, regionStart.y + 105, { steps: 10 });
    await page.mouse.up();
    await page.waitForFunction(() => window.__leveleditor.regions().regions.length >= 1,
      null, { timeout: 60000 });
  }
  await page.click("#region-mode", { noWaitAfter: true });
  const drawnRegions = await page.evaluate(() => window.__leveleditor.regions());
  check("drawing a region creates and saves one", drawnRegions.regions.length >= 1,
    JSON.stringify(drawnRegions.regions.map((region) => region.name)));
  const savedRegions = await page.evaluate(() => window.__leveleditor.api.loadRegions());
  check("regions.json round-trips", savedRegions.ok
    && savedRegions.regions.length === drawnRegions.regions.length);
  await page.evaluate(() => {
    const input = document.getElementById("region-name");
    input.value = "小区";
    input.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await page.waitForTimeout(400);
  await page.evaluate(() => window.__leveleditor.addRegion({
    name: "大区", min: [-14, 0, -14], max: [-6, 2.5, -6],
  }));
  await page.waitForTimeout(700);
  await page.evaluate(() => window.__leveleditor.setD(0.8));
  await page.waitForTimeout(400);
  await page.evaluate(() => { document.getElementById("right").scrollTop = 0; });
  await shot("control_regions.png");
  const drawnName = (await page.evaluate(() => window.__leveleditor.regions())).regions[0]?.name;
  const drawnStats = await page.evaluate((name) =>
    window.__leveleditor.stats({ scope: "region", region: name, metric: "count",
      mode: "center", diameter: 0.5, bin: 0.05 }), drawnName);
  check("the drawn region returns stats", drawnStats.totals.count >= 0,
    `${drawnName}: ${drawnStats.totals.count} props`);
  const regionStats = await page.evaluate(() =>
    window.__leveleditor.stats({ scope: "region", region: "大区", metric: "count",
      mode: "center", diameter: 0.5, bin: 0.05 }));
  check("region stats count/volume/bins", regionStats.totals.count > 0
    && regionStats.bins.reduce((sum, bin) => sum + bin.count, 0) === regionStats.totals.count,
    `${regionStats.totals.count} props, ${regionStats.totals.volume} m3`);
  await page.evaluate(() => {
    document.getElementById("stats-scope").value = "region";
    document.getElementById("stats-scope").dispatchEvent(new Event("change", { bubbles: true }));
    document.querySelector(".ladder-panel").scrollIntoView({ block: "start" });
  });
  await page.waitForTimeout(700);
  await page.click('#stats-metrics [data-metric="count"]');
  await page.waitForTimeout(500);
  await shot("control_region_count.png");
  await page.click('#stats-metrics [data-metric="volume"]');
  await page.waitForTimeout(500);
  await shot("control_region_volume.png");
  await page.evaluate(() => {
    document.getElementById("stats-scope").value = "all";
    document.getElementById("stats-scope").dispatchEvent(new Event("change", { bubbles: true }));
    document.getElementById("right").scrollTop = 0;
  });
  await page.waitForTimeout(500);

  // ------------------------------------------------------------------ //
  // ladder at D=0.50 and D=1.25; palette
  // ------------------------------------------------------------------ //
  await page.evaluate(() => {
    const view = window.__leveleditor.view;
    view.clearSelection();
    view.setCameraPreset("overview");
    view.setShowWorldBoxes(false);
    view.setBoxStyle("obb");
  });
  await page.evaluate(() => {
    document.querySelector(".ladder-panel").scrollIntoView({ block: "start" });
    window.__leveleditor.setD(0.5);
  });
  await page.waitForTimeout(700);
  const ladder50 = await page.evaluate(() => ({
    green: Number(document.getElementById("tier-green").textContent),
    expected: Object.values(window.__leveleditor.state.derived.props)
      .filter((entry) => entry.dReq <= 0.5).length,
  }));
  check("ladder D=0.50 counts", ladder50.green === ladder50.expected, JSON.stringify(ladder50));
  await shot("03_ladder_d050.png");
  await page.evaluate(() => window.__leveleditor.setD(1.25));
  await page.waitForTimeout(700);
  const ladder125 = await page.evaluate(() => ({
    green: Number(document.getElementById("tier-green").textContent),
    expected: Object.values(window.__leveleditor.state.derived.props)
      .filter((entry) => entry.dReq <= 1.25).length,
  }));
  check("ladder D=1.25 counts", ladder125.green === ladder125.expected, JSON.stringify(ladder125));
  await shot("04_ladder_d125.png");
  await page.evaluate(() => window.__leveleditor.setLadderColouring(false));
  await page.fill("#palette-search", "");
  await page.waitForTimeout(500);
  await shot("05_palette.png");

  // ------------------------------------------------------------------ //
  // OBB before/after on a tilted ramp
  // ------------------------------------------------------------------ //
  await page.evaluate(() => {
    document.getElementById("right").scrollTop = 0;
    const view = window.__leveleditor.view;
    view.setTool("hand");
    view.select(["ramp_to_table"], false);
    view.focusSelection();
    view.setShowWorldBoxes(false);
    view.setBoxStyle("world");
    view.needsRender = true;
    view.renderNow();
  });
  await hideToast();
  await page.waitForTimeout(500);
  await shot("control_obb_before.png");
  await page.evaluate(() => {
    const view = window.__leveleditor.view;
    view.setBoxStyle("obb");
    window.__leveleditor.setShowWorldBoxes(true);
    view.needsRender = true;
    view.renderNow();
  });
  await page.waitForTimeout(500);
  await shot("control_obb_after.png");
  await page.evaluate(() => {
    window.__leveleditor.setShowWorldBoxes(false);
    const view = window.__leveleditor.view;
    view.setBoxStyle("obb");
    view.setTool("move");
  });

  // ------------------------------------------------------------------ //
  // a physics-test-field cell still opens; back to the level
  // ------------------------------------------------------------------ //
  await page.selectOption("#level-select", "physics_test_field/C01");
  await page.click("#open-level");
  await page.waitForFunction(() => window.__leveleditor.state.level === "physics_test_field/C01",
    null, { timeout: 150000 });
  const cell = await state();
  check("C01 opens", cell.level === "physics_test_field/C01" && cell.counts.props === 0
    && cell.counts.structures === 7);
  check("C01 palette is empty (no defs[])", await page.evaluate(
    () => document.querySelectorAll(".palette-item").length) === 0);
  await hideToast();
  await shot("08_physics_c01.png");
  await page.selectOption("#level-select", LEVEL);
  await page.click("#open-level");
  await page.waitForFunction((level) => window.__leveleditor.state.level === level,
    LEVEL, { timeout: 150000 });
  await page.waitForTimeout(600);
  check("level reopened byte-identical", (await sha()) === initialSha);

  check("no console errors", consoleErrors.length === 0, consoleErrors.slice(0, 3).join(" | "));
} catch (error) {
  failures.push(`exception: ${error.message}`);
  console.error(error);
} finally {
  await browser.close();
  await stopServer();
}

console.log("\nnotes:");
for (const note of notes) console.log(`  - ${note}`);
if (failures.length) {
  console.error("\nFAILED:");
  for (const failure of failures) console.error(`  - ${failure}`);
  process.exit(1);
}
console.log("\nsmoke: PASS");

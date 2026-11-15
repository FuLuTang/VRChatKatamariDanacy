#!/usr/bin/env node
/**
 * Browser smoke test for the web level editor (optional; needs playwright-core).
 *
 *   npm install playwright-core@1.50.1          # anywhere, not in the repo
 *   LEVELEDITOR_PLAYWRIGHT=/scratch/node_modules/playwright-core \
 *   node Tools/leveleditor/test/smoke.mjs
 *
 * By default it starts its own server on a throwaway copy of the level (port 0,
 * --no-lock) and removes it afterwards.  Set LEVELEDITOR_URL + LEVELEDITOR_TOKEN to
 * run against an editor that is already up instead.
 *
 * Environment:
 *   LEVELEDITOR_URL       optional: an already-running editor (default: spawn one)
 *   LEVELEDITOR_TOKEN     with LEVELEDITOR_URL
 *   LEVELEDITOR_PLAYWRIGHT  path to the playwright-core package (or NODE_PATH)
 *   LEVELEDITOR_CHROMIUM  Chromium/Chrome executable (default: the ms-playwright cache)
 *   LEVELEDITOR_SHOTS     screenshot directory (default /tmp/leveleditor-shots)
 *   LEVELEDITOR_LEVEL     default story_01_bench_size
 *   LEVELEDITOR_PYTHON    default python3
 *
 * What it proves: the page boots with real models, the coordinate audit matches the
 * server's world AABBs, clicking selects, a nudge saves and Ctrl+Z restores the exact
 * file bytes, the ladder recolours/counts, the palette drop adds a prop and undo
 * removes it, walk mode moves the camera, the screenshot button downloads a PNG, and
 * a physics-test-field cell opens.  It leaves the level as it found it (byte hash).
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

// By default this runs against its own throwaway copy of the level, so a failed
// assertion can never leave the tracked placement.json modified.  Pointing it at an
// already-running editor (LEVELEDITOR_URL/TOKEN) is the developer shortcut.
let server = null;
let workdir = null;
let URL_BASE = EXTERNAL_URL;
let TOKEN = EXTERNAL_TOKEN;

async function startServer() {
  workdir = await mkdtemp(join(tmpdir(), "leveleditor-smoke-"));
  const levels = join(workdir, "levels");
  const state = join(workdir, "state");
  const levelsToCopy = new Set([LEVEL, "physics_test_field/C01"]);
  for (const id of levelsToCopy) {
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
    /* try the next candidate */
  }
}
if (!chromium) {
  console.error("playwright-core is not installed. Run `npm install playwright-core@1.50.1` "
    + "in a scratch directory and point LEVELEDITOR_PLAYWRIGHT at its directory "
    + "(or set NODE_PATH to that node_modules).");
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
  console.error("no editor URL/token (set LEVELEDITOR_URL + LEVELEDITOR_TOKEN, or let the script start one)");
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

const shot = async (name) => {
  await page.evaluate(() => window.__leveleditor?.view?.renderNow?.());
  await page.waitForTimeout(120);
  await page.screenshot({ path: join(SHOTS, name), timeout: 180000 });
  console.log(`  shot ${join(SHOTS, name)}`);
};
const state = () => page.evaluate(() => window.__leveleditor.state);
const ready = async () => page.waitForFunction(
  () => document.body.dataset.ready === "1", null, { timeout: 300000 });

try {
  console.log(`open ${URL_BASE} (${LEVEL})`);
  await page.goto(`${URL_BASE}/?token=${TOKEN}`, { waitUntil: "domcontentloaded" });
  await ready();
  await page.waitForTimeout(800);

  const initial = await state();
  const initialSha = initial.file.sha256;
  check("level opened", initial.level === LEVEL, initial.level);
  check("127 props loaded", initial.counts.props === 127, String(initial.counts.props));
  check("gate green", initial.gate.ok, `${initial.gate.errorCount} errors`);

  // 1. real models + coordinate audit against the server's world AABBs ------------
  const audit = await page.evaluate(() => window.__leveleditor.audit());
  check("coordinate audit", audit.ok, `max error ${audit.maxError} m (${audit.worst})`);
  check("rendered 127 prop groups", audit.props === 127, String(audit.props));
  check("lounge chair world box", audit.chair
    && Math.abs(audit.chair[0][0] + 12.6) < 1e-3 && Math.abs(audit.chair[1][0] + 12.0) < 1e-3
    && Math.abs(audit.chair[0][2] + 10.4) < 1e-3 && Math.abs(audit.chair[1][2] + 8.8) < 1e-3
    && Math.abs(audit.chair[1][1] - 0.35) < 1e-3, JSON.stringify(audit.chair));
  check("palette has all defs", await page.evaluate(
    () => document.querySelectorAll(".palette-item").length) >= 44);
  await shot("01_overview_3q.png");

  // 2. select the lounge chair, show the move gizmo ------------------------------
  const chairPoint = await page.evaluate(() => {
    const view = window.__leveleditor.view;
    const group = view.propObjects.get("struct_lounge_chair_01");
    const point = group.position.clone();
    point.y = 0.2;
    point.project(view.camera);
    const rect = view.canvas.getBoundingClientRect();
    return {
      x: rect.left + (point.x + 1) / 2 * rect.width,
      y: rect.top + (1 - point.y) / 2 * rect.height,
    };
  });
  await page.mouse.click(chairPoint.x, chairPoint.y);
  await page.waitForTimeout(300);
  const selection = await page.evaluate(() => window.__leveleditor.selection());
  check("click selects the chair", selection?.id === "struct_lounge_chair_01", selection?.id);
  check("selection panel shows def",
    (await page.textContent("#selection-type")).includes("lounge_chair"));
  check("gizmo attached", await page.evaluate(() => window.__leveleditor.view.gizmo.object === window.__leveleditor.view.proxy));
  await shot("02_selection_gizmo.png");

  // 3. nudge saves, Ctrl+Z restores the exact bytes ------------------------------
  const beforeNudge = (await state()).doc.props.find((prop) => prop.itemId === "struct_lounge_chair_01").position;
  await page.evaluate(() => document.activeElement?.blur());
  await page.keyboard.press("ArrowRight");
  await page.waitForFunction((previous) => {
    const prop = window.__leveleditor.state.doc.props.find((entry) => entry.itemId === "struct_lounge_chair_01");
    return Math.abs(prop.position[0] - previous) > 1e-6;
  }, beforeNudge[0], { timeout: 60000 });
  const afterNudge = (await state()).doc.props.find((prop) => prop.itemId === "struct_lounge_chair_01").position;
  check("arrow nudge moved 0.05 m", Math.abs(afterNudge[0] - beforeNudge[0] - 0.05) < 1e-3,
    `${beforeNudge[0]} -> ${afterNudge[0]}`);
  await page.evaluate(() => document.activeElement?.blur());
  await page.keyboard.press("Control+z");
  await page.waitForFunction((sha) => window.__leveleditor.state.file.sha256 === sha,
    initialSha, { timeout: 60000 });
  check("Ctrl+Z restored the exact file", (await state()).file.sha256 === initialSha);

  // 4. ball-size ladder at D=0.50 and D=1.25 -------------------------------------
  await page.evaluate(() => window.__leveleditor.setD(0.5));
  await page.waitForTimeout(300);
  const ladder50 = await page.evaluate(() => ({
    green: Number(document.getElementById("tier-green").textContent),
    yellow: Number(document.getElementById("tier-yellow").textContent),
    red: Number(document.getElementById("tier-red").textContent),
    expected: window.__leveleditor.state.derived.props
      ? Object.values(window.__leveleditor.state.derived.props).filter((entry) => entry.dReq <= 0.5).length
      : -1,
  }));
  check("ladder D=0.50 counts", ladder50.green === ladder50.expected && ladder50.green === 104,
    JSON.stringify(ladder50));
  await shot("03_ladder_d050.png");

  await page.evaluate(() => window.__leveleditor.setD(1.25));
  await page.waitForTimeout(300);
  const ladder125 = await page.evaluate(() => ({
    green: Number(document.getElementById("tier-green").textContent),
    expected: Object.values(window.__leveleditor.state.derived.props)
      .filter((entry) => entry.dReq <= 1.25).length,
    colours: document.querySelectorAll(".histogram .bar.green").length,
  }));
  check("ladder D=1.25 counts", ladder125.green === ladder125.expected && ladder125.green > ladder50.green,
    JSON.stringify(ladder125));
  await shot("04_ladder_d125.png");

  // 5. asset palette -------------------------------------------------------------
  // Thumbnails are lazy; wait for the visible ones (not all 48).
  await page.waitForFunction(() => document.querySelectorAll(".palette-thumb[src]").length >= 3,
    null, { timeout: 60000 }).catch(() => {});
  await page.fill("#palette-search", "apple");
  await page.waitForTimeout(200);
  const visibleItems = await page.evaluate(
    () => [...document.querySelectorAll(".palette-item")].filter((item) => item.style.display !== "none").length);
  check("palette search filters", visibleItems >= 1 && visibleItems < 20, String(visibleItems));
  await shot("05_palette.png");
  await page.fill("#palette-search", "");

  // 6. drag-and-drop onto the ground adds a prop; undo removes it ----------------
  const emptySpot = await page.evaluate(() => {
    const view = window.__leveleditor.view;
    const rect = view.canvas.getBoundingClientRect();
    // Project a few points over the level and take whatever surface the ray actually
    // hits (a table/BED is a better test than the floor: it exercises support+seat).
    const points = [[-16.2, 0.02, 7.2], [-17.5, 0.02, 5.0], [-15.0, 0.02, 8.0],
      [-18.0, 0.02, 9.0], [-14.0, 0.02, 6.0]];
    for (const [x, y, z] of points) {
      const projected = view.camera.position.clone();
      projected.set(x, y, z);
      projected.project(view.camera);
      const sx = rect.left + (projected.x + 1) / 2 * rect.width;
      const sy = rect.top + (1 - projected.y) / 2 * rect.height;
      const drop = view.dropPoint(sx, sy);
      if (drop && drop.target) return { x: sx, y: sy, drop };
    }
    return null;
  });
  if (!emptySpot) {
    throw new Error("could not find a drop surface for the palette drop");
  }
  const beforeIds = (await state()).doc.props.map((prop) => prop.itemId);
  await page.evaluate(({ x, y }) => {
    const item = document.querySelector('.palette-item[data-def="apple"]');
    const canvas = document.getElementById("canvas");
    const transfer = new DataTransfer();
    item.dispatchEvent(new DragEvent("dragstart", { bubbles: true, dataTransfer: transfer }));
    canvas.dispatchEvent(new DragEvent("dragover", {
      bubbles: true, dataTransfer: transfer, clientX: x, clientY: y }));
    canvas.dispatchEvent(new DragEvent("drop", {
      bubbles: true, dataTransfer: transfer, clientX: x, clientY: y }));
  }, emptySpot);
  const added = await page.waitForFunction(
    () => window.__leveleditor.state.counts.props === 128, null, { timeout: 60000 })
    .then(() => true).catch(() => false);
  check("palette drop added a prop", added, `props=${(await state()).counts.props}`);
  if (added) {
    // The drop selects the new prop after the state refresh; wait for that instead of
    // racing the microtask that follows `props === 128`.
    await page.waitForFunction((previous) => {
      const id = window.__leveleditor.selection()?.id;
      return id && !previous.includes(id);
    }, beforeIds, { timeout: 30000 }).catch(() => {});
    const newId = await page.evaluate(() => window.__leveleditor.selection()?.id);
    const newProp = (await state()).doc.props.find((prop) => prop.itemId === newId);
    check("drop selected/added the new prop", Boolean(newProp) && !beforeIds.includes(newId),
      String(newId));
    const expectedY = emptySpot.drop.point[1] + (newProp?.aabb?.[1] || 0) / 2;
    check("drop rested on the surface under the pointer",
      newProp && Math.abs(newProp.position[1] - expectedY) < 0.06,
      `${JSON.stringify(newProp?.position)} vs ${expectedY.toFixed(4)} ${JSON.stringify(emptySpot.drop.target)}`);
    await page.evaluate(() => document.activeElement?.blur());
    await page.keyboard.press("Control+z");
    await page.waitForFunction((sha) => window.__leveleditor.state.file.sha256 === sha,
      initialSha, { timeout: 60000 });
    check("undo removed the dropped prop", (await state()).counts.props === 127);
  }

  // 7. walk mode + pointer lock + WASD ------------------------------------------
  await page.click("#mode-walk", { noWaitAfter: true });
  await page.waitForTimeout(600);
  const walk = await page.evaluate(() => ({
    mode: window.__leveleditor.view.mode,
    locked: document.pointerLockElement === document.getElementById("canvas"),
    fallback: window.__leveleditor.view.walk.fallback,
    eye: window.__leveleditor.view.camera.position.y,
    x: window.__leveleditor.view.camera.position.x,
    z: window.__leveleditor.view.camera.position.z,
  }));
  notes.push(`pointer lock in headless http: ${walk.locked ? "granted" : "denied"}`);
  check("walk mode entered", walk.mode === "walk");
  check("eye height 0.60 m", Math.abs(walk.eye - 0.6) < 1e-6, String(walk.eye));
  check("walk started inside the level", Math.abs(walk.x) < 20 && Math.abs(walk.z) < 20,
    `${walk.x},${walk.z}`);
  await shot("06_walk_ball_eye.png");
  async function pressW() {
    const before = await page.evaluate(() => window.__leveleditor.view.camera.position.clone().toArray());
    await page.keyboard.down("w");
    await page.waitForTimeout(1400);
    await page.keyboard.up("w");
    const after = await page.evaluate(() => window.__leveleditor.view.camera.position.toArray());
    return {
      before, after,
      distance: Math.hypot(after[0] - before[0], after[2] - before[2]),
    };
  }
  let movement = await pressW();
  if (movement.distance < 0.05 && walk.locked) {
    // Headless Chromium can grant pointer lock and then drop synthetic key presses;
    // releasing the lock is exactly the app's fallback path (right-drag look + WASD).
    notes.push("headless pointer lock swallowed W; re-tested with the lock released");
    await page.evaluate(() => document.exitPointerLock?.());
    await page.waitForTimeout(400);
    movement = await pressW();
  }
  check("W moves the walk camera", movement.distance > 0.05,
    `${movement.before[0].toFixed(2)},${movement.before[2].toFixed(2)} -> ${movement.after[0].toFixed(2)},${movement.after[2].toFixed(2)} (${movement.distance.toFixed(3)} m)`);
  await page.keyboard.press("Tab");
  await page.waitForTimeout(300);
  check("Tab returns to orbit", (await page.evaluate(() => window.__leveleditor.view.mode)) === "orbit");

  // 8. screenshot button downloads a PNG ----------------------------------------
  const [download] = await Promise.all([
    page.waitForEvent("download", { timeout: 120000 }),
    page.click("#screenshot"),
  ]);
  check("screenshot downloads .png", /\.png$/.test(download.suggestedFilename()),
    download.suggestedFilename());

  // 9. a physics test field cell opens (and the level can be reopened) -----------
  await page.selectOption("#level-select", "physics_test_field/C01");
  await page.click("#open-level");
  await page.waitForFunction(() => window.__leveleditor.state.level === "physics_test_field/C01",
    null, { timeout: 120000 });
  const cell = await state();
  check("C01 opened", cell.level === "physics_test_field/C01");
  check("C01 has 0 props and 7 structures",
    cell.counts.props === 0 && cell.counts.structures === 7,
    `${cell.counts.props}/${cell.counts.structures}`);
  await shot("07_physics_c01.png");

  await page.selectOption("#level-select", LEVEL);
  await page.click("#open-level");
  await page.waitForFunction((level) => window.__leveleditor.state.level === level,
    LEVEL, { timeout: 120000 });
  check("level reopened byte-identical", (await state()).file.sha256 === initialSha,
    (await state()).file.sha256);

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

/**
 * UI wiring for the web level editor.
 *
 * The invariant: every edit is one small JSON op to the Python server, which
 * validates, gates and atomically exports placement.json.  This file keeps a mirror
 * of the last server state for display only; it never decides that an edit is valid.
 * The thin client-side calculations that exist (ladder tiers, histogram, snap steps)
 * only *compare* numbers the server produced.
 */

import { API } from "./api.js";
import { EditorView } from "./view.js";

const $ = (id) => document.getElementById(id);
const clamp = (value, low, high) => Math.max(low, Math.min(high, value));

const api = new API();
let view = null;
let state = null;
let refreshChain = Promise.resolve();
let refreshTimer = null;
let toastTimer = null;
let dragDef = null;
let hintsTimer = null;
let paletteSignature = "";
const thumbnails = new Map();
const paletteNodes = new Map();

// --------------------------------------------------------------------------- //
// small UI helpers
// --------------------------------------------------------------------------- //
function toast(message, kind = "error", milliseconds = 5200) {
  const element = $("toast");
  element.textContent = message;
  element.className = `toast ${kind === "ok" ? "ok" : ""}`;
  element.classList.remove("hidden");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add("hidden"), milliseconds);
}

function showError(message, details = []) {
  const extra = (details || []).slice(0, 3).join("\n");
  toast(extra ? `${message}\n${extra}` : message, "error", 9000);
}

function setSaving() {
  $("save-status").className = "save-status saving";
  $("save-text").textContent = "保存中…";
  $("status-save").textContent = "保存中…";
  $("status-save").className = "";
}

function setSaved(saved) {
  if (!saved) {
    $("save-status").className = "save-status";
    $("save-text").textContent = "已就绪 · 没有未保存改动";
    $("status-save").textContent = "";
    $("status-save").className = "";
    return;
  }
  const counts = saved?.diff?.counts;
  let summary = "已保存";
  if (counts) {
    const props = counts.props || { added: 0, updated: 0, removed: 0 };
    const structures = counts.structures || { added: 0, updated: 0, removed: 0 };
    const added = props.added + structures.added;
    const updated = props.updated + structures.updated;
    const removed = props.removed + structures.removed;
    summary = `已保存 +${added} ~${updated} -${removed}`;
  }
  if (saved?.sha256) summary += ` · ${saved.sha256.slice(0, 8)}`;
  $("save-status").className = "save-status saved";
  $("save-text").textContent = summary;
  $("status-save").textContent = summary;
  $("status-save").className = "saved";
}

function setSaveError(message = "未保存（被拒绝）") {
  $("save-status").className = "save-status error";
  $("save-text").textContent = message;
  $("status-save").textContent = message;
  $("status-save").className = "error";
}

function setLoading(text) {
  const element = $("loading");
  if (!text) {
    element.classList.add("hidden");
    return;
  }
  element.classList.remove("hidden");
  $("loading-text").textContent = text;
}

function formatNumber(value, digits = 2) {
  if (value === undefined || value === null || Number.isNaN(Number(value))) return "—";
  return Number(value).toFixed(digits).replace(/\.?0+$/, (match) => (match.includes(".") ? "" : match));
}

function formatVec(vector, digits = 2) {
  if (!Array.isArray(vector)) return "—";
  return `(${vector.map((value) => formatNumber(value, digits)).join(", ")})`;
}

function findEntry(type, id) {
  if (!state) return null;
  return type === "prop"
    ? (state.doc.props || []).find((entry) => entry.itemId === id)
    : (state.doc.structures || []).find((entry) => entry.id === id);
}

// --------------------------------------------------------------------------- //
// one serialised state refresh
// --------------------------------------------------------------------------- //
function refreshState() {
  refreshChain = refreshChain
    .then(async () => {
      const payload = await api.loadLevel();
      await applyState(payload.state);
    })
    .catch((error) => {
      console.error("refresh failed", error);
      showError(`刷新失败：${error.message}`, error.details || []);
      setSaveError("刷新失败");
    });
  return refreshChain;
}

function scheduleRefresh() {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(() => refreshState(), 160);
}

async function applyState(next) {
  const previousLevel = state?.level;
  state = next;
  const levelChanged = previousLevel !== next.level;
  await view.applyState(next);
  if (levelChanged) {
    currentLevelChanged();
  }
  renderAll();
  if (levelChanged) {
    setLoading("");
  }
}

function currentLevelChanged() {
  fillLevelSelect();
  $("level-id").textContent = state.level || "—";
  $("level-path").textContent = state.file?.path || "";
  buildCameraButtons();
  buildPalette(true);
  $("status-save").textContent = "";
  $("status-save").className = "";
  $("save-text").textContent = "已就绪 · 没有未保存改动";
  $("save-status").className = "save-status";
  const start = Number((state.doc.level || {}).ballDiameterStart || 0.3);
  const max = Number((state.doc.level || {}).ballDiameterMax || 2.2);
  const slider = $("ladder");
  slider.max = String(max);
  slider.value = String(clamp(start, Number(slider.min), max));
  updateLadderFromSlider();
  updateStructureDefaults();
}

function renderAll() {
  renderLevelFacts();
  renderValidation();
  renderSelection(view?.selection ? view.selectionInfo() : null);
  renderLadder();
  renderStatus();
  $("undo").disabled = !state.history?.undo;
  $("redo").disabled = !state.history?.redo;
  $("level-id").textContent = state.level || "—";
  $("level-path").textContent = state.file?.path || "";
  if (!state.lastSaved && !$("save-status").classList.contains("saving")) {
    $("save-text").textContent = state.gate?.ok ? "已就绪 · 没有未保存改动" : "已打开（文件有错误）";
    $("save-status").className = "save-status";
  }
  if (!api.hasToken) $("readonly-banner").classList.remove("hidden");
}

function renderStatus() {
  $("status-level").textContent = state?.level || "—";
  const counts = state?.counts || {};
  $("status-counts").textContent = counts.props === undefined
    ? "" : `${counts.props} 道具 · ${counts.structures} 结构 · ${counts.valueUnits} u`;
  // SSE owns the "实时 / 重连中" label; do not overwrite it here.
}

// --------------------------------------------------------------------------- //
// level facts
// --------------------------------------------------------------------------- //
function renderLevelFacts() {
  const doc = state.doc;
  const level = doc.level || {};
  const bounds = level.bounds || [[0, 0], [0, 0]];
  const rows = [
    ["id", level.id || "—"],
    ["bounds", `x ${bounds[0][0]}..${bounds[1][0]} · z ${bounds[0][1]}..${bounds[1][1]}`],
    ["地面", `y = ${formatNumber(level.groundY ?? 0, 3)} m`],
    ["球起点", formatVec(level.ballStart, 2)],
    ["球径", `${formatNumber(level.ballDiameterStart, 2)} → ${formatNumber(level.ballDiameterMax, 2)} m`],
    ["道具", `${state.counts.props} 件 · ${state.counts.valueUnits} u`],
    ["expected", `${level.expected?.propCount ?? "—"} 件 / ${level.expected?.valueUnits ?? "—"} u`],
    ["结构", `${state.counts.structures}（预算 box ${level.budget?.boxes ?? "?"} / ramp ${level.budget?.ramps ?? "?"} / plane ${level.budget?.planes ?? "?"}）`],
  ];
  $("level-facts").innerHTML = rows
    .map(([key, value]) => `<div class="k">${escapeHtml(key)}</div><div class="v">${escapeHtml(value)}</div>`)
    .join("");
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (character) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[character]));
}

// --------------------------------------------------------------------------- //
// cameras and modes
// --------------------------------------------------------------------------- //
function buildCameraButtons() {
  const container = $("camera-buttons");
  container.replaceChildren();
  const cameras = [{ id: "__fit", label: "适配 3/4" }, ...view.cameraButtonNames()];
  for (const camera of cameras) {
    const button = document.createElement("button");
    button.textContent = camera.label;
    button.dataset.camera = camera.id;
    button.className = camera.id === view.currentCamera ? "active" : "";
    button.addEventListener("click", () => {
      if (camera.id === "__fit") view.fitLevelView();
      else view.setCameraPreset(camera.id);
      for (const other of container.querySelectorAll("button")) {
        other.classList.toggle("active", other.dataset.camera === camera.id);
      }
    });
    container.appendChild(button);
  }
}

function setMode(mode) {
  $("mode-orbit").classList.toggle("active", mode === "orbit");
  $("mode-walk").classList.toggle("active", mode === "walk");
  $("walk-hud").classList.toggle("hidden", mode !== "walk");
  $("walk-lock").textContent = view.walk.fallback
    ? "指针锁定不可用：按住右键拖动看方向 · WASD 移动"
    : "点击画面锁定鼠标 · Esc 释放";
  $("eye-height").disabled = false;
}

// --------------------------------------------------------------------------- //
// palette
// --------------------------------------------------------------------------- //
function buildPalette(force = false) {
  const manifests = state.manifests || {};
  const signature = Object.entries(manifests)
    .map(([def, manifest]) => `${def}:${manifest.valueUnits ?? ""}`)
    .join("|");
  if (!force && signature === paletteSignature) {
    updatePaletteValues();
    return;
  }
  paletteSignature = signature;
  paletteNodes.clear();
  const list = $("palette-list");
  list.replaceChildren();
  const defs = Object.keys(manifests).sort();
  $("palette-count").textContent = String(defs.length);

  const categories = [...new Set(defs.map((def) => manifests[def].category || "other"))].sort();
  const filters = $("palette-filters");
  filters.replaceChildren();
  for (const category of ["all", ...categories]) {
    const button = document.createElement("button");
    button.textContent = category === "all" ? "全部" : category;
    button.dataset.category = category;
    button.className = category === "all" ? "active" : "";
    button.addEventListener("click", () => {
      for (const other of filters.querySelectorAll("button")) {
        other.classList.toggle("active", other === button);
      }
      filterPalette();
    });
    filters.appendChild(button);
  }

  for (const def of defs) {
    const manifest = manifests[def];
    const item = document.createElement("div");
    item.className = "palette-item";
    item.draggable = Boolean(manifest.valueUnits);
    item.dataset.def = def;
    item.dataset.category = manifest.category || "other";
    item.dataset.search = `${def} ${manifest.name} ${manifest.nameZh} ${manifest.category}`.toLowerCase();
    item.title = `${def}\n${manifest.nameZh || manifest.name}\n尺寸 ${formatVec(manifest.aabb, 3)} m\nD_req ${formatNumber(manifest.dReq, 3)} m · ${manifest.valueUnits ?? "?"} u`;
    const image = document.createElement("img");
    image.className = "palette-thumb";
    image.alt = def;
    if (thumbnails.has(def)) image.src = thumbnails.get(def);
    const meta = document.createElement("div");
    meta.className = "palette-meta";
    meta.innerHTML = `<div class="palette-name">${escapeHtml(manifest.nameZh || manifest.name || def)}</div>
      <div class="palette-sub" data-sub="${def}"></div>`;
    item.append(image, meta);
    item.addEventListener("dragstart", (event) => {
      dragDef = def;
      event.dataTransfer.effectAllowed = "copy";
      event.dataTransfer.setData("text/plain", def);
      $("drop-def").textContent = `${manifest.nameZh || def}（${def}）`;
    });
    item.addEventListener("dragend", () => {
      dragDef = null;
      $("drop-hint").classList.add("hidden");
    });
    if (!manifest.valueUnits) {
      item.classList.add("disabled");
      item.title += "\n（这个 def 没有在 defs[] 里声明 valueUnits，先 add_def 才能放）";
    }
    paletteNodes.set(def, item);
    list.appendChild(item);
  }
  updatePaletteValues();
  filterPalette();
  observePaletteThumbnails(defs);
}

function updatePaletteValues() {
  const manifests = state.manifests || {};
  for (const [def, item] of paletteNodes) {
    const manifest = manifests[def];
    if (!manifest) continue;
    const sub = item.querySelector(`[data-sub="${def}"]`);
    if (sub) {
      sub.textContent = `${formatVec(manifest.aabb, 2)} · ${manifest.valueUnits ?? "—"} u · D ${formatNumber(manifest.dReq, 2)}`;
    }
  }
}

function filterPalette() {
  const needle = $("palette-search").value.trim().toLowerCase();
  const category = $("palette-filters").querySelector("button.active")?.dataset.category || "all";
  for (const [def, item] of paletteNodes) {
    const matchesText = !needle || item.dataset.search.includes(needle);
    const matchesCategory = category === "all" || item.dataset.category === category;
    item.style.display = matchesText && matchesCategory ? "" : "none";
  }
}

let paletteObserver = null;
let thumbnailQueue = [];
let thumbnailRunning = false;

/** Render one thumbnail at a time; never 48 WebGL frames during startup. */
async function pumpThumbnails() {
  if (thumbnailRunning) return;
  thumbnailRunning = true;
  while (thumbnailQueue.length) {
    const def = thumbnailQueue.shift();
    if (thumbnails.has(def)) continue;
    try {
      const url = await view.renderThumbnail(def);
      thumbnails.set(def, url);
      const image = paletteNodes.get(def)?.querySelector("img");
      if (image) image.src = url;
    } catch (error) {
      console.warn("thumbnail failed", def, error);
    }
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  thumbnailRunning = false;
}

function queueThumbnail(def) {
  if (!def || thumbnails.has(def) || thumbnailQueue.includes(def)) return;
  thumbnailQueue.push(def);
  pumpThumbnails();
}

/**
 * Thumbnails are rendered from the model once, lazily: the visible palette items
 * first, the rest as the owner scrolls or filters.  This keeps the first frame and
 * the first walk fast on a 2-core/software-GL box.
 */
function observePaletteThumbnails(defs) {
  if (paletteObserver) paletteObserver.disconnect();
  if (!("IntersectionObserver" in window)) {
    for (const def of defs) queueThumbnail(def);
    return;
  }
  paletteObserver = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      queueThumbnail(entry.target.dataset.def);
    }
  }, { root: document.getElementById("palette-list"), rootMargin: "120px" });
  for (const def of defs) {
    const item = paletteNodes.get(def);
    if (item) paletteObserver.observe(item);
  }
}

// --------------------------------------------------------------------------- //
// selection panel
// --------------------------------------------------------------------------- //
function renderSelection(info) {
  $("selection-empty").classList.toggle("hidden", Boolean(info));
  $("selection-body").classList.toggle("hidden", !info);
  $("selection-type").textContent = info
    ? (info.type === "prop" ? `prop · ${info.def}` : `structure · ${info.role}`) : "";
  if (!info) return;
  const rows = [];
  rows.push(["id", `<b>${escapeHtml(info.id)}</b>`]);
  if (info.type === "prop") {
    rows.push(["def", escapeHtml(info.def)]);
    rows.push(["kind/zone", escapeHtml(`${info.kind || "?"} / ${info.zone || "—"}`)]);
    rows.push(["aabb", `${formatVec(info.aabb, 3)} m`]);
    rows.push(["D_req", `${formatNumber(info.dReq, 3)} m`]);
    rows.push(["valueUnits", String(info.valueUnits ?? "—")]);
    rows.push(["support", escapeHtml(info.support || info.stackedOn || "—")]);
  } else {
    rows.push(["role", escapeHtml(info.role || "—")]);
    rows.push(["size", `${formatVec(info.size, 3)} m`]);
    rows.push(["kind", escapeHtml(info.kind || "—")]);
  }
  const world = info.worldAabb
    ? `x ${formatNumber(info.worldAabb[0][0], 2)}..${formatNumber(info.worldAabb[1][0], 2)} · y ${formatNumber(info.worldAabb[0][1], 2)}..${formatNumber(info.worldAabb[1][1], 2)} · z ${formatNumber(info.worldAabb[0][2], 2)}..${formatNumber(info.worldAabb[1][2], 2)}`
    : "—";
  rows.push(["世界 AABB", world]);
  rows.push(["position", formatVec(info.position, 3)]);
  $("selection-kv").innerHTML = rows
    .map(([key, value]) => `<div class="k">${key}</div><div class="v">${value}</div>`)
    .join("");
  $("gizmo-move").classList.toggle("active", view.selectionMode === "translate");
  $("gizmo-rotate").classList.toggle("active", view.selectionMode === "rotate");
}

// --------------------------------------------------------------------------- //
// validation
// --------------------------------------------------------------------------- //
function renderValidation() {
  const check = state.check || {};
  const errors = check.errors || [];
  const warnings = check.warnings || [];
  const notes = check.notes || [];
  const line = [];
  if (errors.length) line.push(`<span class="bad">${errors.length} 个错误</span>`);
  else line.push(`<span class="ok">通过</span>`);
  if (warnings.length) line.push(`<span class="warn">${warnings.length} 个警告</span>`);
  if (!errors.length && !warnings.length) line.push("0 错 0 警告");
  $("gate-line").innerHTML = line.join(" · ");

  const list = $("validation");
  list.replaceChildren();
  if (!errors.length && !warnings.length && !notes.length) {
    const item = document.createElement("li");
    item.className = "none";
    item.textContent = "没有错误或警告";
    list.appendChild(item);
    return;
  }
  for (const [kind, messages] of [["error", errors], ["warning", warnings], ["note", notes]]) {
    for (const message of messages) {
      const item = document.createElement("li");
      item.className = kind;
      item.textContent = message;
      item.title = "点击定位到提示里的对象";
      item.addEventListener("click", () => focusFromText(message));
      list.appendChild(item);
    }
  }
}

function focusFromText(text) {
  const ids = [
    ...(state.doc.props || []).map((prop) => prop.itemId),
    ...(state.doc.structures || []).map((structure) => structure.id),
  ].sort((left, right) => right.length - left.length);
  const hit = ids.find((id) => text.includes(id));
  if (!hit) {
    toast("这条提示里没有可以定位的对象 id", "ok", 2500);
    return;
  }
  view.select(hit, { focus: true });
}

// --------------------------------------------------------------------------- //
// ladder + histogram
// --------------------------------------------------------------------------- //
function updateLadderFromSlider() {
  const diameter = Number($("ladder").value);
  $("ladder-value").textContent = diameter.toFixed(2);
  view.setLadder(diameter, $("ladder-on").checked);
  renderLadder();
  scheduleHints();
}

function renderLadder() {
  const diameter = Number($("ladder").value);
  const derived = state.derived?.props || {};
  const manifests = state.manifests || {};
  const max = Number((state.doc.level || {}).ballDiameterMax || 2.2);
  let counts = { green: 0, yellow: 0, red: 0, grey: 0 };
  let greenUnits = 0;
  let totalUnits = 0;
  const tiers = {};
  for (const prop of state.doc.props || []) {
    const info = derived[prop.itemId];
    if (!info) continue;
    totalUnits += info.valueUnits || 0;
    const tier = info.dReq > max ? "grey"
      : info.dReq <= diameter ? "green"
        : info.dReq <= diameter * 1.2 ? "yellow" : "red";
    counts[tier] += 1;
    tiers[prop.itemId] = tier;
    if (tier === "green") greenUnits += info.valueUnits || 0;
  }
  $("tier-green").textContent = counts.green;
  $("tier-yellow").textContent = counts.yellow;
  $("tier-red").textContent = counts.red;
  $("tier-grey").textContent = counts.grey + (state.counts.structures || 0);
  $("sum-green").textContent = greenUnits;
  $("sum-total").textContent = totalUnits;

  // Histogram of D_req: 43 bins of 0.05 m from 0.05 to 2.20.
  const binSize = 0.05;
  const bins = [];
  for (let low = 0.05; low < max + 1e-9; low += binSize) {
    bins.push({ low, high: low + binSize, count: 0 });
  }
  for (const info of Object.values(derived)) {
    const index = clamp(Math.floor((info.dReq - 0.05) / binSize), 0, bins.length - 1);
    bins[index].count += 1;
  }
  const peak = Math.max(1, ...bins.map((bin) => bin.count));
  const histogram = $("histogram");
  histogram.replaceChildren();
  for (const bin of bins) {
    const bar = document.createElement("div");
    const center = (bin.low + bin.high) / 2;
    let tier = "red";
    if (center > max) tier = "grey";
    else if (center <= diameter) tier = "green";
    else if (center <= diameter * 1.2) tier = "yellow";
    bar.className = `bar ${tier}`;
    bar.style.height = `${Math.max(bin.count ? 4 : 1, (bin.count / peak) * 100)}%`;
    bar.title = `${bin.low.toFixed(2)}–${bin.high.toFixed(2)} m: ${bin.count} 件`;
    histogram.appendChild(bar);
  }

  const next = Object.entries(derived)
    .filter(([id, info]) => tiers[id] === "yellow")
    .sort((left, right) => left[1].dReq - right[1].dReq)
    .slice(0, 4)
    .map(([id, info]) => {
      const prop = (state.doc.props || []).find((entry) => entry.itemId === id);
      const manifest = manifests[prop?.def] || {};
      return `${manifest.nameZh || prop?.def || id} ${info.dReq.toFixed(2)}`;
    })
    .filter((label, index, labels) => labels.findIndex((entry) => entry.split(" ")[0] === label.split(" ")[0]) === index);
  $("next-food").textContent = next.length
    ? `再大一点就能吸：${next.join(" · ")}` : "当前尺寸下没有「20% 内」的候选";
}

async function scheduleHints() {
  clearTimeout(hintsTimer);
  hintsTimer = setTimeout(refreshHints, 140);
}

async function refreshHints() {
  if (!state) return;
  const diameter = Number($("ladder").value);
  try {
    const payload = await api.hints(diameter);
    renderHints(payload.hints);
  } catch (error) {
    $("physics-hints").textContent = `提示不可用：${error.message}`;
  }
}

function renderHints(hints) {
  if (!hints) return;
  const container = $("physics-hints");
  const structurePills = (items, className) => items.length
    ? items.map((item) => `<span class="pill" data-focus="${escapeHtml(item.id)}">${escapeHtml(item.id)}</span>`).join("")
    : "—";
  container.innerHTML = `
    <div class="k">D / step</div><div class="v">${hints.diameter.toFixed(2)} m · ${hints.stepLimit.toFixed(3)} m</div>
    <div class="k">道具</div><div class="v">食物 ${hints.counts.food} · 地形 ${hints.counts.terrain} · 挡路 ${hints.counts.blocking}</div>
    <div class="k">可上结构</div><div class="v">${structurePills(hints.structures.terrain)}</div>
    <div class="k">挡路结构</div><div class="v">${structurePills(hints.structures.blocking)}</div>
    <div class="wide">P2 判定：顶面高差 ≤ 0.25·D 视为可上地形，否则挡路；只读提示，不写文件。</div>`;
  for (const pill of container.querySelectorAll("[data-focus]")) {
    pill.style.cursor = "pointer";
    pill.addEventListener("click", () => view.select(pill.dataset.focus, { focus: true }));
  }
}

// --------------------------------------------------------------------------- //
// ops
// --------------------------------------------------------------------------- //
async function sendOp(payload, options = {}) {
  setSaving();
  let response;
  try {
    response = await api.post(payload);
  } catch (error) {
    setSaveError();
    showError(error.message, error.details || []);
    return null;
  }
  if (!response.ok) {
    setSaveError();
    showError(response.error || "操作被拒绝", response.details || []);
    return response;
  }
  await refreshState();
  setSaved(response.saved);
  if (options.selectId) view.select(options.selectId);
  if (options.message) toast(options.message, "ok", 2200);
  return response;
}

async function openLevel(id) {
  if (!api.hasToken) {
    showError("打开关卡需要写入令牌：请用服务器日志打印的 ?token=… 链接打开本页。");
    return;
  }
  setLoading(`打开 ${id}…`);
  try {
    const response = await api.openLevel(id);
    if (!response.ok) {
      showError(response.error || "打不开", response.details || []);
      return;
    }
    const payload = await api.loadLevel();
    await applyState(payload.state);
    toast(`已打开 ${id}`, "ok", 2200);
  } catch (error) {
    showError(error.message, error.details || []);
  } finally {
    setLoading("");
  }
}

async function undo() {
  if (!state.history?.undo) return;
  setSaving();
  try {
    const response = await api.op("undo");
    if (!response.ok) {
      setSaveError("没有可撤销的步骤");
      showError(response.error, response.details || []);
      return;
    }
    await refreshState();
    setSaved(response.saved);
  } catch (error) {
    setSaveError();
    showError(error.message, error.details || []);
  }
}

async function redo() {
  if (!state.history?.redo) return;
  setSaving();
  try {
    const response = await api.op("redo");
    if (!response.ok) {
      setSaveError("没有可重做的步骤");
      showError(response.error, response.details || []);
      return;
    }
    await refreshState();
    setSaved(response.saved);
  } catch (error) {
    setSaveError();
    showError(error.message, error.details || []);
  }
}

async function commitTransform(info, position, quaternion) {
  const entry = findEntry(info.type, info.id);
  if (!entry) return false;
  const ops = [];
  const original = info.type === "prop" ? entry.position : entry.center;
  const moved = original.some((value, index) => Math.abs(value - position[index]) > 0.00005);
  if (moved) ops.push({ op: "move", args: { id: info.id, to: position } });
  const originalQuat = entry.rotationQuat || [0, 0, 0, 1];
  const rotated = quaternion.some((value, index) => Math.abs(value - originalQuat[index]) > 0.000001);
  if (rotated) ops.push({ op: "set", args: { id: info.id, rotationQuat: quaternion } });
  if (!ops.length) return true;
  setSaving();
  try {
    const response = await api.post({ ops });
    if (!response.ok) {
      setSaveError();
      showError(response.error, response.details || []);
      return false;
    }
    await refreshState();
    setSaved(response.saved);
    return true;
  } catch (error) {
    setSaveError();
    showError(error.message, error.details || []);
    return false;
  }
}

async function deleteSelection() {
  if (!view.selection) return;
  const { id, type } = view.selection;
  const response = await sendOp({
    op: "remove",
    args: { id, updateExpected: $("auto-expected").checked },
  });
  if (response?.ok) {
    view.select(null);
    toast(`已删除 ${id}`, "ok", 2000);
  }
}

async function duplicateSelection() {
  const info = view.selectionInfo();
  if (!info) return;
  if (info.type === "prop") {
    const prop = findEntry("prop", info.id);
    if (!prop) return;
    const position = [prop.position[0] + 0.12, prop.position[1], prop.position[2] + 0.12];
    const args = {
      def: prop.def,
      position,
      rotationQuat: prop.rotationQuat,
      kind: prop.kind,
      zone: prop.zone,
      support: prop.support,
      stackedOn: prop.stackedOn,
      valueUnits: prop.valueUnits,
      seat: Boolean(prop.support || prop.stackedOn),
      updateExpected: $("auto-expected").checked,
    };
    const response = await sendOp({ op: "add_prop", args });
    if (response?.ok) {
      view.select(response.result.itemId);
      toast(`已复制为 ${response.result.itemId}`, "ok", 2400);
    }
  } else {
    const structure = findEntry("structure", info.id);
    if (!structure) return;
    const args = {
      role: structure.role,
      kind: structure.kind,
      center: [structure.center[0] + 0.25, structure.center[1], structure.center[2] + 0.25],
      size: structure.size,
      rotationQuat: structure.rotationQuat,
      collision: structure.collision,
      support: structure.support,
      meets: structure.meets,
    };
    const response = await sendOp({ op: "add_structure", args });
    if (response?.ok) {
      view.select(response.result.id);
      toast(`已复制为 ${response.result.id}`, "ok", 2400);
    }
  }
}

async function seatSelection() {
  if (!view.selection) return;
  await sendOp({ op: "seat", args: { ids: [view.selection.id] } });
}

async function addStructureFromForm() {
  const role = $("struct-role").value;
  const center = [Number($("struct-x").value), Number($("struct-y").value), Number($("struct-z").value)];
  const size = [Number($("struct-sx").value), Number($("struct-sy").value), Number($("struct-sz").value)];
  if (size.some((value) => !Number.isFinite(value) || value <= 0)) {
    showError("尺寸必须是正数");
    return;
  }
  const args = { role, center, size, yaw: Number($("struct-yaw").value) || 0 };
  const id = $("struct-id").value.trim();
  if (id) args.id = id;
  const response = await sendOp({ op: "add_structure", args });
  if (response?.ok) {
    view.select(response.result.id);
    toast(`已加入 ${response.result.id}`, "ok", 2200);
  }
}

async function dropPalette(def, event) {
  const manifest = state.manifests[def];
  if (!manifest) return;
  if (!manifest.valueUnits) {
    showError(`def ${def} 没有 valueUnits；先用 add_def 声明价值`);
    return;
  }
  const drop = view.dropPoint(event.clientX, event.clientY);
  const ground = Number((state.doc.level || {}).groundY || 0);
  const aabb = manifest.aabb;
  let position;
  let support;
  let stackedOn;
  if (drop) {
    position = [drop.point[0], Math.max(drop.point[1], ground) + aabb[1] / 2, drop.point[2]];
    if (drop.target?.type === "structure") {
      const structure = (state.doc.structures || []).find((entry) => entry.id === drop.target.id);
      // Ground is the floor (not a support plane), and boundaries are walls; only a
      // support=true box/ramp gives a top plane to seat onto.
      if (structure?.support && ["box", "ramp"].includes(structure.role)) {
        support = structure.id;
      } else {
        position[1] = ground + aabb[1] / 2;
      }
    } else if (drop.target?.type === "prop") {
      stackedOn = drop.target.id;
    }
  } else {
    const level = state.doc.level || {};
    const pad = (state.doc.structures || []).find((entry) => entry.role === "ground");
    const start = level.ballStart
      || (pad ? [pad.center[0], 0.15, pad.center[2]] : [0, 0.15, 0]);
    position = [start[0], ground + aabb[1] / 2, start[2]];
  }
  const args = {
    def,
    position,
    rotationQuat: [0, 0, 0, 1],
    kind: manifest.kind,
    support,
    stackedOn,
    seat: true,
    updateExpected: $("auto-expected").checked,
  };
  const response = await sendOp({ op: "add_prop", args });
  if (response?.ok) {
    view.select(response.result.itemId);
    toast(`已放置 ${manifest.nameZh || def} → ${response.result.itemId}`, "ok", 2400);
  }
}

// --------------------------------------------------------------------------- //
// structure defaults per role
// --------------------------------------------------------------------------- //
function updateStructureDefaults() {
  const role = $("struct-role").value;
  if (role === "ramp") {
    $("struct-y").value = "0.1";
    $("struct-sx").value = "1.0";
    $("struct-sy").value = "0.04";
    $("struct-sz").value = "2.0";
  } else if (role === "boundary") {
    $("struct-y").value = "0.6";
    $("struct-sx").value = "1.2";
    $("struct-sy").value = "0.04";
    $("struct-sz").value = "4.0";
  } else {
    $("struct-y").value = "0.25";
    $("struct-sx").value = "1.0";
    $("struct-sy").value = "0.5";
    $("struct-sz").value = "1.0";
  }
  const center = view._levelCenter?.();
  if (center) {
    $("struct-x").value = center.x.toFixed(2);
    $("struct-z").value = (-center.z).toFixed(2);
  }
}

// --------------------------------------------------------------------------- //
// keyboard
// --------------------------------------------------------------------------- //
function typingInField(event) {
  const tag = event.target?.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

function bindKeyboard() {
  window.addEventListener("keydown", (event) => {
    if (event.code === "Tab") {
      event.preventDefault();
      if (view.mode === "walk") view.exitWalk();
      else view.enterWalk();
      setMode(view.mode);
      return;
    }
    if (typingInField(event)) return;
    if (event.key === "Escape") {
      if (view.mode === "walk") return;
      view.select(null);
      return;
    }
    if (view.mode === "walk") return;
    const command = event.ctrlKey || event.metaKey;
    if (command && event.code === "KeyZ") {
      event.preventDefault();
      if (event.shiftKey) redo();
      else undo();
      return;
    }
    if (command && event.code === "KeyY") {
      event.preventDefault();
      redo();
      return;
    }
    if (command && event.code === "KeyD") {
      event.preventDefault();
      duplicateSelection();
      return;
    }
    if (command) return;
    if (event.code === "KeyG") {
      view.setSelectionMode("translate");
      renderSelection(view.selection ? view.selectionInfo() : null);
    } else if (event.code === "KeyR") {
      view.setSelectionMode("rotate");
      renderSelection(view.selection ? view.selectionInfo() : null);
    } else if (event.code === "KeyF") {
      view.restOnSurface();
    } else if (event.code === "Delete" || event.code === "Backspace") {
      event.preventDefault();
      deleteSelection();
    } else if (event.code.startsWith("Arrow")) {
      event.preventDefault();
      const step = (Number($("snap-translate").value) || 0.05) * (event.shiftKey ? 4 : 1);
      const map = {
        ArrowLeft: [-step, 0, 0],
        ArrowRight: [step, 0, 0],
        ArrowUp: [0, 0, -step],
        ArrowDown: [0, 0, step],
      };
      const delta = map[event.code];
      if (delta) view.nudge(delta[0], delta[1], delta[2]);
    }
  });
}

// --------------------------------------------------------------------------- //
// boot
// --------------------------------------------------------------------------- //
async function boot() {
  setLoading("连接服务器…");
  try {
    const payload = await api.loadLevel();
    state = payload.state;
  } catch (error) {
    setLoading("");
    showError(`连不上服务器：${error.message}`);
    return;
  }

  view = new EditorView($("canvas"), {
    onSelect: (info) => renderSelection(info),
    onTransform: commitTransform,
    onError: (message) => showError(message),
    onProgress: (text) => {
      if (text) setLoading(text);
    },
    onStatus: (status) => {
      if (status.startsWith("pointer")) {
        setMode(view.mode);
        $("walk-lock").textContent = view.walk.fallback
          ? "指针锁定不可用：按住右键拖动看方向 · WASD 移动"
          : (document.pointerLockElement === $("canvas")
            ? "已锁定：移动鼠标看方向 · Esc 释放"
            : "点击画面锁定鼠标 · Esc 释放");
      }
    },
  });

  bindKeyboard();
  bindUI();
  await view.load({ ...state });
  currentLevelChanged();
  renderAll();
  setMode("orbit");
  setLoading("");
  setSaved(null);

  api.connectEvents((version) => {
    if (state && version <= state.version) return;
    scheduleRefresh();
  }, (status) => {
    $("status-live").textContent = status === "live" ? "实时" : "重连中…";
    $("status-live").className = `live ${status === "live" ? "" : "retrying"}`;
  });

  // Test/browser-automation surface.  Read-only conversions are also handy for
  // the owner's own scripts; it is not a second API.
  window.__leveleditor = {
    get state() { return state; },
    view,
    api,
    ready: true,
    audit: (tolerance) => view.audit(tolerance),
    async setD(diameter) {
      $("ladder").value = String(diameter);
      $("ladder-on").checked = true;
      updateLadderFromSlider();
      await refreshHints();
      return Number($("ladder").value);
    },
    setLadderColouring(enabled) {
      $("ladder-on").checked = Boolean(enabled);
      updateLadderFromSlider();
    },
    select(id, focus = true) { return view.select(id, { focus }); },
    setCamera(id) { return view.setCameraPreset(id); },
    walk(on = true) {
      if (on) view.enterWalk();
      else view.exitWalk();
      setMode(view.mode);
    },
    async screenshot() { return view.screenshot(); },
    async openLevel(id) { await openLevel(id); return state?.level; },
    async op(name, args) { return sendOp({ op: name, args }); },
    async refresh() { await refreshState(); return state; },
    selection() { return view.selectionInfo(); },
  };
  document.body.dataset.ready = "1";
}

function bindUI() {
  $("open-level").addEventListener("click", () => openLevel($("level-select").value));
  $("reload-level").addEventListener("click", async () => {
    const response = await sendOp({ op: "reload", args: {} });
    if (response?.ok) toast("已从磁盘重载", "ok", 2200);
  });
  $("undo").addEventListener("click", undo);
  $("redo").addEventListener("click", redo);
  $("screenshot").addEventListener("click", () => {
    const result = view.screenshot();
    toast(`已保存 ${result.name}`, "ok", 3000);
  });
  $("mode-orbit").addEventListener("click", () => {
    view.exitWalk();
    setMode("orbit");
  });
  $("mode-walk").addEventListener("click", () => {
    view.enterWalk();
    setMode("walk");
  });
  $("eye-height").addEventListener("input", () => view.setEyeHeight($("eye-height").value));
  for (const button of document.querySelectorAll("[data-eye]")) {
    button.addEventListener("click", () => {
      $("eye-height").value = button.dataset.eye;
      view.setEyeHeight(button.dataset.eye);
    });
  }
  $("gizmo-move").addEventListener("click", () => {
    view.setSelectionMode("translate");
    renderSelection(view.selectionInfo());
  });
  $("gizmo-rotate").addEventListener("click", () => {
    view.setSelectionMode("rotate");
    renderSelection(view.selectionInfo());
  });
  $("rest-surface").addEventListener("click", () => view.restOnSurface());
  $("drop-ground").addEventListener("click", () => view.dropToGround());
  $("duplicate").addEventListener("click", duplicateSelection);
  $("delete").addEventListener("click", deleteSelection);
  $("seat").addEventListener("click", seatSelection);
  $("snap-translate").addEventListener("change", () => {
    view.setSnapping({ translate: Number($("snap-translate").value) });
  });
  $("snap-rotate").addEventListener("change", () => {
    view.setSnapping({ rotate: Number($("snap-rotate").value) });
  });
  $("ladder").addEventListener("input", () => {
    $("ladder-on").checked = true;
    updateLadderFromSlider();
  });
  $("ladder-on").addEventListener("change", updateLadderFromSlider);
  $("palette-search").addEventListener("input", filterPalette);
  $("struct-role").addEventListener("change", updateStructureDefaults);
  $("add-structure").addEventListener("click", addStructureFromForm);
  $("recheck").addEventListener("click", async () => {
    await refreshState();
    toast("已重新校验", "ok", 1800);
  });
  $("level-select").addEventListener("change", () => {
    // Opening is explicit; just preview the id.
  });

  const viewport = $("viewport");
  viewport.addEventListener("dragover", (event) => {
    if (!dragDef) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
    $("drop-hint").classList.remove("hidden");
  });
  viewport.addEventListener("dragleave", (event) => {
    if (event.target !== viewport) return;
    $("drop-hint").classList.add("hidden");
  });
  viewport.addEventListener("drop", async (event) => {
    event.preventDefault();
    $("drop-hint").classList.add("hidden");
    const def = event.dataTransfer.getData("text/plain") || dragDef;
    dragDef = null;
    if (def) await dropPalette(def, event);
  });
}

// The level list is part of the state payload; rebuild the select from it.
function fillLevelSelect() {
  const select = $("level-select");
  const levels = state.levels || [];
  select.replaceChildren();
  for (const level of levels) {
    const option = document.createElement("option");
    option.value = level.id;
    option.textContent = level.id;
    option.title = `${level.id} · ${level.bytes} B · ${level.modified}`;
    select.appendChild(option);
  }
  select.value = state.level || "";
}

boot().catch((error) => {
  console.error(error);
  setLoading("");
  showError(`启动失败：${error.message}`);
});

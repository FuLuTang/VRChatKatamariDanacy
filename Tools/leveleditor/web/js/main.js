/**
 * UI wiring for the web level editor.
 *
 * Unity scene-view interaction lives in view.js/camera.js.  This file owns the
 * panels, the tool HUD, the op queue, and the one rule that matters: every edit is
 * one JSON op (or one atomic batch) to the Python server, which validates, gates and
 * atomically exports placement.json.  The browser never decides that an edit is valid.
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
let currentTool = "move";
let regions = [];
let activeRegion = null;
let regionMode = false;
let stats = null;
let statsCompare = null;
let statsScope = "all";
let statsMode = "center";
let statsMetric = "count";
let statsLog = false;
let statsTimer = null;
let regionSaveTimer = null;
let statsGeneration = 0;
let regionsLevel = null;
const thumbnails = new Map();
const paletteNodes = new Map();

// --------------------------------------------------------------------------- //
// UI helpers
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
    summary = `已保存 +${props.added + structures.added} `
      + `~${props.updated + structures.updated} -${props.removed + structures.removed}`;
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

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (character) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;",
  }[character]));
}

function findEntry(type, id) {
  if (!state) return null;
  return type === "prop"
    ? (state.doc.props || []).find((entry) => entry.itemId === id)
    : (state.doc.structures || []).find((entry) => entry.id === id);
}

// --------------------------------------------------------------------------- //
// state refresh
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
  const previousFile = state?.file?.path;
  state = next;
  const levelChanged = previousLevel !== next.level
    || previousFile !== next.file?.path;
  await view.applyState(next);
  if (levelChanged) currentLevelChanged();
  renderAll();
  if (levelChanged) setLoading("");
}

function currentLevelChanged() {
  clearTimeout(statsTimer);
  clearTimeout(regionSaveTimer);
  statsGeneration += 1;
  regions = [];
  activeRegion = null;
  regionsLevel = null;
  if (view) view.setRegions([], null);
  statsScope = "all";
  statsMetric = "count";
  stats = null;
  statsCompare = null;
  activeRegion = null;
  $("stats-scope").value = "all";
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
  loadRegionsForLevel();
  updateToolHud();
  if (view) {
    $("show-parent-lines").checked = false;
    view.setParentLines(false);
  }
}

function renderAll() {
  renderLevelFacts();
  renderValidation();
  renderSelection(view?.selectionInfo?.() || null);
  renderStatus();
  scheduleStats(120);
  $("undo").disabled = !state.history?.undo;
  $("redo").disabled = !state.history?.redo;
  $("level-id").textContent = state.level || "—";
  $("level-path").textContent = state.file?.path || "";
  if (!state.lastSaved && !$("save-status").classList.contains("saving")) {
    $("save-text").textContent = state.gate?.ok ? "已就绪 · 没有未保存改动" : "已打开（文件有错误）";
    $("save-status").className = "save-status";
  }
  if (!api.hasToken) $("readonly-banner").classList.remove("hidden");
  renderRefMode();
  updateToolHud();
  updateCameraButtons();
}

// --------------------------------------------------------------------------- //
// UNLICENSED reference levels (read-only, local ripped meshes)
// --------------------------------------------------------------------------- //
function renderRefMode() {
  const ref = state?.ref || null;
  const v2 = state?.v2 || null;
  const legacy = state?.legacy || null;
  const banner = $("ref-banner");
  if (ref) {
    const counts = ref.modelCounts || {};
    banner.classList.remove("hidden");
    if (v2) {
      const c = v2.counts || {};
      const terrain = v2.terrain || {};
      $("ref-banner-text").textContent = `[v2] ${ref.banner} `
        + `${state.file?.path || ""} · `
        + `地形 ${terrain.counts?.triangles ?? "?"} 三角 / ${terrain.counts?.vertices ?? "?"} 顶点 · `
        + `固体 ${c.solids ?? "?"} · 活动墙 ${c.walls ?? "?"} · 道具 ${c.props ?? "?"}`
        + `（旋转 ${c.rotatedProps ?? 0} / 缩放 ${c.scaledProps ?? 0} / 父子 ${c.parentLinks ?? 0}）· `
        + `本地网格 ${counts.resolved ?? 0}/${counts.total ?? 0} · 只读`;
    } else {
      $("ref-banner-text").textContent = `${ref.banner} ${ref.placement} · `
        + `本地网格 ${counts.resolved ?? 0}/${counts.total ?? 0}（${ref.modelsDir ?? "?"}）· `
        + `只读，不能编辑或导出。`;
    }
    document.body.classList.add("ref-mode");
  } else {
    banner.classList.add("hidden");
    $("ref-banner-text").textContent = "";
    document.body.classList.remove("ref-mode");
  }
  const tools = $("ref-tools");
  const stage = ref?.stage || null;
  tools.classList.toggle("hidden", Boolean(v2) || (!ref?.available && !stage?.available));
  const v2tools = $("v2-tools");
  v2tools.classList.toggle("hidden", !v2);
  if (v2) {
    $("show-v2-terrain").checked = view?.showTerrain ?? true;
    $("show-v2-solids").checked = view?.v2ShowSolids ?? true;
    $("show-v2-walls").checked = view?.v2ShowWalls ?? true;
    $("show-v2-kill-plane").checked = view?.v2ShowKill ?? true;
    $("show-v2-parent-lines").checked = view?.showParentLines ?? false;
    $("v2-apply-rotation").checked = view?.v2Rotations ?? true;
    $("v2-apply-scale").checked = view?.v2Scale ?? true;
  }
  if (ref && !v2) {
    $("show-terrain").checked = view?.showTerrain ?? true;
    $("terrain-wireframe").checked = view?.terrainWireframe ?? false;
    $("show-collision").checked = view?.showCollision ?? false;
    $("show-legacy-planes").checked = view?.showLegacyPlanes ?? false;
    $("use-original-height").checked = view?.useOriginalHeight ?? true;
    for (const id of ["show-terrain", "terrain-wireframe", "show-collision",
                      "show-legacy-planes", "use-original-height"]) {
      const input = $(id);
      input.disabled = !stage?.available;
      if (!stage?.available) input.checked = false;
    }
  }
  const legacyBanner = $("legacy-banner");
  if (legacy) {
    legacyBanner.classList.remove("hidden");
    $("legacy-text").textContent = `${legacy.message} 当前文件：`
      + `${state.file?.path || "?"}（v1 校验器：${state.check?.errorCount ?? "?"} 个错误，`
      + `这些数字不代表 v2 数据）。`;
    document.body.classList.add("has-legacy");
  } else {
    legacyBanner.classList.add("hidden");
    document.body.classList.remove("has-legacy");
  }
  for (const id of ["add-structure", "auto-expected", "duplicate", "delete", "seat",
                    "rest-surface", "drop-ground"]) {
    const element = $(id);
    if (!element) continue;
    if (ref) {
      element.dataset.refDisabled = "1";
      element.disabled = true;
    } else if (element.dataset.refDisabled) {
      delete element.dataset.refDisabled;
      element.disabled = false;
    }
  }
  const unitsButton = document.querySelector('#stats-metrics [data-metric="units"]');
  if (unitsButton) unitsButton.textContent = v2 ? "growthMm³" : "valueUnits";
  view?.setReadOnly(Boolean(ref?.readOnly || v2));
  const toggle = $("parent-toggle");
  const links = (v2 ? v2.counts?.parentLinks : ref?.parentLinkCount) || 0;
  toggle.classList.toggle("hidden", !links || Boolean(v2));
  $("parent-count").textContent = links ? `(${links})` : "";
}

function renderStatus() {
  $("status-level").textContent = state?.level || "—";
  const counts = state?.counts || {};
  const v2 = state?.v2 || null;
  $("status-counts").textContent = counts.props === undefined
    ? "" : (v2
      ? `${counts.props} 道具 · ${counts.structures} 固体 · growth ${formatNumber((counts.growthMm3 || 0) / 1e9, 2)} ×10⁹ mm³`
      : `${counts.props} 道具 · ${counts.structures} 结构 · ${counts.valueUnits} u`);
}

// --------------------------------------------------------------------------- //
// level facts
// --------------------------------------------------------------------------- //
function renderLevelFacts() {
  const doc = state.doc;
  const level = doc.level || {};
  const bounds = level.bounds || [[0, 0], [0, 0]];
  const v2 = state.v2 || null;
  const rows = [
    ["id", level.id || "—"],
    ["schema", v2 ? "katamari.placement/2" : "katamari.placement/0 (v1)"],
    ["bounds", `x ${bounds[0][0]}..${bounds[1][0]} · z ${bounds[0][1]}..${bounds[1][1]}`],
    ["地面", `y = ${formatNumber(level.groundY ?? 0, 3)} m`],
    ["球起点", formatVec(level.ballStart, 2)],
    ["球径", `${formatNumber(level.ballDiameterStart, 2)} → ${formatNumber(level.ballDiameterMax, 2)} m`],
    ["道具", `${state.counts.props} 件 · ${state.counts.valueUnits ?? 0} u`
      + (v2 ? ` · growth ${formatNumber((state.counts.growthMm3 || 0) / 1e9, 2)} ×10⁹ mm³` : "")],
    ["expected", v2
      ? `${level.expected?.propCount ?? "—"} 件 / ${level.expected?.structureCount ?? "—"} 固体 / ${level.expected?.terrainTriangles ?? "—"} 地形三角`
      : `${level.expected?.propCount ?? "—"} 件 / ${level.expected?.valueUnits ?? "—"} u`],
    ["结构", v2
      ? `${state.counts.structures} 固体 + ${v2.counts?.walls ?? 0} 活动墙 + kill plane y=${v2.activity?.killPlane?.y ?? "?"}`
      : `${state.counts.structures}（box ${level.budget?.boxes ?? "?"} / ramp ${level.budget?.ramps ?? "?"} / plane ${level.budget?.planes ?? "?"}）`],
  ];
  if (v2) {
    const c = v2.counts || {};
    const t = v2.terrain || {};
    rows.push(["v2 地形", `${t.counts?.triangles ?? "?"} 三角 / ${t.counts?.vertices ?? "?"} 顶点 / `
      + `${t.counts?.chunks ?? t.chunks ?? "?"} chunk / ${t.broadPhaseBoxes ?? "?"} broad-phase`]);
    rows.push(["v2 活动区", `${c.walls ?? 0} 面墙 · kill plane y=${v2.activity?.killPlane?.y ?? "?"}`]);
    rows.push(["v2 变换", `旋转 ${c.rotatedProps ?? 0} 件 · 缩放 ${c.scaledProps ?? 0} 件 · 父子 ${c.parentLinks ?? 0} 条`]);
    rows.push(["时限", `${v2.meta?.timeLimitS ?? "—"} s`]);
    rows.push(["v2 caps", `props ${v2.meta?.caps?.props ?? "?"} / terrain ${v2.meta?.caps?.terrainTriangles ?? "?"} tri / solids ${v2.meta?.caps?.staticSolids ?? "?"}`]);
  }
  if (state.ref) {
    const counts = state.ref.modelCounts || {};
    rows.push(["参考资产", `UNLICENSED · 本地网格 ${counts.resolved ?? 0}/${counts.total ?? 0} · 只读`]);
    rows.push(["父子指针", `${state.ref.parentLinkCount ?? 0} 条（${v2 ? "props[].parent" : "group/groupId"}）`]);
    const stage = state.ref.stage || null;
    if (stage) {
      const instances = stage.instances || {};
      rows.push(["原地形", stage.available
        ? `${stage.terrain?.vertices ?? "?"} 顶点 · ${stage.terrain?.materials ?? "?"} 材质段 · ${stage.dir}`
        : `无（${stage.reason || "?"}）`]);
      if (stage.available) {
        rows.push(["碰撞体", `${stage.collision?.aabbs ?? "?"} AABB · ${stage.collision?.triangles ?? "?"} 三角形 · zone ${stage.zones ?? "?"}`]);
        rows.push(["实例还原", `配对 ${instances.matched ?? "?"}/${stage.counts?.props ?? "?"}`
          + ` · ≤1cm ${instances.exact ?? "?"} · 旋转 ${instances.rotated ?? 0} · 缩放 ${instances.scaled ?? 0}`]);
        rows.push(["原作父表", `真父子 ${instances.parentsResolved ?? 0}（挂接件 ${instances.withParent ?? 0}）`]);
        rows.push(["坐标帧", stage.frame
          ? `reroll = ${stage.frame.scale} × PS2 cm · 中位误差 ${formatNumber(stage.frame.medianError, 4)} m`
          : "?"]);
      }
    }
  }
  $("level-facts").innerHTML = rows
    .map(([key, value]) => `<div class="k">${escapeHtml(key)}</div><div class="v">${escapeHtml(value)}</div>`)
    .join("");
}

// --------------------------------------------------------------------------- //
// cameras and tools
// --------------------------------------------------------------------------- //
function buildCameraButtons() {
  const container = $("camera-buttons");
  container.replaceChildren();
  for (const camera of view.cameraButtonNames()) {
    const button = document.createElement("button");
    button.textContent = camera.label;
    button.dataset.camera = camera.id;
    button.addEventListener("click", () => {
      view.setCameraPreset(camera.id);
      updateCameraButtons();
    });
    container.appendChild(button);
  }
  updateCameraButtons();
}

function updateCameraButtons() {
  for (const button of $("camera-buttons").querySelectorAll("button")) {
    button.classList.toggle("active", button.dataset.camera === view?.currentCamera);
  }
}

function setTool(tool) {
  if (tool === "scale") {
    toast("缩放工具已禁用：道具使用真实尺寸，结构尺寸在左侧数值面板改。", "ok", 4200);
    return;
  }
  if (!view.setTool(tool)) return;
  currentTool = view.tool;
  updateToolHud();
  renderSelection(view.selectionInfo());
}

function updateToolHud() {
  for (const button of document.querySelectorAll("#tool-hud [data-tool]")) {
    button.classList.toggle("active", button.dataset.tool === view?.tool);
  }
  $("space-mode").textContent = view?.spaceMode === "local" ? "Local" : "Global";
  $("selection-count").textContent = `选中 ${view?.selectionIds.size || 0} 个`;
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
  const declared = new Set((state.doc.defs || []).map((entry) => entry.id));
  const used = new Set((state.doc.props || []).map((prop) => prop.def));
  const defs = Object.keys(manifests)
    .filter((def) => declared.has(def) || used.has(def)).sort();
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
    item.draggable = !state.ref && Boolean(manifest.valueUnits);
    item.dataset.def = def;
    item.dataset.category = manifest.category || "other";
    item.dataset.search = `${def} ${manifest.name} ${manifest.nameZh} ${manifest.category}`.toLowerCase();
    item.title = `${def}\n${manifest.nameZh || manifest.name}\n尺寸 ${formatVec(manifest.aabb, 3)} m\n`
      + `Req ${manifest.dReqMm ?? "?"} mm (k=${manifest.volumeK ?? 0.7})\n`
      + `${manifest.valueUnits ?? "?"} u`;
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
  if (!defs.length) {
    const note = document.createElement("div");
    note.className = "palette-empty";
    note.textContent = "此关卡尚未声明 defs[]（没有可放置的 def）。先 add_def 或用带道具的关卡。";
    list.appendChild(note);
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
      sub.textContent = `${formatVec(manifest.aabb, 2)} · ${manifest.valueUnits ?? "—"} u`
        + ` · Req ${manifest.dReqMm ?? "?"} mm`;
    }
  }
}

function filterPalette() {
  const needle = $("palette-search").value.trim().toLowerCase();
  const category = $("palette-filters").querySelector("button.active")?.dataset.category || "all";
  for (const item of paletteNodes.values()) {
    const matchesText = !needle || item.dataset.search.includes(needle);
    const matchesCategory = category === "all" || item.dataset.category === category;
    item.style.display = matchesText && matchesCategory ? "" : "none";
  }
}

let paletteObserver = null;
let thumbnailQueue = [];
let thumbnailRunning = false;

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
  }, { root: $("palette-list"), rootMargin: "120px" });
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
    ? (info.count > 1 ? `${info.count} 个对象`
      : (info.type === "prop" ? `prop · ${info.def}` : `structure · ${info.role}`))
    : "";
  $("selection-count").textContent = `选中 ${info?.count || 0} 个`;
  if (!info) return;

  const rows = [];
  if (info.count === 1) {
    rows.push(["id", `<b>${escapeHtml(info.id)}</b>`]);
    if (info.type === "prop") {
      rows.push(["def", escapeHtml(info.def)]);
      rows.push(["kind/zone", escapeHtml(`${info.kind || "?"} / ${info.zone || "—"}`)]);
      rows.push(["aabb 尺寸", `${formatVec(info.aabb, 3)} m`]);
      rows.push(["Req（整 mm 门槛）",
        `${info.dReqMm === undefined ? "—" : `${info.dReqMm} mm`}`
        + ` <span class="muted">· ${formatNumber(info.dReq, 3)} m</span>`]);
      const ladderD = view?.ladder?.diameter;
      if (ladderD) {
        const ballMm = Math.floor(Number(ladderD) * 1000 + 1e-6);
        rows.push(["可吸 @ 当前梯档",
          `${ballMm} mm 球 → <b>${info.dReqMm !== undefined && info.dReqMm <= ballMm
            ? "现在可吸" : "太大 / 挡路"}</b>`]);
      }
      rows.push(["valueUnits", String(info.valueUnits ?? "—")]);
      {
        // The per-item volume factor k: `Volume = k * a*b*c`, the original's default 0.70.
        // It lives on the DEF (a whole type shares one number); an empty box means
        // "no override" and clearing it restores 0.70 without writing a key.
        const defEntry = (state.doc.defs || []).find((entry) => entry.id === info.def);
        const current = info.volumeK ?? (defEntry && defEntry.volumeK) ?? 0.70;
        // Editable only for a def this level declares: set_def edits defs[] and refuses an
        // undeclared id, so offering the box there would only produce an error message.
        const editable = !state.ref && Boolean(defEntry);
        rows.push(["体积系数 k（def）",
          `<input id="def-volume-k" type="number" step="0.01" min="0.01" max="32"`
          + ` value="${current}"${editable ? "" : " disabled"} style="width:5.5em">`
          + (editable
            ? ` <button id="def-volume-k-apply" type="button">应用</button>`
              + ` <span class="muted">空 = 恢复 0.70 默认</span>`
            : ` <span class="muted">${state.ref ? "参考关卡只读"
              : "此 def 不在 defs[] 里，先 add_def"}</span>`)]);
      }
      rows.push([info.stackedOn ? "stackedOn" : "support",
        escapeHtml(info.stackedOn || info.support || "—")]);
      if (info.group || info.groupId) {
        rows.push(["group", escapeHtml(info.group || info.groupId)]);
        rows.push(["groupId", escapeHtml(info.groupId || info.group || "—")]);
      }
      if (info.parent) rows.push(["parent", escapeHtml(info.parent)]);
      if (info.behaviour) rows.push(["behaviour", escapeHtml(info.behaviour)]);
      if (info.scale && info.scale.some((value) => Number(value) !== 1)) {
        rows.push(["scale", formatVec(info.scale, 4)]);
      }
      if (info.absorbable !== undefined) rows.push(["absorbable", info.absorbable ? "true" : "false"]);
      if (info.growthMm3 !== undefined) rows.push(["growthMm3", String(info.growthMm3 ?? "—")]);
      if (info.refModel) {
        rows.push(["参考网格", "UNLICENSED · 本机缓存 · 禁止分发"]);
      }
      const instance = info.refInstance || null;
      if (instance) {
        const axis = Array.isArray(instance.axis) ? instance.axis : [];
        const turn = `${formatNumber(instance.angleDeg, 1)}° @ (${axis.map((v) => formatNumber(v, 2)).join(", ")})`;
        rows.push(["原作旋转", `${turn} <span class="muted">四元数 ${formatVec(instance.quat, 3)}</span>`]);
        rows.push(["原作缩放", `${formatVec(instance.scale, 3)}`]);
        rows.push(["原作父件", instance.parent
          ? `${escapeHtml(instance.parent)} <span class="muted">#${instance.parentInstance}</span>`
          : (instance.parentInstance === null || instance.parentInstance === undefined
            ? "无"
            : `<span class="muted">#${instance.parentInstance}（挂接件，父为哑锚点）</span>`)]);
        rows.push(["分区", `area ${instance.area ?? "—"} · 实例 #${instance.instance ?? "—"}`
          + (instance.zone === null || instance.zone === undefined
            ? " · 碰撞 zone —"
            : ` · 碰撞 zone ${instance.zone}（面 y=${formatNumber(instance.zoneSurfaceY, 2)}）`)]);
        if (info.positions?.placed && instance.position) {
          const placed = info.positions.placed;
          const original = instance.position;
          rows.push(["高度", info.refApplied
            ? `显示用原作 y=${formatNumber(original[1], 3)} · 文件 y=${formatNumber(placed[1], 3)}`
            : `文件 y=${formatNumber(placed[1], 3)} · 原作 y=${formatNumber(original[1], 3)}`]);
        }
      }
    } else {
      rows.push(["role", escapeHtml(info.role || "—")]);
      rows.push(["size", `${formatVec(info.size, 3)} m`]);
      rows.push(["kind", escapeHtml(info.kind || "—")]);
    }
    rows.push(["position", formatVec(info.position, 3)]);
  } else {
    rows.push(["对象", `${info.props || 0} 道具 + ${info.structures || 0} 结构`]);
    rows.push(["ids", info.ids.slice(0, 6).map(escapeHtml).join(", ") + (info.ids.length > 6 ? " …" : "")]);
    rows.push(["valueUnits", String(info.valueUnits ?? 0)]);
    rows.push(["pivot", formatVec(info.position, 3)]);
  }
  if (info.worldAabb) {
    const low = info.worldAabb[0];
    const high = info.worldAabb[1];
    const size = [high[0] - low[0], high[1] - low[1], high[2] - low[2]];
    rows.push(["世界轴对齐盒", `${formatVec(size, 3)} m`]);
    rows.push(["", `<span class="muted">旋转后的外包盒；v1 粗筛/互穿校验用，v2 用真实 OBB</span>`]);
    rows.push(["世界 AABB", `x ${formatNumber(low[0], 2)}..${formatNumber(high[0], 2)} · y ${formatNumber(low[1], 2)}..${formatNumber(high[1], 2)} · z ${formatNumber(low[2], 2)}..${formatNumber(high[2], 2)}`]);
  }
  $("selection-kv").innerHTML = rows
    .map(([key, value]) => `<div class="k">${key}</div><div class="v">${value}</div>`)
    .join("");
  const applyVolumeK = $("def-volume-k-apply");
  if (applyVolumeK && info.type === "prop") {
    applyVolumeK.addEventListener("click", () => applyDefVolumeK(info.def));
  }
  $("gizmo-move").classList.toggle("active", view.tool === "move");
  $("gizmo-rotate").classList.toggle("active", view.tool === "rotate");
}

// The per-def volume factor (the original's `Volume = k * a*b*c`, default 0.70).  One
// API call, validated server-side by placement_core.volume_k_error (finite, 0 < k <= 32);
// an empty input clears the key, which restores the default and keeps the file
// byte-identical to one that never declared a k.
async function applyDefVolumeK(defId) {
  if (!defId) return;
  const field = $("def-volume-k");
  if (!field) return;
  const raw = field.value.trim();
  let volumeK = null;
  if (raw !== "") {
    volumeK = Number(raw);
    if (!Number.isFinite(volumeK) || volumeK <= 0 || volumeK > 32) {
      showError("k 必须是有限数且 0 < k <= 32（原作默认 0.70）");
      return;
    }
  }
  const response = await sendOp({ op: "set_def", args: { id: defId, volumeK } });
  if (response?.ok) {
    toast(volumeK === null
      ? `def ${defId} 的 k 已恢复默认 0.70`
      : `def ${defId} 的 k = ${volumeK}（Req 已按 k 重算）`, "ok", 2400);
  }
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
  if (check.kind === "structural") line.push(`<span class="warn">${escapeHtml(check.label || "结构检查（v2）")}</span>`);
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
    for (const raw of messages) {
      const message = typeof raw === "string" ? raw : (raw?.message || JSON.stringify(raw));
      const item = document.createElement("li");
      item.className = kind;
      item.textContent = message;
      item.title = "点击定位；悬停高亮相关物体的 OBB";
      item.addEventListener("click", () => focusFromText(message));
      item.addEventListener("mouseenter", () => {
        const ids = idsInText(message);
        if (ids.length) view.highlightIds(ids);
      });
      item.addEventListener("mouseleave", () => view.clearHighlights());
      list.appendChild(item);
    }
  }
}

function idsInText(text) {
  const ids = [
    ...(state.doc.props || []).map((prop) => prop.itemId),
    ...(state.doc.structures || []).map((structure) => structure.id),
  ].sort((left, right) => right.length - left.length);
  const found = [];
  for (const id of ids) {
    if (text.includes(id) && !found.some((other) => other.includes(id))) found.push(id);
  }
  return found;
}

function focusFromText(text) {
  const ids = idsInText(text);
  if (!ids.length) {
    toast("这条提示里没有可以定位的对象 id", "ok", 2500);
    return;
  }
  view.select([ids[0]], { focus: true });
}

// --------------------------------------------------------------------------- //
// ladder + hints
// --------------------------------------------------------------------------- //
function updateLadderFromSlider() {
  const diameter = Number($("ladder").value);
  $("ladder-value").textContent = diameter.toFixed(2);
  view.setLadder(diameter, $("ladder-on").checked);
  scheduleHints();
  scheduleStats(80);
}

// --------------------------------------------------------------------------- //
// regions (sidecar) + server-computed stats
// --------------------------------------------------------------------------- //
function scheduleStats(delay = 150) {
  clearTimeout(statsTimer);
  statsTimer = setTimeout(refreshStats, delay);
}

function statsParams() {
  const params = {
    scope: statsScope === "selection" ? "ids" : statsScope,
    mode: statsMode,
    metric: statsMetric,
    bin: 0.05,
    diameter: Number($("ladder").value),
  };
  if (statsScope === "region") {
    if (!activeRegion) return null;
    params.region = activeRegion;
  } else if (statsScope === "selection") {
    params.ids = view?.selectedIds || [];
  }
  return params;
}

async function refreshStats() {
  if (!state || !view) return;
  const generation = ++statsGeneration;
  const levelAtStart = state.level;
  const params = statsParams();
  if (!params) {
    stats = null;
    $("cumulative").textContent = "先选中/新建一个区域";
    $("compare-table").replaceChildren();
    return;
  }
  try {
    const payload = await api.stats(params);
    if (generation !== statsGeneration || state.level !== levelAtStart) return;
    if (!payload.ok) {
      $("cumulative").textContent = `统计不可用：${payload.error}`;
      return;
    }
    stats = payload.stats;
    renderStats();
    const compare = await api.stats({ compare: 1, mode: statsMode,
      diameter: Number($("ladder").value) });
    if (generation !== statsGeneration || state.level !== levelAtStart) return;
    statsCompare = compare.ok ? compare.stats : null;
    renderCompareTable();
  } catch (error) {
    $("cumulative").textContent = `统计失败：${error.message}`;
  }
}

function renderStats() {
  if (!stats) return;
  const diameter = stats.diameter;
  const max = stats.ballDiameterMax;
  const tiers = stats.tiers;
  for (const tier of ["green", "yellow", "red", "grey"]) {
    $(`tier-${tier}`).textContent = tiers[tier].count;
    $(`tier-${tier}-vol`).textContent = formatNumber(tiers[tier].volume, 3);
  }
  $("sum-green").textContent = tiers.green.units;
  $("sum-total").textContent = stats.totals.units;
  const metric = statsMetric;
  const binValue = (bin) => metric === "volume" ? bin.volume
    : metric === "units" ? bin.units : bin.count;
  const peak = Math.max(1e-9, ...stats.bins.map(binValue));
  const histogram = $("histogram");
  histogram.replaceChildren();
  for (const bin of stats.bins) {
    const center = (bin.low + bin.high) / 2;
    const floorMm = (meters) => Math.floor(Number(meters) * 1000 + 1e-6);
    const centerMm = floorMm(center);
    const ballMm = floorMm(diameter);
    let tierColor = "red";
    if (centerMm > floorMm(max)) tierColor = "grey";
    else if (centerMm <= ballMm) tierColor = "green";
    else if (centerMm <= floorMm(diameter * 1.2)) tierColor = "yellow";
    const value = binValue(bin);
    const ratio = statsLog && value > 0
      ? Math.log(1 + value) / Math.log(1 + peak) : value / peak;
    const bar = document.createElement("div");
    bar.className = `bar ${tierColor}`;
    bar.style.height = `${Math.max(value ? 4 : 1, ratio * 100)}%`;
    const top = bin.items.slice().sort((left, right) =>
      (metric === "volume" ? right.volume - left.volume : right.units - left.units))
      .slice(0, 4).map((item) => item.id);
    bar.title = `${bin.low.toFixed(2)}–${bin.high.toFixed(2)} m`
      + ` (${Math.round(bin.low * 1000)}–${Math.round(bin.high * 1000)} mm)\n`
      + `件数 ${bin.count} · 体积 ${formatNumber(bin.volume, 4)} m³ (${bin.volumePct}%)\n`
      + `valueUnits ${bin.units}\n` + (top.length ? `如 ${top.join(", ")}` : "空");
    if (bin.ids.length) {
      bar.style.cursor = "pointer";
      bar.addEventListener("click", () => {
        view.select(bin.ids);
        renderSelection(view.selectionInfo());
      });
    }
    histogram.appendChild(bar);
  }
  renderCumulative();
  $("cumulative").title = "从起始球径到当前 D 的可吸体积累计";
  view.setStatsScope(stats.includedIds, stats.scope !== "all");
}

function renderCumulative() {
  const element = $("cumulative");
  if (!stats || !stats.cumulative.length) {
    element.replaceChildren();
    return;
  }
  const points = stats.cumulative;
  const maxVolume = Math.max(1e-9, ...points.map((point) => point.volume));
  const width = 300;
  const height = 46;
  const step = points.length > 1 ? width / (points.length - 1) : width;
  const path = points.map((point, index) => {
    const x = index * step;
    const y = height - (point.volume / maxVolume) * (height - 6) - 2;
    return `${index === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
  element.innerHTML = `<svg viewBox="0 0 ${width} ${height}" preserveAspectRatio="none">
    <path d="${path}" fill="none" stroke="#49b8e8" stroke-width="1.5"/>
  </svg><div class="cumulative-label">累计可吸 ${formatNumber(points[points.length - 1].volume, 3)} m³
    · 至 D=${points[points.length - 1].diameter.toFixed(2)} m</div>`;
}

function renderCompareTable() {
  const container = $("compare-table");
  container.replaceChildren();
  if (!statsCompare?.regions?.length) return;
  const table = document.createElement("table");
  table.innerHTML = `<thead><tr><th>区域</th><th>件数</th><th>体积 m³</th>
    <th>可吸体积%</th><th>Req 中位 mm</th></tr></thead>`;
  const body = document.createElement("tbody");
  for (const row of statsCompare.regions) {
    const tr = document.createElement("tr");
    tr.classList.toggle("active", row.name === activeRegion);
    tr.innerHTML = `<td>${escapeHtml(row.name)}</td><td>${row.count}</td>
      <td>${formatNumber(row.volume, 3)}</td>
      <td>${(row.absorbableRatio * 100).toFixed(0)}%</td>
      <td>${row.dReqMedianMm === null || row.dReqMedianMm === undefined
        ? "—" : Math.round(row.dReqMedianMm)}</td>`;
    body.appendChild(tr);
  }
  table.appendChild(body);
  container.appendChild(table);
}

function downloadCsv() {
  if (!stats) return;
  const rows = [["region", "metric", "low_m", "high_m", "count", "volume_m3",
    "units", "volume_pct"]];
  for (const bin of stats.bins) {
    rows.push([stats.region || stats.scope, statsMetric, bin.low, bin.high,
      bin.count, bin.volume, bin.units, bin.volumePct]);
  }
  const text = rows.map((row) => row.map((cell) => {
    const value = String(cell);
    return /[",\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
  }).join(",")).join("\n");
  const blob = new Blob([text], { type: "text/csv;charset=utf-8" });
  const link = document.createElement("a");
  link.href = URL.createObjectURL(blob);
  link.download = `${state.level}_${stats.region || stats.scope}_${statsMetric}.csv`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

async function loadRegionsForLevel() {
  const levelAtStart = state?.level || null;
  try {
    const payload = await api.loadRegions();
    if (state?.level !== levelAtStart) return;
    regions = payload.regions || [];
    regionsLevel = levelAtStart;
    activeRegion = regions.some((entry) => entry.name === activeRegion)
      ? activeRegion : (regions[0]?.name || null);
    view.setRegions(regions, activeRegion);
    renderRegions();
    scheduleStats(80);
  } catch (error) {
    showError(`区域读取失败：${error.message}`);
  }
}

function renderRegions() {
  const list = $("region-list");
  list.replaceChildren();
  for (const region of regions) {
    const row = document.createElement("div");
    row.className = "region-row" + (region.name === activeRegion ? " active" : "");
    const visible = document.createElement("input");
    visible.type = "checkbox";
    visible.checked = region.visible !== false;
    visible.title = "显示/隐藏";
    visible.addEventListener("change", () => {
      region.visible = visible.checked;
      view.setRegions(regions, activeRegion);
      saveRegionsDebounced();
    });
    const name = document.createElement("button");
    name.className = "name";
    name.textContent = region.name;
    name.addEventListener("click", () => {
      activeRegion = region.name;
      view.setActiveRegion(activeRegion);
      renderRegions();
      if (statsScope === "region") scheduleStats(40);
    });
    const rename = document.createElement("button");
    rename.className = "mini";
    rename.textContent = "✎";
    rename.title = "改名";
    rename.addEventListener("click", () => {
      const next = window.prompt("区域名", region.name);
      if (!next || next === region.name) return;
      const clean = next.trim();
      if (!clean || regions.some((entry) => entry !== region && entry.name === clean)) {
        showError("区域名必须非空且唯一");
        return;
      }
      if (activeRegion === region.name) activeRegion = clean;
      region.name = clean;
      view.setRegions(regions, activeRegion);
      renderRegions();
      saveRegionsDebounced();
      scheduleStats(40);
    });
    const remove = document.createElement("button");
    remove.className = "mini danger";
    remove.textContent = "×";
    remove.title = "删除";
    remove.addEventListener("click", () => {
      regions = regions.filter((entry) => entry !== region);
      if (activeRegion === region.name) activeRegion = regions[0]?.name || null;
      view.setRegions(regions, activeRegion);
      renderRegions();
      saveRegionsDebounced();
      scheduleStats(40);
    });
    row.append(visible, name, rename, remove);
    list.appendChild(row);
  }
  const editor = $("region-editor");
  const region = regions.find((entry) => entry.name === activeRegion);
  editor.classList.toggle("hidden", !region);
  if (!region) return;
  $("region-name").value = region.name;
  for (const key of ["min", "max"]) {
    for (let axis = 0; axis < 3; axis += 1) {
      $(`region-${key}-${["x", "y", "z"][axis]}`).value = region[key][axis];
    }
  }
  $("region-visible").checked = region.visible !== false;
}

function saveRegionsDebounced() {
  clearTimeout(regionSaveTimer);
  regionSaveTimer = setTimeout(saveRegionsNow, 250);
}

async function saveRegionsNow() {
  if (regionsLevel !== state?.level) return;
  try {
    const response = await api.saveRegions(regions);
    $("region-save-status").textContent = response.ok
      ? `已保存 ${response.saved.regions} 个区域` : "保存失败";
  } catch (error) {
    $("region-save-status").textContent = `保存失败：${error.message}`;
  }
}

function addRegion(region) {
  let name = region.name || `区域 ${regions.length + 1}`;
  let index = 2;
  while (regions.some((entry) => entry.name === name)) {
    name = `${region.name || "区域"} ${index}`;
    index += 1;
  }
  const colors = ["#8fd4ff", "#ffb35a", "#8ce29a", "#e28fd4"];
  const entry = { name, min: region.min, max: region.max, visible: true,
    color: colors[regions.length % colors.length] };
  regions.push(entry);
  activeRegion = name;
  view.setRegions(regions, activeRegion);
  renderRegions();
  saveRegionsDebounced();
  if (statsScope === "all") {
    statsScope = "region";
    $("stats-scope").value = "region";
  }
  scheduleStats(40);
}

function updateActiveRegionFromInputs() {
  const region = regions.find((entry) => entry.name === activeRegion);
  if (!region) return;
  const name = $("region-name").value.trim();
  if (name && name !== region.name
      && !regions.some((entry) => entry !== region && entry.name === name)) {
    region.name = name;
    activeRegion = name;
  }
  for (const key of ["min", "max"]) {
    for (let axis = 0; axis < 3; axis += 1) {
      const value = Number($(`region-${key}-${["x", "y", "z"][axis]}`).value);
      if (Number.isFinite(value)) region[key][axis] = value;
    }
  }
  for (let axis = 0; axis < 3; axis += 1) {
    if (region.min[axis] >= region.max[axis]) {
      region.max[axis] = region.min[axis] + 0.05;
    }
  }
  region.visible = $("region-visible").checked;
  view.setRegions(regions, activeRegion);
  renderRegions();
  saveRegionsDebounced();
  scheduleStats(60);
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
  const structurePills = (items) => items.length
    ? items.map((item) => `<span class="pill" data-focus="${escapeHtml(item.id)}">${escapeHtml(item.id)}</span>`).join("")
    : "—";
  container.innerHTML = `
    <div class="k">D / step</div><div class="v">${hints.diameter.toFixed(2)} m · ${hints.stepLimit.toFixed(3)} m</div>
    <div class="k">道具</div><div class="v">食物 ${hints.counts.food} · 地形 ${hints.counts.terrain} · 挡路 ${hints.counts.blocking}</div>
    <div class="k">可上结构</div><div class="v">${structurePills(hints.structures.terrain)}</div>
    <div class="k">挡路结构</div><div class="v">${structurePills(hints.structures.blocking)}</div>
    <div class="wide">${hints.unsupported
      ? escapeHtml(hints.reason || "v2 物理提示尚未实现")
      : "P2 判定：顶面高差 ≤ 0.25·D 视为可上地形，否则挡路；只读提示，不写文件。"}</div>`;
  for (const pill of container.querySelectorAll("[data-focus]")) {
    pill.style.cursor = "pointer";
    pill.addEventListener("click", () => view.select([pill.dataset.focus], { focus: true }));
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
  if (options.selectIds) view.select(options.selectIds);
  if (options.message) toast(options.message, "ok", 2200);
  return response;
}

async function onBatch(operations, meta = {}) {
  if (!operations?.length) return true;
  setSaving();
  try {
    const response = await api.post({ ops: operations });
    if (!response.ok) {
      setSaveError();
      showError(response.error || "批量操作被拒绝", response.details || []);
      return false;
    }
    await refreshState();
    setSaved(response.saved);
    if (meta.label) $("status-save").textContent = `${meta.label} · 已保存`;
    return true;
  } catch (error) {
    setSaveError();
    showError(error.message, error.details || []);
    return false;
  }
}

async function openLevel(id, options = {}) {
  if (!api.hasToken) {
    showError("打开关卡需要写入令牌：请用服务器日志打印的 ?token=… 链接打开本页。");
    return;
  }
  setLoading(`打开 ${id}${options.placement ? ` / ${options.placement}` : ""}…`);
  try {
    const response = await api.openLevel(id, options);
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

async function deleteSelection() {
  const ids = view?.selectedIds || [];
  if (!ids.length) return;
  const response = await sendOp({
    op: "remove",
    args: { ids, updateExpected: $("auto-expected").checked },
  });
  if (response?.ok) {
    view.clearSelection();
    toast(`已删除 ${ids.length} 个对象`, "ok", 2000);
  }
}

async function duplicateSelection() {
  const ids = view?.selectedIds || [];
  if (!ids.length) return;
  const operations = [];
  for (const id of ids) {
    const group = view.objectGroup(id);
    if (!group) continue;
    if (group.userData.type === "prop") {
      const prop = findEntry("prop", id);
      operations.push({
        op: "add_prop",
        args: {
          def: prop.def,
          position: [prop.position[0] + 0.12, prop.position[1], prop.position[2] + 0.12],
          rotationQuat: prop.rotationQuat,
          kind: prop.kind,
          zone: prop.zone,
          support: prop.support,
          stackedOn: prop.stackedOn,
          valueUnits: prop.valueUnits,
        },
      });
    } else {
      const structure = findEntry("structure", id);
      operations.push({
        op: "add_structure",
        args: {
          role: structure.role,
          kind: structure.kind,
          center: [structure.center[0] + 0.25, structure.center[1], structure.center[2] + 0.25],
          size: structure.size,
          rotationQuat: structure.rotationQuat,
          collision: structure.collision,
          support: structure.support,
          meets: structure.meets,
        },
      });
    }
  }
  if (!operations.length) return;
  if ($("auto-expected").checked) operations.push({ op: "expected", args: { fromDocument: true } });
  setSaving();
  try {
    const response = await api.post({ ops: operations });
    if (!response.ok) {
      setSaveError();
      showError(response.error, response.details || []);
      return;
    }
    await refreshState();
    setSaved(response.saved);
    const newIds = (response.result?.ops || [])
      .map((result) => result?.itemId || result?.id).filter(Boolean);
    if (newIds.length) view.select(newIds);
    toast(`已复制 ${newIds.length} 个对象`, "ok", 2400);
  } catch (error) {
    setSaveError();
    showError(error.message, error.details || []);
  }
}

async function seatSelection() {
  const ids = view?.selectedIds || [];
  if (!ids.length) return;
  await sendOp({ op: "seat", args: { ids } });
}

async function nudgeSelection(dx, dy, dz) {
  if (!view?.selectionIds.size) return;
  await view.nudgeSelection(dx, dy, dz);
}

// --------------------------------------------------------------------------- //
// drop placement helper
// --------------------------------------------------------------------------- //
function boxAt(position, aabb) {
  return [
    [position[0] - aabb[0] / 2, position[1] - aabb[1] / 2, position[2] - aabb[2] / 2],
    [position[0] + aabb[0] / 2, position[1] + aabb[1] / 2, position[2] + aabb[2] / 2],
  ];
}

function boxVolume(box) {
  return Math.max(0, box[1][0] - box[0][0])
    * Math.max(0, box[1][1] - box[0][1])
    * Math.max(0, box[1][2] - box[0][2]);
}

function intersectionVolume(a, b) {
  const low = [Math.max(a[0][0], b[0][0]), Math.max(a[0][1], b[0][1]), Math.max(a[0][2], b[0][2])];
  const high = [Math.min(a[1][0], b[1][0]), Math.min(a[1][1], b[1][1]), Math.min(a[1][2], b[1][2])];
  return boxVolume([low, high]);
}

function propBoxesOverlapping(box, ignore = null) {
  const hits = [];
  for (const [id, info] of Object.entries(state.derived.props || {})) {
    if (id === ignore) continue;
    const other = info.worldAabb;
    if (!other) continue;
    const overlap = intersectionVolume(box, other);
    if (overlap <= 0) continue;
    const smaller = Math.min(boxVolume(box), boxVolume(other));
    if (smaller > 0 && overlap / smaller > 0.05) hits.push({ id, box: other, top: other[1][1] });
  }
  return hits;
}

function insideBounds(position) {
  const bounds = state.bounds || [[-100, -100], [100, 100]];
  return position[0] > bounds[0][0] + 0.05 && position[0] < bounds[1][0] - 0.05
    && position[2] > bounds[0][1] + 0.05 && position[2] < bounds[1][1] - 0.05;
}

function resolveDrop(def, drop) {
  const manifest = state.manifests[def];
  if (!manifest) return null;
  const aabb = manifest.aabb;
  const half = [aabb[0] / 2, aabb[1] / 2, aabb[2] / 2];
  const ground = Number((state.doc.level || {}).groundY || 0);
  let surface = drop ? Math.max(drop.point[1], ground) : ground;
  let x = drop ? drop.point[0] : (state.doc.level?.ballStart?.[0] ?? 0);
  let z = drop ? drop.point[2] : (state.doc.level?.ballStart?.[2] ?? 0);
  let support = null;
  let stackedOn = null;
  const target = drop?.target;
  if (target?.type === "prop") {
    const other = state.derived.props[target.id]?.worldAabb;
    if (other) {
      stackedOn = target.id;
      surface = other[1][1];
    }
  } else if (target?.type === "structure") {
    const structure = (state.doc.structures || []).find((entry) => entry.id === target.id);
    if (structure?.support && ["box", "ramp"].includes(structure.role)) support = structure.id;
    else surface = ground;
  }
  const make = (px, pz, py) => [px, py + half[1], pz];
  let position = make(x, z, surface);
  let box = boxAt(position, aabb);
  let overlaps = propBoxesOverlapping(box, stackedOn);

  if (overlaps.length) {
    const candidates = overlaps.slice().sort((left, right) => right.top - left.top);
    for (const candidate of candidates) {
      if (candidate.top > surface + 0.3) continue;
      const stacked = make(x, z, candidate.top);
      const stackedBox = boxAt(stacked, aabb);
      if (propBoxesOverlapping(stackedBox, candidate.id).length === 0) {
        position = stacked;
        box = stackedBox;
        stackedOn = candidate.id;
        support = null;
        overlaps = [];
        break;
      }
    }
  }

  if (overlaps.length) {
    // A small spiral on the same surface (or support top) to find a free footprint.
    const y = position[1] - half[1];
    const structure = support
      ? (state.doc.structures || []).find((entry) => entry.id === support) : null;
    const structureBox = support ? state.derived.structures?.[support]?.worldAabb
      : stackedOn ? state.derived.props?.[stackedOn]?.worldAabb : null;
    let found = null;
    for (let radius = 0.12; radius <= 0.9 && !found; radius += 0.12) {
      for (let index = 0; index < 10 && !found; index += 1) {
        const angle = index * Math.PI / 5;
        const px = x + Math.cos(angle) * radius;
        const pz = z + Math.sin(angle) * radius;
        if (!insideBounds([px, y, pz])) continue;
        if (structureBox && (px < structureBox[0][0] || px > structureBox[1][0]
          || pz < structureBox[0][2] || pz > structureBox[1][2])) continue;
        const candidate = make(px, pz, y);
        if (propBoxesOverlapping(boxAt(candidate, aabb), stackedOn).length) continue;
        found = candidate;
      }
    }
    if (found) {
      position = found;
      box = boxAt(position, aabb);
      overlaps = [];
    }
    void structure;
  }

  if (overlaps.length) {
    // Last resort: stack on the highest overlapping prop if that is free.
    const highest = overlaps.slice().sort((left, right) => right.top - left.top)[0];
    const stacked = make(x, z, highest.top);
    if (propBoxesOverlapping(boxAt(stacked, aabb), highest.id).length === 0) {
      position = stacked;
      stackedOn = highest.id;
      support = null;
    }
  }
  return { position, support, stackedOn };
}

async function dropPalette(def, event) {
  const manifest = state.manifests[def];
  if (!manifest) return;
  if (!manifest.valueUnits) {
    showError(`def ${def} 没有 valueUnits；先用 add_def 声明价值`);
    return;
  }
  const drop = view.dropPoint(event.clientX, event.clientY);
  const placement = resolveDrop(def, drop);
  if (!placement) return;
  const args = {
    def,
    position: placement.position,
    rotationQuat: [0, 0, 0, 1],
    kind: manifest.kind,
    support: placement.support || undefined,
    stackedOn: placement.stackedOn || undefined,
    seat: true,
    updateExpected: $("auto-expected").checked,
  };
  const response = await sendOp({ op: "add_prop", args });
  if (response?.ok) {
    view.select([response.result.itemId]);
    toast(`已放置 ${manifest.nameZh || def} → ${response.result.itemId}`, "ok", 2400);
  }
}

// --------------------------------------------------------------------------- //
// structure defaults
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
    view.select([response.result.id]);
    toast(`已加入 ${response.result.id}`, "ok", 2200);
  }
}

// --------------------------------------------------------------------------- //
// keyboard + UI events
// --------------------------------------------------------------------------- //
function typingInField(event) {
  const tag = event.target?.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
}

function bindKeyboard() {
  window.addEventListener("keydown", (event) => {
    if (typingInField(event)) return;
    if (event.code === "Space" && !view.isFlying) {
      event.preventDefault();
      if (!event.repeat) view.panModifier = true;
      return;
    }
    if (view.isFlying) return; // W/A/S/D/Q/E/Shift belong to the fly camera
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
    if (command && event.code === "KeyA") {
      event.preventDefault();
      view.selectAll();
      renderSelection(view.selectionInfo());
      return;
    }
    if (command) return;
    if (event.code === "Escape") {
      view.clearSelection();
      return;
    }
    if (event.code === "KeyQ") setTool("hand");
    else if (event.code === "KeyW") setTool("move");
    else if (event.code === "KeyE") setTool("rotate");
    else if (event.code === "KeyR") setTool("scale");
    else if (event.code === "KeyX") {
      view.toggleSpace();
      updateToolHud();
    } else if (event.code === "KeyF") {
      view.focusSelection();
    } else if (event.code === "End") {
      view.restSelection();
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
      if (delta) nudgeSelection(delta[0], delta[1], delta[2]);
    }
  });
  window.addEventListener("keyup", (event) => {
    if (event.code === "Space") view.panModifier = false;
  });
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
  $("show-aabb").addEventListener("change", () => view.setShowWorldBoxes($("show-aabb").checked));
  $("show-terrain").addEventListener("change", () => {
    const shown = view.setTerrain($("show-terrain").checked);
    if ($("show-terrain").checked && !shown) {
      toast("这个关卡没有原地形提取结果", "ok", 2600);
    }
  });
  $("terrain-wireframe").addEventListener("change", () => {
    view.setTerrainWireframe($("terrain-wireframe").checked);
  });
  $("show-collision").addEventListener("change", async () => {
    const enabled = $("show-collision").checked;
    const built = await view.setCollision(enabled);
    if (enabled && !built) {
      toast("这个关卡没有碰撞数据", "ok", 2600);
      $("show-collision").checked = false;
    }
  });
  $("show-v2-terrain").addEventListener("change", () => {
    view.setV2Terrain($("show-v2-terrain").checked);
  });
  $("show-v2-solids").addEventListener("change", () => {
    const count = view.setV2Solids($("show-v2-solids").checked);
    if ($("show-v2-solids").checked && !count) toast("这个 v2 文档没有固体", "ok", 2400);
  });
  $("show-v2-walls").addEventListener("change", () => {
    const count = view.setV2Walls($("show-v2-walls").checked);
    if ($("show-v2-walls").checked && !count) toast("这个 v2 文档没有活动墙", "ok", 2400);
  });
  $("show-v2-kill-plane").addEventListener("change", () => {
    const shown = view.setV2KillPlane($("show-v2-kill-plane").checked);
    if ($("show-v2-kill-plane").checked && !shown) toast("这个 v2 文档没有 kill plane", "ok", 2400);
  });
  $("show-v2-parent-lines").addEventListener("change", () => {
    const count = view.setParentLines($("show-v2-parent-lines").checked);
    if ($("show-v2-parent-lines").checked && !count) toast("这个 v2 文档没有 props[].parent", "ok", 2400);
  });
  $("v2-apply-rotation").addEventListener("change", async () => {
    await view.setV2Transforms($("v2-apply-rotation").checked, $("v2-apply-scale").checked);
    renderSelection(view.selectionInfo());
  });
  $("v2-apply-scale").addEventListener("change", async () => {
    await view.setV2Transforms($("v2-apply-rotation").checked, $("v2-apply-scale").checked);
    renderSelection(view.selectionInfo());
  });
  $("open-v2").addEventListener("click", async () => {
    const legacy = state?.legacy;
    if (!legacy?.replacedBy) return;
    await openLevel(state.level, { placement: legacy.replacedBy });
  });
  $("show-legacy-planes").addEventListener("change", () => {
    view.setLegacyPlanes($("show-legacy-planes").checked);
  });
  $("use-original-height").addEventListener("change", () => {
    view.setOriginalHeight($("use-original-height").checked);
    renderSelection(view.selectionInfo());
  });
  $("show-parent-lines").addEventListener("change", () => {
    const count = view.setParentLines($("show-parent-lines").checked);
    if ($("show-parent-lines").checked && !count) {
      toast("该关卡没有 group/groupId 父子指针", "ok", 2400);
    }
  });
  $("region-mode").addEventListener("click", () => {
    regionMode = !regionMode;
    view.setRegionMode(regionMode);
    $("region-mode").classList.toggle("active", regionMode);
  });
  $("stats-scope").addEventListener("change", () => {
    statsScope = $("stats-scope").value;
    scheduleStats(40);
  });
  $("stats-mode").addEventListener("change", () => {
    statsMode = $("stats-mode").value;
    scheduleStats(40);
  });
  for (const button of document.querySelectorAll("#stats-metrics [data-metric]")) {
    button.addEventListener("click", () => {
      statsMetric = button.dataset.metric;
      for (const other of document.querySelectorAll("#stats-metrics [data-metric]")) {
        other.classList.toggle("active", other === button);
      }
      renderStats();
      scheduleStats(40);
    });
  }
  $("stats-log").addEventListener("change", () => {
    statsLog = $("stats-log").checked;
    renderStats();
  });
  $("stats-csv").addEventListener("click", downloadCsv);
  $("stats-png").addEventListener("click", () => {
    const result = view.screenshot();
    toast(`已保存 ${result.name}`, "ok", 2500);
  });
  for (const id of ["region-name", "region-min-x", "region-min-y", "region-min-z",
    "region-max-x", "region-max-y", "region-max-z", "region-visible"]) {
    $(id).addEventListener("change", updateActiveRegionFromInputs);
  }
  $("region-delete").addEventListener("click", () => {
    regions = regions.filter((entry) => entry.name !== activeRegion);
    activeRegion = regions[0]?.name || null;
    view.setRegions(regions, activeRegion);
    renderRegions();
    saveRegionsDebounced();
    scheduleStats(40);
  });
  for (const button of document.querySelectorAll("#tool-hud [data-tool]")) {
    button.addEventListener("click", () => setTool(button.dataset.tool));
  }
  $("tool-scale").addEventListener("click", () => setTool("scale"));
  $("gizmo-move").addEventListener("click", () => setTool("move"));
  $("gizmo-rotate").addEventListener("click", () => setTool("rotate"));
  $("rest-surface").addEventListener("click", () => view.restSelection());
  $("drop-ground").addEventListener("click", () => view.dropSelectionToGround());
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

// --------------------------------------------------------------------------- //
// boot
// --------------------------------------------------------------------------- //
async function boot() {
  const bootStarted = performance.now();
  const bootTiming = { startMs: Math.round(bootStarted) };
  setLoading("连接服务器…");
  try {
    const payload = await api.loadLevel();
    state = payload.state;
  } catch (error) {
    setLoading("");
    showError(`连不上服务器：${error.message}`);
    return;
  }
  bootTiming.stateMs = Math.round(performance.now() - bootStarted);

  view = new EditorView($("canvas"), {
    onSelect: (info) => {
      renderSelection(info);
      if (statsScope === "selection") scheduleStats(60);
    },
    onBatch: (operations, meta) => onBatch(operations, meta),
    onError: (message) => showError(message),
    onProgress: (text) => {
      if (text) setLoading(text);
    },
    onStatus: (status, value) => {
      if (status === "tool") {
        currentTool = value;
        updateToolHud();
      } else if (status === "space") {
        updateToolHud();
      } else if (status === "selection") {
        $("selection-count").textContent = `选中 ${value} 个`;
      } else if (status === "camera") {
        updateCameraButtons();
      } else if (status === "fly-start") {
        $("fly-hud").classList.remove("hidden");
        $("fly-speed").textContent = Number(value).toFixed(1);
      } else if (status === "fly-end") {
        $("fly-hud").classList.add("hidden");
      } else if (status === "fly-speed") {
        $("fly-speed").textContent = Number(value).toFixed(1);
      } else if (status === "pointer-fallback") {
        toast("指针锁定不可用：按住右键拖动看方向（WASD/QE 仍然有效）", "ok", 6000);
      }
    },
  });

  bootTiming.viewCtorMs = Math.round(performance.now() - bootStarted - bootTiming.stateMs);
  bindKeyboard();
  bindUI();
  view.apiToken = api.token;
  view.setSnapping({
    translate: Number($("snap-translate").value),
    rotate: Number($("snap-rotate").value),
  });
  view.callbacks.onRegionCreated = (region) => addRegion(region);
  view.callbacks.onRegionChange = (region) => {
    const entry = regions.find((item) => item.name === region.name)
      || regions.find((item) => item === region);
    if (entry) entry.name = region.name;
    renderRegions();
    saveRegionsDebounced();
    if (statsScope === "region") scheduleStats(60);
  };
  await view.load({ ...state });
  bootTiming.loadMs = view.lastLoadTiming?.totalMs ?? null;
  bootTiming.loadDetail = view.lastLoadTiming || null;
  currentLevelChanged();
  renderAll();
  bootTiming.readyMs = Math.round(performance.now() - bootStarted);
  window.__leveleditorBootTiming = bootTiming;
  $("show-aabb").checked = false;
  setLoading("");
  setSaved(null);
  updateToolHud();

  api.connectEvents((version) => {
    if (state && version <= state.version) return;
    scheduleRefresh();
  }, (status) => {
    $("status-live").textContent = status === "live" ? "实时" : "重连中…";
    $("status-live").className = `live ${status === "live" ? "" : "retrying"}`;
  });

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
    select(ids, focus = true) {
      view.select(Array.isArray(ids) ? ids : [ids], { focus });
      renderSelection(view.selectionInfo());
      return view.selectionInfo();
    },
    selectAll() { view.selectAll(); renderSelection(view.selectionInfo()); },
    setTool(tool) { setTool(tool); },
    setSpace(space) {
      if (space === "local" || space === "world") view.spaceMode = space;
      view.gizmo.setSpace(view.spaceMode);
      updateToolHud();
    },
    setCamera(id) { view.setCameraPreset(id); updateCameraButtons(); },
    fly(on = true) {
      if (on) view._beginFly();
      else view._endFly();
    },
    async screenshot() { return view.screenshot(); },
    refStage() { return view.refSummary(); },
    async setTerrain(visible = true) {
      $("show-terrain").checked = Boolean(visible);
      view.setTerrain(visible);
      return view.refSummary();
    },
    async setTerrainWireframe(enabled = true) {
      $("terrain-wireframe").checked = Boolean(enabled);
      view.setTerrainWireframe(enabled);
      return view.refSummary();
    },
    async setCollision(visible = true) {
      $("show-collision").checked = Boolean(visible);
      await view.setCollision(visible);
      return view.refSummary();
    },
    setLegacyPlanes(visible = true) {
      $("show-legacy-planes").checked = Boolean(visible);
      view.setLegacyPlanes(visible);
      return view.refSummary();
    },
    setOriginalHeight(enabled = true) {
      $("use-original-height").checked = Boolean(enabled);
      view.setOriginalHeight(enabled);
      return view.refSummary();
    },
    terrainContact(tolerance = 0.1) { return view.terrainContact(tolerance); },
    benchmarkFrames(count = 60) { return view.benchmarkFrames(count); },
    async lookAt(eye, target) {
      const camera = view.cam;
      camera.setFromPositionTarget(
        camera.pivot.clone().set(eye[0], eye[1], eye[2]),
        camera.pivot.clone().set(target[0], target[1], target[2])
      );
      view.needsRender = true;
      view.renderNow();
      return camera.camera.position.toArray().map((v) => Number(v.toFixed(3)));
    },
    async renderInfo() {
      const info = view.renderer.info;
      return {
        calls: info.render.calls,
        triangles: info.render.triangles,
        geometries: info.memory.geometries,
        textures: info.memory.textures,
        programs: info.programs?.length ?? null,
        lastFrameMs: view.lastFrameMs ?? null,
      };
    },
    async openLevel(id, options = {}) { await openLevel(id, options); return state?.level; },
    setV2Layer(name, visible = true) {
      const methods = {
        terrain: () => view.setV2Terrain(visible),
        solids: () => view.setV2Solids(visible),
        walls: () => view.setV2Walls(visible),
        kill: () => view.setV2KillPlane(visible),
        parents: () => view.setParentLines(visible),
      };
      if (!methods[name]) throw new Error(`unknown v2 layer ${name}`);
      return methods[name]();
    },
    async setV2Transforms(rotation = true, scale = true) {
      const result = await view.setV2Transforms(rotation, scale);
      $("v2-apply-rotation").checked = Boolean(rotation);
      $("v2-apply-scale").checked = Boolean(scale);
      renderSelection(view.selectionInfo());
      return result;
    },
    async op(name, args) { return sendOp({ op: name, args }); },
    async refresh() { await refreshState(); return state; },
    selection() { return view.selectionInfo(); },
    setShowWorldBoxes(enabled) {
      $("show-aabb").checked = Boolean(enabled);
      view.setShowWorldBoxes($("show-aabb").checked);
    },
    debugBoxStyle(style) { view.setBoxStyle(style); },
    async debugRotateSelection(degrees = 30, axis = "y") {
      return view.debugRotateSelection(degrees, axis);
    },
    async debugTranslateSelection(dx = 0.1, dy = 0, dz = 0.1) {
      return view.debugTranslateSelection(dx, dy, dz);
    },
    highlight(ids) { view.highlightIds(ids); },
    regions() { return { regions, active: activeRegion }; },
    async saveRegions() { await saveRegionsNow(); return regions; },
    async addRegion(region) { addRegion(region); await saveRegionsNow(); return regions; },
    async stats(params) { return (await api.stats(params)).stats; },
    async setStatsScope(scope) {
      statsScope = scope;
      $("stats-scope").value = scope;
      await refreshStats();
      return stats;
    },
    clearHighlights() { view.clearHighlights(); },
  };
  document.body.dataset.ready = "1";
}

boot().catch((error) => {
  console.error(error);
  setLoading("");
  showError(`启动失败：${error.message}`);
});

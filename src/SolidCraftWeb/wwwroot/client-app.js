"use strict";

const COLUMNS = ["编号","孔数","孔径","孔距","高度","宽度","子头","母槽","左半圆","右半圆","左斜边","右斜边","斜边Y","斜边Z","子头宽","子头突出","母槽宽","母槽深"];
const FLAGS = new Set(["子头","母槽","左半圆","右半圆","左斜边","右斜边"]);
const PARAMS = ["孔数","辊径","辊距","实体高度","密封宽度","子头开关","母槽开关","左半孔开关","右半孔开关","左斜边开关","右斜边开关","斜边Y","斜边Z","子头宽","子头突出","母槽宽","母槽深"];
const OPTIONAL = new Set(PARAMS.slice(13));
const DEFAULT_ROW = ["1","5","90","120","105","200","1","1","0","0","0","0","30","30","","","",""];
const state = { rows:[DEFAULT_ROW.slice()], busy:false, engine:null, defaults:null };
const el = {};

document.addEventListener("DOMContentLoaded", () => {
  for (const id of ["connectionState","csvFile","importBtn","addBtn","validateBtn","rowCount","grid","jobState","jobMessage","jobProgress","log","stepName","viewGap","combineStep","exportCsv","stepBtn","solidCraftFrame"]) {
    el[id] = document.getElementById(id);
  }
  el.stepName.value = "辊密封_" + dateStamp();
  el.importBtn.addEventListener("click", () => el.csvFile.click());
  el.csvFile.addEventListener("change", importCsv);
  el.addBtn.addEventListener("click", addRow);
  el.validateBtn.addEventListener("click", checkRows);
  el.stepBtn.addEventListener("click", startGeneration);
  el.grid.addEventListener("paste", pasteGrid);
  renderTable();
  const template = new URL("./client-template.solidcraft", location.href).href;
  const url = new URL("./solidcraft/", location.href);
  url.searchParams.set("open", template);
  // Exporting requires normal mode: embed mode disables files_tick/OPFS flushing.
  state.engineUrl = url.toString();
  el.solidCraftFrame.src = state.engineUrl;
  waitForEngine().catch(error => setConnection("SolidCraft 加载失败：" + error.message));
});

function setConnection(message) { el.connectionState.textContent = message; }
function dateStamp() {
  const now = new Date();
  return String(now.getMonth() + 1).padStart(2, "0") + String(now.getDate()).padStart(2, "0");
}
function renderTable() {
  const head = el.grid.tHead, body = el.grid.tBodies[0];
  head.replaceChildren(); body.replaceChildren();
  const header = document.createElement("tr");
  const actionHead = document.createElement("th");
  actionHead.textContent = "操作"; header.appendChild(actionHead);
  for (const name of COLUMNS) { const th = document.createElement("th"); th.textContent = name; header.appendChild(th); }
  head.appendChild(header);
  state.rows.forEach((row, rowIndex) => {
    const tr = document.createElement("tr");
    const action = document.createElement("td");
    const remove = document.createElement("button");
    remove.type = "button"; remove.className = "delete"; remove.textContent = "删除";
    remove.addEventListener("click", () => { state.rows.splice(rowIndex, 1); if (!state.rows.length) state.rows.push(DEFAULT_ROW.slice()); renderTable(); });
    action.appendChild(remove); tr.appendChild(action);
    COLUMNS.forEach((name, colIndex) => {
      const td = document.createElement("td"), input = document.createElement("input");
      input.dataset.row = String(rowIndex); input.dataset.column = String(colIndex);
      if (FLAGS.has(name)) {
        td.className = "flag"; input.type = "checkbox"; input.checked = row[colIndex] === "1";
        input.addEventListener("change", () => { state.rows[rowIndex][colIndex] = input.checked ? "1" : "0"; });
      } else {
        input.type = "text"; input.value = row[colIndex] || ""; input.autocomplete = "off"; input.spellcheck = false;
        input.addEventListener("input", () => { state.rows[rowIndex][colIndex] = input.value; });
      }
      td.appendChild(input); tr.appendChild(td);
    });
    body.appendChild(tr);
  });
  el.rowCount.textContent = state.rows.length + " 行";
}
function addRow() {
  const prior = state.rows[state.rows.length - 1] || DEFAULT_ROW;
  const row = prior.slice(); row[0] = String(state.rows.length + 1);
  state.rows.push(row); renderTable();
}
function pasteGrid(event) {
  const target = event.target;
  if (!(target instanceof HTMLInputElement) || target.type === "checkbox") return;
  const startRow = Number(target.dataset.row), startCol = Number(target.dataset.column);
  if (!Number.isInteger(startRow) || !Number.isInteger(startCol)) return;
  const raw = event.clipboardData.getData("text/plain");
  if (!raw.includes("\t") && !raw.includes("\n")) return;
  event.preventDefault();
  const matrix = raw.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n").filter((line, i, all) => !(i === all.length - 1 && line === "")).map(line => line.split("\t"));
  matrix.forEach((cells, y) => cells.forEach((value, x) => {
    const r = startRow + y, c = startCol + x;
    if (c >= COLUMNS.length) return;
    while (state.rows.length <= r) state.rows.push(DEFAULT_ROW.slice());
    if (FLAGS.has(COLUMNS[c])) state.rows[r][c] = ["1","true","yes","是"].includes(value.trim().toLowerCase()) ? "1" : "0";
    else state.rows[r][c] = value;
  }));
  renderTable();
}
async function importCsv() {
  const file = el.csvFile.files[0];
  if (!file) return;
  try {
    if (file.size > 512 * 1024) throw new Error("CSV 文件不能超过 512 KiB。");
    const bytes = new Uint8Array(await file.arrayBuffer());
    let text;
    if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) text = new TextDecoder("utf-16le").decode(bytes.subarray(2));
    else if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) text = new TextDecoder("utf-16be").decode(bytes.subarray(2));
    else {
      try { text = new TextDecoder("utf-8", {fatal:true}).decode(bytes).replace(/^\uFEFF/, ""); }
      catch (_) { text = new TextDecoder("gb18030").decode(bytes); }
    }
    const matrix = parseCsv(text);
    if (!matrix.length) throw new Error("CSV 没有表头。");
    const header = matrix[0];
    if (new Set(header).size !== header.length) throw new Error("CSV 表头重复。");
    const indices = COLUMNS.map(name => header.indexOf(name));
    if (indices.slice(0,14).some(index => index < 0)) throw new Error("CSV 表头缺少必需列，请使用参数模板。");
    const rows = matrix.slice(1).filter(row => row.some(value => value.trim())).map((cells, lineIndex) => {
      if (cells.length !== header.length) throw new Error("CSV 第 " + (lineIndex + 2) + " 行列数与表头不一致。");
      return indices.map(index => index < 0 ? "" : (cells[index] || "").trim());
    });
    if (!rows.length) throw new Error("CSV 没有数据行。");
    state.rows = rows; renderTable(); writeLog("已在浏览器中导入 " + rows.length + " 行 CSV。");
  } catch (error) { writeLog("导入失败：" + error.message); }
  finally { el.csvFile.value = ""; }
}
function parseCsv(text) {
  const rows = []; let row = [], cell = "", quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (ch === '"') quoted = false;
      else cell += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === ",") { row.push(cell); cell = ""; }
    else if (ch === "\n" || ch === "\r") {
      if (ch === "\r" && text[i + 1] === "\n") i++;
      row.push(cell); rows.push(row); row = []; cell = "";
    } else cell += ch;
  }
  if (quoted) throw new Error("CSV 引号未闭合。");
  if (cell.length || row.length) { row.push(cell); rows.push(row); }
  return rows;
}
function numberCell(value, line, column) {
  const text = value.trim();
  if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$/.test(text)) throw new Error("第 " + line + " 行 " + COLUMNS[column] + " 不是有效数字。");
  const number = Number(text);
  if (!Number.isFinite(number)) throw new Error("第 " + line + " 行 " + COLUMNS[column] + " 不是有效数字。");
  return number;
}
function normalizeRows() {
  if (!state.rows.length) throw new Error("请至少添加一行参数。");
  if (state.rows.length > 500) throw new Error("单次最多生成 500 行。");
  const names = new Set();
  return state.rows.map((source, index) => {
    const line = index + 2, row = source.map(value => String(value == null ? "" : value).trim());
    if (row.slice(0,14).some(value => !value)) throw new Error("第 " + line + " 行有空白参数。");
    let name = row[0].replace(/[<>:"/\\|?*\x00-\x1f]/g, "_").replace(/^[ .]+|[ .]+$/g, "");
    if (!name) throw new Error("第 " + line + " 行编号无效。");
    if (/^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(name.split(".")[0])) name = "_" + name;
    if (names.has(name.toLowerCase())) throw new Error("第 " + line + " 行编号重复：" + name);
    names.add(name.toLowerCase()); row[0] = name;
    if (row.some(value => value.length > 200)) throw new Error("第 " + line + " 行内容过长。");
    const nums = new Array(COLUMNS.length).fill(0);
    for (let c = 1; c < COLUMNS.length; c++) {
      if (OPTIONAL.has(PARAMS[c - 1]) && row[c] === "") continue;
      nums[c] = numberCell(row[c], line, c);
      if (FLAGS.has(COLUMNS[c]) && nums[c] !== 0 && nums[c] !== 1) throw new Error("第 " + line + " 行 " + COLUMNS[c] + " 只能为 0 或 1。");
      row[c] = String(nums[c]);
    }
    for (let c = 14; c < COLUMNS.length; c++) if (row[c] && nums[c] <= 0) throw new Error("第 " + line + " 行 " + COLUMNS[c] + " 必须为正数。");
    const holes = nums[1], ends = nums[8] + nums[9];
    if (holes <= 0 || holes * 2 % 1 !== 0) throw new Error("第 " + line + " 行孔数只允许正整数或 x.5。");
    if (holes % 1 === 0.5 && ends !== 1) throw new Error("第 " + line + " 行 " + holes + " 孔必须只选左半孔或右半孔。");
    if (Number.isInteger(holes) && ends === 1) throw new Error("第 " + line + " 行整数孔数须左右都不选，或左右都选半孔。");
    for (let c = 2; c <= 5; c++) if (nums[c] <= 0) throw new Error("第 " + line + " 行 " + COLUMNS[c] + " 必须大于 0。");
    for (let c = 12; c <= 13; c++) if (nums[c] < 0) throw new Error("第 " + line + " 行 " + COLUMNS[c] + " 不能小于 0。");
    if ((nums[10] === 1 || nums[11] === 1) && (nums[12] === 0 || nums[13] === 0)) throw new Error("第 " + line + " 行启用斜边时，斜边Y、斜边Z都必须大于 0。");
    return row;
  });
}
function checkRows() {
  try { const rows = normalizeRows(); writeLog("参数检查通过，共 " + rows.length + " 行；检查在浏览器本地完成。"); }
  catch (error) { writeLog("参数检查失败：" + error.message); }
}
function csvText(rows) {
  const quote = value => '"' + String(value).replace(/"/g, '""') + '"';
  return "\uFEFF" + [COLUMNS, ...rows].map(row => row.map(quote).join(",")).join("\r\n") + "\r\n";
}
function filePart(name) { return "辊密封_" + dateStamp() + "_" + name; }
function setJob(status, message) {
  const labels = {queued:"排队中",running:"浏览器生成中",completed:"已完成",failed:"失败"};
  el.jobState.textContent = labels[status] || status; el.jobMessage.textContent = message || "";
}
function writeLog(message) {
  if (el.log.textContent) el.log.textContent += "\n";
  el.log.textContent += message; el.log.scrollTop = el.log.scrollHeight;
}
function setBusy(value) {
  state.busy = value; el.stepBtn.disabled = value || !state.engine;
  el.addBtn.disabled = value; el.importBtn.disabled = value; el.validateBtn.disabled = value;
}
async function waitForEngine() {
  setConnection("正在加载 SolidCraft WebAssembly…");
  el.jobMessage.textContent = "浏览器正在下载并启动 SolidCraft 引擎；首次加载可能需要一些时间。";
  const until = Date.now() + 180000;
  while (Date.now() < until) {
    try {
      const api = el.solidCraftFrame.contentWindow && el.solidCraftFrame.contentWindow.solidcraft;
      if (api && typeof api.execute === "function" && typeof api.request === "function") {
        const defaults = await readTemplateDefaults(api, 60000);
        state.engine = api;
        state.defaults = defaults;
        setConnection("SolidCraft 已就绪 · 本机计算");
        el.jobMessage.textContent = "建模、校验与 STEP 导出均在此浏览器完成。";
        setBusy(state.busy);
        return;
      }
    } catch (_) {}
    await new Promise(resolve => setTimeout(resolve, 400));
  }
  throw new Error("没有收到 SolidCraft 浏览器 API。请检查浏览器是否支持 WebAssembly/WebGL，并刷新页面。");
}
async function readTemplateDefaults(api, timeoutMs) {
  const until = Date.now() + timeoutMs;
  let latestError = null;
  while (Date.now() < until) {
    try {
      const result = await api.execute("parameters.list", {filter:"user"});
      const parameters = result && Array.isArray(result.parameters) ? result.parameters : [];
      const byName = new Map(parameters.map(parameter => [parameter.name, parameter]));
      if (PARAMS.every(name => byName.has(name)) && byName.has("半孔校验")) {
        const defaults = {};
        for (const name of OPTIONAL) {
          const parameter = byName.get(name);
          if (parameter.expression !== undefined && parameter.expression !== null) defaults[name] = String(parameter.expression);
          else if (parameter.value !== undefined && parameter.value !== null) defaults[name] = String(parameter.value);
          else throw new Error("无法读取模板默认尺寸：" + name);
        }
        return defaults;
      }
    } catch (error) { latestError = error; }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error(latestError ? "模板未加载完成：" + latestError.message : "未能读取标准辊密封模板参数。");
}
function rowChanges(row) {
  const changes = Object.assign({}, state.defaults);
  PARAMS.forEach((name, index) => { if (row[index + 1] !== "") changes[name] = row[index + 1]; });
  return changes;
}
function readNumber(value) {
  const number = typeof value === "number" ? value : Number(value);
  return Number.isFinite(number) ? number : NaN;
}
function boundsOf(documentInfo) {
  const bodies = documentInfo && documentInfo.bodies;
  if (!Array.isArray(bodies) || bodies.length === 0) throw new Error("SolidCraft 没有返回有效实体。");
  const min = [Infinity,Infinity,Infinity], max = [-Infinity,-Infinity,-Infinity];
  for (const body of bodies) {
    const box = body.bbox || body.bounding_box;
    let lo, hi;
    if (Array.isArray(box) && box.length === 2) [lo,hi] = box;
    else if (box && typeof box === "object") { lo = box.min || box.minimum; hi = box.max || box.maximum; }
    if (!Array.isArray(lo) || !Array.isArray(hi) || lo.length !== 3 || hi.length !== 3) throw new Error("有实体缺少有效包围盒。");
    for (let axis = 0; axis < 3; axis++) {
      const a = readNumber(lo[axis]), b = readNumber(hi[axis]);
      if (!Number.isFinite(a) || !Number.isFinite(b) || a > b) throw new Error("实体包围盒坐标无效。");
      min[axis] = Math.min(min[axis], a); max[axis] = Math.max(max[axis], b);
    }
  }
  return {min,max,bodies:bodies.length};
}
function occurrenceId(result) {
  let occurrence = result && result.occurrence;
  if (occurrence && typeof occurrence === "object" && !Array.isArray(occurrence)) occurrence = occurrence.id;
  if (typeof occurrence !== "string" && typeof occurrence !== "number" && !Array.isArray(occurrence)) throw new Error("SolidCraft 未返回有效 STEP 实例编号。");
  return occurrence;
}
function requireClearInterference(result, expectedBodies) {
  if (!result || !Array.isArray(result.interferences) || !Array.isArray(result.unchecked) ||
      !Number.isInteger(result.count) || !Number.isInteger(result.checked)) throw new Error("相交检查结果缺少必要字段。");
  const pending = result.pairs_not_checked === undefined ? 0 : result.pairs_not_checked;
  if (result.count !== 0 || result.interferences.length || result.unchecked.length || result.checked !== expectedBodies ||
      result.truncated || result.budget_exceeded || pending !== 0) {
    const detail = result.interferences.slice(0,3).map(pair => String(pair.a || "?") + " ↔ " + String(pair.b || "?")).join("；");
    throw new Error("组合 STEP 未导出：相交 " + Math.max(result.count,result.interferences.length) + " 对，未检查 " + result.unchecked.length + " 项，实体 " + result.checked + "/" + expectedBodies + "。" + detail);
  }
}
async function opfsBytes(name, expectedBytes, timeoutMs = 30000) {
  // Engine commands finish before the Web app flushes its virtual files to OPFS.
  const frame = el.solidCraftFrame.contentWindow;
  const storage = frame && frame.navigator.storage;
  if (!storage || typeof storage.getDirectory !== "function") throw new Error("当前浏览器不支持 OPFS 文件存储。");
  const until = Date.now() + timeoutMs;
  let latestError;
  while (Date.now() < until) {
    try {
      const root = await storage.getDirectory();
      const handle = await root.getFileHandle(name);
      const file = await handle.getFile();
      if (file.size > 0 && (!Number.isSafeInteger(expectedBytes) || file.size === expectedBytes)) {
        const bytes = new Uint8Array(await file.arrayBuffer());
        if (bytes.length === file.size) return bytes;
      }
    } catch (error) {
      if (!["NotFoundError", "NotReadableError", "InvalidStateError"].includes(error.name)) throw error;
      latestError = error;
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error("等待浏览器写入导出文件超时：" + name + (latestError ? "（" + latestError.message + "）" : ""));
}
async function engineExecute(command, params) {
  try { return await state.engine.execute(command, params); }
  catch (error) { throw new Error(command + "：" + (error.message || String(error))); }
}
function safeDownload(bytes, name, type) {
  const url = URL.createObjectURL(new Blob([bytes], {type:type || "application/octet-stream"}));
  const link = document.createElement("a");
  link.href = url; link.download = name; link.className = "download-link";
  document.body.appendChild(link); link.click(); link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}
function crc32(bytes) {
  let crc = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) {
    crc ^= bytes[i];
    for (let bit = 0; bit < 8; bit++) crc = (crc & 1) ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1;
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function makeZip(files) {
  const encoder = new TextEncoder(), local = [], central = [];
  let offset = 0;
  const now = new Date(), dosTime = (now.getHours()<<11) | (now.getMinutes()<<5) | Math.floor(now.getSeconds()/2);
  const dosDate = ((Math.max(1980,now.getFullYear())-1980)<<9) | ((now.getMonth()+1)<<5) | now.getDate();
  const push16 = (view, at, value) => view.setUint16(at, value, true);
  const push32 = (view, at, value) => view.setUint32(at, value >>> 0, true);
  for (const item of files) {
    const name = encoder.encode(item.name), data = item.bytes, crc = crc32(data);
    const localHeader = new Uint8Array(30 + name.length), lv = new DataView(localHeader.buffer);
    push32(lv,0,0x04034b50); push16(lv,4,20); push16(lv,6,0x0800); push16(lv,8,0);
    push16(lv,10,dosTime); push16(lv,12,dosDate); push32(lv,14,crc); push32(lv,18,data.length); push32(lv,22,data.length);
    push16(lv,26,name.length); push16(lv,28,0); localHeader.set(name,30);
    local.push(localHeader,data);
    const centralHeader = new Uint8Array(46 + name.length), cv = new DataView(centralHeader.buffer);
    push32(cv,0,0x02014b50); push16(cv,4,20); push16(cv,6,20); push16(cv,8,0x0800); push16(cv,10,0);
    push16(cv,12,dosTime); push16(cv,14,dosDate); push32(cv,16,crc); push32(cv,20,data.length); push32(cv,24,data.length);
    push16(cv,28,name.length); push16(cv,30,0); push16(cv,32,0); push16(cv,34,0); push16(cv,36,0); push32(cv,38,0); push32(cv,42,offset);
    centralHeader.set(name,46); central.push(centralHeader);
    offset += localHeader.length + data.length;
  }
  const centralBytes = central.reduce((sum, part) => sum + part.length, 0);
  const end = new Uint8Array(22), ev = new DataView(end.buffer);
  push32(ev,0,0x06054b50); push16(ev,4,0); push16(ev,6,0); push16(ev,8,files.length); push16(ev,10,files.length);
  push32(ev,12,centralBytes); push32(ev,16,offset); push16(ev,20,0);
  return new Blob([...local,...central,end], {type:"application/zip"});
}
function safeStepName(value) {
  let name = String(value || "").trim();
  if (/\.step$/i.test(name)) name = name.slice(0,-5);
  if (!name) name = "辊密封_" + dateStamp();
  if (name.length > 180 || /[<>:"/\\|?*\x00-\x1f]/.test(name) || /[ .]$/.test(name) ||
      /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$/i.test(name.split(".")[0])) throw new Error("组合 STEP 文件名无效。");
  return name + ".step";
}
async function startGeneration() {
  if (state.busy) return;
  let rows;
  try { rows = normalizeRows(); }
  catch (error) { setJob("failed",error.message); writeLog("生成前检查失败：" + error.message); return; }
  if (!state.engine) { setJob("failed","SolidCraft 浏览器引擎尚未就绪。"); return; }
  const gap = Number(el.viewGap.value);
  if (!Number.isFinite(gap) || gap < 10 || gap > 5000) { setJob("failed","组合件间距须为 10 至 5000 mm。"); return; }
  let combinedName;
  try { combinedName = safeStepName(el.stepName.value); }
  catch (error) { setJob("failed",error.message); return; }
  setBusy(true); el.jobProgress.hidden = false; el.jobProgress.max = rows.length + (el.combineStep.checked ? 1 : 0); el.jobProgress.value = 0;
  el.log.textContent = ""; setJob("running","所有建模和 STEP 导出均在浏览器本地执行。");
  const files = [], pieces = [], prefix = "rs-" + crypto.randomUUID().replace(/-/g,"");
  try {
    if (state.needsReset) {
      state.engine = null;
      await new Promise((resolve, reject) => {
        const frame = el.solidCraftFrame;
        const timeout = setTimeout(() => reject(new Error("重新加载模型模板超时。")), 60000);
        frame.addEventListener("load", () => { clearTimeout(timeout); resolve(); }, {once:true});
        frame.src = state.engineUrl;
      });
      await waitForEngine();
    }
    state.needsReset = true;
    for (let i = 0; i < rows.length; i++) {
      const row = rows[i], base = filePart(row[0]), temp = prefix + "-" + (i + 1);
      setJob("running","正在生成 " + (i + 1) + "/" + rows.length + "：" + row[0]);
      const changes = rowChanges(row);
      await engineExecute("parameters.change",{changes});
      const parameterResult = await engineExecute("parameters.list",{filter:"user"});
      const parameterList = parameterResult && Array.isArray(parameterResult.parameters) ? parameterResult.parameters : [];
      const halfChecks = parameterList.filter(parameter => parameter.name === "半孔校验");
      const halfValue = halfChecks.length === 1 ? readNumber(halfChecks[0].value) : NaN;
      if (halfChecks.length !== 1 || !Number.isFinite(halfValue) || Math.abs(halfValue) > 1e-9) throw new Error("型号 " + row[0] + " 的半孔校验未通过。");
      const doc = await state.engine.inspect({measure:true});
      if (!Array.isArray(doc.bodies) || doc.bodies.length === 0) throw new Error("型号 " + row[0] + " 没有有效实体。");
      const bounds = el.combineStep.checked ? boundsOf(doc) : {bodies:doc.bodies.length,min:[0,0,0],max:[0,0,0]};
      if (Array.isArray(doc.timeline)) {
        const failed = doc.timeline.find(feature => feature.error !== undefined && feature.error !== null && String(feature.error).trim());
        if (failed) throw new Error("型号 " + row[0] + " 重算失败：" + String(failed.error));
      }
      const nativeName = temp + ".solidcraft", stepName = temp + ".step";
      const nativePath = "/opfs/" + nativeName, stepPath = "/opfs/" + stepName;
      const nativeResult = await engineExecute("file.save_as",{path:nativePath,format:"container",cache:true,preview:true});
      const stepResult = await engineExecute("file.export",{path:stepPath,format:"step"});
      files.push({name:base + ".solidcraft",bytes:await opfsBytes(nativeName, nativeResult.bytes)});
      files.push({name:base + ".step",bytes:await opfsBytes(stepName, stepResult.bytes)});
      pieces.push({row,base,bounds,stepPath});
      el.jobProgress.value = i + 1;
      writeLog("已在本机生成 " + base + "（SolidCraft + STEP）。");
    }
    if (el.combineStep.checked) {
      setJob("running","正在浏览器内排布并检查组合 STEP…");
      await engineExecute("file.new",{name:"辊密封组合"});
      let cursor = 0;
      for (const piece of pieces) {
        const offset = cursor - piece.bounds.min[0];
        const inserted = await engineExecute("file.insert_step",{path:piece.stepPath,name:piece.row[0],up:"z"});
        const occurrence = occurrenceId(inserted);
        await engineExecute("occurrence.ground",{occurrence,grounded:false});
        await engineExecute("occurrence.move",{occurrence,space:"world",translate:[offset,0,0]});
        cursor += piece.bounds.max[0] - piece.bounds.min[0] + gap;
      }
      const assembly = await state.engine.inspect({measure:true}), assemblyBounds = boundsOf(assembly);
      const expectedBodies = pieces.reduce((sum,piece) => sum + piece.bounds.bodies,0);
      if (assemblyBounds.bodies !== expectedBodies) throw new Error("组合中的实体数量与单件不一致，已停止导出。");
      const interference = await engineExecute("inspect.interference",{budget_s:240});
      requireClearInterference(interference,expectedBodies);
      const combinedTemp = prefix + "-combined.step", combinedPath = "/opfs/" + combinedTemp;
      const combinedResult = await engineExecute("file.export",{path:combinedPath,format:"step"});
      files.push({name:combinedName,bytes:await opfsBytes(combinedTemp, combinedResult.bytes)});
      el.jobProgress.value = rows.length + 1;
      writeLog("组合 STEP 已导出，浏览器内实体数量与相交检查通过。");
    }
    if (el.exportCsv.checked) files.push({name:"辊密封_" + dateStamp() + ".csv",bytes:new TextEncoder().encode(csvText(rows))});
    const zip = makeZip(files);
    const zipName = "辊密封_" + dateStamp() + "_STEP.zip";
    safeDownload(zip,zipName,"application/zip");
    const generatedAtUtc = new Date().toISOString();
    const recorded = await submitGenerationRecord(rows,generatedAtUtc);
    setJob("completed",recorded ? "生成完成，结果 ZIP 已下载到当前设备。" : "生成完成，结果 ZIP 已下载；服务器生成记录未能提交。");
    writeLog("浏览器已生成并下载 " + zipName + "（" + files.length + " 个文件）；没有上传模型或导出文件。");
  } catch (error) {
    setJob("failed",error && error.message ? error.message : String(error));
    writeLog("生成失败：" + (error && error.message ? error.message : String(error)));
  } finally {
    setBusy(false);
  }
}
async function submitGenerationRecord(rows,generatedAtUtc) {
  try {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 10000);
    let response;
    try {
      response = await fetch("https://srs.arcsiny.com/api/generation-records",{
        method:"POST",headers:{"Content-Type":"application/json"},
        body:JSON.stringify({generatedAtUtc,rows}),signal:controller.signal
      });
    } finally { clearTimeout(timeout); }
    if (!response.ok) {
      const message = await response.text();
      throw new Error(message || ("HTTP " + response.status));
    }
    writeLog("仅生成参数与时间已提交；来源 IP 由服务器从请求连接记录。");
    return true;
  } catch (error) {
    writeLog("服务器生成记录提交失败：" + error.message);
    return false;
  }
}

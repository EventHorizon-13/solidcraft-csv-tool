"use strict";

const els = {};
const state = { columns: [], flags: new Set(), rows: [], defaults: {}, currentJob: null, poll: null, resourceId: sessionStorage.getItem("rollerSealResources") || null, resources: null };
const settingIds = ["combineStep","exportCsv","exportPdf","dxfName","stepName","dimensionStyle","hiddenLineScale","viewGap"];

document.addEventListener("DOMContentLoaded", () => {
  for (const id of ["apiToken","connectBtn","connectionState","csvFile","importBtn","addBtn","validateBtn","rowCount","grid",
    "combineStep","exportCsv","exportPdf","dxfName","stepName","dimensionStyle","hiddenLineScale","viewGap",
    "stepBtn","dxfBtn","capabilities","jobState","jobMessage","jobProgress","cancelBtn","downloadBtn","log","drawingTemplate","fontFiles","uploadResourcesBtn","resetResourcesBtn","resourceStatus"]) {
    els[id] = document.getElementById(id);
  }
  els.apiToken.value = sessionStorage.getItem("rollerSealToken") || "";
  els.connectBtn.addEventListener("click", connect);
  els.importBtn.addEventListener("click", () => els.csvFile.click());
  els.csvFile.addEventListener("change", importCsv);
  els.addBtn.addEventListener("click", addRow);
  els.validateBtn.addEventListener("click", validateRows);
  els.stepBtn.addEventListener("click", () => startJob(false));
  els.dxfBtn.addEventListener("click", () => startJob(true));
  els.cancelBtn.addEventListener("click", cancelJob);
  els.downloadBtn.addEventListener("click", downloadResult);
  els.grid.addEventListener("paste", pasteGrid);
  settingIds.forEach(id => els[id].addEventListener("change", saveSettings));
  els.uploadResourcesBtn.addEventListener("click", uploadResources);
  els.resetResourcesBtn.addEventListener("click", () => { selectResources(null); connect(); });
  connect();
});

function headers(extra) {
  const h = new Headers(extra || {});
  const token = els.apiToken.value.trim();
  if (token) h.set("X-RollerSeal-Token", token);
  return h;
}

async function api(path, options) {
  const init = Object.assign({}, options || {});
  init.headers = headers(init.headers);
  const response = await fetch(path, init);
  const type = response.headers.get("content-type") || "";
  const body = type.includes("application/json") ? await response.json() : null;
  if (!response.ok) throw new Error(body && body.error ? body.error : "请求失败：" + response.status);
  return body;
}

async function connect() {
  sessionStorage.setItem("rollerSealToken", els.apiToken.value.trim());
  setConnection("正在连接…");
  try {
    const data = await api("/api/bootstrap");
    state.columns = data.columns;
    state.flags = new Set(data.flags);
    state.defaults = data.defaults;
    loadSettings();
    if (!state.rows.length) state.rows = [data.defaultRow.slice()];
    renderTable();
    const caps = [];
    caps.push(data.capabilities.solidCraft ? "SolidCraft CLI 已就绪" : "缺少 SolidCraft CLI");
    caps.push(data.capabilities.drawingTemplate ? "DXF 图框已就绪" : "未发现 DXF 图框");
    caps.push(data.capabilities.pdf ? "PDF 资源已发现" : "PDF 资源可能不完整");
    els.capabilities.textContent = caps.join(" · ");
    state.resources = { pdf: data.capabilities.pdf };
    if (state.resourceId) {
      try { selectResources(await api("/api/resources/" + state.resourceId)); }
      catch (error) { selectResources(null); els.resourceStatus.textContent = error.message; }
    }
    setConnection("已连接 · " + data.version);
  } catch (error) {
    setConnection(error.message);
  }
}

function setConnection(text) {
  els.connectionState.textContent = text;
}

function loadSettings() {
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem("rollerSealWebSettings.v1") || "{}"); } catch (_) {}
  for (const id of settingIds) {
    const fallback = state.defaults[id];
    const value = Object.prototype.hasOwnProperty.call(saved, id) ? saved[id] : fallback;
    if (els[id].type === "checkbox") els[id].checked = Boolean(value);
    else if (value !== undefined && value !== null) els[id].value = String(value);
  }
}

function saveSettings() {
  const saved = {};
  for (const id of settingIds) saved[id] = els[id].type === "checkbox" ? els[id].checked : els[id].value;
  localStorage.setItem("rollerSealWebSettings.v1", JSON.stringify(saved));
}

function renderTable() {
  const thead = els.grid.tHead;
  const tbody = els.grid.tBodies[0];
  thead.replaceChildren();
  tbody.replaceChildren();

  const headerRow = document.createElement("tr");
  const actionHead = document.createElement("th");
  actionHead.textContent = "操作";
  headerRow.appendChild(actionHead);
  state.columns.forEach(name => {
    const th = document.createElement("th");
    th.textContent = name;
    headerRow.appendChild(th);
  });
  thead.appendChild(headerRow);

  state.rows.forEach((row, rowIndex) => {
    const tr = document.createElement("tr");
    const action = document.createElement("td");
    const del = document.createElement("button");
    del.type = "button";
    del.className = "delete";
    del.textContent = "删除";
    del.addEventListener("click", () => {
      state.rows.splice(rowIndex, 1);
      if (!state.rows.length) state.rows.push(defaultRowFor(state.rows.length + 1));
      renderTable();
    });
    action.appendChild(del);
    tr.appendChild(action);

    state.columns.forEach((name, columnIndex) => {
      const td = document.createElement("td");
      const input = document.createElement("input");
      input.dataset.row = String(rowIndex);
      input.dataset.column = String(columnIndex);
      if (state.flags.has(name)) {
        td.className = "flag";
        input.type = "checkbox";
        input.checked = row[columnIndex] === "1";
        input.addEventListener("change", () => { state.rows[rowIndex][columnIndex] = input.checked ? "1" : "0"; });
      } else {
        input.type = "text";
        input.value = row[columnIndex] || "";
        input.autocomplete = "off";
        input.spellcheck = false;
        input.addEventListener("input", () => { state.rows[rowIndex][columnIndex] = input.value; });
      }
      td.appendChild(input);
      tr.appendChild(td);
    });
    tbody.appendChild(tr);
  });
  els.rowCount.textContent = state.rows.length + " 行";
}

function defaultRowFor(number) {
  const row = new Array(state.columns.length).fill("");
  if (state.columns.length) {
    const base = state.defaults.defaultRow || null;
    if (base) return base.slice();
    row[0] = String(number);
  }
  return row;
}

function addRow() {
  if (!state.columns.length) return;
  const prior = state.rows[state.rows.length - 1];
  const row = prior ? prior.slice() : new Array(state.columns.length).fill("");
  row[0] = String(state.rows.length + 1);
  state.rows.push(row);
  renderTable();
}

async function importCsv() {
  const file = els.csvFile.files[0];
  if (!file) return;
  try {
    const form = new FormData();
    form.append("file", file, file.name);
    const data = await api("/api/csv/parse", { method: "POST", body: form });
    state.rows = data.rows;
    renderTable();
    writeLog("已导入 " + state.rows.length + " 行 CSV。");
  } catch (error) {
    writeLog("导入失败：" + error.message);
  } finally {
    els.csvFile.value = "";
  }
}

function pasteGrid(event) {
  const target = event.target;
  if (!(target instanceof HTMLInputElement) || target.type === "checkbox") return;
  const startRow = Number(target.dataset.row);
  const startColumn = Number(target.dataset.column);
  if (!Number.isInteger(startRow) || !Number.isInteger(startColumn)) return;
  const text = event.clipboardData.getData("text/plain");
  if (!text.includes("\t") && !text.includes("\n")) return;

  event.preventDefault();
  const lines = text.replace(/\r/g, "").split("\n");
  if (lines.length && lines[lines.length - 1] === "") lines.pop();
  const matrix = lines.map(line => line.split("\t"));
  const needed = Math.min(500, startRow + matrix.length);
  while (state.rows.length < needed) addRowSilently();

  matrix.forEach((line, r) => line.forEach((value, c) => {
    const rr = startRow + r, cc = startColumn + c;
    if (rr >= state.rows.length || cc >= state.columns.length) return;
    if (state.flags.has(state.columns[cc])) state.rows[rr][cc] = /^(1|true|是|yes)$/i.test(value.trim()) ? "1" : "0";
    else state.rows[rr][cc] = value.trim();
  }));
  renderTable();
}

function addRowSilently() {
  const prior = state.rows[state.rows.length - 1];
  const row = prior ? prior.slice() : new Array(state.columns.length).fill("");
  row[0] = String(state.rows.length + 1);
  state.rows.push(row);
}

async function validateRows() {
  try {
    const data = await api("/api/validate", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ rows: state.rows })
    });
    writeLog("参数检查通过：" + data.count + " 行。");
  } catch (error) {
    writeLog("参数检查失败：" + error.message);
  }
}

function jobRequest(exportDxf) {
  return {
    resourceId: state.resourceId,
    rows: state.rows,
    combineStep: els.combineStep.checked,
    exportCsv: els.exportCsv.checked,
    exportDxf: exportDxf,
    exportPdf: exportDxf && els.exportPdf.checked,
    dxfName: els.dxfName.value.trim(),
    stepName: els.stepName.value.trim(),
    hiddenLineScale: Number(els.hiddenLineScale.value),
    viewGap: Number(els.viewGap.value),
    dimensionStyle: els.dimensionStyle.value.trim()
  };
}

async function startJob(exportDxf) {
  if (state.currentJob) return;
  if (exportDxf && els.exportPdf.checked && !state.resources?.pdf) {
    setJobState("failed", "请先上传固定 A4 DXF 图框及 gbenor.shx、gbcbig.shx 字体。");
    return;
  }
  saveSettings();
  clearDownload();
  try {
    const data = await api("/api/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(jobRequest(exportDxf))
    });
    state.currentJob = data.id;
    els.cancelBtn.disabled = false;
    els.stepBtn.disabled = true;
    els.dxfBtn.disabled = true;
    setJobState("queued", "等待执行");
    await pollJob();
    state.poll = setInterval(pollJob, 800);
  } catch (error) {
    setJobState("failed", error.message);
    writeLog("启动失败：" + error.message);
  }
}

async function pollJob() {
  if (!state.currentJob) return;
  try {
    const data = await api("/api/jobs/" + state.currentJob);
    setJobState(data.status, data.message);
    els.jobProgress.max = Math.max(state.rows.length, 1);
    els.jobProgress.value = Math.min(data.completed, els.jobProgress.max);
    els.log.textContent = data.logs.join("\n");
    els.log.scrollTop = els.log.scrollHeight;

    if (["completed","failed","cancelled"].includes(data.status)) {
      clearInterval(state.poll);
      state.poll = null;
      els.cancelBtn.disabled = true;
      els.stepBtn.disabled = false;
      els.dxfBtn.disabled = false;
      if (data.downloadable) {
        els.downloadBtn.dataset.job = state.currentJob;
        els.downloadBtn.classList.remove("disabled");
        els.downloadBtn.setAttribute("aria-disabled", "false");
      }
      state.currentJob = null;
    }
  } catch (error) {
    clearInterval(state.poll);
    state.poll = null;
    state.currentJob = null;
    els.cancelBtn.disabled = true;
    els.stepBtn.disabled = false;
    els.dxfBtn.disabled = false;
    setJobState("failed", error.message);
  }
}

async function cancelJob() {
  if (!state.currentJob) return;
  try {
    await api("/api/jobs/" + state.currentJob + "/cancel", { method: "POST" });
    els.cancelBtn.disabled = true;
  } catch (error) {
    writeLog("取消失败：" + error.message);
  }
}

function setJobState(status, message) {
  const names = { queued:"排队中", running:"生成中", completed:"已完成", failed:"失败", cancelled:"已取消" };
  els.jobState.textContent = names[status] || status;
  els.jobMessage.textContent = message || "";
}

async function downloadResult(event) {
  event.preventDefault();
  const id = els.downloadBtn.dataset.job;
  if (!id) return;
  try {
    const response = await fetch("/api/jobs/" + id + "/download", { headers: headers() });
    if (!response.ok) {
      let message = "下载失败：" + response.status;
      try {
        const body = await response.json();
        if (body.error) message = body.error;
      } catch (_) {}
      throw new Error(message);
    }
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = "辊密封_" + id.slice(0, 8) + ".zip";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  } catch (error) {
    writeLog("下载失败：" + error.message);
  }
}

function clearDownload() {
  delete els.downloadBtn.dataset.job;
  els.downloadBtn.removeAttribute("href");
  els.downloadBtn.classList.add("disabled");
  els.downloadBtn.setAttribute("aria-disabled", "true");
  els.jobProgress.value = 0;
  els.log.textContent = "";
}

function writeLog(text) {
  if (els.log.textContent) els.log.textContent += "\n";
  els.log.textContent += text;
  els.log.scrollTop = els.log.scrollHeight;
}


function selectResources(data) {
  state.resourceId = data?.resourceId || null;
  state.resources = data;
  if (state.resourceId) sessionStorage.setItem("rollerSealResources", state.resourceId);
  else sessionStorage.removeItem("rollerSealResources");
  els.resourceStatus.textContent = data
    ? [data.drawingTemplate ? "图框已上传" : "缺少图框", data.fonts.gbenor ? "gbenor 已上传" : "缺少 gbenor", data.fonts.gbcbig ? "gbcbig 已上传" : "缺少 gbcbig", data.pdf ? "可导出 PDF" : "PDF 资源未齐"].join(" · ")
    : "使用内置资源";
  if (data) els.capabilities.textContent = "当前使用上传资源 · " + (data.pdf ? "图框与字体已齐" : "PDF 资源未齐");
}

async function uploadResources() {
  const form = new FormData();
  const frame = els.drawingTemplate.files[0];
  const fonts = Array.from(els.fontFiles.files);
  if (!frame && !fonts.length) { els.resourceStatus.textContent = "请先选择 DXF 图框或 SHX 字体。"; return; }
  if ((frame && frame.size > 8 * 1024 * 1024) || fonts.some(f => f.size > 4 * 1024 * 1024) || fonts.length > 2) {
    els.resourceStatus.textContent = "DXF 最大 8 MiB，最多两个字体且每个最大 4 MiB。"; return;
  }
  if (state.resourceId) form.append("resourceId", state.resourceId);
  if (frame) form.append("drawingTemplate", frame);
  fonts.forEach(file => form.append("fonts", file));
  els.uploadResourcesBtn.disabled = true;
  els.resourceStatus.textContent = "正在上传…";
  try {
    selectResources(await api("/api/resources", { method: "POST", body: form }));
    els.drawingTemplate.value = "";
    els.fontFiles.value = "";
  } catch (error) { els.resourceStatus.textContent = "上传失败：" + error.message; }
  finally { els.uploadResourcesBtn.disabled = false; }
}

"use strict";

const els = {};
const state = { columns: [], flags: new Set(), rows: [], defaults: {}, currentJob: null, poll: null };
const settingIds = ["combineStep","exportCsv","stepName","hiddenLineScale","viewGap"];

document.addEventListener("DOMContentLoaded", () => {
  for (const id of ["apiToken","connectBtn","connectionState","csvFile","importBtn","addBtn","validateBtn","rowCount","grid","jobPanel","jobActions",
    "combineStep","exportCsv","stepName","hiddenLineScale","viewGap",
    "stepBtn","dxfBtn","pdfBtn","jobState","jobMessage","jobProgress","cancelBtn","downloadBtn","log"]) {
    els[id] = document.getElementById(id);
  }
  sessionStorage.removeItem("rollerSealResources");
  els.apiToken.value = sessionStorage.getItem("rollerSealToken") || "";
  els.connectBtn.addEventListener("click", connect);
  els.importBtn.addEventListener("click", () => els.csvFile.click());
  els.csvFile.addEventListener("change", importCsv);
  els.addBtn.addEventListener("click", addRow);
  els.validateBtn.addEventListener("click", validateRows);
  els.stepBtn.addEventListener("click", () => startJob("step"));
  els.dxfBtn.addEventListener("click", () => startJob("dxf"));
  els.pdfBtn.addEventListener("click", () => startJob("pdf"));
  els.cancelBtn.addEventListener("click", cancelJob);
  els.downloadBtn.addEventListener("click", downloadResult);
  els.grid.addEventListener("paste", pasteGrid);
  settingIds.forEach(id => els[id].addEventListener("change", saveSettings));
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
  if (!localStorage.getItem("rollerSealHiddenScaleDefault.v2")) {
    if (Number(saved.hiddenLineScale) === 1) delete saved.hiddenLineScale;
    localStorage.setItem("rollerSealHiddenScaleDefault.v2", "1");
    localStorage.setItem("rollerSealWebSettings.v1", JSON.stringify(saved));
  }
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

function jobRequest(format) {
  return {
    rows: state.rows,
    combineStep: els.combineStep.checked,
    exportCsv: els.exportCsv.checked,
    outputFormat: format,
    stepName: els.stepName.value.trim(),
    hiddenLineScale: Number(els.hiddenLineScale.value),
    viewGap: Number(els.viewGap.value)
  };
}

async function startJob(format) {
  if (state.currentJob) return;
  saveSettings();
  clearDownload();
  try {
    const data = await api("/api/jobs", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(jobRequest(format))
    });
    state.currentJob = data.id;
    els.jobPanel.open = true;
    document.querySelector(".log-details").open = true;
    els.jobProgress.hidden = false;
    els.jobActions.hidden = false;
    els.cancelBtn.hidden = false;
    els.cancelBtn.disabled = false;
    els.stepBtn.disabled = true;
    els.dxfBtn.disabled = true;
    els.pdfBtn.disabled = true;
    setJobState("queued", "等待执行");
    await pollJob();
    state.poll = setInterval(pollJob, 800);
  } catch (error) {
    els.cancelBtn.hidden = true;
    els.jobProgress.hidden = true;
    els.jobActions.hidden = true;
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
    els.log.textContent = data.logs.filter(line => !line.startsWith("PDF 已保存：") && !line.startsWith("PDF 已保存:")).join("\n");
    els.log.scrollTop = els.log.scrollHeight;

    if (["completed","failed","cancelled"].includes(data.status)) {
      clearInterval(state.poll);
      state.poll = null;
      els.cancelBtn.disabled = true;
      els.cancelBtn.hidden = true;
      els.stepBtn.disabled = false;
      els.dxfBtn.disabled = false;
      els.pdfBtn.disabled = false;
      if (data.downloadable) {
        els.downloadBtn.dataset.job = state.currentJob;
        els.downloadBtn.dataset.filename = data.downloadName || "结果.zip";
        els.downloadBtn.textContent = data.contentType === "application/pdf" ? "下载 PDF" : data.contentType === "application/dxf" ? "下载 DXF" : "下载结果 ZIP";
        els.downloadBtn.classList.remove("disabled");
        els.downloadBtn.setAttribute("aria-disabled", "false");
        els.downloadBtn.hidden = false;
        els.jobActions.hidden = false;
      } else {
        els.downloadBtn.hidden = true;
        els.jobActions.hidden = true;
      }
      state.currentJob = null;
    }
  } catch (error) {
    clearInterval(state.poll);
    state.poll = null;
    state.currentJob = null;
    els.cancelBtn.disabled = true;
    els.cancelBtn.hidden = true;
    els.jobActions.hidden = true;
    els.jobProgress.hidden = true;
    els.stepBtn.disabled = false;
    els.dxfBtn.disabled = false;
      els.pdfBtn.disabled = false;
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
    link.download = els.downloadBtn.dataset.filename || "结果.zip";
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
  els.jobProgress.hidden = true;
  els.cancelBtn.hidden = true;
  els.downloadBtn.hidden = true;
  els.jobActions.hidden = true;
  els.log.textContent = "";
}

function writeLog(text) {
  els.jobPanel.open = true;
  document.querySelector(".log-details").open = true;
  if (els.log.textContent) els.log.textContent += "\n";
  els.log.textContent += text;
  els.log.scrollTop = els.log.scrollHeight;
}



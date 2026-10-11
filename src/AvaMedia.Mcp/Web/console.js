import { McpClient } from "./mcp-client.js";

const $ = id => document.getElementById(id), api = new McpClient();
const selected = new Map(), rows = new Map(), pageSize = 100, taskLimit = 30;
const pendingKey = "avamedia.pending-submission"; // localStorage is isolated by server origin.
let connected = false, directory = null, browseVersion = 0, taskVersion = 0, taskOffset = 0;
let taskNext = null, timer, pending = null, submitting = false, detailId = null, detailMode = "result", detailNext = null, detailVersion = 0;
const states = { Waiting: "等待中", Running: "执行中", Completed: "已完成", Failed: "失败", Cancelled: "已停止", Paused: "已暂停", Stopping: "停止中" };

function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined) element.textContent = text;
  if (className) element.className = className;
  return element;
}
function button(text, action, className) {
  const element = node("button", text, className); element.type = "button";
  element.addEventListener("click", () => run(element, action)); return element;
}
function notice(message, error = false) {
  $("notice").textContent = message; $("notice").hidden = !message; $("notice").classList.toggle("error", error);
}
async function run(element, action) {
  element.disabled = true;
  try { await action(); } catch (error) { notice(error.message, true); }
  finally { element.disabled = false; updateNavigation(); updateSubmission(); }
}
function updateNavigation() {
  $("up").disabled = !directory?.parent;
  $("files-prev").disabled = !directory?.offset;
  $("files-next").disabled = directory?.nextOffset == null;
  $("tasks-prev").disabled = !taskOffset;
  $("tasks-next").disabled = taskNext == null;
}
function bind(id, action) { $(id).addEventListener("click", () => run($(id), action)); }
function uuid() {
  // getRandomValues also works on plain HTTP LAN origins, unlike randomUUID.
  const bytes = crypto.getRandomValues(new Uint8Array(16)); bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
  const hex = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
}
function size(value) {
  if (value == null) return "—";
  const units = ["B", "KB", "MB", "GB", "TB"]; let index = 0;
  while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
  return `${value.toFixed(index ? 1 : 0)} ${units[index]}`;
}
function updateSubmission() {
  $("task-fields").disabled = !connected || submitting || !!pending;
  $("submit").disabled = !selected.size || !connected || submitting || !!pending;
  $("pending").hidden = !pending;
  $("recover").disabled = !connected || submitting;
  $("discard").disabled = submitting;
}
function updateSelection() {
  $("selection-count").textContent = `已选 ${selected.size} 项`;
  $("selected-summary").textContent = `已选文件（${selected.size}）`;
  $("selected-files").replaceChildren(...[...selected].map(([path, name]) => {
    const item = node("li", path); item.title = name;
    item.append(button("移除", () => { selected.delete(path); updateSelection(); })); return item;
  }));
  for (const checkbox of $("files").querySelectorAll("input[type=checkbox]")) checkbox.checked = selected.has(checkbox.value);
  const entries = directory?.entries || [], count = entries.filter(entry => selected.has(entry.path)).length;
  $("select-page").checked = entries.length > 0 && count === entries.length;
  $("select-page").indeterminate = count > 0 && count < entries.length;
  $("select-page").disabled = !entries.length;
  updateSubmission();
}
async function browse(path, offset = 0) {
  if (!path) return;
  const version = ++browseVersion;
  $("file-status").hidden = false; $("file-status").textContent = "读取中…";
  $("files-prev").disabled = true; $("files-next").disabled = true;
  try {
    const page = await api.call("avamedia_browse_directory", {
      folder: path, offset, limit: pageSize, search: $("search").value,
      extensions: $("extensions").value.split(/[,，\s]+/).filter(Boolean), showHidden: $("hidden").checked
    });
    if (version !== browseVersion) return;
    directory = page; $("address").value = page.path;
    $("files").replaceChildren(...page.entries.map(entry => {
      const row = node("tr"), checkCell = node("td"), nameCell = node("td"), checkbox = node("input");
      checkbox.type = "checkbox"; checkbox.value = entry.path; checkbox.setAttribute("aria-label", `选择 ${entry.name}`);
      checkbox.addEventListener("change", () => { if (checkbox.checked) selected.set(entry.path, entry.name); else selected.delete(entry.path); updateSelection(); });
      checkCell.append(checkbox);
      nameCell.append(button(`${entry.isDirectory ? "▸ " : ""}${entry.name}`, () => {
        if (entry.isDirectory) return browse(entry.path);
        if (selected.has(entry.path)) selected.delete(entry.path); else selected.set(entry.path, entry.name);
        updateSelection();
      }, entry.isDirectory ? "file-name folder" : "file-name"));
      row.append(checkCell, nameCell, node("td", entry.isDirectory ? "文件夹" : size(entry.size)),
        node("td", entry.modifiedUtc ? new Date(entry.modifiedUtc).toLocaleString() : "—"));
      return row;
    }));
    $("up").disabled = !page.parent;
    $("files-prev").disabled = page.offset === 0; $("files-next").disabled = page.nextOffset == null;
    $("file-page").textContent = `共 ${page.total} 项 · 第 ${Math.floor(page.offset / pageSize) + 1} 页`;
    $("file-status").textContent = page.entries.length ? "" : "此目录没有匹配项目"; $("file-status").hidden = !!page.entries.length;
    updateSelection();
  } catch (error) {
    if (version !== browseVersion) return;
    $("file-status").textContent = error.message;
    $("files-prev").disabled = !directory?.offset; $("files-next").disabled = directory?.nextOffset == null;
    throw error;
  }
}
async function connect() {
  connected = false; updateSubmission(); clearTimeout(timer); taskVersion++; browseVersion++;
  $("connection").textContent = "连接中…";
  try {
    const [capabilities, runtime, locations] = await Promise.all([
      api.call("avamedia_capabilities"), api.call("avamedia_get_runtime"), api.call("avamedia_file_locations")
    ]);
    $("feature").replaceChildren();
    const groups = new Map();
    for (const feature of capabilities.features.filter(feature => feature.canCreateTask && feature.id !== "download")) {
      if (!groups.has(feature.category)) { const group = node("optgroup"); group.label = feature.category; groups.set(feature.category, group); $("feature").append(group); }
      const option = node("option", feature.name); option.value = feature.id; option.dataset.format = feature.format;
      groups.get(feature.category).append(option);
    }
    $("locations").replaceChildren(...locations.locations.map(location => button(location.name, () => browse(location.path))));
    selected.clear(); directory = null; $("files").replaceChildren(); updateSelection();
    $("output").value = runtime.outputFolder; $("format").value = "";
    $("connection").textContent = `已连接 · ${runtime.version}`; connected = true; updateSubmission();
    notice("");
    await Promise.allSettled([browse(locations.defaultFolder).catch(error => notice(error.message, true)), refreshTasks()]);
  } catch (error) { $("connection").textContent = "连接失败"; throw error; }
}
async function submit() {
  if (!selected.size || pending || submitting) return;
  let options = {};
  if ($("options").value.trim()) options = JSON.parse($("options").value);
  if (!options || typeof options !== "object" || Array.isArray(options)) throw new Error("附加参数须为 JSON 对象。");
  for (const field of ["quality", "width", "height"]) if ($(field).value !== "") options[field] = Number($(field).value);
  if ($("format").value.trim()) options.format = $("format").value.trim();
  const args = { requestId: uuid(), featureId: $("feature").value, paths: [...selected.keys()],
    outputFolder: $("output").value.trim(), options, recursive: $("recursive").checked, startImmediately: $("start").checked };
  // Save before sending so an ambiguous network failure cannot silently create a second batch.
  localStorage.setItem(pendingKey, JSON.stringify(args)); pending = args;
  await sendPending();
}
async function sendPending() {
  if (!pending || submitting) return;
  submitting = true; updateSubmission();
  try {
    const result = await api.call("avamedia_create_task", pending);
    localStorage.removeItem(pendingKey); pending = null; selected.clear(); updateSelection();
    notice(`已创建 ${result.tasks.length} 项任务。`);
    $("task-state").value = "";
    const page = await api.call("avamedia_list_tasks", { limit: 1 });
    taskOffset = Math.max(0, Math.ceil(page.total / taskLimit) - 1) * taskLimit;
    await refreshTasks();
  } finally { submitting = false; updateSubmission(); }
}
function taskRow(task) {
  let entry = rows.get(task.id);
  if (!entry) {
    const row = node("tr"), name = node("td"), status = node("td"), output = node("td"), actionsCell = node("td");
    const title = node("div"), detail = node("small"), state = node("div"), progress = node("progress"), actions = node("div", undefined, "actions");
    progress.max = 100; progress.setAttribute("aria-label", "任务进度");
    name.append(title, detail); status.append(state, progress); actionsCell.append(actions); row.append(name, status, output, actionsCell);
    entry = { row, title, detail, state, progress, output, actions, actionState: null }; rows.set(task.id, entry);
  }
  entry.title.textContent = task.name;
  entry.detail.textContent = task.error || task.detail || task.activity?.detail || "";
  entry.detail.classList.toggle("error", !!task.error);
  entry.state.textContent = `${states[task.state] || task.state} · ${Number(task.progress).toFixed(1)}%`;
  entry.progress.value = task.progress;
  entry.output.textContent = task.output || "—";
  if (task.outputIsPartial && task.output) entry.output.append(node("small", "部分结果 / 尚未完成"));
  if (entry.actionState !== task.state) {
    const actions = { Waiting: [["start","开始"],["pause","暂停"],["stop","停止"]], Running: [["pause","暂停"],["stop","停止"]], Paused: [["resume","恢复"],["stop","停止"]], Failed: [["retry","重试"]], Cancelled: [["retry","重试"]] };
    entry.actions.replaceChildren(button("详情", () => openDetail(task.id, task.name)));
    for (const [action, label] of actions[task.state] || []) entry.actions.append(button(label, async () => {
      await api.call("avamedia_control_task", { taskId: task.id, action }); await refreshTasks();
    }));
    entry.actionState = task.state;
  }
  return entry.row;
}
async function refreshTasks() {
  if (!connected) return;
  clearTimeout(timer); const version = ++taskVersion;
  try {
    const page = await api.call("avamedia_list_tasks", { offset: taskOffset, limit: taskLimit, state: $("task-state").value || null });
    if (version !== taskVersion) return;
    if (!page.tasks.length && taskOffset > 0 && page.total <= taskOffset) { taskOffset = Math.max(0, Math.ceil(page.total / taskLimit) - 1) * taskLimit; return refreshTasks(); }
    const ids = new Set(page.tasks.map(task => task.id));
    for (const [id, entry] of rows) if (!ids.has(id)) { entry.row.remove(); rows.delete(id); }
    page.tasks.forEach((task, index) => {
      const row = taskRow(task), current = $("tasks").children[index];
      if (current !== row) $("tasks").insertBefore(row, current || null);
      if (detailId === task.id) $("detail-state").textContent = `${states[task.state]} · ${Number(task.progress).toFixed(1)}% · ${task.error || task.detail}`;
    });
    taskNext = page.nextOffset;
    $("tasks-prev").disabled = taskOffset === 0; $("tasks-next").disabled = taskNext == null;
    $("task-page").textContent = `共 ${page.total} 项 · 第 ${Math.floor(taskOffset / taskLimit) + 1} 页`;
    $("queue-status").textContent = page.total ? `更新于 ${new Date().toLocaleTimeString()}` : "暂无任务";
  } catch (error) { if (version === taskVersion) $("queue-status").textContent = `刷新失败：${error.message}`; }
  finally { if (version === taskVersion) timer = setTimeout(refreshTasks, document.hidden ? 15000 : 3000); }
}
async function openDetail(id, name) {
  detailId = id; $("detail-title").textContent = name; $("detail-state").textContent = "";
  $("detail-content").textContent = "读取中…"; $("task-detail").showModal();
  await readDetail("result");
}
async function readDetail(mode, offset = 0) {
  if (!detailId) return;
  const version = ++detailVersion, id = detailId; detailMode = mode; detailNext = null; $("detail-next").hidden = true;
  const result = await api.call(mode === "log" ? "avamedia_get_task_log" : "avamedia_get_task", mode === "log"
    ? { taskId: id, offset, maxCharacters: 16000 } : { taskId: id, includeResult: true, offset, limit: 100 });
  if (version !== detailVersion || id !== detailId) return;
  $("detail-content").textContent = mode === "log" ? (result.text || "暂无日志") : JSON.stringify(result, null, 2);
  detailNext = result.nextOffset; $("detail-next").hidden = detailNext == null;
}

$("server").textContent = location.host;
try { pending = JSON.parse(localStorage.getItem(pendingKey) || "null"); } catch { notice("无法读取上次提交记录。", true); }
bind("reconnect", connect);
bind("up", () => browse(directory?.parent));
bind("files-prev", () => browse(directory.path, Math.max(0, directory.offset - pageSize)));
bind("files-next", () => browse(directory.path, directory.nextOffset));
bind("clear-selection", () => { selected.clear(); updateSelection(); });
bind("use-output", () => { if (directory && !pending && !submitting) $("output").value = directory.path; });
$("select-page").addEventListener("change", () => {
  for (const entry of directory?.entries || []) { if ($("select-page").checked) selected.set(entry.path, entry.name); else selected.delete(entry.path); }
  updateSelection();
});
$("navigate").addEventListener("submit", event => { event.preventDefault(); run($("navigate").querySelector("button:last-child"), () => browse($("address").value)); });
$("filter").addEventListener("submit", event => { event.preventDefault(); run($("filter").querySelector("button"), () => browse(directory?.path || $("address").value)); });
$("submit-task").addEventListener("submit", event => { event.preventDefault(); run($("submit"), submit); });
$("feature").addEventListener("change", () => { $("format").value = ""; $("format").placeholder = $("feature").selectedOptions[0]?.dataset.format || "使用工具默认值"; });
bind("recover", sendPending);
bind("discard", () => {
  if (!confirm("服务可能已经接收任务。请先查看队列，确认要放弃本次提交的恢复记录？")) return;
  localStorage.removeItem(pendingKey); pending = null; updateSubmission();
});
bind("refresh-tasks", refreshTasks);
bind("tasks-prev", () => { taskOffset = Math.max(0, taskOffset - taskLimit); return refreshTasks(); });
bind("tasks-next", () => { if (taskNext != null) taskOffset = taskNext; return refreshTasks(); });
$("task-state").addEventListener("change", () => { taskOffset = 0; refreshTasks(); });
bind("close-detail", () => $("task-detail").close());
$("task-detail").addEventListener("close", () => { detailId = null; detailVersion++; });
bind("result", () => readDetail("result")); bind("log", () => readDetail("log"));
bind("detail-next", () => readDetail(detailMode, detailNext));
document.addEventListener("visibilitychange", () => { if (!document.hidden) refreshTasks(); });
run($("reconnect"), connect);

// 万象 PWA 客户端（纯前端，决策 2：不运行 Agent、不持有密钥、不持久缓存）。
// 状态保存在内存；连接配置与令牌按 instanceId 存 IndexedDB（决策 52）。
"use strict";

// ---------- IndexedDB 凭据存储 ----------
const DB = "wanxiang";
const STORE = "connections";
let idb = null;

function openDb() {
  return new Promise((resolve, reject) => {
    if (idb) return resolve(idb);
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE, { keyPath: "instanceId" });
    req.onsuccess = () => { idb = req.result; resolve(idb); };
    req.onerror = () => reject(req.error);
  });
}

async function saveConnection(instanceId, url, token, name) {
  const db = await openDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(STORE, "readwrite");
    tx.objectStore(STORE).put({ instanceId, url, token, name, updatedAt: Date.now() });
    tx.oncomplete = resolve; tx.onerror = () => reject(tx.error);
  });
}

async function loadConnections() {
  const db = await openDb();
  return new Promise((resolve, reject) => {
    const tx = db.transaction(STORE, "readonly");
    const req = tx.objectStore(STORE).getAll();
    req.onsuccess = () => resolve(req.result || []);
    req.onerror = () => reject(req.error);
  });
}

// 请求持久存储（决策 52：IndexedDB 可能被系统清理，丢失后重新配对即可）
if (navigator.storage && navigator.storage.persist) navigator.storage.persist().catch(() => {});

// ---------- WebSocket 客户端 ----------
let ws = null;
let state = {
  connected: false,
  authenticated: false,
  instanceId: null,
  token: null,
  url: null,
  cursor: 0,
  appliedAuthorityEvents: new Set(),
  conversations: new Map(),   // id -> {title, messages:[], lastCommitId, runtimeState}
  convList: [],               // [{conversationId,title,lastCommitId}]
  activeConv: null,
  generationId: null,
  pendingInv: null,
  sendBuffer: "",             // 流式 delta 累积（UI 展示用）
  streaming: false,
};

const $ = (id) => document.getElementById(id);

function toast(msg) {
  const t = document.createElement("div");
  t.className = "toast";
  t.textContent = msg;
  document.body.appendChild(t);
  setTimeout(() => t.remove(), 3500);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function applyConversationEvent(event, commitId) {
  const p = event.data || event.payload || event;
  const convId = p.conversationId;
  if (!convId) return true;
  const conv = state.conversations.get(convId);
  if (!conv) return false;
  if (event.type === "conversation.renamed") {
    conv.title = p.title;
  } else if (event.type === "conversation.config-updated") {
    conv.config = p.config;
  } else if (event.type === "conversation.deleted") {
    conv.deleted = true;
  } else if (event.type === "conversation.forked") {
    if (!state.conversations.has(convId)) state.conversations.set(convId, { title: "(未命名)", messages: [], lastCommitId: commitId, runtimeState: "idle" });
  } else {
    return false;
  }
  conv.lastCommitId = Math.max(Number(conv.lastCommitId || 0), Number(commitId || 0));
  return true;
}

function authorityCommitToEvents(line) {
  try {
    const commit = JSON.parse(line);
    let commitId = commit["id"]
    let formatVersion = commit["formatVersion"]
    let committedAtUtc = commit["committedAtUtc"]
    if (!Number.isSafeInteger(Number(commitId)) || Number(commitId) < 1 ||
        !Number.isInteger(Number(formatVersion)) || !committedAtUtc || !Array.isArray(commit.events)) {
      return false;
    }
    const events = commit.events;
    let applied = true;
    for (let eventIndex = 0; eventIndex < events.length; eventIndex++) {
      const event = events[eventIndex];
      const eventKey = `${commit.id}:${eventIndex}`;
      if (state.appliedAuthorityEvents.has(eventKey)) continue;
      const p = event.data || event.payload || event;
      if (event.type === "agent-message-recorded") {
        const convId = p.conversationId;
        const conv = state.conversations.get(convId);
        // 非观察会话的消息事件：跳过（服务端已按观察范围过滤；此处兜底）
        if (!conv || !convId || p.payload === undefined || p.payload === null) { continue; }
        const messageKey = `${commit.id}:${eventIndex}`;
        if (state.appliedAuthorityEvents.has(messageKey)) continue;
        conv.messages.push({ commitId: Number(commit.id || 0), payload: p.payload });
        conv.lastCommitId = Math.max(Number(conv.lastCommitId || 0), Number(commit.id || 0));
        state.appliedAuthorityEvents.add(messageKey);
        if (convId === state.activeConv) renderMessages();
      } else if (event.type === "conversation.created") {
        const convId = p.conversationId;
        if (!state.conversations.has(convId)) {
          state.conversations.set(convId, { title: p.title || "(未命名)", messages: [], lastCommitId: Number(commit.id || 0), runtimeState: "idle" });
        }
        state.appliedAuthorityEvents.add(eventKey);
      } else if (event.type === "conversation.deleted") {
        state.conversations.delete(p.conversationId);
        state.appliedAuthorityEvents.add(eventKey);
      } else if (event.type === "message.deleted") {
        // 删除消息：从本地会话消息中移除对应 commitId（决策 73 tombstone）
        const convId = p.conversationId;
        const delId = Number(p.messageCommitId || 0);
        const conv = state.conversations.get(convId);
        if (conv) {
          conv.messages = conv.messages.filter((m) => Number(m && m.commitId) !== delId);
          if (convId === state.activeConv) renderMessages();
        }
        state.appliedAuthorityEvents.add(eventKey);
      } else if (applyConversationEvent(event, commit.id)) {
        state.appliedAuthorityEvents.add(eventKey);
      } else {
        // 未识别或依赖完整 snapshot 的事件不能提前确认。
        applied = false;
      }
    }
    if (applied) state.cursor = Math.max(state.cursor, Number(commit.id || 0));
    return applied;
  } catch (_) { toast("权威同步数据无效"); return false; }
}

function uuidv7() { return crypto.randomUUID(); }

function connect(url, token) {
  return new Promise((resolve, reject) => {
    ws = new WebSocket(url);
    state.url = url; state.token = token;
    ws.onopen = () => {
      send({ type: "protocol.hello", payload: { protocol: "wanxiang", version: 1 } });
      if (token) send({ type: "auth.present", payload: { token } });
      resolve();
    };
    ws.onerror = () => reject(new Error("连接失败"));
    ws.onclose = () => {
      state.connected = false;
      state.authenticated = false;
      setStatus("已断开");
    };
    ws.onmessage = (e) => handleEvent(JSON.parse(e.data));
  });
}

// ---------- 事件处理 ----------
async function handleEvent(ev) {
  const p = ev.payload || {};
  switch (ev.type) {
    case "protocol.hello":
      if (p.version !== 1) { toast("协议版本不兼容，请升级客户端"); ws.close(); }
      break;
    case "protocol.upgrade-required":
      toast(`需要升级：服务器协议 v${p.serverVersion}`);
      break;
    case "auth.accepted":
      state.authenticated = true;
      state.instanceId = p.instanceId;
      setStatus("已连接");
      // 保存凭据（按 instanceId 为主键）
      if (state.token) await saveConnection(p.instanceId, state.url, state.token, "PWA");
      $("login").classList.remove("visible");
      $("main").classList.add("visible");
      send({ type: "conversation-list.observe" });
      break;
    case "auth.rejected":
      toast("认证失败：" + p.reason);
      break;
    case "pairing.started":
      $("pair-box").style.display = "flex";
      $("login-status").textContent = "配对码已生成，请在服务器终端查看（有效期 5 分钟）";
      break;
    case "pairing.succeeded":
      state.token = p.token;
      $("login-status").textContent = "配对成功，正在连接…";
      send({ type: "auth.present", payload: { token: p.token } });
      break;
    case "pairing.failed":
      toast("配对失败：" + p.reason);
      break;
    case "authority.catch-up": {
      if (Number(p.fromCursor || 0) !== Number(state.cursor)) {
        toast("同步游标不连续，请重新观察会话");
        break;
      }
      let allApplied = true;
      let expectedCursor = Number(p.fromCursor || 0);
      for (const line of (p.items || [])) {
        if (!allApplied) break;
        let applied = authorityCommitToEvents(line);
        allApplied = allApplied && applied;
        try {
          const commitId = Number(JSON.parse(line).id || 0);
          // 服务端按观察范围过滤后 id 可能跳号（非连续），但必须严格递增
          if (commitId <= expectedCursor) allApplied = false;
          expectedCursor = commitId;
        } catch (_) { allApplied = false; }
      }
      if (allApplied && expectedCursor <= Number(p.toCommitId || 0)) {
        state.cursor = expectedCursor;
        send({ type: "cursor.advanced", payload: { id: state.cursor } });
      } else {
        toast("同步批次不连续，请重新观察会话");
        state.appliedAuthorityEvents.clear();
        if (state.activeConv) send({ type: "conversation.observe", payload: { conversationId: state.activeConv } });
      }
      break;
    }
    case "conversation-list.snapshot":
      state.convList = p.items || [];
      renderList();
      break;
    case "conversation.snapshot": {
      const conv = state.conversations.get(p.conversationId) || { title: p.title, messages: [], lastCommitId: 0, runtimeState: "idle" };
      conv.title = p.title;
      // 快照消息结构：{ commitId, payload }（决策 79：commitId 即消息标识，供 fork 定位）
      conv.messages = (p.messages || []).map((m) => m && typeof m === "object" && "payload" in m ? { commitId: Number(m.commitId || 0), payload: m.payload } : { commitId: 0, payload: m });
      conv.lastCommitId = p.lastCommitId; conv.runtimeState = p.runtimeState;
      state.conversations.set(p.conversationId, conv);
      state.cursor = Math.max(state.cursor, p.lastCommitId);
      send({ type: "cursor.advanced", payload: { id: p.lastCommitId } });
      if (state.activeConv === p.conversationId) renderMessages();
      break;
    }
    case "conversation.message-committed": {
      const conv = state.conversations.get(p.conversationId);
      state.cursor = Math.max(state.cursor, Number(p.commitId || 0));
      if (conv) {
        const eventKey = `${p.commitId}:message:${p.conversationId}:${JSON.stringify(p.payload)}`;
        if (!state.appliedAuthorityEvents.has(eventKey)) {
          if (conv.lastCommitId < p.commitId) {
            conv.messages.push({ commitId: Number(p.commitId || 0), payload: p.payload });
            conv.lastCommitId = p.commitId;
          }
          state.appliedAuthorityEvents.add(eventKey);
        }
        // 流式结束：替换临时增量
        if (state.activeConv === p.conversationId) renderMessages();
      }
      send({ type: "cursor.advanced", payload: { id: state.cursor } });
      break;
    }
    case "conversation.updated":
      // 列表可能变化：重新 observe 列表摘要
      if (!state.conversations.has(p.conversationId)) {
        send({ type: "conversation-list.observe" });
      }
      break;
    case "command.accepted":
      $("login-status").textContent = "";
      break;
    case "command.committed":
      send({ type: "cursor.advanced", payload: { id: p.commitId } });
      break;
    case "command.rejected":
      if (p.code === "stale-projection") {
        toast("状态尚未同步，请稍候重试");
      } else {
        toast("操作被拒绝：" + p.message);
      }
      break;
    case "generation.started":
      state.generationId = p.generationId;
      state.streaming = true;
      state.sendBuffer = "";
      const conv = state.conversations.get(p.conversationId);
      if (conv) { conv.runtimeState = "generating"; if (state.activeConv === p.conversationId) renderMessages(); }
      break;
    case "generation.delta": {
      const c = state.conversations.get(p.conversationId);
      if (c && state.activeConv === p.conversationId) {
        // 提取文本增量（简化：替换为最新累积文本）
        state.sendBuffer = extractText(p.payload);
        renderMessages();
      }
      break;
    }
    case "generation.finished": {
      const c = state.conversations.get(p.conversationId);
      if (c) {
        c.runtimeState = "idle";
        if (state.activeConv === p.conversationId) { state.streaming = false; renderMessages(); }
      }
      if (p.status === "failed") toast("生成失败：" + (p.error || "未知错误"));
      break;
    }
    case "server.error":
      toast(p.message);
      break;
  }
}

function extractText(msg) {
  if (!msg) return "";
  if (typeof msg.text === "string") return msg.text;
  const parts = [];
  const walk = (o) => {
    if (!o || typeof o !== "object") return;
    if (typeof o.Text === "string") parts.push(o.Text);
    if (typeof o.text === "string") parts.push(o.text);
    if (Array.isArray(o.contents)) o.contents.forEach(walk);
    if (Array.isArray(o.Contents)) o.Contents.forEach(walk);
  };
  walk(msg);
  return parts.join("");
}

function roleOf(msg) {
  if (!msg) return "unknown";
  if (typeof msg.role === "string") return msg.role;
  if (msg.Role) return String(msg.Role.Value || msg.Role);
  return "unknown";
}

function displayText(msg) {
  if (!msg) return "";
  const parts = [];
  const walk = (o) => {
    if (!o || typeof o !== "object") return;
    if (typeof o.Text === "string") parts.push(o.Text);
    if (typeof o.text === "string" && typeof o.type === "undefined") parts.push(o.text);
    if (Array.isArray(o.contents)) o.contents.forEach(walk);
    if (Array.isArray(o.Contents)) o.Contents.forEach(walk);
  };
  walk(msg);
  if (parts.length) return parts.join("");
  return JSON.stringify(msg).slice(0, 300);
}

// ---------- 渲染 ----------
function setStatus(text) {
  $("status").textContent = text;
  $("conn-dot").classList.toggle("on", state.authenticated);
}

function renderList() {
  const list = $("conv-list");
  list.innerHTML = "";
  for (const item of state.convList) {
    const div = document.createElement("div");
    div.className = "conv-item" + (state.activeConv === item.conversationId ? " active" : "");
    div.innerHTML = `<div class="title"></div><div class="meta">#${item.lastCommitId}</div>`;
    div.querySelector(".title").textContent = item.title || "(未命名)";
    div.onclick = () => openConversation(item.conversationId);
    list.appendChild(div);
  }
}

function openConversation(id) {
  state.activeConv = id;
  state.streaming = false;
  state.sendBuffer = "";
  send({ type: "conversation.observe", payload: { conversationId: id } });
  // 若本地无数据，等 snapshot
  const conv = state.conversations.get(id);
  if (conv) {
    $("conv-title").textContent = conv.title;
    $("input").disabled = false;
    $("btn-send").disabled = false;
  } else {
    $("conv-title").textContent = "加载中…";
  }
  renderList();
  renderMessages();
}

function renderMessages() {
  const box = $("messages");
  const conv = state.activeConv && state.conversations.get(state.activeConv);
  box.innerHTML = "";
  if (!conv) return;
  $("conv-title").textContent = conv.title || "(未命名)";
  $("input").disabled = false;
  $("btn-send").disabled = false;
  $("gen-status").textContent = conv.runtimeState === "generating" ? "生成中…" : "";
  for (const m of conv.messages) {
    const payload = m && typeof m === "object" && "payload" in m ? m.payload : m;
    appendMessage(roleOf(payload), displayText(payload), false);
  }
  // 流式增量
  if (state.streaming && state.activeConv && state.sendBuffer) {
    appendMessage("assistant", state.sendBuffer, true);
  }
  box.scrollTop = box.scrollHeight;
}

function appendMessage(role, text, streaming) {
  const box = $("messages");
  const div = document.createElement("div");
  div.className = "msg " + (role === "user" ? "user" : role === "tool" ? "tool" : "") + (streaming ? " streaming" : "");
  if (role !== "user" && role !== "tool") {
    const r = document.createElement("div");
    r.className = "role";
    r.textContent = role === "assistant" ? "助手" : role;
    div.appendChild(r);
  }
  const t = document.createElement("div");
  t.textContent = text;
  div.appendChild(t);
  box.appendChild(div);
}

// ---------- 操作 ----------
async function sendMessage() {
  const text = $("input").value.trim();
  if (!text || !state.activeConv || !state.authenticated) return;
  $("input").value = "";
  const invocationId = uuidv7();
  const msg = { role: "user", contents: [{ text }] };
  send({ type: "chat.user-message.enqueue", payload: { invocationId, conversationId: state.activeConv, message: msg } });
  // 服务器提交后会通过 conversation.message-committed 广播权威消息；避免重复展示。
  const conv = state.conversations.get(state.activeConv);
  if (conv) {
    conv.pendingText = text;
    renderMessages();
  }
}

async function createConversation() {
  const title = prompt("会话标题（可留空）", "") || "新会话";
  const conversationId = uuidv7();
  send({
    type: "conversation.create",
    payload: { invocationId: uuidv7(), conversationId, title, config: { provider: "openai", model: "gpt-4o-mini" } },
  });
  // 稍后 observe
  setTimeout(() => send({ type: "conversation.observe", payload: { conversationId } }), 200);
}

function cancelGeneration() {
  if (!state.activeConv || !state.generationId) return;
  send({ type: "generation.cancel", payload: { conversationId: state.activeConv, generationId: state.generationId } });
}

function forkConversation() {
  const conv = state.activeConv && state.conversations.get(state.activeConv);
  if (!conv || !conv.messages.length) return toast("当前会话没有可 fork 的消息");
  const index = Math.max(0, conv.messages.length - 1);
  const parentMsg = conv.messages[index];
  const parentPayload = parentMsg && typeof parentMsg === "object" && "payload" in parentMsg ? parentMsg.payload : parentMsg;
  const edited = prompt("编辑 fork 的消息", displayText(parentPayload));
  if (edited === null) return;
  // 决策 75：fork 点 = 父会话最后一条被继承消息的全局提交 id（commitId 即消息标识，决策 79）
  const forkAfterId = Number(parentMsg && parentMsg.commitId) || 0;
  const conversationId = uuidv7();
  const message = { role: roleOf(parentPayload), contents: [{ text: edited }] };
  send({ type: "conversation.fork", payload: {
    invocationId: uuidv7(), conversationId, parentConversationId: state.activeConv,
    forkAfterId, config: { provider: "openai", model: "gpt-4o-mini" }, message
  }});
  setTimeout(() => send({ type: "conversation.observe", payload: { conversationId } }), 250);
}

$("btn-connect").onclick = async () => {
  const url = $("server-url").value.trim() || "ws://127.0.0.1:8765/ws";
  const token = $("server-token").value.trim();
  $("login-status").textContent = "连接中…";
  try {
    await connect(url, token || null);
  } catch (e) {
    $("login-status").textContent = String(e.message || e);
  }
};

$("btn-pair").onclick = () => {
  send({ type: "pairing.requested", payload: { clientName: "PWA" } });
};

$("btn-pair-submit").onclick = () => {
  const code = $("pair-code").value.trim();
  if (code.length !== 6) { toast("请输入 6 位配对码"); return; }
  send({ type: "pairing.attempted", payload: { code, clientName: "PWA" } });
};

$("btn-send").onclick = sendMessage;
$("input").addEventListener("keydown", (e) => { if (e.key === "Enter") sendMessage(); });
$("btn-new").onclick = createConversation;
$("btn-cancel").onclick = cancelGeneration;
$("btn-fork").onclick = forkConversation;

// 自动使用同源连接（决策 63：同源 PWA 自动连接本实例）
(async () => {
  const conns = await loadConnections();
  const origin = new URL(location.href).origin;
  const sameOrigin = conns.find((c) => c.url.startsWith("ws" + origin.slice(4) + "/ws") || c.url.startsWith("wss" + origin.slice(4) + "/ws"));
  if (sameOrigin) {
    $("server-url").value = sameOrigin.url;
    $("server-token").value = sameOrigin.token;
    $("btn-connect").click();
  } else {
    $("login").classList.add("visible");
    $("server-url").value = origin.replace(/^http/, "ws") + "/ws";
  }
})();

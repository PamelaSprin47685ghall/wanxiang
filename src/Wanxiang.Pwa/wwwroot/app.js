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
  conversations: new Map(),   // id -> {title, messages:[], lastCommitId, runtimeState, snapshotHasMore, snapshotEarliestCommitId}
  convList: [],               // [{conversationId,title,lastCommitId,lastMessage,runtimeState}]
  activeConv: null,
  generationId: null,
  pendingInv: null,
  streamParts: { text: "", reasoning: "" }, // 流式 delta 累积（UI 展示用）
  streaming: false,
  // P1-1：待发送附件引用（上传完成后随下一条用户消息写入）
  pendingAttachment: null,
  pendingAttachmentRaw: null,
  // P1-1：下载缓冲（sha256 -> {parts:[], fileName, mediaType}）
  downloads: new Map(),
  // P2-3：确认缺失的附件
  missingAttachments: new Set(),
  // P1-5：自动重连（指数退避）
  reconnectDelayMs: 1000,
  // 认证失败/升级需要：停止自动重连（避免坏令牌无限循环）
  authFailed: false,
  upgradeRequired: false,
  // 乐观删除回滚暂存（invocationId -> 被删会话摘要）
  pendingDelete: null,
  // P1-2：历史分页进行中标记
  historyLoading: false,
};

const $ = (id) => document.getElementById(id);

function toast(msg) {
  const t = document.createElement("div");
  t.className = "toast";
  // Q197：状态提示对屏幕阅读器可感知
  t.setAttribute("role", "status");
  t.textContent = msg;
  document.body.appendChild(t);
  setTimeout(() => t.remove(), 3500);
}

function send(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

// ---------- P1-3：新版本刷新提示（Q193：提示刷新，不强制 reload） ----------
async function registerServiceWorker() {
  if (!("serviceWorker" in navigator)) return;
  try {
    const reg = await navigator.serviceWorker.register("/sw.js");
    let refreshing = false;
    navigator.serviceWorker.addEventListener("controllerchange", () => {
      if (refreshing) return;
      refreshing = true;
      location.reload();
    });
    const promptUpdate = (worker) => {
      const t = document.createElement("div");
      t.className = "toast";
      t.style.cursor = "pointer";
      t.textContent = "有新版本，点击刷新";
      t.onclick = () => {
        // Q193：不在编辑/生成中强制 reload——若正在生成或输入框有未发送内容则等待并提示
        const busy = state.streaming || (document.activeElement === $("input") && $("input").value.trim());
        if (busy) {
          toast("正在生成或输入中，稍后再刷新以应用新版本");
          return;
        }
        worker.postMessage({ type: "SKIP_WAITING" });
      };
      document.body.appendChild(t);
      setTimeout(() => t.remove(), 15000);
    };
    reg.addEventListener("updatefound", () => {
      const newWorker = reg.installing;
      if (!newWorker) return;
      newWorker.addEventListener("statechange", () => {
        if (newWorker.state === "installed" && navigator.serviceWorker.controller) {
          promptUpdate(newWorker);
        }
      });
    });
  } catch (_) { /* SW 不可用时静默降级 */ }
}
registerServiceWorker();

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
        // 权威删除确认：清掉对应乐观删除暂存（成功路径状态自洽）
        if (state.pendingDelete && state.pendingDelete.item.conversationId === p.conversationId) {
          state.pendingDelete = null;
        }
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
    const socket = new WebSocket(url);
    ws = socket;
    state.url = url; state.token = token;
    socket.onopen = () => {
      send({ type: "protocol.hello", payload: { protocol: "wanxiang", version: 1 } });
      if (token) send({ type: "auth.present", payload: { token } });
      resolve();
    };
    socket.onerror = () => reject(new Error("连接失败"));
    socket.onclose = () => {
      if (ws !== socket) return; // 已有更新的连接
      state.connected = false;
      state.authenticated = false;
      setStatus("已断开");
      scheduleReconnect(socket);
    };
    socket.onmessage = (e) => handleEvent(JSON.parse(e.data));
  });
}

// P1-5：断线自动重连（指数退避 1s→30s；重连成功后重新 observe 此前观察的会话）
function scheduleReconnect(socket) {
  // 认证失败/需要升级：停止自动重连（避免坏令牌无限循环；Q193 不强制打断）
  if (!state.token || socket !== ws || state.authFailed || state.upgradeRequired) return;
  const delay = state.reconnectDelayMs;
  setStatus(`${delay / 1000}s 后自动重连…`);
  setTimeout(() => {
    if (socket !== ws) return;
    setStatus("重连中…");
    connect(state.url, state.token).then(() => {}).catch(() => {});
  }, delay);
}

// ---------- 事件处理 ----------
async function handleEvent(ev) {
  const p = ev.payload || {};
  switch (ev.type) {
    case "protocol.hello":
      if (p.version !== 1) {
        toast("协议版本不兼容，请升级客户端");
        state.upgradeRequired = true;
        ws.close();
      }
      break;
    case "protocol.upgrade-required":
      // 置标志停止自动重连（scheduleReconnect 检查该字段；此前从未赋值导致持续重连）
      state.upgradeRequired = true;
      toast(`需要升级：服务器协议 v${p.serverVersion}`);
      break;
    case "auth.accepted":
      state.authenticated = true;
      state.instanceId = p.instanceId;
      state.reconnectDelayMs = 1000;
      setStatus("已连接");
      // 保存凭据（按 instanceId 为主键）
      if (state.token) await saveConnection(p.instanceId, state.url, state.token, "PWA");
      $("login").classList.remove("visible");
      $("main").classList.add("visible");
      send({ type: "conversation-list.observe" });
      // P1-5：重连后恢复此前观察的会话
      if (state.activeConv) send({ type: "conversation.observe", payload: { conversationId: state.activeConv } });
      $("input").focus();
      break;
    case "auth.rejected":
      toast("认证失败：" + p.reason);
      // 认证失败：停止自动重连（避免坏令牌无限循环），提示重新配对/输入令牌
      state.authFailed = true;
      state.connected = false;
      state.authenticated = false;
      setStatus("认证失败，请重新配对");
      break;
    case "pairing.started":
      $("pair-box").classList.add("visible");
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
      renderMessages(); // 登录后首次渲染：无会话时展示空状态
      break;
    case "conversation.snapshot": {
      const conv = state.conversations.get(p.conversationId) || { title: p.title, messages: [], lastCommitId: 0, runtimeState: "idle" };
      conv.title = p.title;
      // 快照消息结构：{ commitId, payload }（决策 79：commitId 即消息标识，供 fork 定位）
      conv.messages = (p.messages || []).map((m) => m && typeof m === "object" && "payload" in m ? { commitId: Number(m.commitId || 0), payload: m.payload } : { commitId: 0, payload: m });
      conv.lastCommitId = p.lastCommitId; conv.runtimeState = p.runtimeState;
      // P1-2：长会话快照只带尾部，更早历史分页拉取（Q127）
      conv.snapshotHasMore = !!p.snapshotHasMore;
      conv.snapshotEarliestCommitId = Number(p.snapshotEarliestCommitId || 0);
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
      // P2-6：列表摘要（含 runtimeState）可能变化：始终重新 observe 列表
      send({ type: "conversation-list.observe" });
      break;
    case "history.page": {
      // P1-2：按 commitID 前置拼接更早历史（去重）
      const conv = state.conversations.get(p.conversationId);
      state.historyLoading = false;
      if (!conv) break;
      const box = $("messages");
      const prevHeight = box.scrollHeight;
      const existing = new Set(conv.messages.map((m) => Number(m.commitId || 0)));
      const prev = [];
      for (const m of (p.items || [])) {
        const cid = Number(m.commitId || 0);
        if (cid > 0 && !existing.has(cid)) prev.push({ commitId: cid, payload: m.payload });
      }
      if (prev.length) {
        conv.messages = prev.concat(conv.messages);
        conv.snapshotEarliestCommitId = prev[0].commitId;
      }
      conv.snapshotHasMore = !!p.hasMore;
      if (state.activeConv === p.conversationId) {
        renderMessages();
        // 保持滚动位置（新内容已插入到顶部之前）
        box.scrollTop = box.scrollHeight - prevHeight;
      }
      break;
    }
    case "command.accepted":
      $("login-status").textContent = "";
      break;
    case "command.committed":
      send({ type: "cursor.advanced", payload: { id: p.commitId } });
      break;
    case "command.rejected":
      if (p.code === "stale-projection") {
        // 决策 36：requiredCommitId 指明需要追到的全局提交 id，供用户/客户端判断同步进度
        const need = p.requiredCommitId ? `（需同步至 #${p.requiredCommitId}）` : "";
        toast("状态尚未同步，请稍候重试" + need);
      } else {
        toast("操作被拒绝：" + p.message);
      }
      // 乐观删除回滚：若被拒的是删除命令（invocationId 匹配），恢复会话列表（按 conversationId 去重防重复）
      if (state.pendingDelete && p.invocationId === state.pendingDelete.invocationId) {
        const pd = state.pendingDelete;
        state.pendingDelete = null;
        const restored = pd.item;
        state.convList = state.convList.filter((c) => c.conversationId !== restored.conversationId).concat([restored]);
        state.conversations.set(restored.conversationId, { title: restored.title || "(未命名)", messages: [], lastCommitId: 0, runtimeState: "idle" });
        renderList();
      }
      break;
    case "generation.started":
      state.generationId = p.generationId;
      state.streaming = true;
      state.streamParts = { text: "", reasoning: "" };
      const conv = state.conversations.get(p.conversationId);
      if (conv) { conv.runtimeState = "generating"; if (state.activeConv === p.conversationId) renderMessages(); }
      break;
    case "generation.delta": {
      const c = state.conversations.get(p.conversationId);
      if (c && state.activeConv === p.conversationId) {
        // 提取文本增量（简化：替换为最新累积文本）
        state.streamParts = extractParts(p.payload);
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
    case "attachment.committed":
      // P1-1：上传完成，引用随下一条用户消息写入
      state.pendingAttachment = { sha256: p.sha256, size: p.size, mediaType: "application/octet-stream", fileName: p.sha256 };
      if (state.pendingAttachmentRaw) {
        state.pendingAttachment.mediaType = state.pendingAttachmentRaw.mediaType;
        state.pendingAttachment.fileName = state.pendingAttachmentRaw.fileName;
        state.pendingAttachmentRaw = null;
      }
      renderPendingAttachment();
      break;
    case "attachment.aborted":
      state.pendingAttachment = null;
      state.pendingAttachmentRaw = null;
      renderPendingAttachment();
      toast("附件上传失败：" + (p.reason || "未知错误"));
      break;
    case "attachment.download-begin": {
      const key = String(p.sha256 || "").toLowerCase();
      state.downloads.set(key, { parts: [], fileName: p.fileName || key, mediaType: p.mediaType || "application/octet-stream" });
      break;
    }
    case "attachment.download-chunk": {
      const key = String(p.sha256 || "").toLowerCase();
      const dl = state.downloads.get(key);
      if (dl && p.data) dl.parts.push(p.data);
      break;
    }
    case "attachment.download-complete": {
      const key = String(p.sha256 || "").toLowerCase();
      const dl = state.downloads.get(key);
      if (!dl) break;
      state.downloads.delete(key);
      try {
        const binary = atob(dl.parts.join(""));
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        const blob = new Blob([bytes], { type: dl.mediaType });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = dl.fileName || "attachment";
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 5000);
      } catch (e) {
        toast("附件下载失败：" + e.message);
      }
      break;
    }
    case "server.error":
      // P2-3/Q179：附件缺失标记
      if (typeof p.message === "string" && p.message.startsWith("attachment ") && p.message.includes("not found")) {
        const sha = p.message.slice("attachment ".length, "attachment ".length + 64);
        state.missingAttachments.add(sha);
        if (state.activeConv) renderMessages();
      }
      toast(p.message);
      break;
  }
}

function extractParts(msg) {
  const text = [], reasoning = [];
  const walk = (o) => {
    if (!o || typeof o !== "object") return;
    if (o.$type === "reasoning" || o.type === "reasoning") {
      if (typeof o.text === "string") reasoning.push(o.text);
      if (typeof o.Text === "string") reasoning.push(o.Text);
      return; // 思维链单独收集，不并入正文
    }
    if (typeof o.Text === "string") text.push(o.Text);
    if (typeof o.text === "string" && typeof o.type === "undefined") text.push(o.text);
    if (Array.isArray(o.contents)) o.contents.forEach(walk);
    if (Array.isArray(o.Contents)) o.Contents.forEach(walk);
  };
  walk(msg);
  return { text: text.join(""), reasoning: reasoning.join("") };
}

function extractText(msg) {
  if (!msg) return "";
  if (typeof msg.text === "string") return msg.text;
  return extractParts(msg).text;
}

function roleOf(msg) {
  if (!msg) return "unknown";
  if (typeof msg.role === "string") return msg.role;
  if (msg.Role) return String(msg.Role.Value || msg.Role);
  return "unknown";
}

function displayText(msg) {
  if (!msg) return "";
  const parts = extractParts(msg).text;
  if (parts) return parts;
  return JSON.stringify(msg).slice(0, 300);
}

// ---------- 渲染 ----------
function setStatus(text) {
  const s = $("status"); if (s) s.textContent = text;
  $("conn-dot").classList.toggle("on", state.authenticated);
}

// Marked + Highlight.js + Lucide CDN 渲染器 (Mac 风格代码头)
if (window.marked && window.hljs) {
  marked.use({
    gfm: true,
    breaks: true,
    renderer: {
      code({ text, lang }) {
        const language = (lang && hljs.getLanguage(lang)) ? lang : "plaintext";
        let highlighted = text;
        try {
          highlighted = hljs.highlight(text, { language }).value;
        } catch (_) {
          // 与 html renderer 一致的完整转义（含 "）；代码块内容视为不可信文本
          highlighted = text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
        }
        return `<div class="code-block"><div class="code-header"><div class="mac-dots"><span class="mac-dot red"></span><span class="mac-dot yellow"></span><span class="mac-dot green"></span></div><span class="code-lang-tag">${language}</span><button class="code-copy" type="button" aria-label="复制代码"><i data-lucide="copy" style="width:13px;height:13px;"></i> 复制</button></div><pre><code class="hljs language-${language}">${highlighted}</code></pre></div>`;
      },
      // XSS 防护：对话内容视为不可信数据，原始 HTML 一律转义为文本（不执行脚本）
      html({ text }) {
        return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
      },
      // 链接协议白名单：仅 http/https/mailto 保留，javascript:/data: 等危险协议转为纯文本。
      // 链接文本用 parser.parseInline 走自定义 html/text renderer 转义（marked 官方推荐），
      // 避免 [<script>](url) 这类文本侧注入。
      link({ href, title, tokens }) {
        // 先做 HTML 实体解码再查白名单，防 javascript&#58;alert(1) 这类编码绕过
        const decodeHtml = (s) => String(s || "")
          .replace(/&amp;/g, "&").replace(/&#x([0-9a-f]+);/gi, (_, h) => String.fromCharCode(parseInt(h, 16)))
          .replace(/&#(\d+);/g, (_, d) => String.fromCharCode(parseInt(d, 10)))
          .replace(/&quot;/g, '"').replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&#39;/g, "'");
        // 剔除控制字符（换行/制表等）后再查白名单：浏览器解析 href 时忽略控制字符，
        // 若不解码剔除，`java&#x0a;script:` 类实体可绕过协议白名单
        const cleanHref = decodeHtml(href).replace(/[\u0000-\u001F\u007F\u2028\u2029]/g, "").trim().toLowerCase();
        const renderText = () => {
          if (tokens && tokens.length) {
            try { return this.parser.parseInline(tokens); } catch (_) { /* fallthrough */ }
          }
          return String(href || "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
        };
        if (/^(javascript|data|vbscript):/.test(cleanHref)) {
          return renderText();
        }
        // safeHref 全量转义 & < > "，防实体注入与属性逃逸
        const safeHref = String(href || "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
        const titleAttr = title ? ` title="${String(title).replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}"` : "";
        return `<a href="${safeHref}" target="_blank" rel="noopener noreferrer"${titleAttr}>${renderText()}</a>`;
      }
    }
  });
}

function renderMarkdown(raw) {
  if (!raw) return "";
  if (window.marked) {
    const thinkBlocks = [];
    let text = raw.replace(/<think>([\s\S]*?)<\/think>/gi, (_, content) => {
      const idx = thinkBlocks.length;
      thinkBlocks.push(content);
      return `%%THINK_BLOCK_${idx}%%`;
    });

    let html = marked.parse(text);

    html = html.replace(/%%THINK_BLOCK_(\d+)%%/g, (_, idx) => {
      const content = thinkBlocks[Number(idx)];
      const parsed = marked.parse(content);
      return `<details class="thinking-box" open><summary>思考过程</summary><div class="thinking-content">${parsed}</div></details>`;
    });

    return html;
  }
  return raw.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\n/g, "<br>");
}

window.copyCodeBlock = function(btn) {
  const code = btn.closest('.code-block').querySelector('code').innerText;
  navigator.clipboard.writeText(code).then(() => {
    btn.innerHTML = `<i data-lucide="check" style="width:13px;height:13px;"></i> 已复制`;
    if (window.lucide) lucide.createIcons();
    setTimeout(() => {
      btn.innerHTML = `<i data-lucide="copy" style="width:13px;height:13px;"></i> 复制`;
      if (window.lucide) lucide.createIcons();
    }, 2000);
  }).catch(() => toast("复制失败"));
};

// CSP 兼容：代码块复制按钮用事件委托绑定（不依赖内联 onclick，'unsafe-inline' 未开启）
$("messages").addEventListener("click", (e) => {
  const btn = e.target.closest && e.target.closest(".code-copy");
  if (btn && window.copyCodeBlock) window.copyCodeBlock(btn);
});

function renderList(searchQuery = "") {
  const list = $("conv-list");
  list.innerHTML = "";
  let items = state.convList;
  if (searchQuery) {
    const q = searchQuery.toLowerCase();
    items = items.filter(item => 
      (item.title && item.title.toLowerCase().includes(q)) ||
      (item.lastMessage && item.lastMessage.toLowerCase().includes(q))
    );
  }
  if (!items.length) {
    const empty = document.createElement("div");
    empty.className = "list-empty";
    empty.textContent = searchQuery ? "未找到匹配会话" : "还没有会话";
    list.appendChild(empty);
    return;
  }
  for (const item of items) {
    const div = document.createElement("div");
    div.className = "conv-item" + (state.activeConv === item.conversationId ? " active" : "");
    div.dataset.convId = item.conversationId;
    div.setAttribute("role", "button");
    div.tabIndex = 0;
    const titleRow = document.createElement("div");
    titleRow.className = "title-row";
    const title = document.createElement("div");
    title.className = "title";
    title.textContent = item.title || "(未命名)";
    titleRow.appendChild(title);
    // 悬停/聚焦时才揭露的…菜单：重命名/删除（默认零视觉负担，需要时才出现）
    const menuBtn = document.createElement("button");
    menuBtn.className = "conv-menu-btn";
    menuBtn.setAttribute("aria-label", "会话操作");
    menuBtn.innerHTML = `<i data-lucide="more-horizontal"></i>`;
    menuBtn.onclick = (e) => {
      e.stopPropagation();
      showConvMenu(menuBtn, item);
    };
    titleRow.appendChild(menuBtn);
    const meta = document.createElement("div");
    meta.className = "meta";
    const preview = document.createElement("span");
    preview.className = "preview";
    preview.textContent = item.lastMessage ? item.lastMessage : `#${item.lastCommitId}`;
    meta.appendChild(preview);
    if (item.runtimeState === "generating") {
      const badge = document.createElement("span");
      badge.className = "badge";
      badge.textContent = "生成中";
      meta.appendChild(badge);
    }
    div.appendChild(titleRow);
    div.appendChild(meta);
    div.onclick = () => openConversation(item.conversationId);
    div.onkeydown = (e) => { if (e.key === "Enter") openConversation(item.conversationId); };
    list.appendChild(div);
  }
  if (window.lucide) lucide.createIcons();
}

// 会话…菜单：重命名（就地输入框，无需弹框）/删除（轻量确认，复用已有模态框）
let openConvMenu = null;
function closeConvMenu() {
  if (openConvMenu) { openConvMenu.remove(); openConvMenu = null; }
}
document.addEventListener("click", closeConvMenu);
function showConvMenu(anchor, item) {
  closeConvMenu();
  const menu = document.createElement("div");
  menu.className = "conv-menu";
  const renameBtn = document.createElement("button");
  renameBtn.textContent = "重命名";
  renameBtn.onclick = (e) => { e.stopPropagation(); closeConvMenu(); startRename(item); };
  const deleteBtn = document.createElement("button");
  deleteBtn.textContent = "删除";
  deleteBtn.className = "danger";
  deleteBtn.onclick = (e) => { e.stopPropagation(); closeConvMenu(); confirmDelete(item); };
  menu.appendChild(renameBtn);
  menu.appendChild(deleteBtn);
  anchor.closest(".conv-item").appendChild(menu);
  openConvMenu = menu;
}
function startRename(item) {
  const items = document.querySelectorAll("#conv-list .conv-item");
  for (const div of items) {
    if (div.dataset.convId !== item.conversationId) continue;
    const titleEl = div.querySelector(".title");
    const input = document.createElement("input");
    input.className = "title-edit";
    input.value = item.title || "";
    input.maxLength = 80;
    titleEl.replaceWith(input);
    input.focus();
    input.select();
    const commit = () => {
      const next = input.value.trim();
      if (next && next !== item.title) {
        send({ type: "conversation.rename", payload: { invocationId: uuidv7(), conversationId: item.conversationId, title: next } });
        item.title = next;
      }
      renderList();
    };
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); commit(); }
      if (e.key === "Escape") { e.preventDefault(); renderList(); }
    });
    input.addEventListener("blur", commit);
    input.addEventListener("click", (e) => e.stopPropagation());
    break;
  }
}
function confirmDelete(item) {
  const p = document.createElement("p");
  p.className = "muted";
  p.textContent = `确定要删除会话“${item.title || "(未命名)"}”吗？此操作不可撤销。`;
  showModal({ title: "删除会话", body: p, confirmText: "删除", onConfirm: () => {
    const invocationId = uuidv7();
    send({ type: "conversation.delete", payload: { invocationId, conversationId: item.conversationId } });
    // 乐观移除列表项；若 command.rejected（如 stale），按 invocationId 回滚恢复
    state.pendingDelete = { invocationId, item };
    state.convList = state.convList.filter((c) => c.conversationId !== item.conversationId);
    state.conversations.delete(item.conversationId);
    if (state.activeConv === item.conversationId) {
      state.activeConv = null;
      renderMessages();
    }
    renderList();
  }});
}

function openConversation(id) {
  state.activeConv = id;
  state.streaming = false;
  state.streamParts = { text: "", reasoning: "" };
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
  const empty = $("empty-state");
  const conv = state.activeConv && state.conversations.get(state.activeConv);
  box.innerHTML = "";
  if (!conv) {
    // 空状态分级：零会话 → 全品牌引导；有会话但未选 → 轻微提示，不刷脸
    const hasAny = state.convList.length > 0;
    $("empty-state-primary").hidden = hasAny;
    $("empty-state-secondary").hidden = !hasAny;
    $("empty-desc").textContent = hasAny ? "" : "选择左侧会话或点击下方建议开始对话";
    $("prompt-grid").style.display = hasAny ? "none" : "grid";
    empty.classList.add("visible");
    const forkBtn = $("btn-fork"); if (forkBtn) forkBtn.hidden = true;
    $("gen-status").classList.remove("visible");
    $("input").placeholder = hasAny ? "选择会话或新建一个开始…" : "选择或新建一个会话…";
    return;
  }
  $("input").placeholder = "输入消息…  Enter 发送，Shift+Enter 换行";
  // 仅在切换会话/首次打开时清空输入框，流式渲染时保留用户正在输入的内容（Q193 不打断编辑）
  const isActive = document.activeElement === $("input");
  if (!isActive) {
    $("input").value = "";
  }
  $("conv-title").textContent = conv.title || "(未命名)";
  $("input").disabled = false;
  $("btn-send").disabled = false;
  $("btn-attach").disabled = false;
  updateSendBtnState();
  if (!isActive) $("input").focus();
  const gs = $("gen-status");
  gs.classList.toggle("visible", conv.runtimeState === "generating");
  gs.querySelector(".gen-status-text").textContent = "生成中";
  const hasMessages = conv.messages.length > 0;
  $("empty-desc").textContent = hasMessages ? "" : "发送第一条消息，开始对话";
  empty.classList.toggle("visible", !hasMessages && !state.streaming);
  // “编辑并 fork”仅在有可 fork 的消息时才出现，避免常驿无效按钮
  const forkBtn2 = $("btn-fork"); if (forkBtn2) forkBtn2.hidden = !hasMessages;
  for (const m of conv.messages) {
    const payload = m && typeof m === "object" && "payload" in m ? m.payload : m;
    const parts = extractParts(payload);
    appendMessage(roleOf(payload), parts.text || displayText(payload), false, payload, parts.reasoning);
  }
  // 流式增量：有文本显示文本，尚无文本显示打字指示
  if (state.streaming && state.activeConv) {
    if (state.streamParts.text || state.streamParts.reasoning) {
      appendMessage("assistant", state.streamParts.text, true, null, state.streamParts.reasoning);
    } else {
      const div = document.createElement("div");
      div.className = "msg assistant streaming";
      const r = document.createElement("div");
      r.className = "role";
      r.textContent = "助手";
      div.appendChild(r);
      const typing = document.createElement("span");
      typing.className = "typing";
      typing.innerHTML = `<i></i><i></i><i></i>`;
      div.appendChild(typing);
      box.appendChild(div);
    }
  }
  if (window.lucide) lucide.createIcons();
  box.scrollTop = box.scrollHeight;
}

// P1-2：滚动到顶部时请求更早历史（页边界 = 稳定 commitID）
$("messages").addEventListener("scroll", () => {
  const box = $("messages");
  const conv = state.activeConv && state.conversations.get(state.activeConv);
  if (box.scrollTop <= 4 && conv && conv.snapshotHasMore && !state.historyLoading) {
    state.historyLoading = true;
    send({ type: "history.request", payload: { conversationId: state.activeConv, beforeCommitId: conv.snapshotEarliestCommitId, limit: 100 } });
  }
});

function attachmentRefs(payload) {
  const refs = [];
  const walk = (o) => {
    if (!o || typeof o !== "object") return;
    if (Array.isArray(o)) { o.forEach(walk); return; }
    if (o.type === "attachment" && o.sha256) {
      refs.push({ sha256: o.sha256, size: o.size || 0, mediaType: o.mediaType || "application/octet-stream", fileName: o.fileName || o.sha256 });
    }
    for (const k of Object.keys(o)) walk(o[k]);
  };
  walk(payload);
  return refs;
}

function formatSize(n) {
  if (!n) return "";
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + " KiB";
  return (n / (1024 * 1024)).toFixed(1) + " MiB";
}

// P1-1：渲染消息中的附件（可下载；缺失时标记，P2-3）
function appendAttachmentRefs(container, payload) {
  for (const r of attachmentRefs(payload)) {
    if (state.missingAttachments.has(r.sha256)) {
      const miss = document.createElement("div");
      miss.className = "attachment missing";
      miss.textContent = `附件缺失：${r.fileName}（原文件已删除）`;
      container.appendChild(miss);
    } else {
      const link = document.createElement("a");
      link.className = "attachment";
      link.textContent = `下载附件：${r.fileName} (${formatSize(r.size)})`;
      link.href = "#";
      link.onclick = (e) => {
        e.preventDefault();
        send({ type: "attachment.download-request", payload: { sha256: r.sha256 } });
      };
      container.appendChild(link);
    }
  }
}

function appendMessage(role, text, streaming, payload, reasoning) {
  const box = $("messages");

  const row = document.createElement("div");
  row.className = "msg-row " + (role === "user" ? "user-row" : role === "tool" ? "tool-row" : "assistant-row");

  // 不再为任何消息渲染头像——右对齐茄紫气泡 vs. 左对齐无边框正文，本身就是
  // “是我/是它”的唯一提示；头像变为纯重复装饰，反代设计原则


  const wrapper = document.createElement("div");
  wrapper.className = "msg-wrapper";

  // 浮动操作按钮组
  const actions = document.createElement("div");
  actions.className = "msg-actions";

  const copyBtn = document.createElement("button");
  copyBtn.className = "msg-action-btn";
  copyBtn.title = "复制内容";
  copyBtn.innerHTML = `<i data-lucide="copy"></i>`;
  copyBtn.onclick = () => {
    navigator.clipboard.writeText(text).then(() => toast("已复制到剪贴板")).catch(() => toast("复制失败"));
  };
  actions.appendChild(copyBtn);

  if (role === "user") {
    const editBtn = document.createElement("button");
    editBtn.className = "msg-action-btn";
    editBtn.title = "编辑并 fork";
    editBtn.innerHTML = `<i data-lucide="edit-3"></i>`;
    editBtn.onclick = () => forkConversation();
    actions.appendChild(editBtn);
  }

  const div = document.createElement("div");
  div.className = "msg " + (role === "user" ? "user" : role === "tool" ? "tool" : "assistant") + (streaming ? " streaming" : "");
  if (role !== "user" && role !== "tool") {
    const r = document.createElement("div");
    r.className = "role";
    r.textContent = role === "assistant" ? "助手" : role;
    div.appendChild(r);
  }
  // 思维链：折叠区块展示（流式期间展开，提交后收起）
  if (reasoning && role !== "user" && role !== "tool") {
    const think = document.createElement("details");
    think.className = "thinking-box";
    if (streaming) think.open = true;
    const summary = document.createElement("summary");
    summary.textContent = "思考过程";
    const body = document.createElement("div");
    body.className = "thinking-content";
    body.innerHTML = renderMarkdown(reasoning);
    think.appendChild(summary);
    think.appendChild(body);
    div.appendChild(think);
  }
  if (text) {
    const content = document.createElement("div");
    content.className = "msg-content";
    if (role === "user") {
      content.textContent = text;
    } else {
      content.innerHTML = renderMarkdown(text);
    }
    div.appendChild(content);
  }
  if (payload) appendAttachmentRefs(div, payload);

  wrapper.appendChild(div);
  wrapper.appendChild(actions);
  row.appendChild(wrapper);
  box.appendChild(row);
  if (window.lucide) lucide.createIcons();
}

// ---------- 操作 ----------
// 模态框（替代 prompt/confirm，保持页面内交互）
function showModal({ title, body, confirmText, onConfirm }) {
  const m = $("modal");
  $("modal-title").textContent = title;
  $("modal-body").innerHTML = "";
  $("modal-body").appendChild(body);
  $("modal-ok").textContent = confirmText;
  m.hidden = false;
  let done = false;
  const close = (ok) => {
    if (done) return;
    done = true;
    m.hidden = true;
    if (ok) onConfirm();
  };
  $("modal-ok").onclick = () => close(true);
  $("modal-cancel").onclick = () => close(false);
  m.onclick = (e) => { if (e.target === m) close(false); };
  m.onkeydown = (e) => { if (e.key === "Escape") close(false); };
  const first = body.querySelector("input, textarea");
  if (first) { first.focus(); if (first.select) first.select(); }
}

// P1-1：附件上传（分块 256 KiB Base64；决策 71-72）
function renderPendingAttachment() {
  const chip = $("attach-chip");
  if (state.pendingAttachment) {
    $("attach-chip-name").textContent = "已选择：" + state.pendingAttachment.fileName;
    chip.hidden = false;
  } else {
    chip.hidden = true;
  }
}

async function sha256Hex(buffer) {
  const digest = await crypto.subtle.digest("SHA-256", buffer);
  return Array.from(new Uint8Array(digest)).map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function uploadAttachment(file) {
  if (!file) return;
  if (file.size > 64 * 1024 * 1024) { toast("附件超过 64 MiB 上限"); return; }
  const buf = await file.arrayBuffer();
  const sha256 = await sha256Hex(buf);
  const attachmentId = uuidv7();
  const mediaType = file.type || "application/octet-stream";
  // 保留声明值，服务器完成时会嗅探并回传（P2-2/Q176）
  state.pendingAttachmentRaw = { mediaType, fileName: file.name };
  // 选择即反馈：不等服务器回信就先展示“上传中”，避免选了文件却无任何反应的死寂感
  $("attach-chip-name").textContent = `上传中：${file.name}`;
  $("attach-chip").hidden = false;
  send({ type: "attachment.begin", payload: { attachmentId, totalBytes: file.size, sha256, mediaType, fileName: file.name } });
  const chunkSize = 256 * 1024;
  let index = 0;
  const total = buf.byteLength;
  // 分片转 base64：避免 String.fromCharCode.apply 参数上限（>256KiB 时栈溢出）
  function bytesToBase64(u8) {
    let binary = "";
    const step = 32 * 1024;
    for (let i = 0; i < u8.length; i += step) {
      binary += String.fromCharCode.apply(null, u8.subarray(i, Math.min(i + step, u8.length)));
    }
    return btoa(binary);
  }
  for (let offset = 0; offset < total; offset += chunkSize) {
    const slice = new Uint8Array(buf, offset, Math.min(chunkSize, total - offset));
    send({ type: "attachment.chunk", payload: { attachmentId, index, data: bytesToBase64(slice) } });
    index++;
    // 上传进度反馈
    const pct = Math.round((offset + slice.length) / total * 100);
    $("attach-chip-name").textContent = `上传中：${file.name} ${pct}%`;
    await new Promise((r) => setTimeout(r, 0)); // 让出主线程，避免大文件阻塞 UI
  }
  $("attach-chip-name").textContent = `上传完成：${file.name}`;
  send({ type: "attachment.complete", payload: { attachmentId, sha256 } });
}

$("file-input").addEventListener("change", (e) => {
  const file = e.target.files && e.target.files[0];
  e.target.value = "";
  if (file) uploadAttachment(file).catch((err) => toast("上传失败: " + err.message));
});

$("btn-attach").onclick = () => $("file-input").click();

$("attach-chip-remove").onclick = () => {
  state.pendingAttachment = null;
  state.pendingAttachmentRaw = null;
  renderPendingAttachment();
  if (typeof updateSendBtnState === "function") updateSendBtnState();
};

async function sendMessage() {
  const text = $("input").value.trim();
  if ((!text && !state.pendingAttachment) || !state.activeConv || !state.authenticated) return;
  $("input").value = "";
  const invocationId = uuidv7();
  const contents = [];
  if (text) contents.push({ text });
  if (state.pendingAttachment) {
    const r = state.pendingAttachment;
    contents.push({ type: "attachment", sha256: r.sha256, size: r.size, mediaType: r.mediaType, fileName: r.fileName });
    state.pendingAttachment = null;
    state.pendingAttachmentRaw = null;
    renderPendingAttachment();
  }
  if (typeof updateSendBtnState === "function") updateSendBtnState();
  const msg = { role: "user", contents };
  send({ type: "chat.user-message.enqueue", payload: { invocationId, conversationId: state.activeConv, message: msg } });
  // 服务器提交后会通过 conversation.message-committed 广播权威消息；避免重复展示。
  const conv = state.conversations.get(state.activeConv);
  if (conv) {
    conv.pendingText = text;
    renderMessages();
  }
}

// 新建会话：零摩擦——默认 “新会话” 名字；随时可重命名，类似于现代聊天应用的体验
function createConversation() {
  const conversationId = uuidv7();
  const title = "新会话";
  send({
    type: "conversation.create",
    // config 由服务端用 TOML 第一个 provider 填充（客户端不硬编码 provider/model）
    payload: { invocationId: uuidv7(), conversationId, title, config: { provider: "", model: "" } },
  });
  setTimeout(() => {
    send({ type: "conversation.observe", payload: { conversationId } });
    openConversation(conversationId);
  }, 200);
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
  const textarea = document.createElement("textarea");
  textarea.className = "modal-input";
  textarea.value = displayText(parentPayload);
  showModal({ title: "编辑并 fork", body: textarea, confirmText: "fork", onConfirm: () => {
    const edited = textarea.value.trim();
    if (!edited) return toast("消息内容不能为空");
    // 决策 75：fork 点 = 父对话中最后一条被继承消息的全局提交 id。
    // 编辑消息 id=X 时继承其之前的历史，因此取可见消息中 < X 的最大 id（编辑首条则为 0）。
    let target = Number(parentMsg && parentMsg.commitId) || 0;
    let forkAfterId = conv.messages.reduce((acc, m) => {
      const cid = Number(m && m.commitId) || 0;
      return (cid > 0 && cid < target && cid > acc) ? cid : acc;
    }, 0);
    const conversationId = uuidv7();
    const message = { role: roleOf(parentPayload), contents: [{ text: edited }] };
    const forkPayload = {
      invocationId: uuidv7(), conversationId, parentConversationId: state.activeConv,
      // fork 继承父会话配置（决策 81 第二问），客户端无需提供有效 config
      config: { provider: "", model: "" }, message
    };
    // 编辑首条消息（无前一条）：不发送 forkAfterId（服务端按 None 处理）；发送 0 会被当作
    // 不存在的提交 id 拒绝（fork 点必须是父会话可见消息）
    if (forkAfterId > 0) forkPayload.forkAfterId = forkAfterId;
    send({ type: "conversation.fork", payload: forkPayload });
    setTimeout(() => {
      send({ type: "conversation.observe", payload: { conversationId } });
      openConversation(conversationId);
    }, 250);
  }});
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
  // D14：未连接时发送会静默丢弃，给出明确提示
  if (!ws || ws.readyState !== WebSocket.OPEN) { toast("请先连接服务器再请求配对"); return; }
  send({ type: "pairing.requested", payload: { clientName: "PWA" } });
};

$("btn-pair-submit").onclick = () => {
  const code = $("pair-code").value.trim();
  if (code.length !== 6) { toast("请输入 6 位配对码"); return; }
  if (!ws || ws.readyState !== WebSocket.OPEN) { toast("连接已断开，请先重连"); return; }
  send({ type: "pairing.attempted", payload: { code, clientName: "PWA" } });
};

$("btn-send").onclick = sendMessage;
const inputEl = $("input");
function updateSendBtnState() {
  const has = inputEl.value.trim().length > 0 || !!state.pendingAttachment;
  const btn = $("btn-send");
  btn.classList.toggle("empty", !has);
  btn.disabled = !inputEl.disabled && !has;
}
inputEl.addEventListener("input", () => {
  inputEl.style.height = "auto";
  inputEl.style.height = Math.min(inputEl.scrollHeight, 160) + "px";
  updateSendBtnState();
});
inputEl.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    sendMessage();
    inputEl.style.height = "auto";
  }
});
// 全局快捷键：⌘/Ctrl+K 搜索、⌘/Ctrl+N 新建、Esc 关闭浮层
document.addEventListener("keydown", (e) => {
  const mod = e.metaKey || e.ctrlKey;
  if (mod && e.key.toLowerCase() === "k") {
    e.preventDefault();
    $("conv-search").focus();
    $("conv-search").select();
  } else if (mod && e.key.toLowerCase() === "n") {
    e.preventDefault();
    if (state.authenticated) createConversation();
  } else if (e.key === "Escape") {
    closeConvMenu();
    const m = $("modal"); if (!m.hidden) m.hidden = true;
  }
});
$("conv-search").addEventListener("input", (e) => {
  renderList(e.target.value.trim());
});
document.querySelectorAll(".prompt-card").forEach((card) => {
  card.onclick = () => {
    const promptText = card.dataset.prompt;
    if (!promptText) return;
    inputEl.value = promptText;
    inputEl.style.height = "auto";
    inputEl.style.height = Math.min(inputEl.scrollHeight, 160) + "px";
    updateSendBtnState();
    if (!state.activeConv) {
      const conversationId = uuidv7();
      const title = promptText.slice(0, 16).replace(/\n/g, " ") + "…";
      send({
        type: "conversation.create",
        // config 由服务端用 TOML 第一个 provider 填充
        payload: { invocationId: uuidv7(), conversationId, title, config: { provider: "", model: "" } },
      });
      setTimeout(() => {
        send({ type: "conversation.observe", payload: { conversationId } });
        openConversation(conversationId);
      }, 200);
    } else {
      inputEl.focus();
    }
  };
});
$("btn-new").onclick = createConversation;
$("btn-cancel").onclick = cancelGeneration;

// P1-5：手动连接重置退避计时
$("btn-connect").addEventListener("click", () => { state.reconnectDelayMs = 1000; });

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

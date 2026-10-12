// 万象 PWA 引导：加载 .NET/WASM 运行时，注册 IndexedDB 凭据桥（决策 52/53、Q191），启动 Avalonia 应用。
import { dotnet } from './_framework/dotnet.js'

const is_browser = typeof window != "undefined";
if (!is_browser) throw new Error(`Expected to be running in a browser`);

// ---- IndexedDB 凭据存储（与旧 JS PWA 共享 schema：DB "wanxiang"、store "connections"、keyPath "instanceId"）----
const WX_DB = "wanxiang";
const WX_STORE = "connections";

function openDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(WX_DB, 1);
        req.onupgradeneeded = () => req.result.createObjectStore(WX_STORE, { keyPath: "instanceId" });
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

// 返回全部连接记录（JSON 字符串，按 updatedAt 降序；URL 不是身份，instanceId 才是主键）
export async function credList() {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(WX_STORE, "readonly");
        const req = tx.objectStore(WX_STORE).getAll();
        req.onsuccess = () => {
            const rows = (req.result || []).sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
            resolve(JSON.stringify(rows));
        };
        req.onerror = () => reject(req.error);
    });
}

export async function credPut(instanceId, url, token, name) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(WX_STORE, "readwrite");
        tx.objectStore(WX_STORE).put({ instanceId, url, token, name, updatedAt: Date.now() });
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
    });
}

export async function credDelete(instanceId) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(WX_STORE, "readwrite");
        tx.objectStore(WX_STORE).delete(instanceId);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
    });
}

// ---- 本地草稿（W5）：独立数据库，不动凭据库的 schema；只存本机，永不上传 ----
const WX_DRAFT_DB = "wanxiang-drafts";
const WX_DRAFT_STORE = "drafts";
const WX_DRAFT_KEY = "snapshot";

function openDraftDb() {
    return new Promise((resolve, reject) => {
        const req = indexedDB.open(WX_DRAFT_DB, 1);
        req.onupgradeneeded = () => req.result.createObjectStore(WX_DRAFT_STORE);
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error);
    });
}

export async function draftGet() {
    const db = await openDraftDb();
    return new Promise((resolve, reject) => {
        const req = db.transaction(WX_DRAFT_STORE, "readonly").objectStore(WX_DRAFT_STORE).get(WX_DRAFT_KEY);
        req.onsuccess = () => resolve(typeof req.result === "string" ? req.result : "");
        req.onerror = () => reject(req.error);
    });
}

export async function draftPut(json) {
    const db = await openDraftDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(WX_DRAFT_STORE, "readwrite");
        tx.objectStore(WX_DRAFT_STORE).put(json, WX_DRAFT_KEY);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
    });
}

export async function draftDelete() {
    const db = await openDraftDb();
    return new Promise((resolve, reject) => {
        const tx = db.transaction(WX_DRAFT_STORE, "readwrite");
        tx.objectStore(WX_DRAFT_STORE).delete(WX_DRAFT_KEY);
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
    });
}

export function pageUrl() {
    return globalThis.location.href;
}

// 响应式布局用的 CSS 像素视口宽。注意不能拿 Avalonia 的 Bounds.Width 当断点依据：
// Browser 后端在 devicePixelRatio > 1 时上报的布局宽度会被 DPR 除一次
// （900 CSS @1.5 → ~600 units → 误进 compact），而 innerWidth 永远是 CSS 像素。
// 第一次成功对话后才允许安装引导出现（第 210 节）。setupShellStatus 注册回调。
const SUCCEEDED_KEY = "wanxiang.firstConversationAt";
let onConversationSucceeded = null;
export function wxConversationSucceeded() {
    try { if (!localStorage.getItem(SUCCEEDED_KEY)) localStorage.setItem(SUCCEEDED_KEY, String(Date.now())); } catch (_) { }
    if (onConversationSucceeded) { const f = onConversationSucceeded; onConversationSucceeded = null; f(); }
}
const hadConversation = () => { try { return !!localStorage.getItem(SUCCEEDED_KEY); } catch (_) { return false; } };

export function wxViewportWidth() {
    return globalThis.innerWidth || 0;
}

// ---- 恢复信号（第 213 节）：网络恢复 / 页面回到前台 → 计数 +1，F# 侧轮询到变化即跳过退避立刻重连 ----
let resumeEpoch = 0;
export function wxResumeEpoch() { return resumeEpoch; }
addEventListener("online", () => { resumeEpoch++; });
document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") resumeEpoch++; });
addEventListener("pageshow", (e) => { if (e.persisted) resumeEpoch++; });

// ---- 可视视口跟随（第 213 节）：软键盘不盖住输入框 ----
// Chrome Android 靠 meta interactive-widget=resizes-content 已缩布局视口；iOS Safari 不认它，
// 键盘只缩 visualViewport。这里把可视视口的高与偏移写成 CSS 变量，#out 据此定位，
// canvas 随之 resize，Avalonia 的输入框自然留在键盘上方。双指缩放（scale≠1）时不跟随，免得画布抖。
function syncVisualViewport() {
    const vv = globalThis.visualViewport;
    const root = document.documentElement.style;
    if (!vv || Math.abs(vv.scale - 1) > 0.01) {
        root.removeProperty("--wx-vvh"); root.removeProperty("--wx-vvtop"); root.removeProperty("--wx-safe-bottom");
        return;
    }
    const keyboard = globalThis.innerHeight - vv.height > 80; // 小于 80px 的差是地址栏伸缩，不算键盘
    root.setProperty("--wx-vvh", `${Math.round(vv.height)}px`);
    root.setProperty("--wx-vvtop", `${Math.round(vv.offsetTop)}px`);
    if (keyboard) root.setProperty("--wx-safe-bottom", "0px"); else root.removeProperty("--wx-safe-bottom");
    document.documentElement.classList.toggle("wx-keyboard", keyboard);
    // iOS 聚焦输入时会把整页滚上去，把它拉回来：我们自己已经按可视视口布局
    if (keyboard && (globalThis.scrollY || document.documentElement.scrollTop)) globalThis.scrollTo(0, 0);
}
if (globalThis.visualViewport) {
    visualViewport.addEventListener("resize", syncVisualViewport);
    visualViewport.addEventListener("scroll", syncVisualViewport);
}
addEventListener("orientationchange", () => setTimeout(syncVisualViewport, 250));
syncVisualViewport();

// ---- 富文本引擎（highlight.js / KaTeX）----
// 脚本正文由 .NET 侧作为清单资源持有并传进来，浏览器这边只负责执行与调用：
// 一份资源同时服务桌面（Jint）与浏览器（本地 JS 引擎），不会两边各存一套。

// 必须用 <script> 注入而不是 new Function：hljs 的打包体以顶层 `var hljs=…` 收尾，
// 放进函数体里那个 var 就成了局部变量，全局上永远拿不到 hljs。
export function richLoad(source) {
    const el = document.createElement("script");
    el.textContent = source;
    document.head.appendChild(el);
}

export function richHighlight(code, language) {
    try {
        return globalThis.hljs.highlight(code, { language, ignoreIllegals: true }).value;
    } catch (_) {
        return "";
    }
}

export function richMath(tex, displayMode) {
    try {
        return globalThis.katex.renderToString(tex, { displayMode, throwOnError: false, strict: false });
    } catch (_) {
        return "";
    }
}

// ---- Service Worker 注册与新版本提示（Q192/Q193：提示刷新，不强制 reload）----
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
        reg.addEventListener("updatefound", () => {
            const newWorker = reg.installing;
            if (!newWorker) return;
            newWorker.addEventListener("statechange", () => {
                if (newWorker.state === "installed" && navigator.serviceWorker.controller) {
                    // 有新版本：提示用户点击刷新（用户自行选择时机，Q193 不强制）
                    const t = document.createElement("div");
                    t.className = "toast";
                    t.textContent = "有新版本，点击刷新";
                    t.onclick = () => newWorker.postMessage({ type: "SKIP_WAITING" });
                    document.body.appendChild(t);
                    setTimeout(() => t.remove(), 15000);
                }
            });
        });
    } catch (_) { /* SW 不可用时静默降级 */ }
}
registerServiceWorker();

// ---- 壳层状态：离线提示 + 安装引导（位于 canvas 之外，不依赖 .NET 运行时已启动）----
// 连接/重连的权威状态仍在 F# 侧（AppShell + ReconnectBackoff）；这里只反映浏览器自身的网络与安装状态，
// 并在网络恢复时提示用户“将自动重连”，不另造第二套重连逻辑。
function setupShellStatus() {
    const offlineBar = document.getElementById("shell-status");
    const text = offlineBar && offlineBar.querySelector(".shell-text");
    let wasOffline = false;
    let hideTimer = 0;
    const showOffline = () => {
        wasOffline = true;
        clearTimeout(hideTimer);
        text.textContent = "网络已断开。已打开的页面可继续使用，网络恢复后会自动重连。";
        offlineBar.hidden = false;
        const install = document.getElementById("shell-install");
        if (install) install.hidden = true; // 同在顶部：离线提示优先
    };
    const showBack = () => {
        if (!wasOffline) return;
        wasOffline = false;
        text.textContent = "网络已恢复，正在重新连接主机…";
        offlineBar.hidden = false;
        hideTimer = setTimeout(() => { offlineBar.hidden = true; }, 3500);
    };
    if (offlineBar && text) {
        if (!navigator.onLine) showOffline();
        addEventListener("offline", showOffline);
        addEventListener("online", showBack);
    }

    // 安装引导：Chromium 走 beforeinstallprompt；iOS Safari 没有该事件，给出“添加到主屏幕”文字说明。
    const bar = document.getElementById("shell-install");
    if (!bar) return;
    const go = document.getElementById("shell-install-go");
    const dismiss = document.getElementById("shell-install-dismiss");
    const label = document.getElementById("shell-install-text");
    const KEY = "wanxiang.installDismissedAt";
    const standalone = matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
    const recentlyDismissed = () => {
        try { return Date.now() - Number(localStorage.getItem(KEY) || 0) < 14 * 24 * 3600 * 1000; }
        catch (_) { return false; }
    };
    if (standalone || recentlyDismissed()) return;
    let deferred = null;
    const close = (remember) => {
        wantShow = false;
        bar.hidden = true;
        if (remember) { try { localStorage.setItem(KEY, String(Date.now())); } catch (_) { } }
    };
    dismiss.onclick = () => close(true);
    // 首次运行不打扰：第一次成功对话之后才露出；离线提示在时让位（两者都在顶部）。
    let wantShow = false;
    const reveal = () => {
        wantShow = true;
        if (!hadConversation()) { onConversationSucceeded = reveal; return; }
        if (offlineBar && !offlineBar.hidden) return;
        bar.hidden = false;
    };
    addEventListener("online", () => { if (wantShow) setTimeout(reveal, 3600); });
    addEventListener("beforeinstallprompt", (e) => {
        e.preventDefault();
        deferred = e;
        go.hidden = false;
        label.textContent = "安装到主屏幕，像应用一样全屏打开。";
        reveal();
    });
    go.onclick = async () => {
        if (!deferred) return;
        deferred.prompt();
        try { await deferred.userChoice; } catch (_) { }
        deferred = null;
        close(false);
    };
    addEventListener("appinstalled", () => close(false));
    const ua = navigator.userAgent || "";
    const isIos = /iPhone|iPad|iPod/.test(ua) || (ua.includes("Mac") && navigator.maxTouchPoints > 1);
    if (isIos) {
        go.hidden = true;
        label.textContent = "点底部「分享」按钮，选择「添加到主屏幕」即可安装。";
        reveal();
    }
}
setupShellStatus();

// ---- 运行时引导 ----
const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

dotnetRuntime.setModuleImports("wanxiang", {
    credList, credPut, credDelete, draftGet, draftPut, draftDelete, pageUrl, wxViewportWidth, wxResumeEpoch, wxConversationSucceeded,
    richLoad, richHighlight, richMath,
});

const config = dotnetRuntime.getConfig();

function dismissSplash() {
    const splash = document.querySelector(".splash");
    if (!splash) return;
    splash.setAttribute("aria-busy", "false");
    splash.style.transition = "opacity 220ms ease, transform 220ms ease";
    splash.style.opacity = "0";
    splash.style.transform = "scale(0.98)";
    setTimeout(() => splash.remove(), 240);
}

// 等 canvas 被 Avalonia ResizeHandler 设到真实像素尺寸后再收启动画面。
// 注意：切勿在此调用 canvas.getContext —— 会抢在 Avalonia 之前占用 WebGL 上下文，导致启动后白屏。
async function dismissSplashWhenPainted() {
    const deadline = performance.now() + 20000;
    // 不能用固定的 640×400 判断：手机画布本来就只有约 390px 宽，
    // 会因此无条件等满 20 秒才收启动画面。阈值跟随当前物理视口，
    // 同时保留桌面端 640×400 的“已完成真实 resize”门槛。
    const dpr = Math.max(1, globalThis.devicePixelRatio || 1);
    const minWidth = Math.min(640, Math.max(240, Math.floor(globalThis.innerWidth * dpr * 0.8)));
    const minHeight = Math.min(400, Math.max(320, Math.floor(globalThis.innerHeight * dpr * 0.8)));
    while (performance.now() < deadline) {
        const c = document.querySelector("#out canvas");
        if (c && c.width >= minWidth && c.height >= minHeight) {
            await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
            dismissSplash();
            return;
        }
        await new Promise((r) => requestAnimationFrame(r));
    }
    dismissSplash();
}

new MutationObserver((_, obs) => {
    if (document.querySelector("#out canvas")) {
        obs.disconnect();
        dismissSplashWhenPainted();
    }
}).observe(document.getElementById("out"), { childList: true });

try {
    await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
} catch (e) {
    console.error("wanxiang boot failed", e);
    const d = document.createElement("pre");
    // 崩溃屏镜像 Palette.dark（壳层无法运行时读 F# 令牌，此处「镜像 + 注释同源」）：
    // 底色取 dark.codeBg #1E1E28（诊断暗面），字形取 dark.danger #E09385（异常通道），
    // 等宽字体经 var(--wx-mono)（同源 Tokens.monoFontFamily：Sarasa Term SC + Sarasa Gothic SC 回落）。
    d.style.cssText =
        "position:fixed;inset:12px;overflow:auto;" +
        "background:#1E1E28;color:#E09385;padding:16px;z-index:99999;" +
        "font-size:12px;line-height:1.4;font-family:var(--wx-mono);white-space:pre-wrap";
    d.textContent = "万象启动失败\n" + (e && (e.stack || e.message || String(e)));
    document.body.appendChild(d);
    throw e;
}

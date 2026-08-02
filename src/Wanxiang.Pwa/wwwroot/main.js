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

export function pageUrl() {
    return globalThis.location.href;
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

// ---- 运行时引导 ----
const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

dotnetRuntime.setModuleImports("wanxiang", { credList, credPut, credDelete, pageUrl });

const config = dotnetRuntime.getConfig();
await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);

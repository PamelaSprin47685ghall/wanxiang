// Service Worker：只缓存静态资源（决策 192），不缓存任何业务数据。
// 可缓存范围：静态壳白名单 + /_framework/（.NET/WASM 运行时产物）。
// 决策 295：/_framework/ 下的运行时产物改为「按启动清单版本的 cache-first」——
//   dotnet.boot.js（启动清单，列出每个产物的 sha256）与 dotnet.js 仍走 network-first；
//   清单内容一变（主机升级），先清空整个 framework 缓存，再放行本次加载的其余请求。
//   于是二次打开不再为 200+ 个产物逐个回源校验（弱网下这是数秒的往返），升级后也不会拿到旧产物。
// 静态壳（index / main.js / css / 图标）仍是 network-first，缓存只作离线回退。
// Q193/P1-3：不自动 skipWaiting；收到 SKIP_WAITING 消息（用户确认刷新）后才激活新版本。
const CACHE = "wanxiang-av9";
const FRAMEWORK_CACHE = "wanxiang-fw";
// 必须每次回源的 framework 文件：启动清单与入口（其余产物按清单版本缓存）
const FRAMEWORK_ALWAYS_FRESH = ["/_framework/dotnet.boot.js", "/_framework/dotnet.js"];
// 静态壳白名单：只缓存这些路径，其余同源 GET 一律 network-only（Q192）
const STATIC_ASSETS = ["/", "/index.html", "/app.css", "/main.js", "/logo.png", "/manifest.webmanifest",
  "/icon-192.png", "/icon-512.png", "/icon-maskable-512.png", "/apple-touch-icon.png"];

self.addEventListener("install", (e) => {
  // 逐项容错：个别资源暂不可达不应使整个 Service Worker 安装失败（弱网下首访）
  e.waitUntil(caches.open(CACHE).then((c) => Promise.all(STATIC_ASSETS.map((u) => c.add(u).catch(() => {})))));
});
self.addEventListener("activate", (e) => {
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE && k !== FRAMEWORK_CACHE).map((k) => caches.delete(k)))));
  self.clients.claim();
});
self.addEventListener("message", (e) => {
  if (e.data && e.data.type === "SKIP_WAITING") self.skipWaiting();
});
self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  // 业务端点（WebSocket）与非同源请求不拦截
  if (url.origin !== location.origin || url.pathname === "/ws") return;
  if (e.request.method !== "GET") return;
  const isNav = e.request.mode === "navigate";
  if (!isNav && !STATIC_ASSETS.includes(url.pathname) && !url.pathname.startsWith("/_framework/")) return;
  // 全部 network-first：/_framework/ 与 main.js 的文件名并不带内容 hash（dotnet 默认产物），
  // cache-first 会在主机更新后永久提供旧版本（Q193 要求新版本可及时生效）。
  // 命中缓存仅作为离线回退（Q192 只允许缓存静态资源）。
  if (url.pathname.startsWith("/_framework/")) {
    e.respondWith(url.pathname === "/_framework/dotnet.boot.js" ? bootManifest(e.request)
      : FRAMEWORK_ALWAYS_FRESH.includes(url.pathname) ? networkFirst(e.request, FRAMEWORK_CACHE)
      : frameworkAsset(e.request));
    return;
  }
  e.respondWith(
    fetch(e.request).then((resp) => {
      if (resp && resp.ok && !isNav) {
        const copy = resp.clone();
        caches.open(CACHE).then((c) => c.put(e.request, copy));
      }
      return resp;
    }).catch(() => caches.match(e.request).then((hit) =>
      // 离线冷启动：带查询串（?server=…）的导航也回落到缓存的壳 index
      hit || (isNav ? caches.match("/index.html", { ignoreSearch: true }) : null) || Response.error()))
  );
});

// ---- 决策 295：framework 产物按启动清单版本缓存 ----
async function networkFirst(request, cacheName) {
  try {
    const resp = await fetch(request);
    if (resp && resp.ok) {
      const copy = resp.clone();
      caches.open(cacheName).then((c) => c.put(request, copy));
    }
    return resp;
  } catch {
    return (await caches.match(request)) || Response.error();
  }
}

// 启动清单：回源；与缓存里的上一版不同则先清空 framework 缓存（等清完再返回，
// 运行时拿到清单之后才会请求其余产物，所以不会混用新旧两版）。
async function bootManifest(request) {
  const cache = await caches.open(FRAMEWORK_CACHE);
  let resp;
  try {
    resp = await fetch(request, { cache: "no-cache" });
  } catch {
    return (await cache.match(request, { ignoreSearch: true })) || Response.error();
  }
  if (!resp || !resp.ok) return resp;
  const text = await resp.clone().text();
  const prev = await cache.match(request, { ignoreSearch: true });
  if (!prev || (await prev.text()) !== text) {
    for (const key of await cache.keys()) await cache.delete(key);
  }
  await cache.put(request, resp.clone());
  return resp;
}

async function frameworkAsset(request) {
  const cache = await caches.open(FRAMEWORK_CACHE);
  const hit = await cache.match(request);
  if (hit) return hit;
  const resp = await fetch(request);
  if (resp && resp.ok) cache.put(request, resp.clone());
  return resp;
}

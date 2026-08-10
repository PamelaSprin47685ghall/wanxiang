// Service Worker：只缓存静态资源（决策 192），不缓存任何业务数据。
// 可缓存范围：静态壳白名单 + /_framework/（.NET/WASM 运行时产物）。
// 全部为 network-first（文件名不带内容 hash，cache-first 会在更新后提供旧版本），缓存仅作离线回退。
// Q193/P1-3：不自动 skipWaiting；收到 SKIP_WAITING 消息（用户确认刷新）后才激活新版本。
const CACHE = "wanxiang-av6";
// 静态壳白名单：只缓存这些路径，其余同源 GET 一律 network-only（Q192）
const STATIC_ASSETS = ["/", "/index.html", "/app.css", "/main.js", "/logo.png", "/manifest.webmanifest"];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(STATIC_ASSETS)));
});
self.addEventListener("activate", (e) => {
  e.waitUntil(caches.keys().then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k)))));
  self.clients.claim();
});
self.addEventListener("message", (e) => {
  if (e.data && e.data.type === "SKIP_WAITING") self.skipWaiting();
});
self.addEventListener("fetch", (e) => {
  const url = new URL(e.request.url);
  // 业务端点（WebSocket）与非同源请求不拦截
  if (url.origin !== location.origin || url.pathname === "/ws") return;
  if (!STATIC_ASSETS.includes(url.pathname) && !url.pathname.startsWith("/_framework/")) return;
  // 全部 network-first：/_framework/ 与 main.js 的文件名并不带内容 hash（dotnet 默认产物），
  // cache-first 会在服务端更新后永久提供旧版本（Q193 要求新版本可及时生效）。
  // 命中缓存仅作为离线回退（Q192 只允许缓存静态资源）。
  e.respondWith(
    fetch(e.request).then((resp) => {
      if (resp && resp.ok) {
        const copy = resp.clone();
        caches.open(CACHE).then((c) => c.put(e.request, copy));
      }
      return resp;
    }).catch(() => caches.match(e.request).then((hit) => hit || Response.error()))
  );
});

// Service Worker：只缓存版本化静态资源（决策 192），不缓存任何业务数据。
// 可缓存范围：静态壳白名单 + /_framework/（.NET/WASM 运行时产物，文件名带版本 hash）。
// Q193/P1-3：不自动 skipWaiting；收到 SKIP_WAITING 消息（用户确认刷新）后才激活新版本。
const CACHE = "wanxiang-av1";
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
  // /_framework/ 下全是版本化产物（dotnet.js、dotnet.wasm、Avalonia 静态资源），cache-first（Q192）
  if (url.pathname.startsWith("/_framework/")) {
    e.respondWith(
      caches.match(e.request).then((hit) => hit || fetch(e.request).then((resp) => {
        if (resp && resp.ok) {
          const copy = resp.clone();
          caches.open(CACHE).then((c) => c.put(e.request, copy));
        }
        return resp;
      }))
    );
    return;
  }
  // 静态壳白名单；根路径用 network-first（保证新版本页面及时生效，Q193）
  if (!STATIC_ASSETS.includes(url.pathname)) return;
  if (e.request.mode === "navigate") {
    e.respondWith(
      fetch(e.request).then((resp) => {
        if (resp && resp.ok) {
          const copy = resp.clone();
          caches.open(CACHE).then((c) => c.put(e.request, copy));
        }
        return resp;
      }).catch(() => caches.match(e.request).then((hit) => hit || Response.error()))
    );
    return;
  }
  e.respondWith(
    caches.match(e.request).then((hit) => hit || fetch(e.request).then((resp) => {
      if (resp && resp.ok) {
        const copy = resp.clone();
        caches.open(CACHE).then((c) => c.put(e.request, copy));
      }
      return resp;
    }))
  );
});

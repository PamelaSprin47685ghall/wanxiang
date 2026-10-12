#!/usr/bin/env node
// 决策 290：本机弱网模拟代理（Service Worker 发起的请求不受 DevTools 限速影响，只能在网络层模拟）。
// 每个请求输出一行 `REQ <方法> <路径> <状态> <字节>`，用于统计「二次加载有多少请求真正到达主机」。
// 注意：大带宽限速下个别 wasm 请求会被浏览器判 ERR_FAILED，限速只适合定性观察；计数用 --rtt 0 --mbps 100000。
// 用法：node tools/throttle_proxy.js --listen 18913 --target http://127.0.0.1:18912 --rtt 60 --mbps 30
const http = require('node:http');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
const LISTEN = Number(arg('listen', 18913));
const TARGET = new URL(arg('target', 'http://127.0.0.1:18912'));
const RTT = Number(arg('rtt', 60));
const BYTES_PER_MS = Number(arg('mbps', 30)) * 125; // 共享带宽
let nextFree = Date.now();
const agent = new http.Agent({ keepAlive: false });
http.createServer((req, res) => {
  let bytes = 0;
  res.on('finish', () => console.log(`REQ ${req.method} ${req.url} ${res.statusCode} ${bytes}`));
  setTimeout(() => {
    const up = http.request({ host: TARGET.hostname, port: TARGET.port, path: req.url, method: req.method, headers: { ...req.headers, host: TARGET.host }, agent }, (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.on('data', (chunk) => {
        bytes += chunk.length;
        r.pause();
        const now = Date.now();
        nextFree = Math.max(nextFree, now) + chunk.length / BYTES_PER_MS;
        setTimeout(() => { res.write(chunk); r.resume(); }, Math.max(0, nextFree - now));
      });
      r.on('end', () => setTimeout(() => res.end(), Math.max(0, nextFree - Date.now())));
    });
    up.on('error', () => { res.statusCode = 502; res.end(); });
    req.pipe(up);
  }, RTT);
}).listen(LISTEN, '127.0.0.1', () => console.log(`throttle proxy :${LISTEN} → ${TARGET.href} rtt=${RTT}ms ${BYTES_PER_MS / 125}Mbps`));

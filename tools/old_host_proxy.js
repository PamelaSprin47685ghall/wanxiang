#!/usr/bin/env node
// 决策 448：模拟「旧主机」的 QA 代理。转发 HTTP 与 /ws，但
//  - 丢掉主机发出的 wanxiang.dev/host.capabilities（旧主机不声明能力）；
//  - 丢掉客户端发出的、旧主机不认识的请求（默认 config.preset-upsert/delete、chat.continue、conversation.import），
//    与旧主机「未知类型静默忽略」(55.8 R1) 的行为一致。
// 用法：node tools/old_host_proxy.js --listen 8782 --target 127.0.0.1:8781 [--drop a,b,c]
const http = require('node:http');
const { WebSocketServer, WebSocket } = require('ws');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const LISTEN = Number(arg('listen', 8782));
const TARGET = arg('target', '127.0.0.1:8781');
const DROP = new Set(arg('drop', 'config.preset-upsert,config.preset-delete,chat.continue,conversation.import').split(','));
const [th, tp] = TARGET.split(':');
const nameOf = (raw) => {
    try {
        const ev = JSON.parse(String(raw));
        if (ev.type === 'CUSTOM' && String(ev.name).startsWith('wanxiang.dev/')) return ev.name.slice(13);
        return ev.type || '';
    } catch { return ''; }
};
const server = http.createServer((req, res) => {
    const up = http.request({ host: th, port: tp, path: req.url, method: req.method, headers: { ...req.headers, host: TARGET } }, (r) => {
        res.writeHead(r.statusCode, r.headers);
        r.pipe(res);
    });
    up.on('error', () => { res.writeHead(502); res.end(); });
    req.pipe(up);
});
const wss = new WebSocketServer({ noServer: true });
server.on('upgrade', (req, socket, head) => {
    wss.handleUpgrade(req, socket, head, (client) => {
        const upstream = new WebSocket(`ws://${TARGET}${req.url}`, { headers: { origin: `http://${TARGET}` } });
        const pending = [];
        upstream.on('open', () => { for (const m of pending.splice(0)) upstream.send(m); });
        client.on('message', (m, isBinary) => {
            const n = nameOf(m);
            if (DROP.has(n)) { console.log('dropped client →', n); return; }
            const data = isBinary ? m : String(m);
            if (upstream.readyState === WebSocket.OPEN) upstream.send(data); else pending.push(data);
        });
        upstream.on('message', (m, isBinary) => {
            if (nameOf(m) === 'host.capabilities') { console.log('dropped host → host.capabilities'); return; }
            client.send(isBinary ? m : String(m));
        });
        const closeBoth = () => { try { client.close(); } catch {} try { upstream.close(); } catch {} };
        client.on('close', closeBoth); upstream.on('close', closeBoth);
        client.on('error', closeBoth); upstream.on('error', closeBoth);
    });
});
server.listen(LISTEN, '127.0.0.1', () => console.log(`old-host proxy :${LISTEN} → ${TARGET}`));

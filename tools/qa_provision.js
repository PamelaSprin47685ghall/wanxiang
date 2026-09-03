#!/usr/bin/env node
// QA 前置：在干净数据目录的服务端上写入 mock 服务商，否则 qa_shot 的
// LONG/TOOL/FAIL 发出去也没有模型接单，7 态截图全是同一屏（verify-round1 教训）。
// 用法：node tools/qa_provision.js [--url ws://127.0.0.1:8765/ws] [--log /tmp/wanxiang.log]
//   要求 mock_openai.py 已在 8799 运行。幂等：mock 已存在则直接 PASS。

import { readFileSync, existsSync } from 'node:fs';
import { randomUUID } from 'node:crypto';

const argv = process.argv.slice(2);
const arg = (k, d) => {
    const i = argv.indexOf(`--${k}`);
    return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};

const URL = arg('url', 'ws://127.0.0.1:8765/ws');
const LOG = arg('log', '/tmp/wanxiang.log');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function pairingCode() {
    if (!existsSync(LOG)) return null;
    const lines = readFileSync(LOG, 'utf8').split('\n').filter((l) => l.includes('pairing-code'));
    if (!lines.length) return null;
    const m = lines[lines.length - 1].match(/"code":"(\d{6})"/);
    return m ? m[1] : null;
}

class Client {
    constructor(url) {
        this.ws = new WebSocket(url);
        this.events = [];
        this.waiters = [];
        this.cursor = 0n;
        this.ws.onmessage = (e) => {
            const ev = JSON.parse(e.data);
            this.events.push(ev);
            const p = ev.payload || {};
            for (const c of [p.lastCommitId, p.commitId, p.toCommitId]) {
                if (typeof c === 'number' && BigInt(c) > this.cursor) {
                    this.cursor = BigInt(c);
                    this.send('cursor.advanced', { id: Number(this.cursor) });
                }
            }
            this.waiters = this.waiters.filter((w) => {
                if (w.match(ev)) {
                    w.resolve(ev);
                    return false;
                }
                return true;
            });
        };
    }
    open() {
        return new Promise((resolve, reject) => {
            this.ws.onopen = resolve;
            this.ws.onerror = () => reject(new Error('websocket error'));
        });
    }
    send(type, payload) {
        this.ws.send(JSON.stringify(payload ? { type, payload } : { type }));
    }
    waitFor(match, timeoutMs = 20000, label = 'event') {
        const existing = this.events.find(match);
        if (existing) return Promise.resolve(existing);
        return new Promise((resolve, reject) => {
            const w = { match, resolve };
            this.waiters.push(w);
            setTimeout(() => {
                this.waiters = this.waiters.filter((x) => x !== w);
                reject(new Error(`timeout waiting for ${label}`));
            }, timeoutMs);
        });
    }
    close() {
        try { this.ws.close(); } catch { /* already closed */ }
    }
}

async function main() {
    const c = new Client(URL);
    await c.open();
    await c.waitFor((e) => e.type === 'protocol.hello', 10000, 'protocol.hello');
    c.send('protocol.hello', { protocol: 'wanxiang', version: 1 });
    c.send('pairing.requested', { clientName: 'qa-provision' });
    await c.waitFor((e) => e.type === 'pairing.started', 10000, 'pairing.started');
    let code = null;
    for (let i = 0; i < 40 && !code; i++) {
        code = pairingCode();
        if (!code) await sleep(150);
    }
    if (!code) throw new Error('no pairing code found in server log');
    c.send('pairing.attempted', { code, clientName: 'qa-provision' });
    const paired = await c.waitFor((e) => e.type === 'pairing.succeeded', 10000, 'pairing.succeeded');
    c.send('auth.present', { token: paired.payload.token });
    await c.waitFor((e) => e.type === 'auth.accepted', 10000, 'auth.accepted');

    c.send('catalog.request');
    const catalog = await c.waitFor((e) => e.type === 'catalog.snapshot', 10000, 'catalog.snapshot');
    const existing = (catalog.payload.providers || []).find((p) => p.id === 'mock');
    if (existing && (existing.models || []).length === 3) {
        console.log('provision: mock provider already present — PASS');
        c.close();
        return;
    }

    const requestId = randomUUID();
    c.send('config.provider-upsert', {
        requestId,
        provider: {
            id: 'mock',
            label: 'mock',
            baseUrl: 'http://127.0.0.1:8799/v1',
            models: ['mock-gpt', 'mock-gpt-mini', 'mock-reasoner'],
            defaultModel: 'mock-gpt-mini',
            apiKey: 'sk-qa',
        },
    });
    const applied = await c.waitFor(
        (e) => e.type === 'config.applied' && e.payload.requestId === requestId,
        20000,
        'config.applied',
    );
    if (!applied.payload.ok) throw new Error('upsert rejected: ' + (applied.payload.errors || []).join('; '));

    const probeId = randomUUID();
    c.send('provider.probe', { requestId: probeId, providerId: 'mock' });
    const probe = await c.waitFor(
        (e) => e.type === 'provider.probe-result' && e.payload.requestId === probeId,
        20000,
        'probe',
    );
    if (!probe.payload.ok) throw new Error('probe failed: ' + (probe.payload.error || ''));
    console.log(`provision: mock upserted + probed (${probe.payload.models.length} models) — PASS`);
    c.close();
}

main().catch((e) => {
    console.error('provision FAILED:', e.message);
    process.exit(1);
});

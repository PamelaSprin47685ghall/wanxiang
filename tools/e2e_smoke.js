#!/usr/bin/env node
// 万象端到端冒烟：配对 → 目录 → 建会话 → 发消息 → 流式 → 工具 → 自动标题。
// 仅开发期使用。需要服务端在 127.0.0.1:8765 运行，且 mock provider 在 8799。
//   node tools/e2e_smoke.js [--url ws://127.0.0.1:8765/ws] [--log /tmp/wanxiang.log]

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
const uuid = () => randomUUID();

let failures = 0;
function check(name, ok, detail = '') {
    const mark = ok ? 'PASS' : 'FAIL';
    if (!ok) failures++;
    console.log(`  [${mark}] ${name}${detail ? ' — ' + detail : ''}`);
}

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
        this.rejections = [];
        this.ws.onmessage = (e) => {
            const ev = JSON.parse(e.data);
            this.events.push(ev);
            if (ev.type === 'command.rejected') this.rejections.push(ev.payload);
            this.trackCursor(ev);
            this.waiters = this.waiters.filter((w) => {
                if (w.match(ev)) {
                    w.resolve(ev);
                    return false;
                }
                return true;
            });
        };
    }

    // 写命令要求客户端投影追平，因此必须回发 cursor.advanced，否则一律被判 stale 拒绝。
    trackCursor(ev) {
        const p = ev.payload || {};
        const candidates = [p.lastCommitId, p.commitId, p.toCommitId];
        let top = this.cursor;
        for (const c of candidates) {
            if (typeof c === 'number' && BigInt(c) > top) top = BigInt(c);
        }
        if (top > this.cursor) {
            this.cursor = top;
            this.send('cursor.advanced', { id: Number(top) });
        }
    }

    open() {
        return new Promise((resolve, reject) => {
            this.ws.onopen = resolve;
            this.ws.onerror = reject;
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

    ofType(type) {
        return this.events.filter((e) => e.type === type);
    }

    close() {
        try { this.ws.close(); } catch { /* already closed */ }
    }
}

const byType = (type) => (e) => e.type === type;

async function main() {
    const c = new Client(URL);
    await c.open();
    await c.waitFor(byType('protocol.hello'), 10000, 'protocol.hello');
    c.send('protocol.hello', { protocol: 'wanxiang', version: 1 });
    c.send('pairing.requested', { clientName: 'e2e-smoke' });
    await c.waitFor(byType('pairing.started'), 10000, 'pairing.started');

    let code = null;
    for (let i = 0; i < 40 && !code; i++) {
        code = pairingCode();
        if (!code) await sleep(150);
    }
    if (!code) throw new Error('no pairing code found in server log');
    c.send('pairing.attempted', { code, clientName: 'e2e-smoke' });
    const paired = await c.waitFor(byType('pairing.succeeded'), 10000, 'pairing.succeeded');
    c.send('auth.present', { token: paired.payload.token });
    await c.waitFor(byType('auth.accepted'), 10000, 'auth.accepted');
    check('pair and authenticate', true);

    // 目录
    c.send('catalog.request');
    const catalog = await c.waitFor(byType('catalog.snapshot'), 10000, 'catalog.snapshot');
    const providers = catalog.payload.providers || [];
    check('catalog lists providers', providers.length > 0, `${providers.length} provider(s)`);
    const mock = providers.find((p) => p.id === 'mock');
    check('provider carries a model list', !!mock && mock.models.length === 3, mock ? mock.models.join(',') : 'missing');
    check('api key never leaves the server', !!mock && mock.apiKey === undefined && mock.hasApiKey === true);
    check('catalog lists builtin tools', (catalog.payload.tools || []).length >= 2, `${(catalog.payload.tools || []).length} tool(s)`);
    check(
        'file tools stay unregistered without a sandbox',
        !(catalog.payload.tools || []).some((t) => t.id.startsWith('builtin:file')),
    );

    // 探活
    const probeId = uuid();
    c.send('provider.probe', { requestId: probeId, providerId: 'mock' });
    const probe = await c.waitFor((e) => e.type === 'provider.probe-result' && e.payload.requestId === probeId, 20000, 'probe');
    check('provider probe lists models from /models', probe.payload.ok && probe.payload.models.length === 3, probe.payload.error || '');

    c.send('conversation-list.observe');
    await c.waitFor(byType('conversation-list.snapshot'), 10000, 'list snapshot');

    // 建会话（显式选一个非默认模型，验证会话模型真的生效）
    const convId = uuid();
    c.send('conversation.create', {
        invocationId: uuid(),
        conversationId: convId,
        title: '新会话',
        config: {
            provider: 'mock',
            model: 'mock-gpt-mini',
            tools: ['builtin:echo'],
            temperature: 0.4,
            maxTokens: 512,
        },
    });
    await c.waitFor((e) => e.type === 'command.committed', 10000, 'create committed');
    c.send('conversation.observe', { conversationId: convId });
    const snap = await c.waitFor(byType('conversation.snapshot'), 10000, 'conversation snapshot');
    check('session keeps the requested model', snap.payload.config.model === 'mock-gpt-mini', snap.payload.config.model);

    // 发消息 → 流式 → 完成
    c.send('chat.user-message.enqueue', {
        invocationId: uuid(),
        conversationId: convId,
        message: { role: 'user', contents: [{ text: 'REASON 帮我解释一下这个项目的存储模型' }] },
    });
    const started = await c.waitFor(byType('generation.started'), 20000, 'generation.started');
    check('generation reports the effective model', started.payload.model === 'mock-gpt-mini', started.payload.model);
    const finished = await c.waitFor(byType('generation.finished'), 40000, 'generation.finished');
    check('generation completes', finished.payload.status === 'completed', JSON.stringify(finished.payload.error || {}));
    check('token usage is reported', !!finished.payload.usage && finished.payload.usage.totalTokens > 0, JSON.stringify(finished.payload.usage));
    const deltas = c.ofType('generation.delta');
    check('streaming deltas arrive', deltas.length > 3, `${deltas.length} delta(s)`);
    const sawReasoning = deltas.some((d) =>
        JSON.stringify(d.payload.payload || {}).includes('reasoning'),
    );
    check('reasoning stream is forwarded', sawReasoning);

    // 自动标题
    let titled = false;
    for (let i = 0; i < 60 && !titled; i++) {
        c.send('conversation-list.observe');
        const snaps = c.ofType('conversation-list.snapshot');
        const last = snaps[snaps.length - 1];
        const item = (last?.payload.items || []).find((x) => x.conversationId === convId);
        if (item && item.title && item.title !== '新会话') {
            titled = item.title;
            break;
        }
        await sleep(400);
    }
    check('title is generated automatically', !!titled, titled || 'still 新会话');

    // 工具调用
    c.send('chat.user-message.enqueue', {
        invocationId: uuid(),
        conversationId: convId,
        message: { role: 'user', contents: [{ text: 'TOOL 请调用 echo 工具' }] },
    });
    await c.waitFor((e) => e.type === 'generation.started' && c.ofType('generation.started').length >= 2, 20000, 'second generation');
    await c.waitFor(
        (e) => e.type === 'generation.finished' && c.ofType('generation.finished').length >= 2,
        60000,
        'second generation finished',
    );
    const committed = c.ofType('conversation.message-committed');
    const toolResults = committed.filter((m) => JSON.stringify(m.payload.payload).includes('functionResult'));
    check('tool result is persisted', toolResults.length > 0, `${toolResults.length} tool message(s)`);

    c.send('chat.user-message.enqueue', {
        invocationId: uuid(),
        conversationId: convId,
        message: { role: 'user', contents: [{ text: 'FAIL 触发上游错误' }] },
    });
    const failEvent = await c.waitFor(
        (e) => e.type === 'generation.finished' && e.payload.status === 'failed',
        60000,
        'failed generation',
    );
    const err = failEvent.payload.error || {};
    check('failure carries a structured code', !!err.code, err.code || 'none');
    check('failure message is human readable', typeof err.message === 'string' && err.message.length > 0, err.message || '');
    check('failure marks retryability', typeof err.retryable === 'boolean', String(err.retryable));

    const noopId = uuid();
    c.send('chat.regenerate', { invocationId: noopId, conversationId: convId });
    const noop = await c.waitFor(
        (e) => e.type === 'command.rejected' && e.payload.invocationId === noopId,
        20000,
        'regenerate rejection',
    );
    check('regenerate without a model reply is rejected', !!noop.payload.message, noop.payload.message);

    c.send('chat.user-message.enqueue', {
        invocationId: uuid(),
        conversationId: convId,
        message: { role: 'user', contents: [{ text: '再说一句话' }] },
    });
    const okCount = c.ofType('generation.finished').filter((e) => e.payload.status === 'completed').length;
    await c.waitFor(
        () => c.ofType('generation.finished').filter((e) => e.payload.status === 'completed').length > okCount,
        60000,
        'third generation finished',
    );
    const beforeStarts = c.ofType('generation.started').length;
    c.send('chat.regenerate', { invocationId: uuid(), conversationId: convId });
    await c.waitFor(
        () => c.ofType('generation.started').length > beforeStarts,
        30000,
        'regeneration started',
    );
    const regenDone = await c.waitFor(
        () => c.ofType('generation.finished').filter((e) => e.payload.status === 'completed').length > okCount + 1,
        60000,
        'regeneration finished',
    );
    check('regenerate reruns the last turn', !!regenDone);

    // 置顶
    c.send('conversation.flags-set', { invocationId: uuid(), conversationId: convId, pinned: true, archived: false });
    await sleep(600);
    c.send('conversation-list.observe');
    await sleep(600);
    const lastList = c.ofType('conversation-list.snapshot').pop();
    const pinnedItem = (lastList?.payload.items || []).find((x) => x.conversationId === convId);
    check('pin flag is projected', pinnedItem && pinnedItem.pinned === true, JSON.stringify(pinnedItem || {}));

    // 配置写入
    const reqId = uuid();
    c.send('config.provider-upsert', {
        requestId: reqId,
        provider: {
            id: 'probe-target',
            label: '写入测试',
            baseUrl: 'http://127.0.0.1:8799/v1',
            models: ['mock-gpt'],
            defaultModel: 'mock-gpt',
            apiKey: 'sk-written',
        },
    });
    const applied = await c.waitFor((e) => e.type === 'config.applied' && e.payload.requestId === reqId, 20000, 'config.applied');
    check('provider can be added over the protocol', applied.payload.ok, (applied.payload.errors || []).join('; '));

    const badId = uuid();
    c.send('config.provider-upsert', {
        requestId: badId,
        provider: { id: 'broken', baseUrl: 'not-a-url', models: [] },
    });
    const rejected = await c.waitFor((e) => e.type === 'config.applied' && e.payload.requestId === badId, 20000, 'config rejected');
    check('invalid provider is rejected with reasons', !rejected.payload.ok && rejected.payload.errors.length >= 2, (rejected.payload.errors || []).join('; '));

    const delId = uuid();
    c.send('config.provider-delete', { requestId: delId, providerId: 'probe-target' });
    const deleted = await c.waitFor((e) => e.type === 'config.applied' && e.payload.requestId === delId, 20000, 'config delete');
    check('provider can be removed over the protocol', deleted.payload.ok, (deleted.payload.errors || []).join('; '));

    c.close();
    console.log(failures === 0 ? '\nsmoke: ALL PASS' : `\nsmoke: ${failures} FAILURE(S)`);
    process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => {
    console.error('smoke aborted:', e.message);
    process.exit(1);
});

#!/usr/bin/env node
// 第 27 轮 PWA 端到端：对话预设（⋯ 菜单另存、侧栏 ▾ 与命令面板套用、模型/工具缺失回落提示），浅色+深色。
// 用法：node --experimental-websocket tools/qa_r27.js --base http://127.0.0.1:<port> --log <host.log> --theme light|dark [--probe true]
// 依赖：puppeteer-core、google-chrome；主机已用 qa_provision.js 配好 mock 服务。

const puppeteer = require('puppeteer-core');
const { createHash, randomUUID } = require('node:crypto');
const { existsSync, mkdirSync, readFileSync } = require('node:fs');
const { join } = require('node:path');
const { forceTheme } = require('./qa_theme');

const argv = process.argv.slice(2);
const arg = (k, d) => {
    const i = argv.indexOf(`--${k}`);
    return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};

const OUT = arg('out', 'shots-r27');
const BASE = arg('base', 'http://127.0.0.1:8765');
const LOG = arg('log', '/tmp/wanxiang.log');
const WIDTH = Number(arg('width', 1440));
const HEIGHT = Number(arg('height', 900));
const SCALE = Number(arg('scale', 1));
const TAG = arg('tag', 'r27');
const THEME = arg('theme', 'light');
const RESIZE_SWEEP = arg('resize-sweep', 'false') === 'true';

mkdirSync(OUT, { recursive: true });

const CHROME = ['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium'].find(existsSync);
if (!CHROME) {
    console.error('no chrome found');
    process.exit(1);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function latestPairingCode() {
    if (!existsSync(LOG)) return null;
    const lines = readFileSync(LOG, 'utf8').split('\n').filter((l) => l.includes('pairing-code'));
    if (!lines.length) return null;
    const m = lines[lines.length - 1].match(/"code":"(\d{6})"/);
    return m ? m[1] : null;
}

const shots = [];

// ---- WS 协议客户端（与 e2e_smoke 同语义：cursor 追平，否则写命令判 stale）----
// AG-UI 承载（与 qa_r8 同）：CUSTOM wanxiang.dev/<type> → {type,payload}；标准面生成事件归一
function normAgui(ev) {
    if (ev.type === 'CUSTOM' && String(ev.name).startsWith('wanxiang.dev/')) {
        return { type: ev.name.slice(13), payload: (ev.value && ev.value.type && ev.value.payload) ? ev.value.payload : ev.value };
    }
    const ids = { conversationId: ev.threadId, generationId: ev.runId };
    if (ev.type === 'RUN_STARTED') return { type: 'generation.started', payload: { ...ev, ...ids } };
    if (ev.type === 'RUN_FINISHED') {
        const cancelled = ev.outcome && JSON.stringify(ev.outcome).includes('ancel');
        return { type: 'generation.finished', payload: { ...ev, ...ids, status: cancelled ? 'cancelled' : 'completed' } };
    }
    return ev;
}
const aguiFrame = (type, payload) => JSON.stringify({ type: 'CUSTOM', name: 'wanxiang.dev/' + type, value: payload || {} });
function makeClient(url) {
    const ws = new WebSocket(url);
    const events = [];
    const waiters = [];
    let cursor = 0n;
    ws.onmessage = (e) => {
        const ev = normAgui(JSON.parse(String(e.data)));
        if (/rejected|failed/.test(ev.type || '')) console.log('ws:', ev.type, JSON.stringify(ev.payload || {}).slice(0, 300));
        events.push(ev);
        const p = ev.payload || {};
        let top = cursor;
        for (const c of [p.lastCommitId, p.commitId, p.toCommitId, ev.type === 'conversation.message-committed' ? p.messageId : undefined]) {
            if (typeof c === 'number' && BigInt(c) > top) top = BigInt(c);
        }
        if (top > cursor) {
            cursor = top;
            ws.send(aguiFrame('cursor.advanced', { id: Number(top) }));
        }
        for (let i = waiters.length - 1; i >= 0; i--) {
            if (waiters[i].match(ev)) {
                waiters[i].resolve(ev);
                waiters.splice(i, 1);
            }
        }
    };
    return {
        ws,
        events,
        open: () => new Promise((resolve, reject) => {
            ws.onopen = resolve;
            ws.onerror = () => reject(new Error('websocket error'));
        }),
        send: (type, payload) => ws.send(aguiFrame(type, payload)),
        waitFor: (match, timeoutMs, label) => {
            const existing = events.find(match);
            if (existing) return Promise.resolve(existing);
            return new Promise((resolve, reject) => {
                const w = { match, resolve };
                waiters.push(w);
                setTimeout(() => {
                    const ix = waiters.indexOf(w);
                    if (ix >= 0) waiters.splice(ix, 1);
                    reject(new Error(`timeout waiting for ${label}`));
                }, timeoutMs);
            });
        },
        close: () => { try { ws.close(); } catch { /* already closed */ } },
    };
}


const PROBE = arg('probe', 'false') === 'true';
const OLD_HOST = arg('old-host', 'false') === 'true';
const errors = [];

async function main() {
    const browser = await puppeteer.launch({
        executablePath: CHROME,
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox', '--use-gl=angle', '--use-angle=swiftshader',
            '--enable-unsafe-swiftshader', '--disable-dev-shm-usage', `--window-size=${WIDTH},${HEIGHT}`],
        defaultViewport: { width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE },
    });
    try {
        const page = await browser.newPage();
        await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: THEME }]);
        page.on('pageerror', (e) => errors.push(String(e).slice(0, 400)));
        await page.exposeFunction('wxPairingCode', async () => {
            for (let i = 0; i < 60; i++) { const c = latestPairingCode(); if (c) return c; await sleep(150); }
            return null;
        });
        const wsUrl = BASE.replace('http', 'ws') + '/ws';
        const ctl = makeClient(wsUrl);
        await ctl.open();
        ctl.send('pairing.requested', { clientName: 'qa-r27' });
        await ctl.waitFor((e) => e.type === 'pairing.started', 15000, 'pairing.started');
        const code = await page.evaluate(() => window.wxPairingCode());
        ctl.send('pairing.attempted', { code, clientName: 'qa-r27' });
        const paired = await ctl.waitFor((e) => e.type === 'pairing.succeeded', 15000, 'pairing.succeeded');
        ctl.send('auth.present', { token: paired.payload.token });
        const accepted = await ctl.waitFor((e) => e.type === 'auth.accepted', 15000, 'auth.accepted');
        if (!OLD_HOST) {
            const caps = await ctl.waitFor((e) => e.type === 'host.capabilities', 5000, 'host.capabilities');
            if (!caps.payload.features.includes('presets')) throw new Error('host does not declare presets');
            console.log('host.capabilities', JSON.stringify(caps.payload));
        }
        const instanceId = accepted.payload.instanceId;
        ctl.send('tool.approval-capability', { supported: true });
        ctl.send('conversation-list.observe');
        await ctl.waitFor((e) => e.type === 'conversation-list.snapshot', 15000, 'list snapshot');

        // 一段真实对话：之后从 ⋯ 菜单把它的配置存为预设。
        const convId = randomUUID();
        ctl.send('conversation.create', {
            invocationId: randomUUID(), threadId: convId, title: `周报润色 ${THEME}`,
            config: { provider: 'mock', model: 'mock-gpt', tools: ['builtin:echo'], instructions: '你是严谨的中文编辑，只改表达不改原意。' },
        });
        await ctl.waitFor((e) => e.type === 'command.committed', 15000, 'create committed');
        ctl.send('conversation.observe', { threadId: convId });
        await ctl.waitFor((e) => e.type === 'conversation.snapshot', 15000, 'conversation snapshot');
        ctl.send('chat.user-message.enqueue', {
            invocationId: randomUUID(), threadId: convId,
            message: { role: 'user', contents: [{ text: '帮我把这句话改得更简洁：本周我们主要完成了若干项工作。' }] },
        });
        await ctl.waitFor((e) => e.type === 'generation.finished', 60000, 'reply finished');

        // 回落用例：预设指向一个随后被下架的模型 + 一个不存在的工具（决策 436）。
        const upsert = async (preset) => {
            const requestId = randomUUID();
            ctl.send('config.preset-upsert', { requestId, preset });
            const r = await ctl.waitFor((e) => e.type === 'config.applied' && e.payload.requestId === requestId, 10000, 'preset applied');
            return r.payload;
        };
        const fb = OLD_HOST ? { ok: true } : await upsert({ name: `旧模型预设${THEME === 'dark' ? '（深色）' : ''}`, provider: 'mock', model: 'mock-reasoner', tools: ['builtin:echo', 'builtin:gone'] });
        console.log('fallback preset upsert', JSON.stringify(fb));
        if (!fb.ok) {
            const fb2 = await upsert({ name: `旧模型预设${THEME === 'dark' ? '（深色）' : ''}`, provider: 'mock', model: 'mock-reasoner', tools: ['builtin:echo'] });
            console.log('fallback preset (no bad tool)', JSON.stringify(fb2));
        }

        // 浏览器：写入凭据，自动连上同一主机。
        await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
        await sleep(3000);
        await page.evaluate(async (creds) => {
            const db = await new Promise((resolve, reject) => {
                const req = indexedDB.open('wanxiang', 1);
                req.onupgradeneeded = () => req.result.createObjectStore('connections', { keyPath: 'instanceId' });
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error);
            });
            await new Promise((resolve, reject) => {
                const tx = db.transaction('connections', 'readwrite');
                tx.objectStore('connections').put({ ...creds, name: 'qa', updatedAt: Date.now() });
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        }, { instanceId, url: wsUrl, token: paired.payload.token });
        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => { const c = document.querySelector('#out canvas'); return c && c.width >= 600; }, { timeout: 120000, polling: 250 });
        await sleep(5000);
        const geo = await page.evaluate(() => { const r = document.querySelector('#out canvas').getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; });
        await page.mouse.click(geo.x + geo.w - 30, geo.y + geo.h * 0.5);
        await forceTheme(page, geo, THEME);
        await sleep(4000);

        let n = 0;
        const shot = async (name) => {
            const path = join(OUT, `${TAG}-${THEME}-${String(++n).padStart(2, '0')}-${name}.png`);
            await page.screenshot({ path });
            console.log('shot:', path);
            return path;
        };
        const key = async (combo) => {
            const parts = combo.split('+');
            for (const p of parts.slice(0, -1)) await page.keyboard.down(p);
            await page.keyboard.press(parts[parts.length - 1]);
            for (const p of parts.slice(0, -1).reverse()) await page.keyboard.up(p);
        };
        const catalogPresets = async () => {
            ctl.send('catalog.request');
            const before = ctl.events.length;
            const snap = await ctl.waitFor((e) => e.type === 'catalog.snapshot' && ctl.events.indexOf(e) >= before, 10000, 'catalog');
            return ((snap.payload.generation || {}).presets || snap.payload.presets || []).map((p) => p.name);
        };

        // 打开对话（Ctrl+1 = 列表第 1 个）
        const [rx, ry] = arg('row', '90,158').split(',').map(Number);
        await page.mouse.move(geo.x + rx, geo.y + ry);
        await sleep(300);
        await page.mouse.click(geo.x + rx, geo.y + ry, { delay: 80 });
        await sleep(2500);
        if (OLD_HOST) {
            // 旧主机：⋯ → 存为预设 → 保存；请求被静默丢弃，12 秒后对话框应退出等待并提示主机版本较旧。
            await page.mouse.click(geo.x + 1377, geo.y + 14);
            await sleep(1000);
            const [sx, sy] = arg('save-item', '1245,194').split(',').map(Number);
            await page.mouse.click(geo.x + sx, geo.y + sy);
            await sleep(1200);
            await key('Control+KeyA'); await page.keyboard.type('旧主机预设', { delay: 40 });
            await page.keyboard.press('Enter');
            await sleep(2500);
            await shot('old-host-waiting');
            await sleep(11500);
            await shot('old-host-timeout');
            await sleep(15000);
            await shot('old-host-timeout-late');
            return;
        }
        if (PROBE) {
            await shot('probe');
            await key('Control+KeyK'); await sleep(1500); await shot('probe-k');
            await page.keyboard.press('Escape'); await sleep(500);
            await page.mouse.click(geo.x + 88, geo.y + 54); await sleep(1500); await shot('probe-new');
            const info = await page.evaluate(() => ({ dpr: devicePixelRatio, w: innerWidth, h: innerHeight, el: document.elementFromPoint(60,152)?.tagName, active: document.activeElement?.tagName }));
            console.log(JSON.stringify(info, null, 0), JSON.stringify(geo));
            return;
        }

        // 1) ⋯ 菜单 → 存为预设
        const [mx, my] = arg('more', `${geo.w - 28},26`).split(',').map(Number);
        await page.mouse.click(geo.x + mx, geo.y + my);
        await sleep(1000);
        await shot('more-menu');
        const before = await catalogPresets();
        // 菜单项「存为预设…」：用键盘在菜单里找（↓ 逐项 + 看 catalog 是否多一项太慢），这里点坐标参数或回落到命令面板。
        const saveAt = arg('save-item', '1245,194');
        if (saveAt) {
            const [sx, sy] = saveAt.split(',').map(Number);
            await page.mouse.click(geo.x + sx, geo.y + sy);
        } else {
            await page.keyboard.press('Escape');
            await sleep(400);
            await key('Control+KeyK');
            await sleep(900);
            await page.keyboard.type('cwys', { delay: 60 });
            await sleep(700);
            await page.keyboard.press('Enter');
        }
        await sleep(1200);
        await shot('save-dialog');
        await page.keyboard.press('Enter');
        await sleep(2500);
        const after = await catalogPresets();
        const added = after.filter((x) => !before.includes(x));
        console.log('presets before', before, 'after', after);
        if (added.length !== 1) errors.push('save-as-preset did not add exactly one preset');
        await sleep(1500);

        // 2) 侧栏 ▾ → 用预设新建（套用新存的预设）
        const [px, py] = arg('preset-btn', '240,26').split(',').map(Number);
        await page.mouse.click(geo.x + px, geo.y + py);
        await sleep(1000);
        await shot('sidebar-preset-menu');
        const convsBefore = ctl.events.filter((e) => e.type === 'conversation-list.snapshot' || e.type === 'conversation.updated').length;
        await page.keyboard.press('Escape');
        await sleep(500);

        // 3) 命令面板 → 用预设新建：旧模型预设（模型下架后回落 + 提示）
        const providers = await (async () => {
            ctl.send('catalog.request');
            const b = ctl.events.length;
            const s = await ctl.waitFor((e) => e.type === 'catalog.snapshot' && ctl.events.indexOf(e) >= b, 10000, 'catalog');
            return s.payload.providers;
        })();
        const mock = providers.find((p) => p.id === 'mock');
        const req = randomUUID();
        ctl.send('config.provider-upsert', { requestId: req, provider: { ...mock, models: ['mock-gpt', 'mock-gpt-mini'], defaultModel: 'mock-gpt-mini' } });
        const ap = await ctl.waitFor((e) => e.type === 'config.applied' && e.payload.requestId === req, 10000, 'provider applied');
        console.log('retire mock-reasoner', JSON.stringify(ap.payload));
        await sleep(1500);
        await key('Control+KeyK');
        await sleep(900);
        await page.keyboard.type('旧模型', { delay: 60 });
        await sleep(800);
        await shot('palette-preset');
        const created = ctl.events.length;
        await page.keyboard.press('Enter');
        await sleep(1300);
        await shot('fallback-toast');
        void created; void convsBefore;
        ctl.close();
    } catch (error) {
        console.error('qa_r27 FAILED:', error && error.stack ? error.stack : error);
        errors.push(String(error));
    } finally {
        console.log(JSON.stringify({ errors }, null, 2));
        try { await Promise.race([browser.close().catch(() => {}), sleep(2000)]); } catch {}
        process.exit(errors.length > 0 ? 1 : 0);
    }
}

main();

#!/usr/bin/env node
// 万象 PWA 视觉 QA：7 态截图 + 几何门禁。
//
// 设计原则（verify-round2 教训）：纯坐标点击在 compact 抽屉下不可靠
// （点击落在抽屉行上变成选中对话，输入框永远拿不到焦点，三次发送零提交）。
// 因此状态驱动走 WebSocket 协议（配对→建对话→发 LONG/TOOL/FAIL→等 generation.finished，
// 主机 ack 即内容断言），浏览器只负责渲染截图；纯 UI 动作（选对话行、开设置）
// 必须做像素变化断言，失败重试 3 次再报错。
// 用法：node tools/qa_shot.js --tag <name> [--theme dark] [--width 1440] [--height 900] [--scale 1.25]
// 依赖：puppeteer-core + 本机 google-chrome；主机需运行并已 qa_provision.js 配好 mock。

const puppeteer = require('puppeteer-core');
const { createHash, randomUUID } = require('node:crypto');
const { existsSync, mkdirSync, readFileSync } = require('node:fs');
const { join } = require('node:path');

const argv = process.argv.slice(2);
const arg = (k, d) => {
    const i = argv.indexOf(`--${k}`);
    return i >= 0 && argv[i + 1] ? argv[i + 1] : d;
};

const OUT = arg('out', '.scratch/shots');
const BASE = arg('base', 'http://127.0.0.1:8765');
const LOG = arg('log', '/tmp/wanxiang.log');
const WIDTH = Number(arg('width', 1440));
const HEIGHT = Number(arg('height', 900));
const SCALE = Number(arg('scale', 1));
const TAG = arg('tag', 'run');
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

async function main() {
    const errors = [];
    const browser = await puppeteer.launch({
        executablePath: CHROME,
        headless: true,
        args: [
            '--no-sandbox',
            '--disable-setuid-sandbox',
            '--use-gl=angle',
            '--use-angle=swiftshader',
            '--enable-unsafe-swiftshader',
            '--disable-dev-shm-usage',
            `--window-size=${WIDTH},${HEIGHT}`,
        ],
        defaultViewport: { width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE },
    });

    try {
    const page = await browser.newPage();
    await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: THEME }]);
    // r18：无头 Chrome 的 showOpenFilePicker 拦截后拿不到 DOM 节点（accept 抛 DOM.resolveNode），
    // 去掉它让 Avalonia 退回 <input type=file>，puppeteer 的 FileChooser 才能塞文件。
    await page.evaluateOnNewDocument(() => { try { delete window.showOpenFilePicker; window.showOpenFilePicker = undefined; } catch {} });

    const resizeChecks = [];
    page.on('console', (m) => {
        const t = m.text();
        if (/error|fail|exception|unhandled/i.test(t)) errors.push(t.slice(0, 400));
    });
    page.on('pageerror', (e) => errors.push(String(e).slice(0, 400)));

    await page.exposeFunction('wxPairingCode', async () => {
        for (let i = 0; i < 60; i++) {
            const code = latestPairingCode();
            if (code) return code;
            await sleep(150);
        }
        return null;
    });

    // ---- 协议面：配对 + 建对话（mock 模型 + echo 工具）----
    const wsUrl = BASE.replace('http', 'ws') + '/ws';
    const ctl = makeClient(wsUrl);
    await ctl.open();
    // ADR-001 方案 B：主机不再先发 protocol.hello；instanceId 取自 auth.accepted。
    ctl.send('protocol.hello', { protocol: 'wanxiang', version: 1 });
    ctl.send('pairing.requested', { clientName: 'qa' });
    await ctl.waitFor((e) => e.type === 'pairing.started', 15000, 'pairing.started');
    const code = await page.evaluate(() => window.wxPairingCode());
    if (!code) throw new Error('no pairing code in server log');
    ctl.send('pairing.attempted', { code, clientName: 'qa' });
    const paired = await ctl.waitFor((e) => e.type === 'pairing.succeeded', 15000, 'pairing.succeeded');
    ctl.send('auth.present', { token: paired.payload.token });
    const accepted = await ctl.waitFor((e) => e.type === 'auth.accepted', 15000, 'auth.accepted');
    const instanceId = accepted.payload.instanceId;
    console.log('paired instance', instanceId);
    // r11：工具默认要批准（决策 W3）；QA 控制端声明审批能力，由协议批准，浏览器只截图。
    ctl.send('tool.approval-capability', { supported: true });
    ctl.send('catalog.request');
    await ctl.waitFor((e) => e.type === 'catalog.snapshot', 15000, 'catalog.snapshot');
    ctl.send('conversation-list.observe');
    await ctl.waitFor((e) => e.type === 'conversation-list.snapshot', 15000, 'list snapshot');

    const convId = randomUUID();
    ctl.send('conversation.create', {
        invocationId: randomUUID(),
        threadId: convId,
        title: `QA ${TAG}`,
        config: { provider: 'mock', model: 'mock-gpt-mini', tools: ['builtin:echo'] },
    });
    await ctl.waitFor((e) => e.type === 'command.committed', 15000, 'create committed');
    ctl.send('conversation.observe', { threadId: convId });
    await ctl.waitFor((e) => e.type === 'conversation.snapshot', 15000, 'conversation snapshot');

    // ---- 展现面：凭据写入 IndexedDB，浏览器自动连接同一对话列表 ----
    await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await sleep(3500);
    await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await page.evaluate(
        async (creds) => {
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
        },
        { instanceId, url: wsUrl, token: paired.payload.token },
    );

    await page.reload({ waitUntil: 'domcontentloaded' });
    const minCanvasWidth = Math.max(200, Math.min(600, WIDTH * 0.75));
    const minCanvasHeight = Math.max(200, Math.min(400, HEIGHT * 0.75));
    await page.waitForFunction(
        (minW, minH) => {
            const c = document.querySelector('#out canvas');
            return c && c.width >= minW && c.height >= minH;
        },
        { timeout: 120000, polling: 250 },
        minCanvasWidth,
        minCanvasHeight,
    );
    await sleep(4500);

    const geo = await page.evaluate(() => {
        const c = document.querySelector('#out canvas');
        const r = c.getBoundingClientRect();
        return { x: r.x, y: r.y, w: r.width, h: r.height };
    });

    const hashFile = (p) => createHash('md5').update(readFileSync(p)).digest('hex');
    async function shot(name) {
        const path = join(OUT, `${TAG}-${name}.png`);
        await page.screenshot({ path });
        shots.push(path);
        console.log('shot:', path);
        return path;
    }
    // 纯 UI 动作必须自证效果：点前点后像素无变化 = 没点中，重试 3 次再判失败。
    async function clickExpectChange(x, y, label) {
        const probe = join(OUT, `${TAG}-.probe.png`);
        let before = null;
        for (let attempt = 1; attempt <= 3; attempt++) {
            await page.screenshot({ path: probe });
            before = hashFile(probe);
            await page.mouse.click(geo.x + x, geo.y + y);
            await sleep(1200);
            await page.screenshot({ path: probe });
            if (hashFile(probe) !== before) return;
            console.log(`retry ${label} (${attempt}/3): no visual change`);
            await sleep(800);
        }
        throw new Error(`click had no effect: ${label} at (${Math.round(x)},${Math.round(y)})`);
    }
    const currentLayout = async () => {
        return await page.evaluate(() => {
            const canvas = document.querySelector('#out canvas');
            const rect = canvas?.getBoundingClientRect();
            return {
                canvas: rect ? [rect.width, rect.height] : null,
                body: [document.body.scrollWidth, document.body.scrollHeight],
                viewport: [window.innerWidth, window.innerHeight],
                devicePixelRatio: window.devicePixelRatio,
            };
        });
    };
    const verifyLayout = (label, state) => {
        const [vw, vh] = state.viewport;
        const [bw, bh] = state.body;
        const canvas = state.canvas;
        const close = (a, b) => Math.abs(a - b) <= 0.75;
        if (!canvas || !close(canvas[0], vw) || !close(canvas[1], vh) || bw > vw + 0.75 || bh > vh + 0.75) {
            errors.push(`layout ${label}: ${JSON.stringify(state)}`);
        }
        resizeChecks.push({ label, ...state });
    };
    const resizeSweep = async (phase) => {
        if (!RESIZE_SWEEP) return;
        const minWidth = 390;
        const maxWidth = Math.max(WIDTH, 1440);
        const step = 53;
        const widths = [];
        for (let width = maxWidth; width >= minWidth; width -= step) widths.push(width);
        if (widths[widths.length - 1] !== minWidth) widths.push(minWidth);
        const back = widths.slice(0, -1).reverse();
        for (const width of [...widths, ...back]) {
            await page.setViewport({ width, height: HEIGHT, deviceScaleFactor: SCALE });
            await sleep(90);
            verifyLayout(`${phase}-${width}`, await currentLayout());
        }
        await page.setViewport({ width: WIDTH, height: HEIGHT, deviceScaleFactor: SCALE });
        await sleep(180);
        verifyLayout(`${phase}-restored`, await currentLayout());
    };

    const compact = geo.w < 720;
    const rowX = Math.min(140, geo.w * 0.25);

    // 01 已连接（侧栏/抽屉列表态）
    await shot('01-connected');
    await resizeSweep('connected');

    // 02 空对话：点第一行（WS 建的 QA 对话，按更新时间置顶）
    // r10（决策 279）：抽屉布局下坐标点行不可靠（抽屉可能没开、行高随密度变）。
    // 先走 Ctrl+1（契约 C2：按列表顺序打开第 1 个对话，与布局无关），像素变化自证；
    // 不成再在 compact 下先确保抽屉打开、按行高扫几个候选 y。
    async function openFirstConversation() {
        const probe = join(OUT, `${TAG}-.probe.png`);
        await page.screenshot({ path: probe });
        const before = hashFile(probe);
        await page.keyboard.down('Control');
        await page.keyboard.press('Digit1');
        await page.keyboard.up('Control');
        await sleep(1200);
        await page.screenshot({ path: probe });
        if (hashFile(probe) !== before) return 'ctrl+1';
        if (compact) {
            await page.mouse.click(geo.x + 20, geo.y + 26); // 汉堡按钮：打开抽屉
            await sleep(1000);
        }
        // r11：判据只看标题栏（侧栏 hover 也会改像素，曾把点空当成打开）；行 y 按侧栏密度扫描。
        const header = compact
            ? { x: geo.x + 60, y: geo.y, width: Math.min(400, geo.w - 120), height: 48 }
            : { x: geo.x + 300, y: geo.y, width: Math.min(600, geo.w - 320), height: 48 };
        const headerHash = async () => createHash('md5').update(await page.screenshot({ clip: header })).digest('hex');
        for (const y of [222, 200, 245, 180, 265, 160]) {
            const before = await headerHash();
            await page.mouse.click(geo.x + rowX, geo.y + y);
            await sleep(1200);
            if ((await headerHash()) !== before) return `row@${y}`;
        }
        throw new Error('could not open the QA conversation');
    }
    console.log('open conversation via', await openFirstConversation());
    await sleep(800);
    await shot('02-new-conversation');

    // 03/04 LONG：流式中 vs 完成（主机 ack 即内容断言）
    ctl.send('chat.user-message.enqueue', {
        invocationId: randomUUID(),
        threadId: convId,
        message: { role: 'user', contents: [{ text: 'LONG show me full markdown layout' }] },
    });
    await ctl.waitFor((e) => e.type === 'generation.started', 20000, 'long started');
    await sleep(900);
    await shot('03-streaming');
    await resizeSweep('streaming');
    await ctl.waitFor((e) => e.type === 'generation.finished' && e.payload.status === 'completed', 60000, 'long finished');
    await sleep(600);
    await shot('04-markdown');

    // 05 TOOL
    const startsBefore = ctl.events.filter((e) => e.type === 'generation.started').length;
    ctl.send('chat.user-message.enqueue', {
        invocationId: randomUUID(),
        threadId: convId,
        message: { role: 'user', contents: [{ text: 'TOOL call echo' }] },
    });
    await ctl.waitFor(
        () => ctl.events.filter((e) => e.type === 'generation.started').length > startsBefore,
        20000,
        'tool started',
    );
    const finishedBefore = ctl.events.filter((e) => e.type === 'generation.finished').length;
    // r18：echo 可能已被策略设为「总是允许」，这时没有审批请求；不当作失败。
    const approval = await ctl.waitFor((e) => e.type === 'tool.approval-requested', 8000, 'tool approval requested').catch(() => null);
    await sleep(900);
    await shot(approval ? '05-tool-approval' : '05-tool-auto');
    if (approval) ctl.send('tool.approval-decision', { approvalId: approval.payload.approvalId, approved: true });
    await ctl.waitFor(
        () => ctl.events.filter((e) => e.type === 'generation.finished').length > finishedBefore,
        60000,
        'tool finished',
    );
    await sleep(600);
    await shot('05b-tool-call');

    // 06 FAIL：等 failed 终态
    ctl.send('chat.user-message.enqueue', {
        invocationId: randomUUID(),
        threadId: convId,
        message: { role: 'user', contents: [{ text: 'FAIL trigger upstream error' }] },
    });
    await ctl.waitFor((e) => e.type === 'generation.finished' && e.payload.status === 'failed', 60000, 'fail finished');
    await sleep(600);
    await shot('06-error-card');

    // 06b RATE：429 + Retry-After，限流卡只说一遍（决策 277）+ 倒计时
    const failedBefore = ctl.events.filter((e) => e.type === 'generation.finished' && e.payload.status === 'failed').length;
    ctl.send('chat.user-message.enqueue', {
        invocationId: randomUUID(),
        threadId: convId,
        message: { role: 'user', contents: [{ text: 'RATE trigger rate limit' }] },
    });
    await ctl.waitFor(
        () => ctl.events.filter((e) => e.type === 'generation.finished' && e.payload.status === 'failed').length > failedBefore,
        60000,
        'rate finished',
    );
    await sleep(900);
    await shot('06b-rate-limit');

    // 08 附件（r18）：点回形针，拦截文件选择器塞一张 PNG 和一个文本文件，截附件托盘态。
    // 回形针位置随布局变：按候选坐标逐个试，拿到 file chooser 即止；拿不到只记日志不判失败。
    {
        const { writeFileSync } = require('node:fs');
        const png = join(OUT, '.qa-attach.png');
        const txt = join(OUT, '.qa-notes.txt');
        // 64x48 纯色 PNG（无依赖手写）：zlib 压一行行扫描线
        const zlib = require('node:zlib');
        const w = 64, h = 48;
        const raw = Buffer.alloc((w * 3 + 1) * h);
        for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
            const o = y * (w * 3 + 1) + 1 + x * 3; raw[o] = 40 + x * 3; raw[o + 1] = 120; raw[o + 2] = 200 - y * 2;
        }
        const crc = (b) => { let c = ~0; for (const v of b) { c ^= v; for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (0xedb88320 & -(c & 1)); } return ~c >>> 0; };
        const chunk = (t, d) => { const l = Buffer.alloc(4); l.writeUInt32BE(d.length); const td = Buffer.concat([Buffer.from(t), d]); const c = Buffer.alloc(4); c.writeUInt32BE(crc(td)); return Buffer.concat([l, td, c]); };
        const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
        writeFileSync(png, Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]));
        writeFileSync(txt, '会议纪要\n- 附件 QA\n');
        const candidates = compact
            ? [[geo.w - 76, geo.h - 30], [geo.w - 60, geo.h - 30], [geo.w - 90, geo.h - 30], [geo.w - 76, geo.h - 50]]
            : [[geo.w * 0.813, geo.h - 44], [geo.w * 0.813, geo.h - 38], [geo.w * 0.80, geo.h - 44]];
        let attached = false;
        for (const [x, y] of candidates) {
            try {
                const [chooser] = await Promise.all([
                    page.waitForFileChooser({ timeout: 2500 }),
                    page.mouse.click(geo.x + x, geo.y + y),
                ]);
                await chooser.accept(chooser.isMultiple() ? [png, txt] : [png]);
                attached = true;
                break;
            } catch {
                await page.keyboard.press('Escape');
                await sleep(300);
            }
        }
        console.log('attachment step:', attached ? 'attached' : 'no file chooser (skipped)');
        await sleep(1500);
        await shot(attached ? '08-attachment' : '08-attachment-skipped');
    }

    // 07 设置：齿轮常驻（wide）；compact 先确保抽屉打开再点齿轮
    const settingsX = compact ? geo.w - 26 : 264;
    try {
        await clickExpectChange(settingsX, geo.h - 26, 'open-settings');
    } catch {
        await page.mouse.click(geo.x + 20, geo.y + 26);
        await sleep(1000);
        await clickExpectChange(settingsX, geo.h - 26, 'open-settings-after-drawer');
    }
    await shot('07-settings');
    await resizeSweep('settings');

    ctl.close();
    const layout = await currentLayout();
    console.log(JSON.stringify({ shots, layout, resizeChecks, errors: errors.slice(0, 12) }, null, 2));
    if (errors.length > 0) {
        console.error(`qa_shot: ${errors.length} browser/layout error(s)`);
        process.exitCode = 1;
    }
    } catch (error) {
        // finally 里 process.exit 会吞掉异常：这里先打印并计入失败（r11：曾静默 rc=0）
        console.error('qa_shot FAILED:', error && error.stack ? error.stack : error);
        errors.push(String(error));
    } finally {
        // SwANGLE 下 browser.close() 偶发挂起：2 秒关不掉就直接退出
        try {
            await Promise.race([
                browser.close().catch(() => {}),
                sleep(2000).then(() => {}),
            ]);
        } catch {}
        process.exit(errors.length > 0 ? 1 : 0);
    }
}

main().catch((error) => {
    console.error(error);
    process.exitCode = 1;
});

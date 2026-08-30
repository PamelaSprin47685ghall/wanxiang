#!/usr/bin/env node
// 万象 PWA 视觉 QA：驱动真实交互并逐态截图。
// 用法：node tools/qa_shot.js --tag <name> [--theme dark] [--width 1440] [--height 900]
// 依赖：puppeteer-core + 本机 google-chrome；服务端需在 127.0.0.1:8765 运行。

import puppeteer from 'puppeteer-core';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

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
const TAG = arg('tag', 'run');
const THEME = arg('theme', 'light');

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
    defaultViewport: { width: WIDTH, height: HEIGHT },
});

try {
    const page = await browser.newPage();
    await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: THEME }]);

    const errors = [];
    page.on('console', (m) => {
        const t = m.text();
        if (/error|fail|exception|unhandled/i.test(t)) errors.push(t.slice(0, 400));
    });
    page.on('pageerror', (e) => errors.push(String(e).slice(0, 400)));

    // 配对码只出现在服务端 stderr，页面拿不到；用桥函数让页面向 Node 索取。
    await page.exposeFunction('wxPairingCode', async () => {
        for (let i = 0; i < 60; i++) {
            const code = latestPairingCode();
            if (code) return code;
            await sleep(150);
        }
        return null;
    });

    await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
    // Service Worker 首次接管会触发 location.reload() 销毁执行上下文，故先让它 reload 完再注入脚本。
    await sleep(3500);
    await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
    await sleep(1200);

    const credentials = await page.evaluate(async (base) => {
        const wsUrl = base.replace('http', 'ws') + '/ws';
        return await new Promise((resolve, reject) => {
            const ws = new WebSocket(wsUrl);
            let instanceId = null;
            const timer = setTimeout(() => reject(new Error('pairing timeout')), 30000);
            ws.onmessage = async (e) => {
                const ev = JSON.parse(e.data);
                if (ev.type === 'protocol.hello') {
                    instanceId = ev.payload.instanceId;
                    ws.send(JSON.stringify({ type: 'protocol.hello', payload: { protocol: 'wanxiang', version: 1 } }));
                    ws.send(JSON.stringify({ type: 'pairing.requested', payload: { clientName: 'qa' } }));
                } else if (ev.type === 'pairing.started') {
                    const code = await window.wxPairingCode();
                    if (!code) {
                        clearTimeout(timer);
                        reject(new Error('no pairing code in server log'));
                        return;
                    }
                    ws.send(JSON.stringify({ type: 'pairing.attempted', payload: { code, clientName: 'qa' } }));
                } else if (ev.type === 'pairing.succeeded') {
                    clearTimeout(timer);
                    resolve({ instanceId, token: ev.payload.token });
                    ws.close();
                } else if (ev.type === 'pairing.failed') {
                    clearTimeout(timer);
                    reject(new Error(ev.payload.reason));
                }
            };
            ws.onerror = () => reject(new Error('websocket error'));
        });
    }, BASE);
    console.log('paired instance', credentials.instanceId);

    // 写入 IndexedDB：Avalonia 启动后会自动读它并连接（决策 52/53、Q191）
    await page.evaluate(
        async (base, cred) => {
            const db = await new Promise((resolve, reject) => {
                const req = indexedDB.open('wanxiang', 1);
                req.onupgradeneeded = () => req.result.createObjectStore('connections', { keyPath: 'instanceId' });
                req.onsuccess = () => resolve(req.result);
                req.onerror = () => reject(req.error);
            });
            await new Promise((resolve, reject) => {
                const tx = db.transaction('connections', 'readwrite');
                tx.objectStore('connections').put({
                    instanceId: cred.instanceId,
                    url: base.replace('http', 'ws') + '/ws',
                    token: cred.token,
                    name: 'qa',
                    updatedAt: Date.now(),
                });
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        },
        BASE,
        credentials,
    );

    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForFunction(
        () => {
            const c = document.querySelector('#out canvas');
            return c && c.width > 600 && c.height > 400;
        },
        { timeout: 120000, polling: 250 },
    );
    await sleep(4500);

    const geo = await page.evaluate(() => {
        const c = document.querySelector('#out canvas');
        const r = c.getBoundingClientRect();
        return { x: r.x, y: r.y, w: r.width, h: r.height };
    });

    async function shot(name) {
        const path = join(OUT, `${TAG}-${name}.png`);
        await page.screenshot({ path });
        shots.push(path);
        console.log('shot:', path);
    }
    const click = async (x, y, settle = 600) => {
        await page.mouse.click(geo.x + x, geo.y + y);
        await sleep(settle);
    };
    const typeText = async (text) => {
        await page.keyboard.type(text, { delay: 8 });
        await sleep(250);
    };
    const composerY = geo.h - 70;

    await shot('01-connected');

    // 侧栏右上角「新建会话」
    await click(258, 26, 1800);
    await shot('02-new-conversation');

    await click(geo.w / 2, composerY);
    await typeText('LONG show me full markdown layout');
    await page.keyboard.press('Enter');
    await sleep(1400);
    await shot('03-streaming');
    await sleep(5000);
    await shot('04-markdown');

    await click(geo.w / 2, composerY);
    await typeText('TOOL call echo');
    await page.keyboard.press('Enter');
    await sleep(4500);
    await shot('05-tool-call');

    await click(geo.w / 2, composerY);
    await typeText('FAIL trigger upstream error');
    await page.keyboard.press('Enter');
    await sleep(4000);
    await shot('06-error-card');

    // 侧栏右下角齿轮 → 设置
    await click(geo.w - 1440 + 264, geo.h - 26, 1600);
    await shot('07-settings');

    console.log(JSON.stringify({ shots, errors: errors.slice(0, 12) }, null, 2));
} finally {
    await browser.close();
}

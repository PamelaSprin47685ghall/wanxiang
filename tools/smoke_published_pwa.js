#!/usr/bin/env node
// 决策 411：对「发布产物」做 PWA 冒烟（不是 bin/ 里的开发构建）。headless Chrome、DPR 2：
//   1) 页面渲染出 Avalonia 画布（非空白、无未捕获异常）；
//   2) 画布像素尺寸 = CSS 尺寸 × 2（决策 400 的 DPR 补丁确实在产物里生效）；
//   3) 文本上屏桥：CDP Input.insertText 进输入框只落一次（决策 391）。
// 用法：node tools/smoke_published_pwa.js --base http://127.0.0.1:<port> [--out <dir>] [--chrome /usr/bin/google-chrome]
const puppeteer = require('puppeteer-core');
const { mkdirSync } = require('node:fs');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const BASE = arg('base', '');
const OUT = arg('out', '/tmp/wanxiang-smoke');
const CHROME = arg('chrome', process.env.CHROME_PATH || '/usr/bin/google-chrome');
if (!BASE) { console.error('--base required'); process.exit(2); }
mkdirSync(OUT, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failed = 0;
const check = (name, ok, detail) => { if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); };
(async () => {
    const browser = await puppeteer.launch({ executablePath: CHROME, headless: 'new', args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', (e) => errors.push(String(e)));
        await page.setViewport({ width: 1200, height: 800, deviceScaleFactor: 2 });
        await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
        // 等 Avalonia 画布出现并有尺寸（事件/条件等待，上限 120 s）。
        const canvasReady = await page.waitForFunction(() => {
            const c = document.querySelector('canvas');
            return c && c.width > 0 && c.clientWidth > 0 && document.querySelector('.avalonia-input-element');
        }, { timeout: 120000, polling: 250 }).then(() => true, () => false);
        check('Avalonia 画布与输入桥元素出现', canvasReady);
        if (!canvasReady) { await page.screenshot({ path: `${OUT}/smoke-timeout.png` }); return; }
        // 首帧之后再量（给渲染一点时间）。
        await sleep(3000);
        const dims = await page.evaluate(() => {
            const c = document.querySelector('canvas');
            return { w: c.width, h: c.height, cw: c.clientWidth, ch: c.clientHeight, dpr: window.devicePixelRatio };
        });
        check('DPR 2 画布像素 = CSS × 2', dims.dpr === 2 && Math.abs(dims.w - dims.cw * 2) <= 2 && Math.abs(dims.h - dims.ch * 2) <= 2, JSON.stringify(dims));
        const shot = `${OUT}/smoke-render.png`;
        await page.screenshot({ path: shot });
        // 非空白：截图里至少有两种以上明显不同的颜色（画布截取像素统计）。
        const distinct = await page.evaluate(async () => {
            const c = document.querySelector('canvas');
            const off = document.createElement('canvas'); off.width = 64; off.height = 64;
            const ctx = off.getContext('2d'); ctx.drawImage(c, 0, 0, 64, 64);
            const d = ctx.getImageData(0, 0, 64, 64).data; const set = new Set();
            for (let i = 0; i < d.length; i += 4) set.add(((d[i] >> 4) << 8) | ((d[i + 1] >> 4) << 4) | (d[i + 2] >> 4));
            return set.size;
        });
        check('画布有实际内容（非纯色）', distinct >= 3, `distinct colors=${distinct}`);
        // 文本上屏桥：点输入框后直接 insertText。
        const cdp = await page.target().createCDPSession();
        await page.mouse.click(738, 720); await sleep(800);
        await cdp.send('Input.insertText', { text: '万象' }); await sleep(600);
        await cdp.send('Input.insertText', { text: 'ok' }); await sleep(800);
        const v = await page.evaluate(() => document.querySelector('.avalonia-input-element').value);
        await page.screenshot({ path: `${OUT}/smoke-insert.png` });
        check('文本上屏桥：insertText 各落一次', v === '万象ok', `value=${JSON.stringify(v)}`);
        check('无未捕获页面异常', errors.length === 0, errors.slice(0, 3).join(' | '));
    } finally { await browser.close(); }
    process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });

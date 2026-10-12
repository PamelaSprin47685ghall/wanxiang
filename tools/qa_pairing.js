#!/usr/bin/env node
// 未配对视角 QA（r11）：手机抽屉里的「连接主机」、配对码输入数字后的状态。
// 用法：node tools/qa_pairing.js --base http://127.0.0.1:8772 --out shots [--steps "click:x,y;type:123456;shot:name;wait:ms;key:Tab"]
// 坐标为 CSS 像素（相对画布左上角）。每步后可 shot。
const puppeteer = require('puppeteer-core');
const { existsSync, mkdirSync } = require('node:fs');
const { join } = require('node:path');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const BASE = arg('base', 'http://127.0.0.1:8765');
const OUT = arg('out', '.scratch/shots');
const W = Number(arg('width', 390)), H = Number(arg('height', 844)), S = Number(arg('scale', 2));
const THEME = arg('theme', 'light');
const STEPS = arg('steps', 'shot:first');
const TAG = arg('tag', 'pair');
mkdirSync(OUT, { recursive: true });
const CHROME = ['/usr/bin/google-chrome', '/usr/bin/google-chrome-stable', '/usr/bin/chromium'].find(existsSync);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
    const browser = await puppeteer.launch({ executablePath: CHROME, headless: true,
        args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'],
        defaultViewport: { width: W, height: H, deviceScaleFactor: S, isMobile: W < 600, hasTouch: W < 600 } });
    let code = 0;
    try {
        const page = await browser.newPage();
        await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: THEME }]);
        await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
        await page.waitForFunction(() => { const c = document.querySelector('#out canvas'); return c && c.width > 200; }, { timeout: 120000 });
        await sleep(5000);
        for (const step of STEPS.split(';').filter(Boolean)) {
            const [k, v] = [step.slice(0, step.indexOf(':')), step.slice(step.indexOf(':') + 1)];
            if (k === 'click') { const [x, y] = v.split(',').map(Number); await page.mouse.click(x, y); await sleep(1200); }
            else if (k === 'tap') { const [x, y] = v.split(',').map(Number); await page.touchscreen.tap(x, y); await sleep(1200); }
            else if (k === 'type') { await page.keyboard.type(v, { delay: 120 }); await sleep(800); }
            else if (k === 'key') { await page.keyboard.press(v); await sleep(600); }
            else if (k === 'wait') await sleep(Number(v));
            else if (k === 'shot') { const p = join(OUT, `${TAG}-${v}.png`); await page.screenshot({ path: p }); console.log('shot:', p); }
        }
    } catch (e) { console.error('qa_pairing FAILED:', e); code = 1; }
    finally { await Promise.race([browser.close().catch(() => {}), sleep(2000)]); process.exit(code); }
})();

#!/usr/bin/env node
// 决策 388：PWA 中文输入法组合回归（CDP Input.imeSetComposition，headless Chrome）。
// 覆盖输入框、侧栏搜索、命令面板：组合中回车不提交/不发送；预编辑可见；提交不重复。
// 用法：node tools/qa_ime_pwa.js [--base http://127.0.0.1:18791] [--out .scratch/ime]
// 依赖 puppeteer-core（NODE_PATH 指向其 node_modules）+ 本机 google-chrome；主机需带 --pwa 运行（无需配对）。
const puppeteer = require('puppeteer-core');
const { mkdirSync } = require('node:fs');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const BASE = arg('base', 'http://127.0.0.1:18791');
const OUT = arg('out', '.scratch/ime');
mkdirSync(OUT, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
    const browser = await puppeteer.launch({ executablePath: '/usr/bin/google-chrome', headless: 'new', args: ['--no-sandbox'] });
    let failed = 0;
    try {
        const page = await browser.newPage();
        await page.setViewport({ width: 1200, height: 800 });
        const cdp = await page.target().createCDPSession();
        await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
        await sleep(18000);
        const value = () => page.evaluate(() => document.querySelector('.avalonia-input-element').value);
        // 组合中按回车：浏览器给的是 keyCode 229 + isComposing。
        const enter = async () => { for (const type of ['keyDown', 'keyUp']) await cdp.send('Input.dispatchKeyEvent', { type, key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 229, nativeVirtualKeyCode: 229 }); };
        const run = async (name, focus) => {
            await focus(); await sleep(600);
            await page.keyboard.type('ab'); await sleep(300);
            await cdp.send('Input.imeSetComposition', { text: 'ni', selectionStart: 2, selectionEnd: 2 }); await sleep(400);
            await page.screenshot({ path: `${OUT}/${name}-preedit.png` });
            await enter(); await sleep(400);
            await cdp.send('Input.imeSetComposition', { text: 'nih', selectionStart: 3, selectionEnd: 3 }); await sleep(200);
            await cdp.send('Input.insertText', { text: '你' }); await sleep(600);
            await page.screenshot({ path: `${OUT}/${name}-commit.png` });
            const v = await value();
            const ok = v === 'ab你';
            if (!ok) failed++;
            console.log(`${ok ? 'PASS' : 'FAIL'} ${name}: 文本=${JSON.stringify(v)}（期望 "ab你"，组合中回车未提交、无重复字）`);
            await page.keyboard.press('Escape'); await sleep(300);
        };
        await run('composer', () => page.mouse.click(738, 720));
        await run('search', () => page.mouse.click(140, 132));
        await run('palette', async () => { await page.mouse.click(738, 720); await page.keyboard.down('Control'); await page.keyboard.press('KeyK'); await page.keyboard.up('Control'); });
    } finally { await browser.close(); }
    process.exit(failed ? 1 : 0);
})();

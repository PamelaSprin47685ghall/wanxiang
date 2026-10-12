#!/usr/bin/env node
// 决策 388：PWA 中文输入法组合回归（CDP Input.imeSetComposition，headless Chrome）。
// 覆盖输入框、侧栏搜索、命令面板：组合中回车不提交/不发送；预编辑可见；提交不重复。
// 决策 391：另测非组合上屏（CDP Input.insertText 直接上屏、insertReplacementText 联想/自动更正替换、
// insertFromPaste 长按粘贴），每种只落一次；字段含输入框、搜索、命令面板、配对码。
// 判据读隐藏 <input> 的值：桥 preventDefault 后它只由 Avalonia 的 SetSurroundingText 回写，即 Avalonia 文本。
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
        // 非组合上屏：直接 insertText、自动更正替换光标前的词（"你好" → "您好"）、粘贴。
        const fire = (inputType, data, sel) => page.evaluate(({ inputType, data, sel }) => {
            const el = document.querySelector('.avalonia-input-element');
            if (sel) el.setSelectionRange(sel[0], sel[1]);
            const init = { inputType, cancelable: true, bubbles: true };
            if (inputType === 'insertFromPaste') { const dt = new DataTransfer(); dt.setData('text/plain', data); init.dataTransfer = dt; } else init.data = data;
            el.dispatchEvent(new InputEvent('beforeinput', init));
        }, { inputType, data, sel });
        const direct = async (name, focus, cleanup, t = ['ab', '你好', '您好', '!', 'ab您好!']) => {
            await focus(); await sleep(600);
            await page.keyboard.press('End'); for (let i = 0; i < 30; i++) await page.keyboard.press('Backspace');
            await cdp.send('Input.insertText', { text: t[0] }); await sleep(400);
            await cdp.send('Input.insertText', { text: t[1] }); await sleep(400);
            await fire('insertReplacementText', t[2], [2, 4]); await sleep(400);
            await fire('insertFromPaste', t[3]); await sleep(500);
            await page.screenshot({ path: `${OUT}/${name}-direct.png` });
            const v = await value();
            const ok = v === t[4];
            if (!ok) failed++;
            console.log(`${ok ? 'PASS' : 'FAIL'} ${name}/非组合: 文本=${JSON.stringify(v)}（期望 ${JSON.stringify(t[4])}：insertText 各一次、光标前的词被替换、粘贴一次）`);
            if (cleanup) await cleanup(); else { await page.keyboard.press('End'); for (let i = 0; i < 30; i++) await page.keyboard.press('Backspace'); }
            await page.keyboard.press('Escape'); await sleep(300);
        };
        await direct('composer', () => page.mouse.click(738, 720));
        await direct('search', () => page.mouse.click(140, 132));
        await direct('palette', async () => { await page.mouse.click(738, 720); await page.keyboard.down('Control'); await page.keyboard.press('KeyK'); await page.keyboard.up('Control'); });
        await run('composer', () => page.mouse.click(738, 720));
        await run('search', () => page.mouse.click(140, 132));
        await run('palette', async () => { await page.mouse.click(738, 720); await page.keyboard.down('Control'); await page.keyboard.press('KeyK'); await page.keyboard.up('Control'); });
        // 配对对话框（坐标为 1200×800 视口）：「连接主机」后配对码框自动聚焦；主机地址框需点选。
        const openPair = async (x, y) => { await page.mouse.click(745, 402); await sleep(1500); if (x) { await page.mouse.click(x, y); await sleep(400); } };
        const clear = async () => { await page.keyboard.press('End'); for (let i = 0; i < 40; i++) await page.keyboard.press('Backspace'); };
        // 配对码框只收数字（满 6 位自动提交），用 5 位数字验证。
        await direct('pairing', () => openPair(), clear, ['12', '34', '56', '7', '12567']);
        await direct('host-url', () => openPair(600, 275), clear);
        await page.screenshot({ path: `${OUT}/pairing-open.png` });
    } finally { await browser.close(); }
    process.exit(failed ? 1 : 0);
})();

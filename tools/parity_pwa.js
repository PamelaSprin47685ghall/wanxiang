#!/usr/bin/env node
// 决策 453：PWA 运行时一致性冒烟。用 ?wxprobe=1 打开发布产物（探针默认关闭），
// 触发各条生命周期路径，读 globalThis.__wxProbe 断言它们在浏览器里真的跑到了。
// 第 27 轮的教训：MainView 先挂上再 Build，导致 4 个计时器在 PWA 里从不运行，而桌面测试全绿。
// 用法：node tools/parity_pwa.js --base http://127.0.0.1:<port> [--out <dir>] [--chrome <path>] [--json <file>]
const puppeteer = require('puppeteer-core');
const { mkdirSync, writeFileSync } = require('node:fs');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 && argv[i + 1] ? argv[i + 1] : d; };
const BASE = arg('base', '');
const OUT = arg('out', '/tmp/wanxiang-parity');
const JSON_OUT = arg('json', '');
const CHROME = arg('chrome', process.env.CHROME_PATH || '/usr/bin/google-chrome');
if (!BASE) { console.error('--base required'); process.exit(2); }
mkdirSync(OUT, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failed = 0;
const results = [];
const check = (name, ok, detail) => { if (!ok) failed++; results.push({ name, ok, detail: detail || '' }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); };
const probe = (page) => page.evaluate(() => Object.assign({}, globalThis.__wxProbe || {}));
async function waitProbe(page, key, timeout = 15000) {
    return page.waitForFunction((k) => (globalThis.__wxProbe || {})[k] > 0, { timeout, polling: 200 }, key).then(() => true, () => false);
}
(async () => {
    const browser = await puppeteer.launch({ executablePath: CHROME, headless: 'new', args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', (e) => errors.push(String(e)));
        await page.setViewport({ width: 1200, height: 800, deviceScaleFactor: 1 });

        // 0) 探针默认关闭：不带参数打开时 __wxProbe 不存在。
        await page.goto(BASE, { waitUntil: 'domcontentloaded', timeout: 60000 });
        await page.waitForFunction(() => document.querySelector('canvas') && document.querySelector('.avalonia-input-element'), { timeout: 120000, polling: 250 });
        await sleep(1500);
        check('探针默认关闭（无 wxprobe 时不记录）', await page.evaluate(() => typeof globalThis.__wxProbe === 'undefined'));

        // 1) 打开探针。
        await page.goto(`${BASE}/?wxprobe=1`, { waitUntil: 'domcontentloaded', timeout: 60000 });
        const ready = await page.waitForFunction(() => document.querySelector('canvas') && document.querySelector('.avalonia-input-element'), { timeout: 120000, polling: 250 }).then(() => true, () => false);
        check('画布出现', ready);
        if (!ready) { await page.screenshot({ path: `${OUT}/parity-timeout.png` }); return; }

        // 挂载与启动路径
        for (const key of ['lifecycle.shellAttached', 'connect.auto', 'drafts.loaded', 'theme.systemSubscribed'])
            check(`启动：${key}`, await waitProbe(page, key, 20000));
        // 周期计时器（决策 449 的回归面）：都要在几秒内 tick。
        for (const key of ['timer.viewport', 'timer.delivery', 'timer.draft'])
            check(`计时器：${key}`, await waitProbe(page, key, 15000));

        // 网络恢复事件 → 立即重连路径
        await page.evaluate(() => dispatchEvent(new Event('online')));
        check('online 事件 → reconnect.resume', await waitProbe(page, 'reconnect.resume', 5000));

        // 键盘快捷键（Ctrl+K 命令面板）
        await page.mouse.click(600, 400); await sleep(300);
        await page.keyboard.down('Control'); await page.keyboard.press('KeyK'); await page.keyboard.up('Control');
        check('快捷键 Ctrl+K → shortcut.resolved', await waitProbe(page, 'shortcut.resolved', 5000));
        await page.keyboard.press('Escape'); await sleep(300);

        // 偏好持久化（决策 452）：轮换主题后 localStorage 里有偏好，刷新后仍在。
        await page.keyboard.down('Control'); await page.keyboard.down('Shift'); await page.keyboard.press('KeyS');
        await page.keyboard.up('Shift'); await page.keyboard.up('Control');
        await sleep(800);
        const prefs = await page.evaluate(() => localStorage.getItem('wanxiang.local:ui.json'));
        check('界面偏好写入 localStorage', !!prefs && prefs.includes('"theme"'), prefs ? prefs.slice(0, 60).replace(/\s+/g, ' ') : 'null');

        // 页面转后台 → 立刻落草稿（决策 451）
        await page.evaluate(() => dispatchEvent(new PageTransitionEvent('pagehide', { persisted: false })));
        check('pagehide → drafts.flushOnHide', await waitProbe(page, 'drafts.flushOnHide', 5000));

        // 深链：同源地址 #open?c=…&m=… 会被页面链接轮询接住
        await page.evaluate(() => { location.hash = 'open?c=00000000-0000-4000-8000-000000000001&m=1'; });
        check('#open 深链 → deeplink.open', await waitProbe(page, 'deeplink.open', 5000));

        // 刷新后偏好仍在（不是内存文件系统）
        await page.goto(`${BASE}/?wxprobe=1`, { waitUntil: 'domcontentloaded', timeout: 60000 });
        await waitProbe(page, 'lifecycle.shellAttached', 60000);
        const after = await page.evaluate(() => localStorage.getItem('wanxiang.local:ui.json'));
        check('刷新后偏好仍在', !!prefs && after === prefs);

        const final = await probe(page);
        await page.screenshot({ path: `${OUT}/parity.png` });
        console.log('probe counts:', JSON.stringify(final));
        check('无未捕获页面异常', errors.length === 0, errors.slice(0, 3).join(' | '));
        if (JSON_OUT) writeFileSync(JSON_OUT, JSON.stringify({ results, final }, null, 2));
    } finally { await browser.close(); }
    process.exit(failed ? 1 : 0);
})().catch((e) => { console.error(e); process.exit(1); });

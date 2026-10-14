// PWA 真浏览器端到端冒烟（可重跑）：由 tools/pwa_smoke.sh 起 mock 模型与主机后调用，
//   bash tools/pwa_smoke.sh        （需要 Release 构建、google-chrome、playwright-core）
// 覆盖：页面加载 → 真实配对流程（配对码取自主机日志）→ 发送 LONG（mock 回复含 fsharp 代码块与公式）
//       → 着色/公式确由 .NET 经 BrowserRich 调到 hljs/KaTeX（JS 侧计数 + 截图）→ 草稿刷新后仍在。
const { chromium } = require('playwright-core');
const fs = require('fs');
const LOG = process.argv[2] || '/tmp/pwa-smoke-host.log';
const BASE = 'http://127.0.0.1:18799';
const OUT = process.env.WX_SMOKE_OUT || require('path').join(__dirname, '..', '.scratch', 'pwa-smoke');
fs.mkdirSync(OUT, { recursive: true });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const S = 1200 / 1024; // 截图坐标（1024 宽）→ 视口坐标
const at = (x, y) => [Math.round(x * S), Math.round(y * S)];
const results = [];
const check = (n, ok, d = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${n}${d ? ' — ' + d : ''}`); };
const lastCode = () => { const m = [...fs.readFileSync(LOG, 'utf8').matchAll(/"code":"(\d{6})"/g)]; return m.length ? m[m.length - 1][1] : null; };
const readDraft = (page) => page.evaluate(() => new Promise((res) => {
  const r = indexedDB.open('wanxiang-drafts');
  r.onsuccess = () => { const db = r.result; if (!db.objectStoreNames.contains('drafts')) return res('');
    const g = db.transaction('drafts', 'readonly').objectStore('drafts').getAll(); g.onsuccess = () => res(JSON.stringify(g.result)); g.onerror = () => res(''); };
  r.onerror = () => res(''); }));
// 在 hljs/KaTeX 上计数：richHighlight/richMath 每次调用时才取 globalThis.hljs/katex，所以包一层即可看到 .NET 侧的请求
const instrument = (page) => page.evaluate(() => {
  globalThis.__rich = { hl: [], math: [] };
  const wrap = () => {
    if (globalThis.hljs && !globalThis.hljs.__wx) { const o = hljs.highlight.bind(hljs); hljs.highlight = (c, opt) => { const v = o(c, opt); __rich.hl.push({ lang: opt && opt.language, spans: (v.value.match(/hljs-/g) || []).length }); return v; }; hljs.__wx = 1; }
    if (globalThis.katex && !globalThis.katex.__wx) { const o = katex.renderToString.bind(katex); katex.renderToString = (t, opt) => { const v = o(t, opt); __rich.math.push({ tex: t, display: !!(opt && opt.displayMode), ok: v.includes('katex-html') }); return v; }; katex.__wx = 1; }
  };
  wrap(); setInterval(wrap, 50);
});
const ready = (page) => page.waitForFunction(() => document.querySelector('canvas') && document.querySelector('.avalonia-input-element'), null, { timeout: 180000, polling: 250 }).then(() => true, () => false);
(async () => {
  const browser = await chromium.launch({ executablePath: '/usr/bin/google-chrome', headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage', '--use-gl=swiftshader', '--enable-unsafe-swiftshader'] });
  const ctx = await browser.newContext({ viewport: { width: 1200, height: 800 } });
  const page = await ctx.newPage();
  const errors = []; page.on('pageerror', (e) => errors.push(String(e)));
  await page.goto(BASE + '/?wxprobe=1', { waitUntil: 'domcontentloaded', timeout: 60000 });
  check('页面加载', await ready(page));
  await sleep(3000);
  await instrument(page);

  // ---- 配对（真实流程：连接主机 → 主机生成配对码 → 输入提交）----
  const before = lastCode();
  await page.mouse.click(...at(635, 345)); // 「连接主机」
  let code = null;
  for (let i = 0; i < 50 && (!code || code === before); i++) { await sleep(200); code = lastCode(); }
  check('主机生成了配对码', !!code && code !== before, code || '');
  await page.mouse.click(...at(512, 397)); await sleep(300);
  await page.keyboard.type(code, { delay: 60 });
  await page.mouse.click(...at(512, 487)); // 「提交配对码」
  await sleep(4000);
  await page.screenshot({ path: OUT + '/1-paired.png' });
  const creds = await page.evaluate(() => new Promise((res) => { const r = indexedDB.open('wanxiang', 1); r.onsuccess = () => { const g = r.result.transaction('connections', 'readonly').objectStore('connections').getAll(); g.onsuccess = () => res(g.result.length); }; r.onerror = () => res(0); }));
  check('配对成功，凭据经 BrowserCredentials 写入 IndexedDB', creds > 0, `${creds} 条`);

  // ---- 发送 LONG：mock 回复含 ```fsharp 代码块与行内/展示公式 ----
  // 连上后空对话的输入框在页面中部
  await page.mouse.click(...at(635, 328)); await sleep(400);
  await page.keyboard.type('LONG smoke', { delay: 40 });
  await page.keyboard.press('Enter');
  let rich = { hl: [], math: [] };
  for (let i = 0; i < 60; i++) { await sleep(500); rich = await page.evaluate(() => globalThis.__rich); if (rich.hl.some((h) => h.lang === 'fsharp') && rich.math.length >= 4) break; }
  await sleep(2500);
  await page.screenshot({ path: OUT + '/2-reply.png' });
  const fs1 = rich.hl.filter((h) => h.lang === 'fsharp');
  check('代码块由 .NET 请求 fsharp 着色（按需语言已加载）且产出 hljs span', fs1.length > 0 && fs1.every((h) => h.spans > 0), JSON.stringify(fs1.slice(0, 2)));
  const texs = [...new Set(rich.math.map((m) => m.tex))];
  check('公式由 .NET 请求 KaTeX 排版（行内与展示式）且产出 katex-html', rich.math.some((m) => m.display) && rich.math.some((m) => !m.display) && rich.math.every((m) => m.ok), texs.join(' | '));
  // 回复在画布上：滚到代码块处再截一张
  await page.mouse.move(...at(635, 300)); await page.mouse.wheel(0, 600); await sleep(1500);
  await page.screenshot({ path: OUT + '/3-reply-scrolled.png' });

  // ---- 草稿：输入不发送 → 页面隐藏落盘 → 刷新后仍在 ----
  await page.mouse.click(...at(635, 615)); await sleep(400);
  await page.keyboard.type('draft-survives-reload-42', { delay: 40 });
  await sleep(1500);
  await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true }); document.dispatchEvent(new Event('visibilitychange')); dispatchEvent(new Event('pagehide')); });
  await sleep(1500);
  const d1 = await readDraft(page);
  check('草稿经 BrowserDrafts 写入 IndexedDB', d1.includes('draft-survives-reload-42'), d1.slice(0, 160));
  await page.reload({ waitUntil: 'domcontentloaded' });
  check('刷新后页面重新就绪', await ready(page));
  await sleep(6000);
  await page.screenshot({ path: OUT + '/4-after-reload.png' });
  const d2 = await readDraft(page);
  check('刷新后 IndexedDB 里草稿仍在', d2.includes('draft-survives-reload-42'));
  // 刷新后默认是新对话；草稿属于刚才那个对话：点侧栏里的它，输入框应恢复草稿（见 5-draft-restored.png）
  await page.mouse.click(...at(120, 183)); await sleep(3000);
  await page.screenshot({ path: OUT + '/5-draft-restored.png' });
  const probe = await page.evaluate(() => Object.assign({}, globalThis.__wxProbe || {}));
  check('刷新后草稿重新载入（drafts.loaded）', (probe['drafts.loaded'] || 0) > 0, JSON.stringify(probe));
  check('全程无页面级 JS 错误', errors.length === 0, errors.join(' | ').slice(0, 300));
  await browser.close();
  console.log(results.every(Boolean) ? 'ALL PASS' : 'SOME FAILED');
  process.exit(results.every(Boolean) ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(2); });

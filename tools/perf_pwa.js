#!/usr/bin/env node
// PWA 感知性能基准（决策 290）：首访冷加载 / 已缓存二次加载 → 启动画面收起（画布已按真实尺寸画出）耗时。
// 二次加载 = 关掉浏览器、用同一份用户目录重新打开（模拟「再次打开应用」；同进程重载会命中 Blink 内存缓存，不真实）。
// 用法：NODE_PATH=<puppeteer-core 所在 node_modules> node tools/perf_pwa.js --base http://127.0.0.1:18912 [--runs 3]
// 配合 tools/perf_pwa_count.sh 统计每个阶段真正到达主机的请求（阶段之间请求一次 /__perf_mark_<阶段>）。
const puppeteer = require('puppeteer-core');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
const BASE = arg('base', 'http://127.0.0.1:18912');
const RUNS = Number(arg('runs', 3));

async function load(profile, settleMs) {
  const browser = await puppeteer.launch({ executablePath: '/usr/bin/google-chrome', headless: 'new', userDataDir: profile,
    args: ['--no-sandbox', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--window-size=1280,800'] });
  try {
    const page = await browser.newPage();
    await page.setViewport({ width: 1280, height: 800 });
    const t0 = Date.now();
    await page.goto(BASE + '/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => { const s = document.querySelector('.splash'); return !s || s.getAttribute('aria-busy') === 'false'; }, { timeout: 120000, polling: 50 });
    const ms = Date.now() - t0;
    if (settleMs) await new Promise((r) => setTimeout(r, settleMs)); // 让 SW 安装、缓存写完
    return ms;
  } finally { await browser.close(); }
}

const mark = (name) => fetch(`${BASE}/__perf_mark_${name}`).catch(() => {});

(async () => {
  const res = { cold: [], cached: [] };
  for (let r = 0; r < RUNS; r++) {
    const profile = mkdtempSync(join(tmpdir(), 'wxperf-'));
    try {
      res.cold.push(await load(profile, 4000));
      await mark('cached');
      res.cached.push(await load(profile, 0));
      await mark('end');
    } finally { rmSync(profile, { recursive: true, force: true }); }
  }
  const med = (xs) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];
  console.log(JSON.stringify({ base: BASE, coldMedianMs: med(res.cold), cachedMedianMs: med(res.cached), ...res }));
})().catch((e) => { console.error(e); process.exit(1); });

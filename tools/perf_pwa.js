#!/usr/bin/env node
// PWA 感知性能基准（决策 290）：首访冷加载 / 已缓存二次加载 → 启动画面收起（可交互）耗时与传输字节。
// 用法：NODE_PATH=<puppeteer-core 所在 node_modules> node tools/perf_pwa.js --base http://127.0.0.1:18912 [--mbps 40] [--runs 3]
const puppeteer = require('puppeteer-core');
const { mkdtempSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const argv = process.argv.slice(2);
const arg = (k, d) => { const i = argv.indexOf(`--${k}`); return i >= 0 ? argv[i + 1] : d; };
const BASE = arg('base', 'http://127.0.0.1:18912');
const MBPS = Number(arg('mbps', 0));
const RUNS = Number(arg('runs', 3));

async function load(page, cdp) {
  let bytes = 0, reqs = 0;
  const onData = (e) => { bytes += e.encodedDataLength; reqs++; };
  cdp.on('Network.loadingFinished', onData);
  const t0 = Date.now();
  await page.goto(BASE + '/', { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => document.querySelector('.splash') === null || document.querySelector('.splash').getAttribute('aria-busy') === 'false', { timeout: 120000, polling: 50 });
  const ms = Date.now() - t0;
  cdp.off('Network.loadingFinished', onData);
  const tr = await page.evaluate(() => performance.getEntriesByType('resource').reduce((a, e) => [a[0] + (e.transferSize || 0), a[1] + (e.decodedBodySize || 0)], [0, 0]));
  return { ms, wireMB: +(Math.max(bytes, tr[0]) / 1048576).toFixed(1), bodyMB: +(tr[1] / 1048576).toFixed(1), reqs };
}

(async () => {
  const res = { cold: [], cached: [] };
  for (let r = 0; r < RUNS; r++) {
    const profile = mkdtempSync(join(tmpdir(), 'wxperf-'));
    const browser = await puppeteer.launch({ executablePath: '/usr/bin/google-chrome', headless: 'new', userDataDir: profile,
      args: ['--no-sandbox', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--window-size=1280,800'] });
    try {
      const page = await browser.newPage();
      await page.setViewport({ width: 1280, height: 800 });
      const cdp = await page.target().createCDPSession();
      await cdp.send('Network.enable');
      if (MBPS > 0) await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 40, downloadThroughput: MBPS * 125000, uploadThroughput: MBPS * 125000 });
      res.cold.push(await load(page, cdp));
      await new Promise((r) => setTimeout(r, 3000)); // 让 SW 安装、缓存写完
      res.cached.push(await load(page, cdp));
    } finally { await browser.close(); rmSync(profile, { recursive: true, force: true }); }
  }
  const med = (xs) => xs.map((x) => x.ms).sort((a, b) => a - b)[Math.floor(xs.length / 2)];
  console.log(JSON.stringify({ base: BASE, mbps: MBPS, coldMedianMs: med(res.cold), cachedMedianMs: med(res.cached), ...res }));
})().catch((e) => { console.error(e); process.exit(1); });

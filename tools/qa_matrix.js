#!/usr/bin/env node
// 精品 UI 发布矩阵：逐组调用 qa_shot.js，并把“七态 + viewport + console”变成硬门禁。

const { spawnSync } = require('node:child_process');
const { createHash } = require('node:crypto');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');

const argv = process.argv.slice(2);
const arg = (key, fallback) => {
    const index = argv.indexOf(`--${key}`);
    return index >= 0 && argv[index + 1] ? argv[index + 1] : fallback;
};

const OUT = arg('out', '.scratch/craft-matrix');
const PREFIX = arg('prefix', 'craft');
const BASE = arg('base', 'http://127.0.0.1:8765');
const LOG = arg('log', '/tmp/wanxiang.log');

const cases = [
    ['1440-light', 1440, 900, 'light', 1],
    ['1440-dark', 1440, 900, 'dark', 1],
    ['1240-light', 1240, 800, 'light', 1],
    ['900-light', 900, 620, 'light', 1],
    ['900-dark', 900, 620, 'dark', 1],
    ['900-low', 900, 500, 'light', 1],
    ['719', 719, 700, 'light', 1],
    ['721', 721, 700, 'light', 1],
    ['540', 540, 600, 'light', 1],
    ['390', 390, 844, 'light', 1],
    ['scale125', 900, 620, 'light', 1.25],
    ['scale150', 900, 620, 'light', 1.5],
];

function parseResult(stdout) {
    const marker = stdout.lastIndexOf('\n{');
    const json = marker >= 0 ? stdout.slice(marker + 1) : stdout;
    return JSON.parse(json.trim());
}

function close(a, b) {
    return Math.abs(a - b) <= 0.75;
}

const results = [];
let failed = false;

// 前序挂起的 headless chrome 会占住 GPU/端口：先清场（失败也无妨）。
try {
    spawnSync('pkill', ['-f', 'chrome.*headless'], { stdio: 'ignore' });
} catch { /* no pkill available */ }

for (const [name, width, height, theme, scale] of cases) {
    const tag = `${PREFIX}-${name}`;
    console.log(`=== MATRIX ${tag} ${width}x${height} ${theme} @${scale} ===`);
    const run = spawnSync(
        process.execPath,
        [
            join(__dirname, 'qa_shot.js'),
            '--out', OUT,
            '--tag', tag,
            '--base', BASE,
            '--log', LOG,
            '--width', String(width),
            '--height', String(height),
            '--theme', theme,
            '--scale', String(scale),
        ],
        { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 8 * 1024 * 1024 },
    );
    process.stdout.write(run.stdout || '');
    process.stderr.write(run.stderr || '');

    let result = null;
    try {
        result = parseResult(run.stdout || '');
    } catch (error) {
        failed = true;
        results.push({ name, ok: false, reason: `invalid result JSON: ${error.message}` });
        continue;
    }

    const sevenStates = Array.isArray(result.shots) && result.shots.length === 7;
    // 内容门禁：存在 7 个文件不代表 7 个状态。生成态（03–06）若与 07-settings
    // 逐字节相同，说明 LONG/TOOL/FAIL 根本没渲染（verify-round1 教训：空数据
    // 目录无 provider，提交后自动跳设置，后续点击全落在设置页上）。
    let distinct = false;
    let distinctDetail = 'shots missing';
    if (sevenStates) {
        const hashOf = (p) => {
            try {
                return createHash('md5').update(readFileSync(p)).digest('hex').slice(0, 12);
            } catch {
                return null;
            }
        };
        const hashes = result.shots.map(hashOf);
        const settingsHash = hashes[6];
        const genStates = hashes.slice(2, 6);
        const allNull = hashes.some((h) => h === null);
        const allSameAsSettings = !allNull && genStates.every((h) => h === settingsHash);
        distinct = !allNull && !allSameAsSettings;
        distinctDetail = allNull ? 'unreadable shot' : `03-06 vs 07: ${genStates.map((h) => (h === settingsHash ? 'SAME' : 'diff')).join(',')}`;
    }
    const noErrors = Array.isArray(result.errors) && result.errors.length === 0;
    const canvas = result.layout?.canvas;
    const body = result.layout?.body;
    const viewport = result.layout?.viewport || [width, height];
    const geometry =
        canvas
        && body
        && close(canvas[0], viewport[0])
        && close(canvas[1], viewport[1])
        && body[0] <= viewport[0] + 0.75
        && body[1] <= viewport[1] + 0.75;
    const dpr = close(result.layout?.devicePixelRatio ?? scale, scale);
    const ok = run.status === 0 && sevenStates && noErrors && geometry && dpr && distinct;
    if (!ok) failed = true;
    results.push({ name, ok, sevenStates, noErrors, geometry, dpr, distinct, distinctDetail, layout: result.layout, errors: result.errors });
}

console.log('=== MATRIX SUMMARY ===');
console.log(JSON.stringify({ out: OUT, cases: results, ok: !failed }, null, 2));
if (failed) process.exitCode = 1;

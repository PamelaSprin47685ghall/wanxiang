#!/usr/bin/env node
// 精品 UI 发布矩阵：逐组调用 qa_shot.js，并把“七态 + viewport + console”变成硬门禁。

const { spawnSync } = require('node:child_process');
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
    const ok = run.status === 0 && sevenStates && noErrors && geometry && dpr;
    if (!ok) failed = true;
    results.push({ name, ok, sevenStates, noErrors, geometry, dpr, layout: result.layout, errors: result.errors });
}

console.log('=== MATRIX SUMMARY ===');
console.log(JSON.stringify({ out: OUT, cases: results, ok: !failed }, null, 2));
if (failed) process.exitCode = 1;

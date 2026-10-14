#!/usr/bin/env node
// 装配界面仍持有的 KaTeX 字体（Assets/Fonts/KaTeX_*）。
//
// highlight.js 与 KaTeX 的脚本资源已归 wanxiang2 的 libs/RichScripts，由那边自己生成；
// 旧仓库绝不写入新系统，这里只产出界面自己的字体。
// 字体版本必须与 RichScripts 内嵌的 KaTeX 版本一致（目前 0.18.4）。
//
// 用法：node tools/build_webassets.js
'use strict';

const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const KATEX_VERSION = '0.18.4';

const repoRoot = path.resolve(__dirname, '..');
const fontOut = path.join(repoRoot, 'src/Wanxiang.UI/Assets/Fonts');
const workDir = path.join(repoRoot, '.scratch/webassets-build');

function fetchKatex() {
  fs.mkdirSync(workDir, { recursive: true });
  const pkg = path.join(workDir, 'package.json');
  if (!fs.existsSync(pkg)) fs.writeFileSync(pkg, JSON.stringify({ private: true }, null, 2));
  console.log(`installing katex@${KATEX_VERSION}`);
  execFileSync('npm', ['install', '--silent', '--no-audit', '--no-fund', `katex@${KATEX_VERSION}`],
    { cwd: workDir, stdio: ['ignore', 'ignore', 'inherit'] });
}

function main() {
  fetchKatex();
  const nm = path.join(workDir, 'node_modules');

  fs.mkdirSync(fontOut, { recursive: true });
  // 只清 KaTeX_ 前缀，正文字体与许可证留在原处
  for (const f of fs.readdirSync(fontOut)) {
    if (f.startsWith('KaTeX_')) fs.rmSync(path.join(fontOut, f));
  }

  let fontCount = 0, fontBytes = 0;
  for (const f of fs.readdirSync(path.join(nm, 'katex/dist/fonts')).sort()) {
    if (!f.endsWith('.ttf')) continue;
    const buf = fs.readFileSync(path.join(nm, 'katex/dist/fonts', f));
    fs.writeFileSync(path.join(fontOut, f), buf);
    fontCount += 1; fontBytes += buf.length;
  }
  fs.copyFileSync(path.join(nm, 'katex/LICENSE'), path.join(fontOut, 'LICENSE-KaTeX.txt'));

  // 一个字族一张字形表：KaTeX_Main-Italic 少了减号与正负号，
  // 让排版栈按样式去 family 里挑成员，浏览器端就会挑错并渲染成豆腐块。
  console.log('renaming KaTeX faces to unique families');
  execFileSync('python3', [path.join(repoRoot, 'tools/katex_font_rename.py'), fontOut],
    { stdio: ['ignore', 'inherit', 'inherit'] });

  console.log(`\nFonts/KaTeX_*     ${fontCount} ttf, ${(fontBytes / 1024).toFixed(0)}KB`);
}

main();

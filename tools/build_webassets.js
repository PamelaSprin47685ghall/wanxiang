#!/usr/bin/env node
// 装配内嵌的 highlight.js 与 KaTeX 资源。
//
// 为什么要装配而不是直接塞原包：
//   1. hljs 的官方全语言打包体约 1MB，全量塞进 WASM 里解析太贵。这里把
//      「core + common 36 种」作为基座一次性加载，其余 54 种拆成单文件按需注册
//      （平均 8KB / 7ms），既覆盖真实语言面又不拖启动。
//   2. .NET 的 Regex 不认 \p{XID_Start} / \p{XID_Continue} 这类派生属性
//      （python 语法用了），Jint 转换时会抛。构建期换成等价的通用类别组合。
//   3. 单语言文件是 CommonJS，需包一层 module 垫片才能在裸 JS 引擎里注册。
//
// 用法：node tools/build_webassets.js
'use strict';

const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFileSync } = require('child_process');

const KATEX_VERSION = '0.18.4';
const HLJS_VERSION = '11.12.0';

const repoRoot = path.resolve(__dirname, '..');
const assetsDir = path.join(repoRoot, 'src/Wanxiang.UI/Assets');
const jsOut = path.join(assetsDir, 'Js');
const langOut = path.join(jsOut, 'hljs.lang');
const fontOut = path.join(assetsDir, 'Fonts');
const workDir = path.join(repoRoot, '.scratch/webassets-build');

// common 之外，AI 对话里真实会出现的语言。刻意不求全：每一种都占体积，
// 而 hljs 全量 384 种里绝大多数（Gauss、Inform7、RouterOS…）在这里毫无意义。
const ON_DEMAND = [
  'apache', 'awk', 'clojure', 'cmake', 'coffeescript', 'crystal', 'd', 'dart',
  'delphi', 'django', 'dockerfile', 'dos', 'elixir', 'elm', 'erb', 'erlang',
  'fortran', 'fsharp', 'gherkin', 'glsl', 'gradle', 'groovy', 'haml',
  'handlebars', 'haskell', 'http', 'julia', 'latex', 'lisp', 'mathematica',
  'matlab', 'nginx', 'nim', 'nix', 'ocaml', 'powershell', 'processing',
  'prolog', 'properties', 'protobuf', 'puppet', 'qml', 'reasonml', 'scala',
  'scheme', 'scilab', 'stylus', 'tcl', 'thrift', 'twig', 'vala', 'verilog',
  'vim', 'x86asm',
];

/** .NET Regex 不支持的派生 Unicode 属性 → 等价通用类别组合。 */
function patchUnicodeEscapes(source) {
  return source
    .split('[\\p{XID_Start}_]').join('[\\p{L}\\p{Nl}_]')
    .split('\\p{XID_Continue}').join('[\\p{L}\\p{Nl}\\p{Mn}\\p{Mc}\\p{Nd}\\p{Pc}]');
}

function rmrf(dir) { fs.rmSync(dir, { recursive: true, force: true }); }

function fetchPackages() {
  fs.mkdirSync(workDir, { recursive: true });
  const pkg = path.join(workDir, 'package.json');
  if (!fs.existsSync(pkg)) fs.writeFileSync(pkg, JSON.stringify({ private: true }, null, 2));
  console.log(`installing katex@${KATEX_VERSION} highlight.js@${HLJS_VERSION}`);
  execFileSync('npm', ['install', '--silent', '--no-audit', '--no-fund',
    `katex@${KATEX_VERSION}`, `highlight.js@${HLJS_VERSION}`],
    { cwd: workDir, stdio: ['ignore', 'ignore', 'inherit'] });
}

/** hljs 的自包含 UMD 打包体（core + common 36 种）只在 cdn-release 仓库里有。 */
function fetchHljsBundle() {
  const dest = path.join(workDir, 'hljs.common.js');
  const url = `https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@${HLJS_VERSION}/build/highlight.min.js`;
  console.log(`fetching ${url}`);
  execFileSync('curl', ['-sSLf', '-o', dest, url], { stdio: ['ignore', 'ignore', 'inherit'] });
  return dest;
}

function main() {
  fetchPackages();
  const bundlePath = fetchHljsBundle();
  const nm = path.join(workDir, 'node_modules');

  rmrf(jsOut);
  fs.mkdirSync(langOut, { recursive: true });
  fs.mkdirSync(fontOut, { recursive: true });
  // 只清 KaTeX_ 前缀，正文字体与许可证留在原处
  for (const f of fs.readdirSync(fontOut)) {
    if (f.startsWith('KaTeX_')) fs.rmSync(path.join(fontOut, f));
  }

  // ---- KaTeX ----
  const katexSrc = fs.readFileSync(path.join(nm, 'katex/dist/katex.min.js'), 'utf8');
  fs.writeFileSync(path.join(jsOut, 'katex.min.js'), katexSrc);
  let fontCount = 0, fontBytes = 0;
  for (const f of fs.readdirSync(path.join(nm, 'katex/dist/fonts')).sort()) {
    if (!f.endsWith('.ttf')) continue;
    const buf = fs.readFileSync(path.join(nm, 'katex/dist/fonts', f));
    fs.writeFileSync(path.join(fontOut, f), buf);
    fontCount += 1; fontBytes += buf.length;
  }
  fs.copyFileSync(path.join(nm, 'katex/LICENSE'), path.join(fontOut, 'LICENSE-KaTeX.txt'));

  // 一个字族一张字形表：KaTeX_Main-Italic 少了减号与正负号，
  // 让排版栈按样式去family里挑成员，浏览器端就会挑错并渲染成豆腐块。
  console.log('renaming KaTeX faces to unique families');
  execFileSync('python3', [path.join(repoRoot, 'tools/katex_font_rename.py'), fontOut],
    { stdio: ['ignore', 'inherit', 'inherit'] });

  // ---- highlight.js 基座 ----
  const base = patchUnicodeEscapes(fs.readFileSync(bundlePath, 'utf8'));
  fs.writeFileSync(path.join(jsOut, 'hljs.core.js'), base);
  fs.copyFileSync(path.join(nm, 'highlight.js/LICENSE'), path.join(jsOut, 'LICENSE-highlight.js.txt'));

  const common = require(path.join(nm, 'highlight.js/lib/common')).listLanguages();
  const registered = new Set(common);

  // 别名只能从**真** hljs 上读：语法函数内部会调 hljs.regex/COMMENT 等成员，
  // 拿桩对象去调必抛，而抛了就静默读不到 grammar.aliases（得到一张错的别名表）。
  const fullHljs = require(path.join(nm, 'highlight.js'));
  const aliases = {};
  const addAliases = (canonical) => {
    aliases[canonical] = canonical;
    const grammar = fullHljs.getLanguage(canonical);
    for (const a of (grammar && grammar.aliases) || []) aliases[String(a).toLowerCase()] = canonical;
  };
  for (const name of common) addAliases(name);

  // ---- highlight.js 按需语言 ----
  const langDir = path.join(nm, 'highlight.js/lib/languages');
  const onDemand = [];
  let langBytes = 0;
  for (const name of [...new Set(ON_DEMAND)].sort()) {
    if (registered.has(name)) continue;
    const file = path.join(langDir, `${name}.js`);
    if (!fs.existsSync(file)) { console.warn(`  skip ${name}: not in highlight.js ${HLJS_VERSION}`); continue; }
    const wrapped = `(function(){var module={exports:{}};\n${patchUnicodeEscapes(fs.readFileSync(file, 'utf8'))}\n;hljs.registerLanguage(${JSON.stringify(name)},module.exports);})();\n`;
    fs.writeFileSync(path.join(langOut, `${name}.js`), wrapped);
    langBytes += wrapped.length;
    onDemand.push(name);
    addAliases(name);
  }

  // 别名表让 ```ts / ```py 这种写法在按需加载前就能定位到文件
  fs.writeFileSync(path.join(jsOut, 'hljs.aliases.json'),
    JSON.stringify({ builtin: common.sort(), onDemand, aliases }, null, 0));

  const kb = (n) => `${(n / 1024).toFixed(0)}KB`;
  console.log(`\nkatex.min.js      ${kb(katexSrc.length)}`);
  console.log(`Fonts/KaTeX_*     ${fontCount} ttf, ${kb(fontBytes)}`);
  console.log(`hljs.core.js      ${kb(base.length)}  (${common.length} 种内置)`);
  console.log(`hljs.lang/        ${onDemand.length} 种按需, ${kb(langBytes)}`);
  console.log(`aliases           ${Object.keys(aliases).length} 条`);
  console.log('done');
}

main();

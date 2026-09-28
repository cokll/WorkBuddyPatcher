// set_default_context_1m.js —— WorkBuddy 会话默认上下文预算改为优先 1M 档
// 用法: node set_default_context_1m.js [目标文件/目录...]
//   不带参数: 自动枚举 C:\Program Files\WorkBuddy* 全部安装下的
//   resources\app.asar.unpacked\cli\dist\codebuddy*.js
//
// 原理:
//   CLI 权威判定函数 resolveEffectiveContextBudget 的优先级为
//   override(用户手选) → defaultLength(服务端默认档, 通常 300K) → 最小档 → maxInputTokens。
//   本补丁在 override 之后、defaultLength 之前插入 "1M 档优先" 一档:
//     isBudget(<p>.overrideContextWindow)?...override...:isBudget(1e6)?1e6:isBudget(<p>.contextWindow?.defaultLength)...
//   isBudget 内部已经过 normalizeSupportedContextWindows 过滤(去重 + ≤ maxInputTokens),
//   因此模型不支持 1M 时自动回落原逻辑, 不会越界。
//
// 安全性:
//   - 锚点为规则代码特征, 不依赖偏移量/变量名(捕获组兼容任意压缩参数名), 产品更新后重跑即可
//   - 首次修改前生成 <file>.1mcontext.bak(已存在则不覆盖, 保留最早原始版本)
//   - 幂等: 已补丁文件报告「已补丁, 跳过」
//   - 只动 unpacked 明文 JS, 不解包/重打包 asar
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const ANCHOR = /isBudget\((\w+)\.overrideContextWindow\)\?\1\.overrideContextWindow:isBudget\(\1\.contextWindow\?\.defaultLength\)/g;
const PATCHED_MARK = ':isBudget(1e6)?1e6:isBudget(';
const BAK_SUFFIX = '.1mcontext.bak';

function discoverTargets(args) {
  if (args.length > 0) {
    const out = [];
    for (const a of args) {
      const st = fs.statSync(a);
      if (st.isDirectory()) {
        for (const f of fs.readdirSync(a)) {
          if (/^codebuddy.*\.js$/.test(f)) out.push(path.join(a, f));
        }
      } else out.push(a);
    }
    return out;
  }
  const out = [];
  for (const entry of fs.readdirSync('C:\\Program Files')) {
    if (!/^WorkBuddy/i.test(entry)) continue;
    const dist = path.join('C:\\Program Files', entry, 'resources', 'app.asar.unpacked', 'cli', 'dist');
    if (!fs.existsSync(dist)) continue;
    for (const f of fs.readdirSync(dist)) {
      if (/^codebuddy.*\.js$/.test(f)) out.push(path.join(dist, f));
    }
  }
  return out;
}

function patchFile(file) {
  const name = path.basename(file);
  const s = fs.readFileSync(file, 'utf8');
  if (s.includes(PATCHED_MARK)) {
    console.log(`跳过(已补丁): ${file}`);
    return { file, status: 'already' };
  }
  ANCHOR.lastIndex = 0;
  let hits = 0;
  const out = s.replace(ANCHOR, (m, p1) => {
    hits++;
    return `isBudget(${p1}.overrideContextWindow)?${p1}.overrideContextWindow:isBudget(1e6)?1e6:isBudget(${p1}.contextWindow?.defaultLength)`;
  });
  if (hits === 0) {
    console.log(`无需修改(未找到锚点): ${file}`);
    return { file, status: 'no-anchor' };
  }
  const bak = file + BAK_SUFFIX;
  if (!fs.existsSync(bak)) {
    fs.copyFileSync(file, bak);
    console.log(`备份: ${bak}`);
  } else {
    console.log(`备份已存在, 不覆盖: ${bak}`);
  }
  fs.writeFileSync(file, out);
  console.log(`已补丁: ${file} (插入 ${hits} 处 1M 优先档)`);
  return { file, status: 'patched', hits };
}

function main() {
  const targets = discoverTargets(process.argv.slice(2));
  if (targets.length === 0) {
    console.log('未发现目标文件');
    process.exitCode = 1;
    return;
  }
  const results = targets.map(patchFile);
  console.log('--- 语法校验 ---');
  let allOk = true;
  for (const r of results) {
    if (r.status !== 'patched') continue;
    try {
      execFileSync(process.execPath, ['--check', r.file], { stdio: 'pipe' });
      console.log(`node --check 通过: ${r.file}`);
    } catch (e) {
      allOk = false;
      console.error(`node --check 失败: ${r.file}\n${e.stderr}`);
    }
  }
  const patched = results.filter((r) => r.status === 'patched').length;
  console.log(`完成: ${patched}/${results.length} 个文件已补丁, 语法${allOk ? '全部通过' : '存在失败!'}`);
  if (!allOk) process.exitCode = 2;
}

main();

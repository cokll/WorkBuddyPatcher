// patch_load_boundary_stop.js —— 会话重载时在最新压缩边界处停止解析, 消除切换会话的内存尖峰
// 用法: node patch_load_boundary_stop.js [目标文件/目录...]
//   不带参数: 自动枚举 C:\Program Files\WorkBuddy* 全部安装下的
//   resources\app.asar.unpacked\cli\dist\codebuddy*.(js|mjs)
//
// 背景:
//   会话 JSONL 在磁盘上按"最新在前"倒序追加保存(多轮压缩的全部旧轮次都在)。
//   SessionStore.deserializeSessionFromPath 用反向 LineReader 从尾部(最新端)开始
//   逐行 JSON.parse, 最多读 resolveSessionMaxItems()(默认 1e3)条。切换会话再切回
//   时, 旧会话对象尚未被 GC, 新会话又把历史全量解析一遍, 两者叠加形成 10GB 级
//   尖峰, 之后才回落到稳态。
//
// 原理:
//   在解析回调里加一个停止条件: 解析出带 providerData.isCompacted / isSummary /
//   isSessionSeparator 标记的条目(即最新的压缩边界/会话分隔符, 从最新端读到的
//   第一条就是它)后立即停读——边界之前的所有旧轮次不再解析。语义与内存侧的
//   getCompactionBoundaryHistory 过滤、以及 patch_compact_free_memory.js 的压缩
//   后截断完全一致: 内存里只保留最新边界及之后的内容。停读瞬间顺手触发一次
//   V8 GC(尽力而为), 及时回收刚被切换掉的旧会话。
//
// 安全性:
//   - 无边界标记的会话行为与原来完全一致(仍按 maxItems/user-message 停止)
//   - hasCompactedHistory、meta、sessionId 提取等后续逻辑不受影响(边界条目本身
//     仍会被 push 进数组)
//   - 磁盘 JSONL 不做任何修改
//   - 锚点为解析回调的规则代码特征(变量名经捕获组适配), 产品更新后重跑即可
//   - 首次修改前生成 <file>.loadtrim.bak(已存在则不覆盖)
//   - 幂等: 已补丁文件报告「已补丁, 跳过」
//   - 只动 unpacked 明文 JS, 不解包/重打包 asar
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

// 形状: if(ARR.push(ITEM),void 0!==LIMIT){if(ARR.length>=MAX)return void DONE(!1)}else if(...)return void DONE(!1)}
// 注: 末尾的 } 是解析回调里 try 块的收尾, 替换时必须原样保留
const ANCHOR = /if\((\w+)\.push\((\w+)\),void 0!==(\w+)\)\{if\(\1\.length>=(\w+)\)return void (\w+)\(!1\)\}else if\(\1\.length>=\4&&"message"===\2\.type&&"user"===\2\.role\|\|\1\.length>\4\+100\)return void \5\(!1\)\}/g;
const PATCHED_MARK = '/*WB_LOAD_TRIM*/';
const BAK_SUFFIX = '.loadtrim.bak';

// 尽力触发 V8 GC 的 IIFE 表达式(单行)
const GC_EXPR = '(function(){try{if(typeof globalThis.gc==="function")return void globalThis.gc();if(typeof require==="function"){let wbV8=require("v8"),wbVm=require("vm");wbV8.setFlagsFromString("--expose-gc"),wbVm.runInNewContext("gc")()}}catch(wbE){}})()';

function discoverTargets(args) {
  if (args.length > 0) {
    const out = [];
    for (const a of args) {
      const st = fs.statSync(a);
      if (st.isDirectory()) {
        for (const f of fs.readdirSync(a)) {
          if (/^codebuddy.*\.(js|mjs)$/.test(f)) out.push(path.join(a, f));
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
      if (/^codebuddy.*\.(js|mjs)$/.test(f)) out.push(path.join(dist, f));
    }
  }
  return out;
}

function patchFile(file) {
  const s = fs.readFileSync(file, 'utf8');
  if (s.includes(PATCHED_MARK)) {
    console.log(`跳过(已补丁): ${file}`);
    return { file, status: 'already' };
  }
  ANCHOR.lastIndex = 0;
  let hits = 0;
  const out = s.replace(ANCHOR, (m, arr, item, lim, max, done) => {
    hits++;
    // 压缩边界/会话分隔符条目入列后立即停读并触发 GC; 其余停止条件保持原样
    return `if(${arr}.push(${item}),${item}&&${item}.providerData&&(${item}.providerData.isCompacted===!0||${item}.providerData.isSummary===!0||${item}.providerData.isSessionSeparator===!0))return ${done}(!1),${PATCHED_MARK}${GC_EXPR},void 0;if(void 0!==${lim}){if(${arr}.length>=${max})return void ${done}(!1)}else if(${arr}.length>=${max}&&"message"===${item}.type&&"user"===${item}.role||${arr}.length>${max}+100)return void ${done}(!1)}`;
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
  console.log(`已补丁: ${file} (解析回调边界停读 ${hits} 处)`);
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

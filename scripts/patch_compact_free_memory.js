// patch_compact_free_memory.js —— WorkBuddy 压缩完成后物理释放边界前历史内存
// 用法: node patch_compact_free_memory.js [目标文件/目录...]
//   不带参数: 自动枚举 C:\Program Files\WorkBuddy* 全部安装下的
//   resources\app.asar.unpacked\cli\dist\codebuddy*.(js|mjs)
//
// 原理:
//   会话历史 session.history 是只增数组: 三种压缩策略(Blocking/PRE_MESSAGE_AUTO/
//   EMERGENCY_AUTO MaxToken)完成后都只在 history 里追加一条带
//   providerData.isCompacted/isSummary 标记的摘要消息, 旧消息(全部工具输出/
//   读取的文件内容等)仍被该数组持有, 直到会话结束 => 每压缩一轮内存涨一截。
//   本补丁在四种策略共用的成功收尾点 CompactStrategyBase#markCompactionComplete
//   入口注入: 自后向前找最后一个 isCompacted/isSummary 摘要项, 把它之前的所有
//   旧历史从数组里 splice 掉(就地截断, 模型可见上下文不变——视图层本来就按
//   该边界过滤), 随后尽力触发一次 V8 GC(优先 globalThis.gc, 否则临时开启
//   --expose-gc 后在新 context 里取 gc; ESM 环境无 require 时静默跳过)。
//
// 安全性:
//   - 找不到边界摘要项时不截断(压缩失败/回滚路径不经过 markCompactionComplete,
//     或摘要尚未入列时不会误删)
//   - 截断只影响内存数组; 会话 JSONL 落盘文件不动, /resume /export 仍可从磁盘
//     读到完整记录
//   - 锚点为方法签名特征(markCompactionComplete(参数,参数="compact-blocking"){),
//     不依赖偏移量, 产品更新后重跑即可
//   - 首次修改前生成 <file>.compactfree.bak(已存在则不覆盖, 保留最早原始版本)
//   - 幂等: 已补丁文件报告「已补丁, 跳过」
//   - 只动 unpacked 明文 JS, 不解包/重打包 asar
//
// 代价/限制:
//   - 压缩边界之前的原始消息不再驻留内存: 依赖"读完整 history"的功能(如
//     压缩前 undo、部分统计)在内存里只能看到摘要及之后的内容(磁盘记录完整)
//   - 这是缓解补丁: UI 渲染层/Electron 端自身的会话副本不在本补丁范围内
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const ANCHOR = /markCompactionComplete\((\w+)(?:,(\w+)="([^"]*)")?\)\{/g;
const PATCHED_MARK = '/*WB_COMPACT_FREE*/';
const BAK_SUFFIX = '.compactfree.bak';

// 注入代码: 参数 P = 会话对象; 全部 wb* 前缀避免与压缩变量名冲突; 单行, 无 ASI 风险
const FREE_MEM = (P) => `try{let wbH=${P}&&Array.isArray(${P}.history)?${P}.history:null;if(wbH&&wbH.length>1){let wbB=-1;for(let wbI=wbH.length-1;wbI>=0;wbI--){let wbP=wbH[wbI]&&wbH[wbI].providerData;if(wbP&&(wbP.isCompacted===!0||wbP.isSummary===!0)){wbB=wbI;break}}if(wbB>0){wbH.splice(0,wbB);try{if(typeof globalThis.gc==="function")globalThis.gc();else if(typeof require==="function"){let wbVm=require("vm"),wbV8=require("v8");wbV8.setFlagsFromString("--expose-gc");wbVm.runInNewContext("gc")()}}catch(wbE){}}}}catch(wbE){}`;

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
  const out = s.replace(ANCHOR, (m, p1, p2, p3) => {
    hits++;
    const sig = p2 ? `${p1},${p2}="${p3}"` : p1;
    return `markCompactionComplete(${sig}){${PATCHED_MARK}${FREE_MEM(p1)}`;
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
  console.log(`已补丁: ${file} (markCompactionComplete 入口注入 ${hits} 处)`);
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

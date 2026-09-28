// patch_session_prune.js —— 每次压缩完成后自动清理旧会话文件, 只保留最近 N 个
// 用法: node patch_session_prune.js [目标文件/目录...]
//   不带参数: 自动枚举 C:\Program Files\WorkBuddy* 全部安装下的
//   resources\app.asar.unpacked\cli\dist\codebuddy*.(js|mjs)
//
// 背景:
//   会话 JSONL 只追加不收缩(1.3GB/279 个且持续增长)。内置
//   SessionHistoryCleanService 只按 cleanupPeriodDays 天龄清理, 启动后跑一次
//   + 每 24 小时一次, 没有"按数量保留"的能力。
//
// 原理:
//   在 CompactStrategyBase#markCompactionComplete 入口(压缩成功收尾点, 与
//   patch_compact_free_memory.js 同一位置, 互不影响)注入一个 fire-and-forget
//   异步清理器(延迟 3 秒执行, 避开压缩收尾与继续消息发送):
//     1. 扫描 ~\.workbuddy-ai\projects 与 ~\.workbuddy\projects 下全部 .jsonl
//     2. 仅主会话文件(路径不含 \subagents\)参与"最近 N 个"排序(N 由环境变量
//        WB_KEEP_SESSIONS 控制, 默认 5; 设为 0 整体停用清理)
//     3. 被淘汰的主会话文件删除时连带删除 .meta.json 与同名目录(会话的
//        subagents 目录); 未入选的会话文件一律不动
//     4. 30 分钟内修改过的会话一律跳过(宽限期, 保护多窗口/刚结束的会话)
//     5. 10 分钟节流; 运行中不重入; 全程 try/catch, 异常静默
//     6. 实际删除数量 >0 时向终端打印一行 [WB_PRUNE] 摘要
//
// 代价(务必知晓):
//   - 被清理的会话无法再 /resume; 磁盘记录被删除(这是本补丁的目的)
//   - 锚点为方法签名特征, 产品更新后重跑即可
//   - 首次修改前生成 <file>.prune.bak(已存在则不覆盖)
//   - 幂等: 已补丁文件报告「已补丁, 跳过」
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const ANCHOR = /markCompactionComplete\((\w+)(?:,(\w+)="([^"]*)")?\)\{/g;
const PATCHED_MARK = '/*WB_SESSION_PRUNE*/';
const BAK_SUFFIX = '.prune.bak';

// 注入代码: 延迟 3 秒的 fire-and-forget 清理器; 全部 wb* 前缀避免变量冲突; 单行
const PRUNE = `try{if(!globalThis.wbPruneAt||Date.now()-globalThis.wbPruneAt>6e5){globalThis.wbPruneAt=Date.now();setTimeout(function(){(async function(){try{let wbOs=require("os"),wbFs=require("fs"),wbPth=require("path");let wbK=parseInt(process.env.WB_KEEP_SESSIONS||"5",10);if(wbK===0)return;if(!(wbK>0))wbK=5;let wbCands=[];for(let wbHb of[".workbuddy-ai",".workbuddy"]){let wbRoot=wbPth.join(wbOs.homedir(),wbHb,"projects");let wbSt=wbFs.existsSync(wbRoot)&&wbFs.statSync(wbRoot);if(!wbSt||!wbSt.isDirectory())continue;(function wbScan(wbD){for(let wbE of wbFs.readdirSync(wbD,{withFileTypes:!0})){let wbP=wbPth.join(wbD,wbE.name);if(wbE.isDirectory())wbScan(wbP);else if(wbE.name.endsWith(".jsonl")){try{wbCands.push({p:wbP,st:wbFs.statSync(wbP)})}catch(wbE2){}}}})(wbRoot)}let wbNow=Date.now();let wbMains=wbCands.filter(function(wbC){return!/[\\\\/]subagents[\\\\/]/.test(wbC.p)}).sort(function(a,b){return b.st.mtimeMs-a.st.mtimeMs});let wbKeep=new Set;for(let wbC of wbMains.slice(0,wbK))wbKeep.add(wbC.p);let wbRemoved=0;for(let wbC of wbMains){if(wbKeep.has(wbC.p))continue;if(wbC.st.mtimeMs>wbNow-18e5)continue;try{wbFs.unlinkSync(wbC.p);wbRemoved++;let wbMeta=wbC.p.replace(/\\.jsonl$/,".meta.json");try{wbFs.unlinkSync(wbMeta)}catch(wbE2){}let wbDir=wbC.p.replace(/\\.jsonl$/,"");try{wbFs.rmSync(wbDir,{recursive:!0,force:!0})}catch(wbE2){}}catch(wbE2){}}if(wbRemoved>0)console.log("[WB_PRUNE] 已清理",wbRemoved,"个旧会话文件 (保留最近",wbK,"个, 可用 WB_KEEP_SESSIONS 调整)")}catch(wbE2){}finally{globalThis.wbPruneAt=Date.now()}})()},3e3)}}catch(wbE2){}`;

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
    return `markCompactionComplete(${sig}){${PATCHED_MARK}${PRUNE}`;
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
  console.log(`已补丁: ${file} (markCompactionComplete 入口注入会话清理器 ${hits} 处)`);
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

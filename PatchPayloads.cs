// PatchPayloads.cs —— 由 _gen_payloads.js 自动生成, 请勿手改
// 来源: patch_compact_free_memory.js / patch_load_boundary_stop.js / patch_session_prune.js
// 重新生成: node _gen_payloads.js && build.bat
namespace WorkBuddyPatcher
{
    public static class Payloads
    {
        // 锚点正则(与 JS 脚本同源)
        public const string Pat1 = "markCompactionComplete\\((\\w+)(?:,(\\w+)=\"([^\"]*)\")?\\)\\{";
        public const string Pat2 = "if\\((\\w+)\\.push\\((\\w+)\\),void 0!==(\\w+)\\)\\{if\\(\\1\\.length>=(\\w+)\\)return void (\\w+)\\(!1\\)\\}else if\\(\\1\\.length>=\\4&&\"message\"===\\2\\.type&&\"user\"===\\2\\.role\\|\\|\\1\\.length>\\4\\+100\\)return void \\5\\(!1\\)\\}";

        // 幂等标记
        public const string Mark1 = "/*WB_COMPACT_FREE*/";
        public const string Mark2 = "/*WB_LOAD_TRIM*/";
        public const string Mark3 = "/*WB_SESSION_PRUNE*/";

        // 备份后缀
        public const string Bak1 = ".compactfree.bak";
        public const string Bak2 = ".loadtrim.bak";
        public const string Bak3 = ".prune.bak";
        public const string Bak4 = ".1mcontext.bak";

        // 补丁一注入体 (@@P@@ = markCompactionComplete 首参名)
        public const string Tpl1 = @"try{let wbH=@@P@@&&Array.isArray(@@P@@.history)?@@P@@.history:null;if(wbH&&wbH.length>1){let wbB=-1;for(let wbI=wbH.length-1;wbI>=0;wbI--){let wbP=wbH[wbI]&&wbH[wbI].providerData;if(wbP&&(wbP.isCompacted===!0||wbP.isSummary===!0)){wbB=wbI;break}}if(wbB>0){wbH.splice(0,wbB);try{if(typeof globalThis.gc===""function"")globalThis.gc();else if(typeof require===""function""){let wbVm=require(""vm""),wbV8=require(""v8"");wbV8.setFlagsFromString(""--expose-gc"");wbVm.runInNewContext(""gc"")()}}catch(wbE){}}}}catch(wbE){}";

        // 补丁二注入体 (@@A@@=数组 @@I@@=条目 @@L@@=limit @@M@@=max @@D@@=done)
        public const string Tpl2 = @"if(@@A@@.push(@@I@@),@@I@@&&@@I@@.providerData&&(@@I@@.providerData.isCompacted===!0||@@I@@.providerData.isSummary===!0||@@I@@.providerData.isSessionSeparator===!0))return @@D@@(!1),/*WB_LOAD_TRIM*/(function(){try{if(typeof globalThis.gc===""function"")return void globalThis.gc();if(typeof require===""function""){let wbV8=require(""v8""),wbVm=require(""vm"");wbV8.setFlagsFromString(""--expose-gc""),wbVm.runInNewContext(""gc"")()}}catch(wbE){}})(),void 0;if(void 0!==@@L@@){if(@@A@@.length>=@@M@@)return void @@D@@(!1)}else if(@@A@@.length>=@@M@@&&""message""===@@I@@.type&&""user""===@@I@@.role||@@A@@.length>@@M@@+100)return void @@D@@(!1)}";

        // 补丁三注入体 (无参数)
        public const string Tpl3 = @"try{if(!globalThis.wbPruneAt||Date.now()-globalThis.wbPruneAt>6e5){globalThis.wbPruneAt=Date.now();setTimeout(function(){(async function(){try{let wbOs=require(""os""),wbFs=require(""fs""),wbPth=require(""path"");let wbK=parseInt(process.env.WB_KEEP_SESSIONS||""5"",10);if(wbK===0)return;if(!(wbK>0))wbK=5;let wbCands=[];for(let wbHb of["".workbuddy-ai"","".workbuddy""]){let wbRoot=wbPth.join(wbOs.homedir(),wbHb,""projects"");let wbSt=wbFs.existsSync(wbRoot)&&wbFs.statSync(wbRoot);if(!wbSt||!wbSt.isDirectory())continue;(function wbScan(wbD){for(let wbE of wbFs.readdirSync(wbD,{withFileTypes:!0})){let wbP=wbPth.join(wbD,wbE.name);if(wbE.isDirectory())wbScan(wbP);else if(wbE.name.endsWith("".jsonl"")){try{wbCands.push({p:wbP,st:wbFs.statSync(wbP)})}catch(wbE2){}}}})(wbRoot)}let wbNow=Date.now();let wbMains=wbCands.filter(function(wbC){return!/[\\/]subagents[\\/]/.test(wbC.p)}).sort(function(a,b){return b.st.mtimeMs-a.st.mtimeMs});let wbKeep=new Set;for(let wbC of wbMains.slice(0,wbK))wbKeep.add(wbC.p);let wbRemoved=0;for(let wbC of wbMains){if(wbKeep.has(wbC.p))continue;if(wbC.st.mtimeMs>wbNow-18e5)continue;try{wbFs.unlinkSync(wbC.p);wbRemoved++;let wbMeta=wbC.p.replace(/\.jsonl$/,"".meta.json"");try{wbFs.unlinkSync(wbMeta)}catch(wbE2){}let wbDir=wbC.p.replace(/\.jsonl$/,"""");try{wbFs.rmSync(wbDir,{recursive:!0,force:!0})}catch(wbE2){}}catch(wbE2){}}if(wbRemoved>0)console.log(""[WB_PRUNE] 已清理"",wbRemoved,""个旧会话文件 (保留最近"",wbK,""个, 可用 WB_KEEP_SESSIONS 调整)"")}catch(wbE2){}finally{globalThis.wbPruneAt=Date.now()}})()},3e3)}}catch(wbE2){}";

        // 补丁四注入体 (@@P@@ = isBudget 首参名)
        public const string Tpl4 = @"isBudget(@@P@@.overrideContextWindow)?@@P@@.overrideContextWindow:isBudget(1e6)?1e6:isBudget(@@P@@.contextWindow?.defaultLength)";
        public const string Mark4 = ":isBudget(1e6)?1e6:isBudget(";
        public const string Pat4 = "isBudget\\((\\w+)\\.overrideContextWindow\\)\\?\\1\\.overrideContextWindow:isBudget\\(\\1\\.contextWindow\\?\\.defaultLength\\)";

        // 补丁六删除型锚点 (整体替换为 "0"); Mark6 = 消息文本, 已应用 = 文件中不存在
        public const string Pat6 = "[\\w$.]*\\.(?:warn|debug)\\?\\.\\(\"(?:\\[ACP StreamManager\\] sendToClient: Standalone SSE is closed, cannot send notification)\"\\)";
        public const string Mark6 = "sendToClient: Standalone SSE is closed";
        public const string Bak6 = ".ssespam.bak";

        // 补丁七轮转脚本全文 (应用时写入 exe 目录并注册计划任务, --keep-days 由 UI 传入)
        public const string RotateScriptJs = @"// rotate_sdk_logs.js —— WorkBuddy SDK 会话日志按大小轮转 + 历史清理
//
// 背景:
//   桌面端把 SDK 会话日志写到 <home>/.workbuddy*/logs/<yyyy-MM-dd>/sdk/conversations/<uuid>.log,
//   追加不收缩, 单日单文件实测 1.1GB。应用内置组件:
//     * ConversationLogDispatcher(写入方, app.asar 内, 签名不可改): 每次用 fs.appendFile
//       追加(不长期持句柄), 文件被改名后下一次写入自动重建原名文件 → 外部""改名即轮转""成立;
//       其 GLOBAL_MAX_LINES/GLOBAL_MAX_BYTES 等常量只是内存队列限制(超限丢行), 与磁盘无关,
//       且为硬编码、无环境变量/配置可改。
//     * ConversationLogArchiver(按天归档): 只在跨天时打包""昨天""为 conversations.zip 后删源,
//       用 adm_zip 全内存压缩 —— 遇到 780MB/1.1GB 的文件会崩溃(实测连续两天死在 writeZip
//       中途, 留下 lock 目录与 zip.tmp 半成品, 源文件不删); 且只重试""昨天"", 更早的日期永远
//       不会再被处理。
//
// 策略:
//   1. ""今天""目录: conversations/*.log 超过上限(默认 100MB, WB_SDK_LOG_MAX_MB)→ 改名
//      <uuid>.rot-<yyyyMMdd-HHmmss>.log; 每个会话只保留最近 K 个轮转段(默认 1,
//      WB_SDK_LOG_KEEP_SEGMENTS), 更旧的删除 → 单会话磁盘占用上限 ≈ 上限×(K+1)
//   2. 过期历史目录(默认 KEEP_DAYS=0, 即除今天外全删; WB_SDK_LOG_KEEP_DAYS 可保留 N 天):
//      删除 conversations 内全部 *.log(内置 zip 已证实不可靠, 不留档), 目录清空后移除,
//      并清理 mtime 超过 5 分钟的残留 conversations.zip.tmp-* 与 conversations.zip.lock
//      (5 分钟对齐应用内 ARCHIVE_LOCK_STALE_MS, 避免碰正在进行的归档)
//   3. 扫描根目录: ~/.workbuddy-ai/logs, ~/.workbuddy/logs, E:\soft\WorkBuddy\.workbuddy-ai\logs,
//      E:\soft\WorkBuddy\.workbuddy\logs, realpath 去重(兼容 C 盘 junction);
//      可用 WB_SDK_LOG_ROOTS=""路径1;路径2"" 覆盖
//   4. 有动作时追加到本脚本旁 rotate_sdk_logs.log(超过 64KB 截断头部)
//
// 计划任务(每 10 分钟, 当前用户):
//   schtasks /Create /F /TN ""WorkBuddySdkLogRotate"" /SC MINUTE /MO 10
//     /TR ""\""<node.exe>\"" \""E:\GitHub\workbuddy\rotate_sdk_logs.js\""""
//   删除: schtasks /Delete /TN ""WorkBuddySdkLogRotate"" /F
'use strict';
const fs = require('fs');
const os = require('os');
const path = require('path');

const MAX_BYTES = Math.max(1, parseInt(process.env.WB_SDK_LOG_MAX_MB || '100', 10)) * 1024 * 1024;
const KEEP_SEGS = Math.max(0, parseInt(process.env.WB_SDK_LOG_KEEP_SEGMENTS || '1', 10));
// 保留天数: 命令行 --keep-days N 优先, 其次环境变量 WB_SDK_LOG_KEEP_DAYS, 默认 0(仅保留当天)
const KEEP_DAYS = (() => {
  const a = process.argv;
  const i = a.indexOf('--keep-days');
  const v = i >= 0 ? parseInt(a[i + 1], 10) : NaN;
  return Math.max(0, parseInt(Number.isFinite(v) ? v : (process.env.WB_SDK_LOG_KEEP_DAYS || '0'), 10));
})();
const STALE_MS = 5 * 60 * 1000; // 对齐应用内 ARCHIVE_LOCK_STALE_MS
const TODAY = (() => {
  const d = new Date(), p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
})();
const EXPIRED_BEFORE = (() => { // 早于该日期(含当天)的历史目录视为过期
  const [y, m, d] = TODAY.split('-').map(Number);
  return new Date(y, m - 1, d - KEEP_DAYS);
})();
const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;
const ACTIVE_RE = /^[0-9a-fA-F-]{36}\.log$/; // 活动文件: <uuid>.log
const ROT_RE = /^(.+)\.rot-\d{8}-\d{6}\.log$/; // 轮转段: <uuid>.rot-<ts>.log

const actions = [];
function note(msg) { actions.push(msg); console.log(msg); }

function defaultRoots() {
  const home = os.homedir();
  return [
    path.join(home, '.workbuddy-ai', 'logs'),
    path.join(home, '.workbuddy', 'logs'),
    'E:\\soft\\WorkBuddy\\.workbuddy-ai\\logs',
    'E:\\soft\\WorkBuddy\\.workbuddy\\logs',
  ];
}

function resolveRoots() {
  const raw = (process.env.WB_SDK_LOG_ROOTS || defaultRoots().join(';')).split(';');
  const seen = new Set(), out = [];
  for (const r of raw) {
    const t = r.trim();
    if (!t || !fs.existsSync(t)) continue;
    let real;
    try { real = fs.realpathSync(t); } catch { continue; }
    if (seen.has(real)) continue;
    seen.add(real);
    out.push(real);
  }
  return out;
}

function sleepSync(ms) { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); }

// 历史过期目录: 删除全部文件(不限扩展名, 会话日志旁可能残留 .swp.ndd 等交换文件),
// 清空后移除目录, 清理过期 lock/tmp 残留
function purgePastConversations(convDir, sdkDir) {
  let names;
  try { names = fs.readdirSync(convDir); } catch { return; }
  for (const name of names) {
    try {
      const full = path.join(convDir, name);
      const size = fs.statSync(full).size;
      try {
        fs.unlinkSync(full);
      } catch (e) {
        if (e.code !== 'EPERM') throw e;
        fs.chmodSync(full, 0o666); // 只读属性会导致 EPERM, 清除后重试一次
        fs.unlinkSync(full);
      }
      note(`清理历史 ${convDir}\\${name} (${(size / 1048576).toFixed(0)}MB)`);
    } catch (e) {
      note(`清理历史失败 ${convDir}\\${name}: ${e.message}`);
    }
  }
  try {
    if (fs.readdirSync(convDir).length === 0) fs.rmdirSync(convDir);
  } catch { }
  // 清理过期的归档残留(zip 半成品 / lock 目录); mtime 新于 5 分钟视为归档仍在进行
  let sdkEntries;
  try { sdkEntries = fs.readdirSync(sdkDir); } catch { return; }
  for (const name of sdkEntries) {
    const full = path.join(sdkDir, name);
    let st;
    try { st = fs.statSync(full); } catch { continue; }
    const isTmp = /^conversations\.zip\.tmp-/.test(name) && st.isFile();
    const isLock = name === 'conversations.zip.lock' && st.isDirectory();
    if (!isTmp && !isLock) continue;
    if (Date.now() - st.mtimeMs < STALE_MS) continue;
    try {
      fs.rmSync(full, { recursive: true, force: true });
      note(`清理归档残留 ${full}`);
    } catch (e) {
      note(`清理归档残留失败 ${full}: ${e.message}`);
    }
  }
}

// 今天目录: 轮转超限活动文件 + 按会话保留 K 段
function processTodayConversations(dir) {
  let names;
  try { names = fs.readdirSync(dir); } catch { return; }
  const stats = new Map(); // name -> size
  for (const name of names) {
    if (!/\.log$/.test(name)) continue;
    try {
      const st = fs.statSync(path.join(dir, name));
      if (st.isFile()) stats.set(name, st.size);
    } catch { }
  }
  // 1) 轮转超限的活动文件(偶发 EPERM: 写入方正 append / 杀毒扫描, 短重试 3 次)
  for (const [name, size] of stats) {
    if (!ACTIVE_RE.test(name) || size <= MAX_BYTES) continue;
    const d = new Date(), p = (n) => String(n).padStart(2, '0');
    const ts = `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
    const target = path.join(dir, name.replace(/\.log$/, '') + `.rot-${ts}.log`);
    let done = false;
    for (let attempt = 0; attempt < 3 && !done; attempt++) {
      try {
        if (attempt > 0) sleepSync(2000);
        fs.renameSync(path.join(dir, name), target);
        done = true;
      } catch (e) {
        if (attempt === 2) note(`轮转失败 ${dir}\\${name}: ${e.message}`);
      }
    }
    if (done) {
      note(`轮转 ${dir}\\${name} (${(size / 1048576).toFixed(0)}MB)`);
      stats.delete(name);
      stats.set(path.basename(target), size);
    }
  }
  // 2) 每个会话只保留最近 K 个轮转段(按文件名内时间戳倒序 = 字典序倒序)
  const byBase = new Map();
  for (const name of stats.keys()) {
    const m = ROT_RE.exec(name);
    if (!m) continue;
    const base = m[1];
    if (!byBase.has(base)) byBase.set(base, []);
    byBase.get(base).push(name);
  }
  for (const [base, segs] of byBase) {
    segs.sort();
    for (const old of segs.slice(0, Math.max(0, segs.length - KEEP_SEGS))) {
      try {
        fs.unlinkSync(path.join(dir, old));
        note(`清理旧段 ${dir}\\${old} (${(stats.get(old) / 1048576).toFixed(0)}MB)`);
      } catch (e) {
        note(`清理失败 ${dir}\\${old}: ${e.message}`);
      }
    }
  }
}

function main() {
  let hits = 0;
  for (const logsRoot of resolveRoots()) {
    let dates;
    try { dates = fs.readdirSync(logsRoot); } catch { continue; }
    for (const date of dates) {
      if (!DATE_RE.test(date)) continue;
      const sdkDir = path.join(logsRoot, date, 'sdk');
      const convDir = path.join(sdkDir, 'conversations');
      if (!fs.existsSync(convDir)) continue;
      hits++;
      if (date === TODAY) processTodayConversations(convDir);
      else {
        const [y, m, d] = date.split('-').map(Number);
        if (new Date(y, m - 1, d) < EXPIRED_BEFORE) purgePastConversations(convDir, sdkDir);
      }
    }
  }
  if (hits === 0) console.log(`[${TODAY}] 未找到待处理的 conversations 目录`);
  // 有动作时落一行摘要(供计划任务留痕), 超 64KB 截头
  if (actions.length) {
    try {
      const logFile = path.join(__dirname, 'rotate_sdk_logs.log');
      let prev = '';
      try { prev = fs.readFileSync(logFile, 'utf8'); } catch { }
      const line = `[${new Date().toISOString()}] ${actions.length} 项: ${actions.join(' | ')}\n`;
      let next = (prev + line).split('\n');
      if (next.join('\n').length > 65536) next = next.slice(Math.ceil(next.length / 2));
      fs.writeFileSync(logFile, next.join('\n'), 'utf8');
    } catch { }
  }
}

main();
";
    }
}

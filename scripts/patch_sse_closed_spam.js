// patch_sse_closed_spam.js —— 屏蔽 [ACP StreamManager] "Standalone SSE is closed" 刷屏日志
// 用法: node patch_sse_closed_spam.js [目标文件/目录...]
//   不带参数: 自动枚举 C:\Program Files\WorkBuddy* 全部安装下的
//   resources\app.asar.unpacked\cli\dist\codebuddy*.(js|mjs)
//
// 背景:
//   headless CLI 的 ACP StreamManager 向桌面端推送通知时, 若 standalone SSE
//   通道已关闭(客户端断开/会话结束后仍有通知产生), 每次尝试都记一条
//   Warning: "sendToClient: Standalone SSE is closed, cannot send notification"。
//   通道持续关闭时以毫秒级频率刷屏 —— 实测单日单文件 165 万条, 占该日志
//   体积约 80%(376MB 文件)。
//
// 原理:
//   全 bundle 仅此一处调用点。把整条 logger.warn?/debug?.(...) 调用替换为
//   void 0, 彻底移除 —— 任何级别都不再产生该日志。三元表达式退化为
//   ?void 0: 语法不变。
//   注意: 降级方案(info/debug)都不够 —— Info 会落盘(165 万条照样写满),
//   debug 虽不落盘但按需求"不需要任何日志", 故直接删除调用。
//
// 代价(务必知晓):
//   - 该警告不再出现在日志文件中, SSE 推送失败只能靠相邻的 error 日志判断
//   - 锚点为消息文本特征, 产品更新后重跑即可
//   - 首次修改前生成 <file>.ssespam.bak(已存在则不覆盖)
//   - 幂等: 已补丁文件报告「已补丁, 跳过」; 补丁后需重启 WorkBuddy 生效
'use strict';
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

// 锚点连同接收者一起消费(this.logger.warn?/debug?.(...)), 替换为 0,
// 使三元表达式退化为 ?void 0: ; 若只替换方法调用会留下悬空的 this.logger.0
const ANCHOR = /[\w$.]*\.(?:warn|debug)\?\.\("(?:\[ACP StreamManager\] sendToClient: Standalone SSE is closed, cannot send notification)"\)/g;
const MSG_MARK = 'sendToClient: Standalone SSE is closed';
const BAK_SUFFIX = '.ssespam.bak';

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
  if (!s.includes(MSG_MARK)) {
    console.log(`跳过(已补丁): ${file}`);
    return { file, status: 'already' };
  }
  ANCHOR.lastIndex = 0;
  let hits = 0;
  const out = s.replace(ANCHOR, () => {
    hits++;
    return '0';
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
  console.log(`已补丁: ${file} (移除该日志调用 ${hits} 处)`);
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

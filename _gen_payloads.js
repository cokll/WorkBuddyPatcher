// _gen_payloads.js —— 从三个 JS 补丁脚本提取注入体/正则/标记, 生成 PatchPayloads.cs
// 保证 C# 工具与 JS 脚本注入内容字节级一致; 重新生成: node _gen_payloads.js
'use strict';
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const ROOT = __dirname;
const read = (f) => fs.readFileSync(path.join(ROOT, f), 'utf8');
// 优先读仓库内 scripts/, 其次上级目录(兼容本地工作区布局)
const readScript = (name) => read(fs.existsSync(path.join(ROOT, 'scripts', name)) ? path.join('scripts', name) : path.join('..', name));
const sha = (s) => crypto.createHash('sha256').update(s, 'utf8').digest('hex').slice(0, 16);

// ---- 补丁一: patch_compact_free_memory.js ----
const s1 = readScript('patch_compact_free_memory.js');
function tplOf(src, head) {
  const i = src.indexOf(head);
  if (i < 0) throw new Error('未找到: ' + head);
  const t1 = src.indexOf('`', i) + 1;
  const t2 = src.indexOf('`;', t1);
  if (t2 < 0) throw new Error('模板未闭合: ' + head);
  return src.slice(t1, t2);
}
const Tpl1 = new Function('P', 'return `' + tplOf(s1, 'const FREE_MEM = (P) => `') + '`')('@@P@@');
const Mark1 = /const PATCHED_MARK = '([^']*)'/.exec(s1)[1];
const Pat1 = /const ANCHOR = \/(.+)\/g;/.exec(s1)[1];
const Bak1 = /const BAK_SUFFIX = '([^']*)'/.exec(s1)[1];

// ---- 补丁二: patch_load_boundary_stop.js ----
const s2 = readScript('patch_load_boundary_stop.js');
const Mark2 = /const PATCHED_MARK = '([^']*)'/.exec(s2)[1];
const Pat2 = /const ANCHOR = \/(.+)\/g;/.exec(s2)[1];
const Bak2 = /const BAK_SUFFIX = '([^']*)'/.exec(s2)[1];
const GCExpr = /const GC_EXPR = '(.*)';/.exec(s2)[1];
{
  const i = s2.indexOf('return `if(');
  if (i < 0) throw new Error('补丁二模板未找到');
  const t1 = s2.indexOf('`', i) + 1;
  const t2 = s2.indexOf('`;', t1);
  var Tpl2 = new Function('arr,item,lim,max,done,PATCHED_MARK,GC_EXPR', 'return `' + s2.slice(t1, t2) + '`')(
    '@@A@@', '@@I@@', '@@L@@', '@@M@@', '@@D@@', Mark2, GCExpr);
}

// ---- 补丁三: patch_session_prune.js ----
const s3 = readScript('patch_session_prune.js');
const Mark3 = /const PATCHED_MARK = '([^']*)'/.exec(s3)[1];
const Bak3 = /const BAK_SUFFIX = '([^']*)'/.exec(s3)[1];
const Tpl3 = new Function('return `' + tplOf(s3, 'const PRUNE = `') + '`')();
if (Pat1 !== /const ANCHOR = \/(.+)\/g;/.exec(s3)[1]) throw new Error('补丁一/三锚点不一致');

// ---- 补丁四: set_default_context_1m.js ----
const s4 = readScript('set_default_context_1m.js');
const Mark4 = /const PATCHED_MARK = '([^']*)'/.exec(s4)[1];
const Pat4 = /const ANCHOR = \/(.+)\/g;/.exec(s4)[1];
const Bak4 = /const BAK_SUFFIX = '([^']*)'/.exec(s4)[1];
{
  const i = s4.indexOf('return `isBudget(');
  if (i < 0) throw new Error('补丁四模板未找到');
  const t1 = s4.indexOf('`', i) + 1;
  const t2 = s4.indexOf('`;', t1);
  if (t2 < 0) throw new Error('补丁四模板未闭合');
  var Tpl4 = new Function('p1', 'return `' + s4.slice(t1, t2) + '`')('@@P@@');
}

// ---- 补丁六: patch_sse_closed_spam.js (删除型: 锚点整体替换为 0, 无注入体) ----
const s6 = readScript('patch_sse_closed_spam.js');
const Mark6 = /const MSG_MARK = '([^']*)'/.exec(s6)[1];   // 已应用标记 = 该消息文本整体消失
const Pat6 = /const ANCHOR = \/(.+)\/g;/.exec(s6)[1];
const Bak6 = /const BAK_SUFFIX = '([^']*)'/.exec(s6)[1];

// ---- 补丁七: rotate_sdk_logs.js (任务型: 整个脚本内嵌, 应用时落盘 + 注册计划任务) ----
const RotScript = readScript('rotate_sdk_logs.js');

// ---- 校验注入体约束 ----
for (const [n, t] of [['Tpl1', Tpl1], ['Tpl2', Tpl2], ['Tpl3', Tpl3], ['Tpl4', Tpl4]]) {
  if (/\$\{/.test(t)) throw new Error(n + ' 含未求值的插值');
  if (/[\r\n]/.test(t)) throw new Error(n + ' 含换行');
  if (/`/.test(t)) throw new Error(n + ' 含反引号');
}

// ---- 生成 C# ----
const csStr = (s) => '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
const csVerbatim = (s) => '@"' + s.replace(/"/g, '""') + '"';

const out = `// PatchPayloads.cs —— 由 _gen_payloads.js 自动生成, 请勿手改
// 来源: patch_compact_free_memory.js / patch_load_boundary_stop.js / patch_session_prune.js
// 重新生成: node _gen_payloads.js && build.bat
namespace WorkBuddyPatcher
{
    public static class Payloads
    {
        // 锚点正则(与 JS 脚本同源)
        public const string Pat1 = ${csStr(Pat1)};
        public const string Pat2 = ${csStr(Pat2)};

        // 幂等标记
        public const string Mark1 = ${csStr(Mark1)};
        public const string Mark2 = ${csStr(Mark2)};
        public const string Mark3 = ${csStr(Mark3)};

        // 备份后缀
        public const string Bak1 = ${csStr(Bak1)};
        public const string Bak2 = ${csStr(Bak2)};
        public const string Bak3 = ${csStr(Bak3)};
        public const string Bak4 = ${csStr(Bak4)};

        // 补丁一注入体 (@@P@@ = markCompactionComplete 首参名)
        public const string Tpl1 = ${csVerbatim(Tpl1)};

        // 补丁二注入体 (@@A@@=数组 @@I@@=条目 @@L@@=limit @@M@@=max @@D@@=done)
        public const string Tpl2 = ${csVerbatim(Tpl2)};

        // 补丁三注入体 (无参数)
        public const string Tpl3 = ${csVerbatim(Tpl3)};

        // 补丁四注入体 (@@P@@ = isBudget 首参名)
        public const string Tpl4 = ${csVerbatim(Tpl4)};
        public const string Mark4 = ${csStr(Mark4)};
        public const string Pat4 = ${csStr(Pat4)};

        // 补丁六删除型锚点 (整体替换为 "0"); Mark6 = 消息文本, 已应用 = 文件中不存在
        public const string Pat6 = ${csStr(Pat6)};
        public const string Mark6 = ${csStr(Mark6)};
        public const string Bak6 = ${csStr(Bak6)};

        // 补丁七轮转脚本全文 (应用时写入 exe 目录并注册计划任务, --keep-days 由 UI 传入)
        public const string RotateScriptJs = ${csVerbatim(RotScript)};
    }
}
`;
fs.writeFileSync(path.join(ROOT, 'PatchPayloads.cs'), out, 'utf8');
console.log('已生成 PatchPayloads.cs');
console.log('  Pat1:', Pat1);
console.log('  Pat2:', Pat2.slice(0, 60) + '...');
console.log('  Pat4:', Pat4);
console.log('  Pat6:', Pat6);
console.log('  Mark6:', Mark6);
console.log('  RotateScriptJs:', RotScript.length, 'chars, sha:', sha(RotScript));
console.log('  指纹  Tpl1:', sha(Tpl1), ' Tpl2:', sha(Tpl2), ' Tpl3:', sha(Tpl3), ' Tpl4:', sha(Tpl4));
console.log('  指纹  GCExpr:', sha(GCExpr));

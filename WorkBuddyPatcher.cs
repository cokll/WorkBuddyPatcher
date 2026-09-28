// WorkBuddyPatcher.cs —— WorkBuddy 补丁管理器 (.NET Framework 4, WinForms + 控制台模式)
//
// 六个补丁(注入体与 JS 脚本同源, 见 PatchPayloads.cs, 由 _gen_payloads.js 生成):
//   一  压缩后物理截断边界前历史 + GC     dist 文本补丁  .compactfree.bak
//   二  会话重载在最新压缩边界停读 + GC    dist 文本补丁  .loadtrim.bak
//   三  压缩后清理旧会话文件(保留最近N个)  dist 文本补丁  .prune.bak
//   四  会话默认上下文预算优先 1M 档        dist 文本补丁  .1mcontext.bak
//   五  默认完全访问 permissions.defaultMode  设置文件配置  .permdefault.bak
//       (原 app.asar 原位补丁已废弃: asar 有签名不可修改; 改用 CLI 设置, 更新存活)
//   六  移除 ACP "Standalone SSE is closed" 刷屏日志(锚点整体替换为 0, 删除型)
//       dist 文本补丁  .ssespam.bak; 已应用标记 = 消息文本在文件中消失
//   七  SDK 会话日志轮转与历史清理(任务型, 与 dist 补丁无关):
//       内嵌 rotate_sdk_logs.js 写到 exe 目录 + 注册每 10 分钟计划任务
//       "WorkBuddySdkLogRotate"(今日 100MB 上限轮转; 历史目录按保留天数清理,
//       并清过期 lock/zip 残留)。应用=创建/更新任务(--keep-days 由 UI 传入),
//       还原=删除计划任务(停止轮转)。状态判定=计划任务是否存在。
//
// 控制台模式(黑窗修复: GUI 为 winexe 无控制台; 控制台模式经 AttachConsole 输出):
//   WorkBuddyPatcher.exe                       图形界面
//   WorkBuddyPatcher.exe --check [文件...]     查看状态
//   WorkBuddyPatcher.exe --console [文件...]   应用全部补丁
//   WorkBuddyPatcher.exe --restore [文件...]   从备份还原
//   WorkBuddyPatcher.exe --uitest              自检(构建UI+发现+状态后退出)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WorkBuddyPatcher
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();
        private const int ATTACH_PARENT_PROCESS = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                TryLogError(e.Exception);
                MessageBox.Show("发生异常: " + e.Exception.Message, "WorkBuddy 补丁管理器");
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                TryLogError(e.ExceptionObject as Exception);
            };

            if (args.Length > 0)
            {
                // winexe 无控制台: 附着父进程控制台输出(重定向管道亦有效)
                AttachConsole(ATTACH_PARENT_PROCESS);
                try
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                }
                catch { }

                string mode = args[0];
                List<string> files = new List<string>();
                for (int i = 1; i < args.Length; i++) files.Add(args[i]);
                int rc;
                switch (mode)
                {
                    case "--check": rc = Store.CheckAll(files); break;
                    case "--console": rc = Store.ApplyAll(files); break;
                    case "--restore": rc = Store.RestoreAll(files); break;
                    case "--uitest": rc = RunUiTest(); break;
                    default:
                        Console.WriteLine("用法: WorkBuddyPatcher.exe [--check|--console|--restore|--uitest] [文件...]");
                        rc = 1;
                        break;
                }
                Console.Out.Flush();
                FreeConsole();
                return rc;
            }
            Application.Run(new MainForm());
            return 0;
        }

        private static int RunUiTest()
        {
            MainForm f = new MainForm();
            return f.RunUiTest() > 0 ? 0 : 4;
        }

        private static void TryLogError(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "wbpatcher-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + Environment.NewLine);
            }
            catch { }
        }
    }

    public class PatchDef
    {
        public string Name;       // 显示名
        public string Kind;       // "text" | "settings"
        public string Mode;       // text: "sig"(签名前缀+注入) | "replace"(整体替换) | "delete"(锚点整体替换为 "0")
        public string Marker;
        public bool MarkerAbsent; // 已应用判定取反: 标记在文件中消失(删除型补丁, 如补丁六)
        public string BakSuffix;
        public Regex Anchor;
        public string Template;   // @@P@@ / @@A@@..@@D@@ 占位; delete 模式忽略
        public string Value = "fullAccess"; // settings: 期望的 defaultMode

        // 该补丁在给定文本上是否处于已应用状态
        public bool IsApplied(string text)
        {
            return MarkerAbsent ? !text.Contains(Marker) : text.Contains(Marker);
        }

        public string Build(Match m)
        {
            if (Mode == "sig")
            {
                string p = m.Groups[1].Value;
                string sig = m.Groups[2].Success
                    ? p + "," + m.Groups[2].Value + "=\"" + m.Groups[3].Value + "\""
                    : p;
                return "markCompactionComplete(" + sig + "){" + Marker + Template.Replace("@@P@@", p);
            }
            if (Mode == "delete") return "0";
            string t = Template;
            if (t.Contains("@@A@@"))
                t = t.Replace("@@A@@", m.Groups[1].Value)
                     .Replace("@@I@@", m.Groups[2].Value)
                     .Replace("@@L@@", m.Groups[3].Value)
                     .Replace("@@M@@", m.Groups[4].Value)
                     .Replace("@@D@@", m.Groups[5].Value);
            else if (t.Contains("@@P@@"))
                t = t.Replace("@@P@@", m.Groups[1].Value);
            return t;
        }
    }

    public class Target
    {
        public string Path;
        public string Kind; // "dist" | "settings"
        public Target(string path, string kind) { Path = path; Kind = kind; }
        public override string ToString() { return Path; }
    }

    public static class Store
    {
        public static readonly string[] FullAccessModes = new string[] { "fullAccess", "bypassPermissions" };
        public const string TaskName = "WorkBuddySdkLogRotate";

        public static readonly PatchDef[] Patches = new PatchDef[]
        {
            new PatchDef{ Name="补丁一 压缩截断+GC",     Kind="text", Mode="sig",
                Marker=Payloads.Mark1, BakSuffix=Payloads.Bak1, Anchor=new Regex(Payloads.Pat1), Template=Payloads.Tpl1 },
            new PatchDef{ Name="补丁二 重载停读+GC",     Kind="text", Mode="replace",
                Marker=Payloads.Mark2, BakSuffix=Payloads.Bak2, Anchor=new Regex(Payloads.Pat2), Template=Payloads.Tpl2 },
            new PatchDef{ Name="补丁三 会话文件清理",    Kind="text", Mode="sig",
                Marker=Payloads.Mark3, BakSuffix=Payloads.Bak3, Anchor=new Regex(Payloads.Pat1), Template=Payloads.Tpl3 },
            new PatchDef{ Name="补丁四 默认1M上下文",    Kind="text", Mode="replace",
                Marker=Payloads.Mark4, BakSuffix=Payloads.Bak4, Anchor=new Regex(Payloads.Pat4), Template=Payloads.Tpl4 },
            new PatchDef{ Name="补丁五 默认完全访问",    Kind="settings", Mode="replace",
                Marker="", BakSuffix=".permdefault.bak", Anchor=null, Template="" },
            new PatchDef{ Name="补丁六 SSE刷屏日志移除", Kind="text", Mode="delete",
                Marker=Payloads.Mark6, MarkerAbsent=true, BakSuffix=Payloads.Bak6,
                Anchor=new Regex(Payloads.Pat6), Template="0" },
            new PatchDef{ Name="补丁七 SDK日志轮转",     Kind="task", Mode="replace",
                Marker=TaskName, BakSuffix="", Anchor=null, Template="", Value="0" },
        };

        public static List<Target> Discover()
        {
            List<Target> list = new List<Target>();
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                foreach (string dir in Directory.GetDirectories(pf, "WorkBuddy*"))
                {
                    string dist = Path.Combine(dir, "resources", "app.asar.unpacked", "cli", "dist");
                    if (!Directory.Exists(dist)) continue;
                    foreach (string f in Directory.GetFiles(dist, "codebuddy*"))
                    {
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".js" || ext == ".mjs") list.Add(new Target(f, "dist"));
                    }
                }
            }
            catch { }
            string home = "";
            try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
            if (home != null && home.Length > 0)
            {
                foreach (string hb in new string[] { ".workbuddy-ai", ".workbuddy" })
                    list.Add(new Target(Path.Combine(home, hb, "settings.json"), "settings"));
            }
            list.Add(new Target("计划任务 " + TaskName, "task"));
            list.Sort(delegate(Target a, Target b) { return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static string ReadText(string file, out bool hadBom)
        {
            byte[] b = File.ReadAllBytes(file);
            hadBom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            int off = hadBom ? 3 : 0;
            return new UTF8Encoding(false).GetString(b, off, b.Length - off);
        }

        public static void WriteText(string file, string s, bool bom)
        {
            UTF8Encoding e = new UTF8Encoding(false);
            byte[] body = e.GetBytes(s);
            if (bom)
            {
                byte[] head = e.GetPreamble();
                byte[] all = new byte[head.Length + body.Length];
                Array.Copy(head, all, head.Length);
                Array.Copy(body, 0, all, head.Length, body.Length);
                File.WriteAllBytes(file, all);
            }
            else
            {
                File.WriteAllBytes(file, body);
            }
        }

        // ---- dist 文本补丁 ----
        // status: "already" / "no-anchor" / "patched"
        public static string ApplyText(string file, PatchDef pd, out string detail)
        {
            bool bom;
            string text = ReadText(file, out bom);
            if (pd.IsApplied(text)) { detail = "已应用, 跳过"; return "already"; }
            MatchCollection mc = pd.Anchor.Matches(text);
            if (mc.Count == 0) { detail = "未找到锚点(产品可能已更新, 请反馈新特征)"; return "no-anchor"; }
            string bak = file + pd.BakSuffix;
            detail = "";
            if (File.Exists(bak))
            {
                // 备份应为无补丁的原始版本; 若产品已更新(当前无任何补丁处于已应用状态且与备份不同), 刷新备份到当前版本
                bool bakBom;
                string bakText = ReadText(bak, out bakBom);
                bool curHasAnyMarker = false;
                foreach (PatchDef p in Patches)
                {
                    if (p.Kind == "text" && p.IsApplied(text)) { curHasAnyMarker = true; break; }
                }
                if (!curHasAnyMarker && bakText != text)
                {
                    File.Copy(file, bak, true);
                    detail = "检测到产品更新, 备份已刷新为当前原始版本; ";
                }
            }
            else
            {
                File.Copy(file, bak);
                detail = "备份: " + bak + "; ";
            }
            int hits = mc.Count;
            string outText = pd.Anchor.Replace(text, delegate(Match m) { return pd.Build(m); });
            WriteText(file, outText, bom);
            detail += "注入 " + hits + " 处";
            return "patched";
        }

        // ---- settings 配置补丁(纯 JSON 文本编辑, 保留原格式; 拒绝 asar 等二进制修改) ----
        public static string CurrentDefaultMode(string file)
        {
            try
            {
                bool bom;
                string text = ReadText(file, out bom);
                Match m = Regex.Match(text, "\"defaultMode\"\\s*:\\s*\"([^\"]*)\"");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch { return ""; }
        }

        // status: "already" / "no-anchor"(无法安全编辑) / "patched"
        public static string ApplySettings(string file, PatchDef pd, out string detail)
        {
            detail = "";
            bool exists = File.Exists(file);
            if (!exists)
            {
                string dir = Path.GetDirectoryName(file);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                WriteText(file, "{\n  \"permissions\": { \"defaultMode\": \"" + pd.Value + "\" }\n}\n", false);
                detail = "创建设置文件并写入; ";
                return "patched";
            }
            string bak = file + pd.BakSuffix;
            if (!File.Exists(bak)) { File.Copy(file, bak); detail = "备份: " + bak + "; "; }
            bool bom2;
            string text2 = ReadText(file, out bom2);
            string cur = CurrentDefaultMode(file);
            foreach (string ok in FullAccessModes)
            {
                if (cur == ok)
                {
                    if (cur == pd.Value) { detail += "已是 " + cur + ", 跳过"; return "already"; }
                    detail += "当前 " + cur + " 已属完全访问类, 保留不改动";
                    return "already";
                }
            }
            string outText;
            if (cur.Length > 0)
            {
                outText = new Regex("(\"defaultMode\"\\s*:\\s*)\"[^\"]*\"").Replace(text2, delegate(Match m)
                    { return m.Groups[1].Value + "\"" + pd.Value + "\""; }, 1);
            }
            else if (Regex.IsMatch(text2, "\"permissions\"\\s*:\\s*\\{"))
            {
                outText = new Regex("(\"permissions\"\\s*:\\s*\\{)").Replace(text2, delegate(Match m)
                    { return m.Groups[1].Value + " \"defaultMode\": \"" + pd.Value + "\","; }, 1);
            }
            else
            {
                int brace = text2.IndexOf('{');
                if (brace < 0) { detail += "设置文件结构异常, 拒绝修改"; return "no-anchor"; }
                outText = text2.Substring(0, brace + 1) + "\n  \"permissions\": { \"defaultMode\": \"" + pd.Value + "\" }," + text2.Substring(brace + 1);
            }
            // 写回前验证仍是合法 JSON
            if (!IsValidJson(outText)) { detail += "编辑后 JSON 校验失败, 已放弃"; return "no-anchor"; }
            WriteText(file, outText, bom2);
            detail += "defaultMode → " + pd.Value;
            return "patched";
        }

        private static bool IsValidJson(string s)
        {
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                ser.DeserializeObject(s);
                return true;
            }
            catch { return false; }
        }

        // ---- task 任务补丁(补丁七): 落盘轮转脚本 + 注册/更新计划任务 ----
        private static string RunCapture(string exe, string args, out int exitCode)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    exitCode = p.ExitCode;
                    return ((so + " " + se).Trim());
                }
            }
            catch (Exception ex) { exitCode = -1; return ex.Message; }
        }

        public static bool TaskExists()
        {
            int rc;
            RunCapture("schtasks.exe", "/Query /TN " + TaskName, out rc);
            return rc == 0;
        }

        public static string FindNode()
        {
            string[] candidates = new string[]
            {
                "C:\\Program Files\\nodejs\\node.exe",
                "E:\\soft\\WorkBuddy\\.workbuddy\\binaries\\node\\node.exe",
            };
            foreach (string c in candidates) if (File.Exists(c)) return c;
            int rc;
            string where = RunCapture("where.exe", "node", out rc);
            if (rc == 0)
            {
                foreach (string line in where.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(t)) return t;
                }
            }
            return null;
        }

        // status: "already" / "no-anchor" / "patched"
        public static string ApplyTask(PatchDef pd, out string detail)
        {
            string node = FindNode();
            if (node == null) { detail = "未找到 node.exe, 无法注册轮转计划任务"; return "no-anchor"; }
            string script = Path.Combine(Application.StartupPath, "rotate_sdk_logs.js");
            try { File.WriteAllText(script, Payloads.RotateScriptJs, new UTF8Encoding(false)); }
            catch (Exception ex) { detail = "写入轮转脚本失败: " + ex.Message; return "no-anchor"; }
            int days;
            if (!int.TryParse(pd.Value, out days) || days < 0) days = 0;
            string tr = "\\\"" + node + "\\\" \\\"" + script + "\\\" --keep-days " + days;
            int rc;
            string output = RunCapture("schtasks.exe",
                "/Create /F /TN " + TaskName + " /SC MINUTE /MO 10 /TR \"" + tr + "\"", out rc);
            if (rc != 0) { detail = "创建计划任务失败: " + output; return "no-anchor"; }
            detail = "计划任务已写入: 每 10 分钟, 今日日志 100MB 轮转, 历史保留 " + days + " 天";
            return "patched";
        }

        public static string RestoreTask(out string detail)
        {
            if (!TaskExists()) { detail = "计划任务不存在, 无需还原"; return "already"; }
            int rc;
            string output = RunCapture("schtasks.exe", "/Delete /F /TN " + TaskName, out rc);
            if (rc != 0) { detail = "删除计划任务失败: " + output; return "no-anchor"; }
            detail = "计划任务已删除(轮转停止); 脚本保留在 exe 目录";
            return "patched";
        }

        public static string ApplyOne(string file, string kind, PatchDef pd, out string detail)
        {
            if (kind == "settings") return ApplySettings(file, pd, out detail);
            if (kind == "task") return ApplyTask(pd, out detail);
            return ApplyText(file, pd, out detail);
        }

        public static bool HasBackup(string file, PatchDef pd) { return File.Exists(file + pd.BakSuffix); }

        public static string[] StatusOf(Target t)
        {
            string[] res = new string[Patches.Length];
            for (int i = 0; i < Patches.Length; i++) res[i] = "—";
            if (t.Kind == "task")
            {
                for (int i = 0; i < Patches.Length; i++)
                    if (Patches[i].Kind == "task") res[i] = TaskExists() ? "✔ 已应用" : "未应用";
                return res;
            }
            if (t.Kind == "dist")
            {
                bool bom;
                string text;
                try { text = ReadText(t.Path, out bom); }
                catch (Exception)
                {
                    string msg = File.Exists(t.Path) ? "读取失败" : "文件缺失";
                    for (int i = 0; i < Patches.Length; i++)
                        if (Patches[i].Kind == "text") res[i] = msg;
                    return res;
                }
                for (int i = 0; i < Patches.Length; i++)
                {
                    PatchDef p = Patches[i];
                    if (p.Kind != "text") continue;
                    if (p.IsApplied(text)) res[i] = "✔ 已应用";
                    else if (p.Anchor != null && p.Anchor.IsMatch(text)) res[i] = "未应用";
                    else res[i] = "⚠ 无锚点";
                }
            }
            else
            {
                for (int i = 0; i < Patches.Length; i++)
                {
                    if (Patches[i].Kind != "settings") continue;
                    if (!File.Exists(t.Path)) { res[i] = "未创建"; continue; }
                    string cur = CurrentDefaultMode(t.Path);
                    bool done = false;
                    foreach (string ok in FullAccessModes)
                    {
                        if (cur == ok) { res[i] = "✔ 已应用(" + cur + ")"; done = true; break; }
                    }
                    if (!done) res[i] = cur.Length > 0 ? "未应用(当前: " + cur + ")" : "未应用";
                }
            }
            return res;
        }

        public static string NodeCheck(string file)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("node.exe", "--check \"" + file + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return "node --check 超时"; }
                    if (p.ExitCode == 0) return "语法校验通过";
                    return "语法校验失败: " + (se.Length > 400 ? se.Substring(0, 400) : se);
                }
            }
            catch (System.ComponentModel.Win32Exception) { return "未检测到 node, 跳过语法校验"; }
            catch (Exception ex) { return "语法校验异常: " + ex.Message; }
        }

        public static List<Target> Filter(List<Target> all, string kind)
        {
            List<Target> outList = new List<Target>();
            foreach (Target t in all) if (t.Kind == kind) outList.Add(t);
            return outList;
        }

        private static string KindOfArg(string f)
        {
            if (f.IndexOf(TaskName, StringComparison.OrdinalIgnoreCase) >= 0) return "task";
            return f.ToLowerInvariant().EndsWith("settings.json") ? "settings" : "dist";
        }

        public static int CheckAll(List<string> files)
        {
            List<Target> targets;
            if (files.Count == 0) targets = Discover();
            else
            {
                targets = new List<Target>();
                foreach (string f in files) targets.Add(new Target(f, KindOfArg(f)));
            }
            if (targets.Count == 0) { Console.WriteLine("未发现 WorkBuddy 安装 (C:\\Program Files\\WorkBuddy*)"); return 1; }
            int rc = 0;
            foreach (Target t in targets)
            {
                Console.WriteLine("== " + t.Path);
                if (t.Kind == "settings" && !File.Exists(t.Path)) { Console.WriteLine("   设置文件不存在(应用时创建)"); continue; }
                string[] st = StatusOf(t);
                for (int i = 0; i < Patches.Length; i++)
                {
                    if (st[i] == "—") continue;
                    Console.WriteLine("   " + Patches[i].Name + " : " + st[i] + (HasBackup(t.Path, Patches[i]) ? " (有备份)" : ""));
                }
            }
            return rc;
        }

        public static int ApplyAll(List<string> files)
        {
            List<Target> targets;
            if (files.Count == 0) targets = Discover();
            else
            {
                targets = new List<Target>();
                foreach (string f in files) targets.Add(new Target(f, KindOfArg(f)));
            }
            if (targets.Count == 0) { Console.WriteLine("未发现 WorkBuddy 安装 (C:\\Program Files\\WorkBuddy*)"); return 1; }
            int rc = 0;
            foreach (Target t in targets)
            {
                Console.WriteLine("== " + t.Path);
                bool any = false;
                for (int i = 0; i < Patches.Length; i++)
                {
                    PatchDef pd = Patches[i];
                    if (pd.Kind != t.Kind) continue;
                    string detail;
                    string r;
                    try { r = ApplyOne(t.Path, t.Kind, pd, out detail); }
                    catch (Exception ex) { Console.WriteLine("   " + pd.Name + " : 失败 - " + ex.Message); rc = 1; continue; }
                    Console.WriteLine("   " + pd.Name + " : " + detail);
                    if (r == "no-anchor") rc = 2;
                    if (r == "patched") any = true;
                }
                if (any && t.Kind == "dist") Console.WriteLine("   " + NodeCheck(t.Path));
            }
            Console.WriteLine(rc == 0 ? "完成" : "完成(存在警告/失败)");
            return rc;
        }

        public static int RestoreAll(List<string> files)
        {
            List<Target> targets;
            if (files.Count == 0) targets = Discover();
            else
            {
                targets = new List<Target>();
                foreach (string f in files) targets.Add(new Target(f, KindOfArg(f)));
            }
            int rc = 0;
            foreach (Target t in targets)
            {
                foreach (PatchDef pd in Patches)
                {
                    if (pd.Kind != t.Kind) continue;
                    if (pd.Kind == "task")
                    {
                        string d;
                        try { string r = RestoreTask(out d); Console.WriteLine((r == "patched" ? "已还原 " : pd.Name + " : ") + d); if (r == "no-anchor") rc = 1; }
                        catch (Exception ex) { Console.WriteLine("还原失败 " + pd.Name + " : " + ex.Message); rc = 1; }
                        continue;
                    }
                    string bak = t.Path + pd.BakSuffix;
                    if (!File.Exists(bak)) continue;
                    try
                    {
                        File.Copy(bak, t.Path, true);
                        Console.WriteLine("已还原 " + pd.Name + " : " + t.Path);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("还原失败 " + bak + " : " + ex.Message);
                        rc = 1;
                    }
                }
            }
            return rc;
        }
    }

    public class MainForm : Form
    {
        private CheckBox[] _checks = new CheckBox[7];
        private ComboBox _modeCombo;
        private NumericUpDown _daysNum;
        private Button _btnRefresh;
        private Button _btnApply;
        private Button _btnRestore;
        private ListView _lv;
        private TextBox _txtLog;

        public MainForm()
        {
            Text = "WorkBuddy 补丁管理器 (注入体与 JS 脚本同源, 产品更新后重扫即可)";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 724);
            MinimumSize = new Size(760, 560);

            // 顶部固定高度面板: 复选框/按钮/提示 (Dock=Top, 不随窗口缩放变形)
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 232;

            Label lbl = new Label();
            lbl.Text = "选择要应用的补丁:";
            lbl.Location = new Point(12, 10);
            lbl.AutoSize = true;
            top.Controls.Add(lbl);

            string[] names = new string[] {
                "补丁一: 压缩后物理截断边界前历史 + GC (内存随压缩轮次增长)",
                "补丁二: 切回会话只解析到最新压缩边界 (消除重载 10GB 尖峰)",
                "补丁三: 每次压缩后清理旧会话文件 (默认保留最近 5 个, WB_KEEP_SESSIONS 可调)",
                "补丁四: 会话默认上下文预算优先 1M 档 (模型不支持时自动回落)",
                "补丁五: 默认完全访问 permissions.defaultMode (app.asar 有签名不可改, 走 CLI 设置)",
                "补丁六: 移除 ACP \"Standalone SSE is closed\" 刷屏日志 (单日可刷百万条)",
                "补丁七: SDK 会话日志轮转与历史清理 (计划任务每 10 分钟, 今日 100MB 上限)" };
            for (int i = 0; i < 7; i++)
            {
                CheckBox c = new CheckBox();
                c.Text = names[i];
                c.Location = new Point(28, 32 + i * 23);
                c.AutoSize = true;
                c.Checked = true;
                _checks[i] = c;
                top.Controls.Add(c);
            }

            Label lblMode = new Label();
            lblMode.Text = "补丁五取值:";
            lblMode.Location = new Point(560, 32 + 4 * 23);
            lblMode.AutoSize = true;
            top.Controls.Add(lblMode);
            _modeCombo = new ComboBox();
            _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _modeCombo.Items.AddRange(new object[] { "fullAccess", "bypassPermissions" });
            _modeCombo.SelectedIndex = 0;
            _modeCombo.Location = new Point(650, 32 + 4 * 23 - 3);
            _modeCombo.Size = new Size(160, 24);
            top.Controls.Add(_modeCombo);

            Label lblDays = new Label();
            lblDays.Text = "补丁七历史保留天数:";
            lblDays.Location = new Point(560, 32 + 5 * 23);
            lblDays.AutoSize = true;
            top.Controls.Add(lblDays);
            _daysNum = new NumericUpDown();
            _daysNum.Minimum = 0;
            _daysNum.Maximum = 30;
            _daysNum.Value = 0;
            _daysNum.Location = new Point(700, 32 + 5 * 23 - 3);
            _daysNum.Size = new Size(60, 24);
            top.Controls.Add(_daysNum);

            _btnRefresh = MakeButton("刷新状态", new Point(12, 196), 110, delegate { RefreshStatusAsync(); });
            _btnApply = MakeButton("应用选中补丁", new Point(130, 196), 130, delegate { ApplySelected(); });
            _btnRestore = MakeButton("从备份还原选中补丁", new Point(268, 196), 180, delegate { RestoreSelected(); });
            Label hint = new Label();
            hint.Text = "写入 Program Files 需管理员权限; 补丁一/三在下次压缩完成后由 CLI 自动执行";
            hint.ForeColor = Color.Gray;
            hint.Location = new Point(460, 201);
            hint.AutoSize = true;
            top.Controls.Add(hint);

            // 列表与日志放进水平分割容器: 随窗口缩放, 各自充满, 拖动中缝可调比例
            _lv = new ListView();
            _lv.View = View.Details;
            _lv.FullRowSelect = true;
            _lv.GridLines = true;
            _lv.Dock = DockStyle.Fill;
            _lv.Columns.Add("文件", 340);
            _lv.Columns.Add("补丁一", 108);
            _lv.Columns.Add("补丁二", 108);
            _lv.Columns.Add("补丁三", 108);
            _lv.Columns.Add("补丁四", 108);
            _lv.Columns.Add("补丁五", 150);
            _lv.Columns.Add("补丁六", 130);
            _lv.Columns.Add("补丁七", 130);
            _lv.Columns.Add("备份", 56);
            _lv.Columns.Add("修改时间", 108);

            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.WordWrap = false;
            _txtLog.Font = new Font("Consolas", 9F);
            _txtLog.Dock = DockStyle.Fill;

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterWidth = 6;
            split.Panel1.Controls.Add(_lv);
            split.Panel2.Controls.Add(_txtLog);

            Controls.Add(split); // 先加 Fill, 再加 Top: WinForms 按索引逆序停靠
            Controls.Add(top);

            Shown += delegate
            {
                try { split.SplitterDistance = 270; } catch { }
                Log("就绪。先刷新状态, 再应用/还原。");
                RefreshStatusAsync();
            };
        }

        private Button MakeButton(string text, Point loc, int w, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = loc;
            b.Size = new Size(w, 28);
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        private bool[] Selected()
        {
            bool[] sel = new bool[_checks.Length];
            for (int i = 0; i < _checks.Length; i++) sel[i] = _checks[i].Checked;
            return sel;
        }

        private void SetBusy(bool busy)
        {
            _btnRefresh.Enabled = !busy;
            _btnApply.Enabled = !busy;
            _btnRestore.Enabled = !busy;
        }

        private void Log(string s)
        {
            if (_txtLog.InvokeRequired) { _txtLog.BeginInvoke((MethodInvoker)delegate { LogDirect(s); }); return; }
            LogDirect(s);
        }

        private void LogDirect(string s)
        {
            _txtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine);
            if (_txtLog.Text.Length > 200000) _txtLog.Text = _txtLog.Text.Substring(100000);
        }

        private List<Target> Targets()
        {
            List<Target> files = Store.Discover();
            if (files.Count == 0) Log("未发现 WorkBuddy 安装 (C:\\Program Files\\WorkBuddy*)");
            return files;
        }

        private string[][] ComputeStatus(List<Target> targets)
        {
            string[][] rows = new string[targets.Count][];
            for (int i = 0; i < targets.Count; i++)
            {
                Target t = targets[i];
                string[] st = Store.StatusOf(t);
                string bak = "";
                int bakCount = 0;
                int kindCount = 0;
                for (int p = 0; p < Store.Patches.Length; p++)
                {
                    if (Store.Patches[p].Kind != t.Kind) continue;
                    kindCount++;
                    if (Store.HasBackup(t.Path, Store.Patches[p])) bakCount++;
                }
                bak = bakCount + "/" + kindCount;
                string mtime = "";
                try { mtime = new FileInfo(t.Path).LastWriteTime.ToString("yyyy-MM-dd HH:mm"); } catch { }
                string[] row = new string[10];
                row[0] = t.Path;
                for (int p = 0; p < Store.Patches.Length; p++) row[p + 1] = st[p];
                row[8] = bak; row[9] = mtime;
                rows[i] = row;
            }
            return rows;
        }

        private void FillRows(string[][] rows)
        {
            _lv.BeginUpdate();
            _lv.Items.Clear();
            foreach (string[] r in rows)
            {
                ListViewItem it = new ListViewItem(r[0]);
                for (int i = 1; i < r.Length; i++) it.SubItems.Add(r[i]);
                _lv.Items.Add(it);
            }
            _lv.EndUpdate();
        }

        private void RefreshStatusAsync()
        {
            Store.Patches[4].Value = _modeCombo.Text;
            Store.Patches[6].Value = _daysNum.Value.ToString();
            SetBusy(true);
            Thread t = new Thread(delegate()
            {
                try
                {
                    List<Target> targets = Targets();
                    string[][] rows = ComputeStatus(targets);
                    BeginInvoke((MethodInvoker)delegate { FillRows(rows); });
                }
                catch (Exception ex) { Log("刷新失败: " + ex.Message); }
                finally { BeginInvoke((MethodInvoker)delegate { SetBusy(false); }); }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ApplySelected()
        {
            bool[] sel = Selected();
            int selCount = 0;
            for (int i = 0; i < sel.Length; i++) if (sel[i]) selCount++;
            if (selCount == 0) { MessageBox.Show("请先勾选至少一个补丁"); return; }
            Store.Patches[4].Value = _modeCombo.Text;
            Store.Patches[6].Value = _daysNum.Value.ToString();
            SetBusy(true);
            Thread t = new Thread(delegate()
            {
                try
                {
                    List<Target> targets = Targets();
                    foreach (Target tg in targets)
                    {
                        Log("== " + tg.Path);
                        bool any = false;
                        for (int i = 0; i < Store.Patches.Length; i++)
                        {
                            if (i >= sel.Length || !sel[i]) continue;
                            PatchDef pd = Store.Patches[i];
                            if (pd.Kind != tg.Kind) continue;
                            string detail;
                            string r;
                            try { r = Store.ApplyOne(tg.Path, tg.Kind, pd, out detail); }
                            catch (Exception ex) { Log("  " + pd.Name + " : 失败 - " + ex.Message); continue; }
                            Log("  " + pd.Name + " : " + detail);
                            if (r == "patched") any = true;
                        }
                        if (any && tg.Kind == "dist") Log("  " + Store.NodeCheck(tg.Path));
                    }
                    Log("应用完成。重启 WorkBuddy 生效。");
                }
                catch (Exception ex) { Log("应用失败: " + ex.Message); }
                finally { BeginInvoke((MethodInvoker)delegate { SetBusy(false); }); }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RestoreSelected()
        {
            bool[] sel = Selected();
            int selCount = 0;
            for (int i = 0; i < sel.Length; i++) if (sel[i]) selCount++;
            if (selCount == 0) { MessageBox.Show("请先勾选要还原的补丁"); return; }
            DialogResult dr = MessageBox.Show(
                "将从 .bak 备份还原所选补丁对应的原始文件。\r\n\r\n注意:\r\n1) 还原补丁一会连同一并撤销其之后应用的其他补丁(备份是逐级叠加的);\r\n2) 若 WorkBuddy 刚更新过, 备份属于旧版本, 还原会导致版本回退!\r\n\r\n确认继续?",
                "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            SetBusy(true);
            Thread t = new Thread(delegate()
            {
                try
                {
                    List<Target> targets = Targets();
                    foreach (Target tg in targets)
                    {
                        for (int i = 0; i < Store.Patches.Length; i++)
                        {
                            if (i >= sel.Length || !sel[i]) continue;
                            PatchDef pd = Store.Patches[i];
                            if (pd.Kind != tg.Kind) continue;
                            if (pd.Kind == "task")
                            {
                                string d;
                                try { string r = Store.RestoreTask(out d); Log((r == "patched" ? "已还原 " : "  " + pd.Name + " : ") + d); }
                                catch (Exception ex) { Log("还原失败 " + pd.Name + " : " + ex.Message); }
                                continue;
                            }
                            string bak = tg.Path + pd.BakSuffix;
                            if (!File.Exists(bak)) { Log("无备份, 跳过: " + bak); continue; }
                            try
                            {
                                File.Copy(bak, tg.Path, true);
                                Log("已还原 " + pd.Name + " : " + tg.Path);
                            }
                            catch (Exception ex) { Log("还原失败 " + bak + " : " + ex.Message); }
                        }
                    }
                    Log("还原完成。");
                }
                catch (Exception ex) { Log("还原失败: " + ex.Message); }
                finally { BeginInvoke((MethodInvoker)delegate { SetBusy(false); }); }
            });
            t.IsBackground = true;
            t.Start();
        }

        // --uitest: 构建 UI + 发现 + 状态填充(不显示窗口), 返回发现的文件数
        public int RunUiTest()
        {
            List<Target> targets = Targets();
            string[][] rows = ComputeStatus(targets);
            FillRows(rows);
            foreach (string[] r in rows)
                Console.WriteLine(r[0] + " | " + r[1] + " | " + r[2] + " | " + r[3] + " | " + r[4] + " | " + r[5] + " | " + r[6] + " | " + r[7] + " | 备份 " + r[8]);
            return rows.Length;
        }
    }
}

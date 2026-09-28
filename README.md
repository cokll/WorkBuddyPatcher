# WorkBuddyPatcher —— WorkBuddy AI 桌面版补丁管理器

一个带界面(WinForms)和命令行模式的本地补丁工具,管理 **7 个**针对 WorkBuddy AI 桌面版的补丁:内存增长、重载尖峰、会话文件膨胀、上下文预算、默认权限、日志刷屏、SDK 日志轮转。支持一键应用 / 状态扫描 / 从备份还原,注入体与仓库中的 JS 补丁脚本**同源且字节级一致**。

> **免责声明**:本项目为独立工具,与 WorkBuddy / CodeBuddy 官方无关;仅修改**本机已安装副本**的本地文件,不分发任何产品文件或资源。修改程序文件存在风险,请自行评估;所有补丁均提供备份与还原机制,详见下文。

## 背景:为什么需要这些补丁

WorkBuddy AI 桌面版是 Electron 应用,内置一个 headless CLI(`codebuddy`)作为 agent 端,与桌面 UI 之间走 ACP 协议。长期高强度使用中实测暴露了七个问题:

| # | 问题(实测数据) | 根因 | 补丁方案 |
|---|---|---|---|
| 一 | 内存随压缩轮次持续增长 | 会话压缩完成后,边界之前的历史仍驻留内存 | 压缩收尾点注入:物理截断边界前历史 + 触发 GC |
| 二 | 切回长会话时内存尖峰 10GB | 重载会话时解析全部 JSONL 历史 | 解析处停读:只解析到最新压缩边界 + GC |
| 三 | 会话文件只增不减(1.3GB/279 个) | 内置清理只按天数,没有按数量保留 | 压缩收尾点清理旧会话,保留最近 N 个(默认 5) |
| 四 | 会话默认上下文预算 200K | 预算选择逻辑优先级问题 | 预算判断优先 1M 档,模型不支持自动回落 |
| 五 | 会话默认沙盒权限 | 桌面端默认 `defaultMode` 为空时回落沙盒 | 写 CLI 用户设置为 `fullAccess`/`bypassPermissions` |
| 六 | 日志刷屏:`Standalone SSE is closed` 单日 **165 万条**,占单文件体积 80%(376MB) | ACP 推送通道关闭后,每次推送尝试仍记一条 Warning,毫秒级重试 | 删除型补丁:调用点整体替换为 `void 0` |
| 七 | SDK 会话日志单日 **1.1GB**;内置按天归档器对大文件崩溃(连续两天死在压缩中途,残留 lock 与 zip 半成品,更早日期永不再处理) | 写入方在 asar 内不可改;归档器用 adm_zip 全内存压缩大文件溢出,且只重试"昨天" | 外部计划任务每 10 分钟轮转:今日超 100MB 改名分段,历史按保留天数清理(GUI 可设,默认仅留当天) |

## 目标文件与关键约束

- **CLI bundle**:`C:\Program Files\WorkBuddy*\resources\app.asar.unpacked\cli\dist\codebuddy*.(js|mjs)`。`app.asar.unpacked` 下的文件是未打包、未签名的明文 minified JS,可以安全原位修改。
- **app.asar 本体不可改**:带完整性保护,原位等长修改无效(本项目早期尝试已废弃)。因此桌面端(asar 内)的行为改不了——涉及桌面端的补丁都绕道:补丁五走 CLI 的用户设置文件,补丁七走外部计划任务。
- **产品更新**:更新后 dist 回到原版,重开工具重新应用即可。锚点是**规则代码特征**(正则),与版本字节偏移无关;若新版本改动了代码形状,状态会显示 `⚠ 无锚点` 而不是盲改。

## 七个补丁详解

### 补丁一 / 二 / 三:压缩、重载与文件清理(注入型)

三者共享同一注入点思路:在 minified bundle 中按特征正则找到目标函数/调用,注入一段自包含的 JS(所有变量带 `wb` 前缀避免冲突,单行,全程 try/catch 静默):

- **补丁一**:锚定 `markCompactionComplete(...)`,压缩成功收尾时把边界之前的会话历史从内存中物理截断并触发 GC。采用 **sig 模式**:捕获函数签名的 minified 参数名(随构建变化),原样重建函数头再插入标记与注入体。
- **补丁二**:锚定会话加载处的数组解析逻辑(**replace 模式**:锚点整体替换为注入模板,模板中的 `@@A@@/@@I@@/...` 占位符由捕获的局部变量名填充),只解析到最新压缩边界为止,消除切回会话时的重载尖峰。
- **补丁三**:与补丁一同一注入点(fire-and-forget,延迟 3 秒执行),扫描 `~/.workbuddy-ai/projects` 与 `~/.workbuddy/projects` 下全部 `.jsonl`,按修改时间保留最近 N 个主会话(子代理目录随主会话一并处理);30 分钟内修改过的跳过(保护多窗口),10 分钟节流。`WB_KEEP_SESSIONS` 环境变量可调,`0` 停用。

### 补丁四:默认 1M 上下文(replace 模式)

锚定预算判断 `isBudget(overrideContextWindow)??...:isBudget(contextWindow?.defaultLength)`,注入后优先使用 1M 档;模型不支持 1M 时自动回落原值。会话级覆盖不受影响。

### 补丁五:默认完全访问(设置文件,非二进制)

`app.asar` 改不了,但 CLI 会话的权限默认值来自用户设置:把 `~/.workbuddy-ai/settings.json` 与 `~/.workbuddy/settings.json` 的 `permissions.defaultMode` 写为 `fullAccess` 或 `bypassPermissions`(GUI 下拉可选)。纯 JSON 文本编辑:保留原格式与其余键,写回前校验 JSON 合法性;当前值已属完全访问类时不重复写入。首次修改前备份为 `.permdefault.bak`。

### 补丁六:SSE 刷屏日志移除(删除型)

全 bundle 仅一处 `logger.warn?.("[ACP StreamManager] sendToClient: Standalone SSE is closed, cannot send notification")` 调用点。**delete 模式**:锚点连同接收者一起整体替换为 `0`,三元表达式退化为 `?void 0:`,任何日志级别都不再产生该条。(教训:若只替换方法调用会留下悬空的 `this.logger.0`,`node --check` 会当场拦住——本工具每文件每次应用后都跑语法校验。)

删除型的"已应用"判定是**消息文本在文件中消失**(`MarkerAbsent`),与常规"标记存在=已应用"相反。副作用:若未来产品版本自己删除了这条日志,状态会误报已应用,届时应下线本补丁。

### 补丁七:SDK 日志轮转与历史清理(任务型)

桌面端把 SDK 会话日志追加到 `<home>/.workbuddy*/logs/<yyyy-MM-dd>/sdk/conversations/<uuid>.log`,追加不收缩,实测单文件 1.1GB。内置归档器(`ConversationLogArchiver`)只在跨天时打包"昨天":用 adm_zip 全内存压缩,大文件直接崩溃——实测连续两天死在 `writeZip` 中途,留下 `conversations.zip.lock` 目录与 `conversations.zip.tmp-*` 半成品、源文件不删;且它只重试"昨天",更早的日期成为永久孤儿。

写入方(`ConversationLogDispatcher`)每次用 `fs.appendFile` 追加、不长期持有句柄——**文件被改名后,下一次写入会自动重建原文件名**,这是"改名即轮转"成立的可行性依据(已在真实环境验证)。

应用补丁七 = 把内嵌的 `rotate_sdk_logs.js` 写到 exe 目录 + 注册计划任务 `WorkBuddySdkLogRotate`(每 10 分钟,`--keep-days N` 参数由 GUI 传入),脚本做三件事:

1. **今日目录**:`sdk/conversations/*.log` 超过 100MB(`WB_SDK_LOG_MAX_MB` 可调)→ 原地改名为 `<uuid>.rot-<时间戳>.log`;每个会话只保留最近 1 个轮转段(`WB_SDK_LOG_KEEP_SEGMENTS`),更旧的删除 → 单会话磁盘占用上限约 200MB;
2. **历史目录**:超过保留天数(默认 0=仅留当天,GUI 可设 0~30)的日期目录整体清理(不限扩展名,含 `.swp.ndd`、旧版 `.log.N` 等杂项),并清理 mtime 超过 5 分钟的 lock/tmp 残留(5 分钟对齐应用内 `ARCHIVE_LOCK_STALE_MS`,不会碰正在进行的归档);
3. **多根去重**:同时覆盖 `~/.workbuddy-ai/logs`、`~/.workbuddy/logs` 与数据盘自定义路径,realpath 去重(兼容 junction);有动作时留痕日志。

**还原** = 删除计划任务(轮转停止),脚本保留可手动运行。注意:内置归档器崩溃的根因(全内存压缩大文件)不在本补丁的修复范围——轮转+历史清理让"昨天"的文件始终很小,归档器即使再尝试也不会再碰到 GB 级文件。

## 工作方式(工程机制)

### 保真流水线

```
scripts/*.js(可独立运行的补丁脚本,含自测锚点)
        │  node _gen_payloads.js:提取 ANCHOR 正则 / 标记 / 备份后缀 / 注入体模板
        ▼
PatchPayloads.cs(自动生成,勿手改)
        │  build.bat:系统自带 .NET Framework 4 csc.exe 编译(无需 SDK)
        ▼
WorkBuddyPatcher.exe(GUI + 控制台,winexe 无黑窗)
```

同一份 bundle,用 JS 脚本应用与用 exe 应用,结果**逐字节一致**(`cmp` 验证)——注入体是同一份来源,只是执行者不同。

### 三种补丁模式与状态机

| 模式 | 做法 | 使用者 |
|---|---|---|
| `sig` | 捕获 minified 函数签名的参数名,重建函数头 + 插入标记与注入体 | 补丁一、三 |
| `replace` | 锚点匹配整体替换为模板,`@@P@@`/`@@A@@..@@D@@` 占位符由捕获组填充 | 补丁二、四 |
| `delete` | 锚点连同接收者整体替换为 `0`;配合 `MarkerAbsent` 反转已应用判定 | 补丁六 |

每个 dist 文件每补丁三态:**✔ 已应用**(标记存在/删除型消息消失)/**未应用**(锚点可匹配)/**⚠ 无锚点**(产品代码形状变了,拒绝盲改)。应用后自动 `node --check` 语法校验。

### 备份与还原语义

- 首次修改前把原文件复制为 `<file>.<补丁名>.bak`,**已存在则不覆盖**;
- dist 备份**逐级叠加**(按 一→二→三→四 应用):`.compactfree.bak`=全净版,`.loadtrim.bak`=一,`.prune.bak`=一二,`.1mcontext.bak`=一二三;补丁六的锚点与一~四独立,`.ssespam.bak`=含一~四不含六;只还原补丁一等于退回全净版(等效撤销四个补丁);
- **产品更新自动检测**:应用时若发现"当前文件无任何已应用补丁、但与已有备份不同"(即产品已更新),自动把备份刷新为新版原始内容——保证"还原"永远等于"撤销本版补丁",而不是把产品回退到旧版本;产品刚更新后不要手动还原。

## 使用

双击 `WorkBuddyPatcher.exe`(内嵌 manifest 自动提权,winexe 无黑窗):

1. **刷新状态**——自动发现全部安装的 CLI bundle、两个用户设置文件与计划任务,逐补丁显示三态与备份情况;
2. 勾选补丁 → **应用选中补丁**——自动备份、注入/写入/建任务、dist 自动语法校验。补丁五取值由下拉框选择;补丁七"历史保留天数"由数字框选择(改天数后重新应用即生效);
3. **从备份还原选中补丁**——dist 补丁把对应 `.bak` 复制回原文件;补丁五恢复原始设置;补丁七删除计划任务。

命令行模式(输出经 AttachConsole 附着到调用方终端,便于脚本化):

```bat
WorkBuddyPatcher.exe --check                       :: 查看全部安装的补丁状态
WorkBuddyPatcher.exe --console                     :: 应用全部七个补丁
WorkBuddyPatcher.exe --restore                     :: 从备份还原(补丁七=删除计划任务)
WorkBuddyPatcher.exe --console <文件...>           :: 只处理指定文件;传 WorkBuddySdkLogRotate
                                                      按任务补丁处理,settings.json 按设置补丁处理
WorkBuddyPatcher.exe --uitest                      :: 自检(构建UI+发现+状态后退出)
```

### 可调参数

| 参数 | 默认 | 说明 |
|---|---|---|
| `WB_KEEP_SESSIONS` | 5 | 补丁三保留的会话数,0=停用 |
| 补丁五取值(GUI) | fullAccess | `fullAccess` / `bypassPermissions` |
| 补丁七保留天数(GUI) | 0 | 历史日期日志保留天数,0=仅留当天 |
| `WB_SDK_LOG_MAX_MB` | 100 | 补丁七今日单文件轮转阈值(MB) |
| `WB_SDK_LOG_KEEP_SEGMENTS` | 1 | 补丁七每会话保留的轮转段数 |
| `WB_SDK_LOG_KEEP_DAYS` | 0 | 同 GUI 天数(命令行优先级: `--keep-days` > 环境变量) |
| `WB_SDK_LOG_ROOTS` | 内置四路径 | 补丁七扫描根目录,`;` 分隔 |

## 构建与目录结构

```
WorkBuddyPatcher\
├─ WorkBuddyPatcher.cs    主程序 (WinForms + 控制台模式, C# 5 / net4, winexe)
├─ PatchPayloads.cs       注入体/正则/标记 (由 _gen_payloads.js 自动生成, 勿手改)
├─ _gen_payloads.js       生成器: 从 scripts/ 提取锚点与注入体(找不到时回退上级目录)
├─ scripts\               六个 JS 补丁/运维脚本(补丁一~四、六、七的注入体与逻辑来源)
├─ app.manifest           requireAdministrator 提权清单
├─ build.bat              Framework64\v4.0.30319\csc.exe 编译, 无需 SDK
└─ WorkBuddyPatcher.exe   已编译产物(可直接使用)
```

重新构建(需要 node):

```
node _gen_payloads.js && build.bat
```

运行期依赖:补丁一~六应用后由 WorkBuddy 自身的 Node 运行;补丁七的计划任务需要 `node.exe`(自动探测 PATH 与常见安装路径)。

## 产品更新后怎么办

1. 重开工具 → **刷新状态**:dist 补丁显示"未应用"的重新勾选应用即可;
2. 显示 `⚠ 无锚点` 说明新版本改动了相关代码形状:按新代码更新 `scripts/` 对应脚本的锚点,然后 `node _gen_payloads.js && build.bat`;
3. **产品刚更新后不要还原**(备份属于旧版本,还原会版本回退),直接重打;
4. 补丁五、七基于设置文件/计划任务,产品更新通常不受影响。

## FAQ

- **会上传任何数据吗?** 不会。全部逻辑纯本地,只读本机 WorkBuddy 安装与用户目录,唯一网络行为是补丁七不涉及网络。
- **如何完全卸载?** 工具内"从备份还原选中补丁"勾选全部 → 应用;或 `WorkBuddyPatcher.exe --restore`,dist 回到原版、设置文件恢复、计划任务删除。
- **为什么GUI要管理员权限?** 写 `C:\Program Files` 与注册系统计划任务都需要;manifest 已内嵌自动提权。
- **补丁会不会影响 WorkBuddy 云端账号?** 不会。所有修改都在本地文件与本地计划任务层。

## 验证记录(2026-09)

- **保真验证**:同一份 bundle,exe 应用文本补丁 vs JS 脚本应用 → `cmp` 逐字节一致,`node --check` 通过
- **补丁六**:4 个 bundle 文件状态/幂等/往返全部通过;删除型标记判定正确
- **补丁七**:任务往返(还原→未应用→应用→重建)通过;PowerShell 确认任务动作 `node.exe` + `rotate_sdk_logs.js --keep-days 0`、间隔 PT10M、LastTaskResult=0;脚本落盘与源文件逐字节一致
- **实跑效果**:SSE 刷屏日志归零(原单日 165 万条);SDK 会话日志 09-14~09-28 历史残留(含 1.1GB/781MB 大文件、lock、zip 半成品)全部清理,E 盘回收约 7GB;今日日志被 100MB 上限管住
- **黑窗修复**:GUI 为 winexe 无控制台窗口,控制台模式经 AttachConsole 输出正常

## License

[MIT](LICENSE)

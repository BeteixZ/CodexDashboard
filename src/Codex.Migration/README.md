# 离线迁移与回滚

本库只操作调用方明确传入的源、目标、事务三个目录。它不查询系统用户目录，不停止进程，不解密凭据，不启动微信、模型或 Codex，也不会提交任何任务。

当前用于 P1 隔离验收。**未提供生产接收者停机/写入隔离器；默认拒绝实际迁移。** 因此本库测试通过不能勾选整个 P1-10/P1-12 或代表真实用户数据已迁移。

## 入口

- `MigrationDiscovery.Locate(explicitDirectories)` 仅检查文件和目录元数据，返回所有候选；不读取配置或凭据、不自动选中任何目录。
- `ResolveDataDirectory(explicitInstallationDirectory, explicitFallback)` 只读取所选安装目录的 `data-directory.txt`；要求 UTF-8、绝对路径、最多 8192 字节，拒绝目录链接/重解析点。多份候选由调用方让用户选择。
- `MigrationEngine.Plan(request)` 枚举白名单及体积，不打开配置或凭据内容。
- `MigrationEngine.Inspect(request)` 只读取有界事务日志与启动标记，返回阶段、激活记录、文件计数和回滚条件；不打开保护载荷、不核对载荷哈希、不调用接收者 guard、不创建任何目录。结果不是执行许可；`NeedsReceiverGuardAndIntegrityCheck` 仍要求独立停机与全量一致性检查。
- `MigrateAsync(request)` 执行或从事务阶段恢复。
- `RollbackAsync(request)` 只在新助手尚未激活且源/目标数据未改变时自动回滚；激活过或存在新增数据时返回需要对账的错误，保留现场。
- `MigrationStartup.AcquireRuntimeLease(dataDirectory)` 必须在助手读取凭据前调用，保留至接收者、模型和任务控制全部停止。它检查迁移标记、记录首次激活，并持有与迁移相同的文件锁。`CanStart` 可用于纯状态展示。

三个目录必须为显式绝对路径，互不相等、互不嵌套，不能为磁盘根或包含重解析点。目标首次迁移必须为空（可有本库的锁文件）；不支持把已有账号目录合并进去。事务目录必须单独保存，不能自动删除。

## 接收者排他门

调用方必须实现 `ILegacyReceiverGuard`，返回一个在整个事务期间有效的 `IReceiverExclusionLease`。必须能证明：

1. 旧程序已停止且不能把内存中的配置/凭据重新写回。
2. 新助手也已停止，期间不能启动和写入。
3. Windows 用户保护范围和 OAuth 应用注册身份允许原有受保护数据继续使用。

每个不可逆阶段前重新校验该 lease。接口故意不提供“我确认了”的生产布尔参数；测试使用假 guard，不能拿它代替正式停机检查。新版助手的文件锁不能证明旧版 exe 已停止，因为旧版不认识这个锁。

若保护范围或应用注册不兼容，应设计独立的重新授权流程；不得把 `CredentialScopeCompatible` 设为 true 跳过判断。

## 白名单和配置

文件名依据旧工作区源码核对：

| 来源 | 内容 |
| --- | --- |
| `settings.json` | 提取 `weChat`、`chatGpt`、主机注册表、采集参数、并发数、输入合并窗口、历史归档及媒体设置，目标 `version=1` |
| `chatgpt.credentials` | 原样保存的受保护账号库 |
| `task-control.state` | 原样保存的受保护队列，保留身份、幂等键及所有状态 |
| `conversations.state`、`history-archive`的已知文件 | 原样保留对话、操作、发送、订阅水位、撤销依赖和归档卷；未知归档格式拒绝而非遗漏 |
| `owned-state/*.owned`、`owned-control.json`、`existing-control.json` | 保留任务归属和控制配置，不重建任务身份 |
| `owned-acceptance`、`existing-acceptance`的已知证明文件 | 仅保留原始字节；证明绑定原目录，迁移到新目录后仍须重新验收安装，不能据此开放控制 |
| `attachments/<身份摘要>/<附件摘要>.state` | 保留加密的收件附件和已准备的发件产物 |
| `assistant/profiles/<64 位十六进制 hash>/AGENTS.md` | 规则原文 |
| `assistant/profiles/<64 位十六进制 hash>/memory.state` | 原样保存的受保护记忆 |

微信游标、用户身份、`SeenMessages` 等在 `weChat.protectedBinding` 内，发送去重在 `weChat.recentAttempts`，均随设置原样转移。模型库内稳定 HostId 和账号身份不重建。队列不解析、不重置 `sending`/`unconfirmed`；新宿主加载队列时仍适用原有“不自动重发未知结果”的语义。

不会迁移诊断日志、任意同名目录、未识别文件或 Dashboard 新版的 `dashboard.settings.json`。旧设置版本只接受 0–2；未知版本或 JSON 重复键（包括大小写变体）均拒绝并保留原文件。每个文件最大 16 MiB，设置读取最大 1 MiB，总量 64 MiB，白名单最多 2048 项。

## 阶段与恢复

`Preparing → Prepared → DisablingSource → SourceDisabled → Publishing → Published → Completed`

- 先记录带长度和 SHA-256 的源清单，复制到事务 `backup/` 并校验，再生成 `staging/`。
- 发布前给目标写入 `pending` 标记，新助手不能启动。
- 源 `weChat.protectedBinding` 和 `protectedPushPlusToken` 清空，微信、pushplus、模型和任务控制开关禁用；源 `chatgpt.credentials` 在确认备份后移除。
- 只有源禁用得到验证，才把 staging 文件发布到目标。最后校验完整清单，释放目标 `ready` 标记。
- 使用刷新到磁盘的临时文件加原子替换。每个可观察阶段记录 journal；断点后重跑校验已有文件，绝不覆盖不匹配文件。
- 所有错误返回阶段和非秘密错误码；不把异常正文、原配置、密文或令牌写入摘要。
- 保留备份与 staging；不清空损坏文件、不自动删除备份。

文件锁异常、权限拒绝、源/目标在事务中变化、损坏备份或未知版本会停止推进。重解析点、任意路径注入、两个事务同时处理同一源/目标都受到检查。

测试注入覆盖已落盘阶段之间的中断，不等同于物理断电或文件系统故障。若物理中断留下无法证明归属的临时文件，本库保留并拒绝自动覆盖；需要人工检查/对账，不能声称已经验证任意存储故障的恢复。

## 回滚

回滚同样取得旧/新接收者排他 lease，先验证新助手从未激活、目标清单与提交时完全一致、源仍为已禁用状态。

随后：

1. 目标标记改为 `rollback-pending`。
2. 禁用目标微信/模型/控制，移除目标账号凭据；staging 保存其原副本。
3. 还原源白名单文件，最后恢复源设置。
4. 目标标记为 `rolled-back`，禁止继续启动。

恢复期间的重复调用不会创建队列条目或执行任务。新助手一旦激活，外部副作用可能已发生，**即使文件恰好相同也不自动回滚**。目标出现新对话、更新队列或新记忆时同样拒绝，不恢复旧快照覆盖新数据。正式产品仍需补充“导出新数据、对账命令和转换记录后回滚”的交互流程。

## 验证

Windows 助手提供“迁移检查”预览页，或单独使用 `--migration-preview` 在加载设置、凭据和服务前打开。目录必须显式填写；可选旧安装目录只用于读取 `data-directory.txt`。页面只显示文件类别、数量、字节、记录的阶段和回滚条件，不包含执行迁移/回滚按钮。普通主窗口打开预览时，当前助手继续持有自己的运行目录 lease。生产迁移仍使用 `DenyMigrationGuard`。

事务元数据与实际文件核对是两个步骤。已激活时预览明确要求对账；尚未激活不等于可以直接回滚。窗口不读取受保护载荷，也不展示账户正文。损坏、重复键、不同目录的事务日志均不能变成许可。

Windows 合成宿主测试入口 `--migration-host-test <新绝对输出目录>` 使用当次生成的 DPAPI 设置、微信身份、模型库、队列及规则记忆。先在从未激活的独立场景验证精确回滚；再用另一场景构造实际设置读取器、账户库、记忆、队列和 `DurableAssistantRuntime`，验证旧消息去重、新只读消息持久化与激活后回滚拒绝。网络和任务执行均由内存假实现替代。测试中的排他 guard 只绑定合成请求，不能作为正式停机证明；验证旧配置被去凭据也不等于已经运行旧版 exe 完成联调。

测试使用合成 JSON、假接收者 guard、合成受保护载荷以及独立目录；没有读取真实数据。

```powershell
dotnet run --project tests/Codex.Migration.Tests/Codex.Migration.Tests.csproj -c Release -- `
  --output C:\明确指定的新测试目录
```

输出目录必须不存在。每个场景保留独立文件，`checks.json` 只记录名称、通过状态和耗时。生产不能使用测试 guard。

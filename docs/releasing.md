# 发布流程

本文面向维护者。公开预发行版本使用 `v0.1.0` 格式的标签，Windows x64 的 Dashboard 和助手分别发布安装程序。

## 版本与范围

发布前同步软件版本、安装器版本、更新记录和文档。保持两个产品独立安装、独立用户数据、独立生命周期。首版不自动迁移旧版数据，不执行生产接收者交接。

公开源码应来自明确审阅过的提交。不要从本地开发目录直接打包上传所有文件；本地历史、测试证据、会话内容、账号配置和凭据不属于发行材料。`scripts/export_release_source.py` 可导出经过筛选的源码到一个不存在的新目录，并在目标目录旁写出文件清单；该清单不是代码审阅或签名的替代品。

## 本机构建

需要 Windows x64、.NET 10 SDK、PowerShell 7 和 Inno Setup 6。普通开发产物：

```powershell
./Build-Products.ps1
```

此入口默认产物依赖本机运行环境。公开发行使用 `Build-Release.ps1` 生成自包含程序目录及 Windows 安装器，包含 .NET 和本机 Python，并保留第三方许可证。输出目录必须是新的绝对路径，例如：

```powershell
./Build-Release.ps1 `
  -Version '0.1.0' `
  -OutputDirectory (Join-Path $PWD 'artifacts\releases\0.1.0-local') `
  -InnoCompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

脚本从 CPython 官方 HTTPS 地址下载固定版本的嵌入式运行环境，并核对固定 SHA-256。离线构建可加 `-PythonEmbedArchive '<本地嵌入式ZIP的绝对路径>'`；该文件仍必须匹配配置的校验值。`-Configuration` 默认为 `Release`。需要更换 Python 版本时，同时审阅 `-PythonEmbedUrl`、`-PythonEmbedSha256`、许可证和依赖兼容性，不要只改下载地址。

输出目录的 `packages` 包含两个自包含产品目录，`release` 包含两个安装程序、两个便携 ZIP、`release-manifest.json` 和 `SHA256SUMS.txt`。发布元数据记录版本、运行环境、是否自包含、是否签名、源码提交（可获得时）、Python 来源及产物长度和摘要。

不要直接把普通开发产物重新命名为自包含安装包。发行包中不得包含运行数据、访问密钥、凭据、真实个人路径、会话 ID 或内部测试证据。

## GitHub Actions 发布

在公开仓库 **Actions → Build and release Windows packages → Run workflow** 中选择已经审阅的分支：

- `version` 填 `0.1.0`，不带 `v` 前缀。
- `publish` 留为 `false` 时只准备并校验草稿；设为 `true` 时，通过全部检查后公开为预发行版。

工作流先运行 CI，再构建和检查自包含包、脚本安装及产品隔离。随后创建草稿，上传六个文件，重新下载并逐字节核对摘要；全部成功后才按 `publish` 选择公开。标签使用 `v0.1.0` 并指向本次工作流提交。已有标签若指向不同源码、已有公开 Release 或已有带附件草稿，工作流会拒绝覆盖，需先人工核对。

也可以推送同格式的版本标签触发发布；标签触发在验证完成后会公开为预发行版。失败时保留草稿或工作流产物，检查原因后再决定重试，不要修改已发布版本的附件而沿用原版本。

## 验证

按修改范围运行采集器、网页模型、Core、协议、控制和迁移检查，并记录实际运行的测试集合。安装器至少验证：

1. 无需预装 .NET 或 Python 的新安装。
2. 从开始菜单启动，确认运行环境和实际数据目录。
3. 两个产品分别退出后，另一方仍可运行。
4. 退出对应产品后升级，设置和数据保留。
5. 从 Windows 应用列表卸载，用户数据保留。
6. 下载文件的 SHA-256 与发布清单一致。

包检查可使用：

```powershell
./tests/ReleasePackaging.Tests.ps1 `
  -ReleaseDirectory '<输出目录>\release' `
  -PackageDirectory '<输出目录>\packages'
```

自动检查不替代正常桌面安装点击、手机收信、真实 SSH 或长时间恢复验收。专用测试环境的模拟检查、真实桌面操作和外部服务联调应分别报告。未执行的场景保留在 [已知限制](known-limitations.md)。

## 发布材料

- `CodexDashboard-0.1.0-win-x64-Setup.exe`。
- `CodexAssistant-0.1.0-win-x64-Setup.exe`。
- `CodexDashboard-0.1.0-win-x64-portable.zip`。
- `CodexAssistant-0.1.0-win-x64-portable.zip`。
- `release-manifest.json`。
- `SHA256SUMS.txt`，覆盖上述五个文件。

项目 MIT 许可证及完整第三方声明包含在产品包内。发布说明写明主要功能、安装方式、变更、已验证范围及已知限制。

首版为 **Pre-release**，安装程序暂未数字签名。发布说明应明确 `unsigned`、没有自动更新，以及旧版数据不自动迁移。校验值不能替代代码签名或发布者身份认证。

## 发布后

核对公开下载链接、版本标签和校验值是否一致。只在实际完成安装和正常启动后声明通过相应验收。后续修复更新 CHANGELOG，并以新版本发布，不替换已发布版本的文件而沿用旧校验值。

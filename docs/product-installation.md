# 安装、升级与卸载

本页区分公开发行的自包含安装包和从源码生成的开发包。两个产品分别安装，可以只选其一。

## 公开发行：Windows 安装程序

在 [Releases](https://github.com/BeteixZ/CodexDashboard/releases) 下载对应版本：

| 产品 | v0.1.0 安装文件 |
| --- | --- |
| Dashboard | `CodexDashboard-0.1.0-win-x64-Setup.exe` |
| 助手 | `CodexAssistant-0.1.0-win-x64-Setup.exe` |

安装包面向 Windows x64，随包包含 .NET 运行环境和本机采集用 Python，无需安装 SDK 或额外下载本机 Python。远程采集仍需要 SSH 客户端、正常的远程登录、远端 Python 3 和可读取的 Codex 记录。

双击安装程序，按提示选择程序位置。安装完成后从开始菜单启动；可从 Windows 应用列表卸载。默认位置如下：

| 内容 | 默认位置 |
| --- | --- |
| Dashboard 程序 | `%LOCALAPPDATA%\Programs\CodexDashboard` |
| 助手程序 | `%LOCALAPPDATA%\Programs\CodexAssistant` |
| Dashboard 数据 | `%LOCALAPPDATA%\CodexDashboardInstalled` |
| 助手数据 | `%LOCALAPPDATA%\CodexAssistantInstalled` |

程序目录和数据目录分开。两个产品的新安装均不自动迁移旧版数据、模型授权或微信绑定。旧版数据会保留；不要把新版指向仍在运行的旧数据目录，也不要同时运行使用同一微信绑定的接收者。

当前安装程序**未数字签名**。Windows 可能显示未知发布者；请从项目发布页下载，并用该版本的 SHA-256 清单核对文件。校验值不能代替发布者数字签名。

### 升级

目前没有自动更新。退出要升级的产品，下载新版本安装程序，使用原安装位置安装。另一产品无须一起升级或退出。升级程序文件不会自动转换所有历史数据；涉及数据格式和恢复边界时，以对应版本说明为准。

### 卸载

先从对应托盘菜单完全退出，再到 Windows 应用列表卸载。卸载保留独立用户数据，重新安装可以再次使用同一安装数据目录。若要彻底删除个人数据，先确定实际保存位置和备份需要，再单独处理；不要误删另一个产品的数据或 Codex 原始记录。

保留旧程序不等于可以安全回滚数据。新版产生消息、任务操作或其他状态后，不能简单恢复旧备份并重发未确认操作。正式迁移、恢复服务启用和兼容回滚仍有限制，见 [已知限制](known-limitations.md)。

## 公开发行：便携包

发布页另提供同前缀的 `portable.zip`。解压到新目录，保留包内全部文件，不要只复制 exe。双击 exe 时默认分别保存到 `%LOCALAPPDATA%\CodexDashboardPortable` 和 `%LOCALAPPDATA%\CodexAssistantPortable`，与安装版及旧开发包分开。也可通过包内启动脚本指定新的绝对目录，例如：

```powershell
.\Start-Dashboard.ps1 -DataDirectory 'C:\CodexProducts\Dashboard' -ProtocolProfile 'personal'
.\Start-Assistant.ps1 -DataDirectory 'C:\CodexProducts\Assistant' -ProtocolProfile 'personal'
```

两个产品使用不同数据目录；若需要互相显示状态，使用相同的协议名称。启动脚本兼容 Windows PowerShell 5.1 与 PowerShell 7。便携使用不会自动创建卸载项；退出后可以移除其程序目录，保留另行指定的数据。

## 从源码生成的开发包

仓库根目录运行：

```powershell
.\Build-Products.ps1
```

这是开发构建入口，需要 Windows、PowerShell 7 和 .NET 10 SDK。默认输出到新的 `artifacts/builds/<时间>`，下含 `dashboard`、`assistant`。此类产物默认依赖本机 .NET 10 Desktop Runtime、ASP.NET Core 10 Runtime 和 Python 3.10+，与公开的自包含安装包不同。

开发包中的 `Setup.cmd` 会打开 PowerShell 图形安装界面；也可用 `Install-Product.ps1`、`Launch-InstalledProduct.ps1`、`Add-ProductShortcut.ps1` 和 `Uninstall-Product.ps1` 管理此类脚本安装。它们不等同于公开发行的 Windows 安装程序，不应混用安装目录。

以下示例需从对应发行包运行，并把路径替换为自己的选择：

```powershell
.\Install-Product.ps1 -InstallationDirectory 'C:\Apps\CodexAssistant' -DataDirectory 'C:\CodexProducts\Assistant'
& 'C:\Apps\CodexAssistant\Launch-InstalledProduct.ps1' -Preview
& 'C:\Apps\CodexAssistant\Launch-InstalledProduct.ps1'
```

脚本安装要求程序和数据目录不同且互不嵌套。升级时使用相同的程序位置、数据目录和协议名；新版本复制、校验完成后才更新活动版本。脚本不会自动关闭进程、接管旧绑定或启动新接收者。

脚本卸载先退出对应产品，再运行：

```powershell
& 'C:\Apps\CodexAssistant\Uninstall-Product.ps1' -Preview
& 'C:\Apps\CodexAssistant\Uninstall-Product.ps1'
```

预览不删除文件。执行时核对安装归属和文件清单，只移除登记的程序文件，保留独立数据；文件占用或未知内容可能阻止卸载。对公开 Windows 安装程序建立的安装，请使用 Windows 应用列表中的卸载入口。

## 首次配置

安装不会自动登录外部服务。Dashboard 需要核对采集数据源；助手按需要配置微信、模型和任务控制。完整步骤见 [开始使用](getting-started.md)。

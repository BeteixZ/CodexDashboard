# 独立助手离线备份

需要 PowerShell 7。先从托盘退出要备份的独立助手，再在此发行目录打开 PowerShell，将以下示例替换为实际的绝对路径：

```powershell
./Backup-AssistantData.ps1 -DataDirectory 'D:\AssistantData' -BackupDirectory 'D:\PrivateBackups\Assistant-20261021'
./Test-AssistantBackup.ps1 -BackupDirectory 'D:\PrivateBackups\Assistant-20261021'
```

数据目录可在助手设置的数据源页查看。备份命令不会自动查找真实数据目录。目标必须不存在且位于源目录之外；目录联接不受支持。运行中的独立助手持有排他租约，备份会失败，请退出后使用新的目标目录重试，不要手动删除锁文件。

完整副本保存在目标的 `data` 子目录；最后生成 `backup-manifest.json`，记录文件长度与 SHA-256。失败时保留现场，没有完整清单的副本不能视作已完成。验证器核对文件集合与字节，不修改备份。

备份包含个人数据和原样复制的受保护凭据文件，应保存在当前用户控制的位置。它不解密凭据；跨用户或机器不保证能解密。删除记忆不会删除已有备份，备份由用户单独管理。

完整性通过只说明文件与清单一致，清单不是签名。它不证明业务数据有效、旧版本兼容或可以安全重新执行外部操作。没有自动恢复入口：不得覆盖正在使用的数据目录，也不得恢复旧快照后直接启动接收器。正式恢复需核对新收件、发送回执、结果未知的操作、记忆撤销和订阅进度，避免重放副作用。

该租约仅约束遵守协议的独立助手，不替代旧 Dashboard 的停机与禁止重启证明。本工具不执行旧版迁移。

## 准备暂停的恢复工作目录

```powershell
./Prepare-AssistantRecovery.ps1 -BackupDirectory 'D:\PrivateBackups\Assistant-20261021' -RecoveryDirectory 'D:\Recovery\Assistant-review'
```

目标必须不存在且位于备份之外。工具先验证备份，再复制到目标的 `data` 子目录；复制前写入持久暂停标记，失败也保留暂停现场。随后调用包内程序生成 `inspection.json`，不启动接收器。备份原件保持不变；目标会额外包含新的暂停标记，因此不是原备份的字节一致镜像。

准备成功只表示可以继续审阅，不代表获准恢复运行。当前没有正式解除暂停或发布入口，不能删掉标记后直接启用旧快照。跨用户解密失败或账本损坏时工具退出，已复制的数据不自动删除。

# 参与开发

欢迎提交可复现的问题、文档修正和小范围改进。新功能或架构调整请先通过 Issue 说明使用场景、预期行为和影响范围，再开始较大的实现。

## 开发环境

- Windows x64。
- .NET 10 SDK。
- PowerShell 7。
- Python 3.10 或更高版本，用于采集器开发和测试。
- Node.js，用于网页模型测试。

```powershell
./Build-Products.ps1
```

默认产物位于新的 `artifacts/builds/<时间>`，分别包含两个产品。公开安装包的构建方式见 [发布流程](docs/releasing.md)。

## 修改与验证

保持修改范围清晰，说明问题的触发方式、修改后的行为和验证结果。涉及采集、控制、持久化或恢复时，补充能区分错误行为的回归检查；普通文案调整不需要新增实现镜像测试。

按改动范围选用检查，例如：

```powershell
python -m unittest discover -s tests -p "test_*.py"
node --test tests/web_model.test.mjs
dotnet run --project tests/Assistant.Core.Tests/Assistant.Core.Tests.csproj -- --conversation-loop-tests
dotnet run --project tests/Assistant.Core.Tests/Assistant.Core.Tests.csproj -- --dependent-work-tests
```

其他协议、控制、迁移及安装测试位于 `tests`。部分检查是可执行测试项目，需用 `dotnet run`，不能仅以 `dotnet test` 成功作为已执行证明。针对 Windows 窗口、安装程序和真实外部服务的验证，应单独记录操作过程及结果。

## 使用隔离数据

开发和测试应使用新建的数据目录、合成账号及专用测试任务。不要让测试程序接管正在使用的绑定、凭据或工作任务。模拟服务测试、接口受理、实际任务完成和手机展示是不同验证范围，应分别说明。

模型调用会消耗账户额度；微信测试会真实发送消息。运行这些检查前，应确认具体测试已获授权。同一微信绑定同时只运行一个接收者。

## 提交前检查

- 不提交构建产物、私有日志、凭据、访问密钥、真实会话内容或个人绝对路径。
- 示例使用 `<host>`、`<task-id>` 或通用示例目录，保持运行数据在仓库外。
- 更新相关用户文档，区分已经实现、已经验证和仍有限制的范围。
- 保留第三方许可证与必要声明。

安全漏洞请按 [安全说明](SECURITY.md) 报告。提交贡献表示你有权提交这些内容，并同意按项目 [MIT 许可证](LICENSE) 分发。

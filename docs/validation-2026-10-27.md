# 跨平台迁移验收记录

版本：1.2.0。工具链：.NET SDK 10.0.403、Avalonia 12.1.4、Herdr 0.9.3。

## 自动化与构建

| 环境 | 锁定依赖还原 | Release 构建 | 自动化测试 |
| --- | --- | --- | --- |
| Apple Silicon / macOS 26.6 | 通过 | 零警告、零错误 | 81 通过，0 失败 |
| Windows 11 专业版 x64 / 10.0.26300 | 通过 | 零警告、零错误 | 69 通过，0 失败 |

原 Swift 版的 53 项场景均已迁移，新增路径边界、显式代理、防直连回退、日志轮换、进程超时、停止竞争、缺失身份和协议边界测试。macOS 还对命名管道与 Unix socket 执行同一组传输/HTTP 测试，因此比 Windows 的原生命名管道组合多 12 项。

测试使用单调时钟推进冷却时间；真实终端端到端验收另外等待实际的 10 秒。TRX 文件位于 `dist/qa/macos-tests/` 和 `dist/qa/windows-tests/`。

## Windows 真实桌面

通过 SSH 构建并部署，在 alex 已登录的桌面会话 1 中运行临时交互式计划任务，以 UI Automation 操作实际窗口。未将后台 SSH 进程启动成功作为图形界面验收结论。

最终一轮结果：

- 初始为停止状态；启动后显示隔离会话、容量故障、独立倒计时和代理状态。
- 冷却期间关闭窗口，随后等待 11 秒，测试 agent 未收到输入。
- 重新打开，使用 Ctrl+Enter 启动，约 **10.85 秒**后收到一次完整的“继续”及回车。
- Herdr 确认 agent 进入 working；继续观察后未收到重复输入。
- 停止按钮、运行中关闭窗口、日志文件定位均通过。
- 已检查中文显示、当前远程桌面缩放和最小窗口布局截图。
- 从实际 ZIP 解压到含中文和空格的目录，令 `DOTNET_ROOT` 指向不存在的目录，Release 图形程序仍正常启动并可关闭。

实际 Herdr 仅执行只读探针，成功读取 3 个 agent。实际 Clash Party 命名管道成功读取全局模式、7890 混合端口及 15 个日本节点。该机器当前的 HTTPS 代理连通检查失败；未人为修改网络或切换真实节点。故障恢复、测速排序、切换确认和手动切换竞争使用隔离服务测试。

结果：`dist/qa/windows/result.json`。截图：同目录下的 `01-stopped.png`、`02-cooldown.png`、`03-minimum-size.png`、`04-resumed.png`、`05-release-portable.png`。

## macOS 真实窗口与发布包

- Debug 应用在隔离 Herdr 会话中显示真实窗口并捕获渲染结果。
- 默认停止；关闭窗口清除倒计时、停止 agent 和代理监控，等待超过冷却时长后仍未发送。
- 重新启动后，从识别容量故障到发送约 **10.15 秒**，Herdr 确认 working，测试 agent 记录一次“继续”及回车，没有重复输入。
- 停止再启动和运行中关闭窗口均通过。
- `.app` 包及 ZIP 解压后的应用均通过 `codesign --verify --deep --strict`。
- 解压后将 `DOTNET_ROOT` 指向不存在的目录，Release 窗口仍正常启动，确认使用随包运行时。
- 只读探针成功读取真实 Herdr 和 Clash；macOS 的代理连通检查通过。

结果及截图位于 `dist/qa/macos-cancel/`、`dist/qa/macos-resume/`。后者同时保留收到输入的凭据和完整测试操作日志。

## 交付与清理

| 文件 | SHA-256 |
| --- | --- |
| `dist/HerdrAutoContinue-win-x64.zip` | `b3dbf61bf82edcd97e015df25fef103ec96cf520253fbd57278f0f7a93d7d8b9` |
| `dist/HerdrAutoContinue-osx-arm64.zip` | `dda08966276fe7ab883cb42af3f3cdeec052a328fedfae7580eb3132757a4e9d` |

构建产物、截图及 TRX 按仓库规则保存在被忽略的 `dist/` 中。测试创建的 Herdr 会话和临时计划任务已清理，保留真实默认会话。源码和脚本不含 SSH 密码。

Windows 10、Intel macOS 尚未进行实机验收；Windows 包未进行商业代码签名，macOS 使用本地签名、未进行 Apple 公证。GitHub Actions 已配置双端检查与打包，本轮未推送或触发远端 CI。

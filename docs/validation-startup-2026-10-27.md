# 自启动与后台运行验收

此记录对应最初的 LaunchAgent 实现。macOS 已改用原生登录项；最新双端系统列表及静默启动验收见 [原生登录项验收](validation-native-startup-2026-10-27.md)。

## 功能行为

- 主窗口新增“设置”，提供“登录后自动启动并运行续接服务”开关、应用保存和取消。旧版本升级默认关闭。
- Windows 登记当前用户的 Run 项；macOS 写入当前用户 LaunchAgent。登录启动不显示主窗口，服务自动运行；macOS 同时隐藏 Dock 图标。
- 开启后手动启动新进程也自动运行服务。已有实例时只唤醒窗口，保留手动停止状态；静默重复启动不唤醒窗口。
- 关闭窗口隐藏到托盘／菜单栏，保留后台服务；菜单提供显示窗口、设置、启停和退出，窗口也提供“退出应用”。退出会停止服务并取消待发送操作。
- 自启动开关不改变当前运行状态，通用设置与地区设置分别保存。保存失败回滚系统启动项；损坏设置、失效启动项及移动后的路径暂停自动运行并提示。
- 单实例使用用户目录中的独占文件锁与本地管道，跨进程验证不同 TMPDIR、崩溃后重新启动和长作用域名称。锁文件保留，进程退出时由系统释放独占锁。

## 构建与自动化

| 环境 | Release 构建 | Release 测试 | 桌面启动场景 |
| --- | --- | --- | --- |
| 本机 macOS / Apple Silicon | 0 警告、0 错误 | 175 通过，0 失败 | 7 项通过 |
| 远程 Windows x64 | 0 警告、0 错误 | 162 通过，0 失败 | 7 项通过 |

新增 20 项用例，覆盖默认值、独立设置、损坏或未来版本设置、系统登记缺失、保存和登记失败、回滚失败、并发保存、plist 参数、Windows 注册表读写与命令转义及长度、跨进程单实例。macOS 多出的 13 项来自已有 Unix socket 传输组合。

两端桌面验收覆盖：取消不保存、启用、登录静默启动、手动启动自动运行、窗口关闭继续运行、单实例唤醒、手动停止后再次打开、关闭自启动、关闭后的普通与静默启动。系统退出请求路径不取消退出，显式退出确认服务与代理监控均停止。

## 平台实测

- macOS 使用隔离 Debug `.app`，在当前用户目录登记真实 LaunchAgent，通过 `launchctl bootstrap gui/<uid>` 启动。通过 CoreGraphics 确认无可见普通窗口，并通过 NSRunningApplication 确认 accessory 激活策略，即无 Dock 图标。
- Windows 在已登录桌面 Session 1，以普通用户权限运行隔离 Debug 应用，读取实际生成的 Run 项并按其完整参数启动；UI Automation 确认静默阶段没有该进程的桌面窗口。
- Windows 创建隔离 Herdr 会话与测试 agent，自动启动后识别错误并提交一次“继续”和回车，收到唯一回执；测试 agent 记录等待时间为 13.321 秒，应用事件确认续接完成。现有完整 10 秒冷却和代理判定回归测试通过。
- macOS 原有生产监控仍在运行，会同时识别测试会话，因此最终 macOS 桌面验收未创建测试 agent；实际续接集成验证在 Windows 完成。本机生产服务保持原状态。
- 本次没有实际重启或注销系统。验收直接使用真实 LaunchAgent 和 Windows 登记命令，验证启动入口与后台行为；系统退出通过应用的同一退出处理路径验证。
- 临时 LaunchAgent、Windows Run QA 项、QA 计划任务、隔离应用与运行目录已清理，未修改生产自启动设置、地区选择或真实 Clash 节点。

## 可复用验收入口

- macOS：`python3 scripts/qa-startup-macos.py --app-bundle <Debug.app> --output-directory <report-directory>`。
- Windows：在交互桌面运行 `scripts/qa-startup-windows.ps1 -AppPath <Debug/HerdrAutoContinue.exe> -OutputDirectory <report-directory>`；增加 `-FakeAgentPath <codex.exe>` 验证实际自动续接。
- 两端可选测试 agent 验证都应在没有生产监控实例的环境运行，脚本会检查常规生产实例以避免干扰。
- 报告与截图：`dist/qa/watchdog-qa-startup-native-macos-complete/`、`dist/qa/watchdog-qa-startup-ui-windows-agent/`。
- Release 测试结果：`dist/qa/startup-macos-release-tests/`、`dist/qa/startup-windows-release-tests/`，Windows 最终结果为 `16_26_36` 的 TRX 文件。

此次验收未覆盖安装本机应用或发布新版本。

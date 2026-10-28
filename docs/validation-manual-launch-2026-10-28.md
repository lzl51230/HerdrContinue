# 手动启动服务验收

手动打开新的主进程始终运行续接服务；登录自启动开关仅控制系统登录后的静默启动。已有进程被再次打开时仅显示窗口，保留手动停止状态。两端共用同一启动策略，设置格式未变。

## 验证结果

- 修改启动策略前，新增回归断言出现 5 项失败，覆盖登录自启动关闭、通用设置损坏和系统登记缺失时的手动启动；修改后通过。
- 使用仓库内 `.tools/dotnet/dotnet.exe`（SDK 10.0.403）完成 Release 构建，0 警告、0 错误。
- 完整测试共 201 项：200 项通过，`InfrastructureTests.TranscriptRejectsSymlinkEscape` 因当前 Windows 账户缺少创建符号链接的权限失败。单独排除该用例后，200 项全部通过，未修改或跳过该用例的源码。
- Windows 桌面隔离 Debug 验收通过：首次手动打开、关闭登录自启动后的手动打开、损坏设置不被覆盖、失效登记仍允许手动运行、有效登录启动无桌面窗口、关闭窗口继续运行、重复打开保留手动停止状态、设置取消与保存、关闭登录自启动不停止当前服务，以及显式退出和系统退出请求。
- 地区选择及恢复、跨订阅源恢复与取消、六套主题及持久化回归通过。

## 复用入口与产物

- 启动：`scripts/qa-startup-windows.ps1`。
- 地区、订阅源、主题：`scripts/qa-regions-windows.ps1`、`scripts/qa-sources-windows.ps1`、`scripts/qa-themes-windows.ps1`。
- 报告与截图：`dist/qa/manual-launch/`。Debug 应用使用隔离设置、会话及代理夹具。
- 新版自包含便携包：`dist/HerdrAutoContinue-win-x64-manual-launch.zip`，含 `HerdrAutoContinue.exe` 和 `WatchdogProbe.exe`。打包时使用独立输出目录，避免影响运行中的旧实例；随后按用户要求正常退出旧版，将新版替换到原发布目录并启动，已确认自动监控 2 个 agent。登录启动项继续使用原路径。

本次未在 macOS 实机验证原生登录事件，也未重启或注销 Windows。由于已有生产实例运行，本次未创建测试 agent 进行实际续接输入；冷却、续接和停止取消由现有自动化测试覆盖。

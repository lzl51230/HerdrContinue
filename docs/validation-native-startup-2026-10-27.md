# 原生登录项及系统启动列表验收

日期：2026-10-27。环境为本机 Apple Silicon macOS，以及 `alex@192.168.100.123` 的 Windows 11 x64 已登录桌面。

## 实现及状态

macOS 改为在应用进程内调用 `SMAppService.mainApp`，登记在“系统设置 → 通用 → 登录项 → 登录时打开”。设置面板分别显示已启用、等待许可及未登记状态；已保存的开启意愿与系统实际生效状态分开处理。外部关闭或移除后不会在刷新设置或仅修改主题时自动重新登记。

系统返回 `requiresApproval` 时，用户明确开启自启动或点击“处理登录项许可”会看到原生 NSAlert 说明框，可选择“前往设置”或“稍后处理”。前者调用 `openSystemSettingsLoginItems`。此说明框不授予系统权限，也不冒充 macOS 授权对话框；许可仍须由用户在系统设置中处理。[Apple：SMAppService](https://developer.apple.com/documentation/servicemanagement/smappservice)

保存时保留原有回滚逻辑。旧 LaunchAgent 仅在标签与完整启动参数匹配当前应用、且已保存开启意愿时迁移。原生登记成功或等待许可后删除旧文件，防止旧入口绕过许可；迁移失败保留或恢复旧文件并提示。不会卸载当前正在运行的旧任务。

原生登录启动通过 `kAEOpenApplication` 的 `keyAELaunchedAsLogInItem` 识别，无需 `--autostart` 参数。登录入口不创建主窗口、不显示 Dock 图标且不抢焦点；再次手动打开显示已有窗口，保持本次手动停止状态。

Windows 保持当前用户的 `Run` 注册方式，测试同时检查真实系统列表和所登记的完整命令。系统列表中禁用应用后，Run 值仍存在，因此应用内只标注“已登记”，并提示在 Windows 启动应用中查看启用状态，不把注册表存在等同于系统允许启动。[Microsoft：Run 注册表项](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)

## 验证结果

| 检查 | 结果 |
| --- | --- |
| macOS Release 构建与测试 | 0 警告、0 错误；205 通过，0 失败 |
| Windows Release 构建与测试 | 0 警告、0 错误；192 通过，0 失败 |
| macOS 系统列表 | 登记后“登录时打开”增加一项，名称与图标正确；BTM 记录的 bundle ID 和路径与独立 QA 应用一致；关闭后列表恢复基线 |
| macOS 原生登录事件 | 无可见主窗口、accessory 激活策略、未成为前台应用；服务自动启动；重复登录事件及手动重新打开均保留手动停止状态 |
| macOS 原生说明框 | 实际显示并截图；“前往设置”和“稍后处理”均执行完成 |
| macOS 主题 GUI 回归 | 六套主题、控件状态、预览取消、保存失败、重启恢复及隐藏启动恢复全部通过；服务及登录登记保持原状态 |
| Windows 系统设置 → 应用 → 启动 | 显示 Herdr Auto Continue 及图标；确认开启、系统禁用和重新开启；应用关闭自启动后消失 |
| Windows 任务管理器 → 启动应用 | 显示 HerdrAutoContinue.exe 及图标；确认已启用、已禁用；关闭自启动后消失 |
| Windows 桌面生命周期 | 7 个场景通过，覆盖静默启动、手动启动、取消、关闭窗口、单实例、停止后唤醒、关闭自启动及退出 |
| macOS Release 包 | 本地签名与严格校验通过；原生 dylib 含 arm64、x86_64；Release 无自动操作说明框的 QA 导出函数 |

新增用例覆盖五种原生状态、等待许可、外部禁用、主题保存不触碰系统登记、迁移成功或失败、外部文件保护及设置保存失败回滚。两端测试数量差异来自已有 Unix socket 平台用例。

## 证据与复现

本地产物均位于忽略提交的 `dist/qa/`：

- `watchdog-qa-native-macos-verified/result.json`：原生登记、静默启动、移除及提示框结果；同目录包含系统列表和原生提示框截图、BTM 条目及 `login/launch.log`。
- `watchdog-qa-native-themes-macos/result.json`：主题 GUI 回归。
- `watchdog-qa-native-startup-windows-verified/result.json`：Windows 7 个场景及两个系统列表结果；同目录包含 `settings-{enabled,disabled,removed}.png` 和 `task-manager-{enabled,disabled,removed}.png`，均已目视检查。
- `native-login-macos-final-tests/`、`native-login-windows-final-tests/`：Release TRX。
- `native-login-release-build.log`：macOS Release 打包日志。

macOS：`python3 scripts/qa-startup-macos.py --app-bundle <Debug.app> --output-directory <新目录>`。脚本使用独立 bundle ID、签名、配置和目录，验证结束后取消登记并清理隔离应用。

Windows：`scripts/invoke-startup-qa-windows.ps1 -AppPath <Debug/HerdrAutoContinue.exe> -OutputDirectory <新目录>`。应用及主验收任务以 Limited 权限运行；独立 Highest 验证进程仅用于读取 elevated Task Manager 的 UI Automation 和操作本次 QA 启动项。临时任务、QA Run 项及其 StartupApproved 残留均清理。

Windows 设置和任务管理器均会缓存列表；验收刷新设置页面，并在系统切换启用状态后重新打开任务管理器。读取注册命令时验证 QA 值名及独立应用路径；若出现多个相似条目则停止，避免操作生产项。

## 验证边界

本次没有实际重启或注销。macOS 使用真实 Apple event 模拟登录启动原因，Windows 使用真实 Run 命令启动；不将这些检查表述为完成真实重启验收。

本机 QA 登记直接返回 Enabled，未人为改变系统许可数据库来制造 `requiresApproval`。等待许可与迁移分支由单元夹具覆盖；原生说明框及两个按钮使用仅 Debug 可用的定时按钮操作独立验证。

隔离验收期间，生产应用、服务、地区配置及 Clash 节点保持原状。

## 本机覆盖安装

随后按用户要求于 2026-10-27 18:54 覆盖安装到 `/Applications/Herdr Auto Continue.app`，旧版正常退出并保留带时间戳的备份。新版严格签名校验通过，启动后自动恢复续接服务，原有通用设置及地区设置的文件哈希均保持不变。

生产旧 LaunchAgent 文件已迁移清理；BTM 记录确认生产 bundle ID `local.alex.herdr-auto-continue` 指向 `/Applications/Herdr Auto Continue.app/`，状态为 `enabled, allowed, notified`。覆盖安装同样未重启或注销系统，未发布新 Release。安装基线及 BTM 证据保存在 `dist/qa/native-login-local-install/`。

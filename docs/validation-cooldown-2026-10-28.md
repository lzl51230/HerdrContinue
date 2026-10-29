# 介入冷却验证（2026-10-28）

冷却默认 5 秒，设置范围为 1～300 整数秒，保存后运行中生效。首次识别符合条件的 agent 错误立即复核；后续只等待距上次提交尝试剩余的时间。网络首次探测失败立即开始恢复，每次自动节点切换独立遵守网络的切换间隔。每个 agent 与网络分别计时。

## 自动化结果

- macOS：完整测试 243 项通过；新增保存失败、偏好保留、停止后重启等用例后，定向测试 38 项通过（含其中 4 项新增用例）。
- Windows：完整测试 229 项通过；同样的定向测试 38 项通过。平台相关测试数量存在差异。
- 两端另各通过 1 项真实计时器测试：冷却设为 1 秒，模拟代理服务实际收到两个不同节点的选择请求，确认请求间隔。该项使用真实 Task.Delay，网络服务仍为隔离替身。
- 覆盖首次立即续接、5/10 秒边界、剩余时间等待、独立 agent、重连不重置、证据变化、身份复核、明确未发送重试、结果不明去重、设置动态延长与缩短，以及切换等待期间的取消、手动变更和网络恢复。
- 两端 Debug 桌面构建与独立发布通过；git diff --check 通过。

## 桌面验收

macOS 本机与 Windows 远程机 alex@192.168.100.123 均完成真实 Avalonia 窗口检查：默认值和范围、取消保留旧值、应用并保存、主界面即时显示配置、重新打开与进程重启恢复，以及默认/最小窗口尺寸截图。

Windows 在 alex 已登录的交互桌面会话中通过临时计划任务运行；任务已移除。验证使用独立目录、隔离设置及单独的 .NET 10 SDK，未替换现有应用。未运行真实代理节点切换或真实用户 agent 输入；恢复逻辑由隔离测试覆盖。

验证材料：

- macOS 测试结果：`dist/qa/cooldown-macos-tests/cooldown.trx`
- macOS 桌面报告与截图：`dist/qa/cooldown-macos-v2/`
- Windows 桌面报告与截图已取回：`dist/qa/cooldown-windows/`
- Windows 完整测试结果位于远程 `C:\Users\alex\herdr-cooldown-20261028\dist\qa\windows-tests\cooldown.trx`
- Windows 界面验收入口：`scripts/qa-cooldown-windows.ps1`，需在登录桌面会话运行 Debug 发布目录中的应用。

网络健康探测仍为每 10 秒一次，agent 扫描仍为每 2 秒一次；“首次立即”指监测到问题后没有额外冷却等待。

## 本机覆盖安装（2026-10-29）

Release 模式完整测试 248 项通过。使用 `scripts/build-install.sh` 构建并签名本机 Apple Silicon 发布包，退出旧进程后安装至 `/Applications/Herdr Auto Continue.app` 并重新启动。安装包签名校验通过，安装目录主程序集与构建产物 SHA-256 一致。旧版备份为 `/Applications/Herdr Auto Continue.app.backup.20261029-094058`，原有用户设置保留。

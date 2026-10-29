# 独立功能开关验证（2026-10-29）

设置新增“Clash 代理自动恢复”和“Herdr agent 自动续接”，默认开启，保存后立即生效。关闭代理功能后，agent 不依赖 Clash 健康检查；关闭 agent 续接后仍显示状态。两者都关闭时保留只读监测，全局停止服务仍停止所有后台监测。

## 自动化验证

- macOS 完整测试阶段 266 项通过；最后增加的本地偏好初始化用例包含在随后通过的 18 项功能开关定向测试中。
- Windows 完整测试阶段 250 项通过；代理启动防护及初始化检查补充后，37 项相关功能、代理和监控定向测试通过。
- 覆盖四种组合、设置读写和旧配置、非法字段独立回退、部分更新、保存失败、关闭时取消发送前复核和测速、重新开启保留去重与冷却、已发请求继续记录结果、无 Herdr 时代理恢复、无 Clash 时本地偏好初始化，以及关闭的代理不能被其他启动调用重新拉起。
- 两端 Debug 自包含桌面发布成功，git diff --check 通过。

## 桌面验证

macOS 本机及 Windows 远程机 `alex@192.168.100.123` 的登录桌面会话均完成真实 Avalonia 窗口验收：四种开关组合依次保存并即时应用，全局服务状态保持运行，取消保留原设置，进程重启恢复最后保存的代理关闭、agent 开启组合。

已检查默认及最小窗口尺寸截图。Windows 通过临时交互式计划任务运行，任务结束后移除，截图与报告已取回。界面验收未对真实用户 agent 发送输入；行为测试使用隔离服务替身。

材料位置：

- macOS：`dist/qa/features-macos/initial/`、`dist/qa/features-macos/restart/`
- Windows 已取回报告和截图：`dist/qa/features-windows/`
- Windows 远程测试结果：`C:\Users\alex\herdr-features-20261029\dist\qa\windows-tests\`
- Windows 桌面验收脚本：`scripts/qa-features-windows.ps1`，需在登录桌面运行 Debug 发布目录中的应用。

## 本机覆盖安装

Release 模式完整测试 267 项通过。本机发布包签名校验通过，已退出旧进程并覆盖安装至 `/Applications/Herdr Auto Continue.app`，随后重新启动。主程序、核心与基础设施程序集均与构建产物 SHA-256 一致，原有用户设置保留。旧版备份：`/Applications/Herdr Auto Continue.app.backup.20261029-102031`。

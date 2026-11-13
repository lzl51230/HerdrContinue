# Windows UI 对齐 macOS 验收

## 实现

两端继续共用 Avalonia 窗口、主题资源和业务模型。Windows 明确使用 Microsoft YaHei UI，窗口字体回退至 Microsoft YaHei；刷新、排序、服务按钮和提示使用矢量图形。Windows 地区旗帜采用内置 flag-icons 7.3.2 图片，保留 MIT 许可证，无运行时下载。

主窗口统一表头和数据行间距，顶部操作及底部退出入口固定，agent 列表和日志各自滚动；空间不足时中间内容可整体滚动。窗口初次显示、跨屏移动和缩放改变时按所在显示器工作区限制尺寸。测速结果采用固定标题和说明、可伸缩的滚动列表。通用设置和切换设置使用同一工作区约束。

不改变业务协议、配置文件格式、代理恢复或续接规则。Windows 保留原生标题栏、托盘及登录启动入口。

## 本地验证

- .NET 10.0.403，Avalonia 12.1.4，Release 构建零警告、零错误。
- 现有测试 304 项通过。
- macOS Debug GUI：六套主题、下拉框、悬停、禁用、焦点、最小窗口、系统主题通知、预览取消、保存失败回退、重启及静默启动恢复通过。
- 新增关键按钮边界检查、12 条 agent 数据和 20 条测速结果截图；人工检查有数据列表保持独立滚动，日志及退出入口可见。
- macOS 证据：`dist/ui-parity/macos-delivery/`。

## Windows 验证方式

远程 Windows 11 用户 alex，交互桌面 Session 1。使用本机交叉发布的自包含 Debug 包；通过临时、普通用户权限的交互式计划任务运行，不要求远程安装 .NET 10 SDK。

```powershell
./scripts/invoke-ui-parity-qa-windows.ps1 -BuildDirectory <目录>
```

目录包含 `windows-debug/` 与仓库 `scripts/`。脚本运行 themes、features、cooldown、regions 四套独立子进程，使用独立 `watchdog-qa-` 配置和模拟 Clash，保存离屏截图、前台应用的真实桌面截图及 JSON。临时计划任务在结束后删除。

地区脚本的旧窗口标题已更新为“切换设置”。新增 `-UiOnly` 模式验证勾选、排序、取消、保存及重启恢复，保留原有完整恢复模式。本轮完整恢复模式在等待“代理正常”时超时，不能计为通过；UI 对齐验收显式跳过该业务时序段，不修改或放宽其断言。

Windows 最终结果：四套 UI 套件通过。六套主题及预览／回退、功能开关四种组合、冷却范围及持久化、地区勾选／排序／取消／重启恢复均通过。最终证据位于 `dist/ui-parity/windows-delivery/`，远程原始目录为 `C:\Users\alex\herdr-ui-parity-20261113\results-20261113-160205`。

实际 Avalonia RenderScaling 为 1.75；应用仅枚举到一块活动屏幕，工作区为 4096 × 2076，主窗口逻辑客户区约 1080 × 850。窗口移动后的边界检查通过；虽然硬件查询返回两个显示设备，不能据此声称完成双屏 DPI 切换测试。真实桌面截图及主窗口、地区旗帜、有数据列表截图已人工抽查。

## 交付

`dist/HerdrAutoContinue-win-x64-ui-parity.zip`，包含 .NET 运行时和 WatchdogProbe。保留当前 1.6.0 版本号，为本次未发布的工作区构建。没有覆盖已有安装或发布 Release。

系统字体抗锯齿、原生标题栏及 macOS 原生旗帜与 Windows 图片旗帜允许存在外观差异。没有修改机器全局缩放设置；除实际显示器缩放外，不声称完成其它 DPI 档位实机验证。

交付 ZIP SHA-256：`f1f3614114b6a9eeb2379b9ed0ae9b86bc5de2c989eba2eb7c2952e9e669d53f`。ZIP 完整性、DLL 与最终发布目录一致、许可证随包分发、Release 不含 QA 会话入口均已检查。

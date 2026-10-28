# 静默启动验收

日期：2026-10-28。Windows 环境为 `alex@192.168.100.123` 的 Windows 11 x64（10.0.26300）、已登录桌面会话 1。使用仓库自带 .NET SDK 10.0.403。验证在独立目录和 QA 配置中进行，正式应用保持运行。

## 结果

| 检查 | 结果 |
| --- | --- |
| Windows 锁定依赖还原、Release 构建 | 通过，0 警告、0 错误 |
| Windows 自动测试 | 216 通过，0 失败 |
| Windows Debug GUI 启动验收 | 14 场景全部通过 |
| Windows 自包含 Release ZIP | 构建成功 |
| 本机 macOS Release 构建、自动测试 | 0 警告、0 错误；230 通过，0 失败 |
| 本机 macOS Debug 构建 | 通过 |
| 本机 macOS Release 覆盖安装 | 签名校验通过；安装文件与构建产物一致；新进程及主窗口存在 |

GUI 覆盖手动与实际 Run 登记命令启动：静默关闭均显示主窗口；静默开启均不创建主窗口，并通过独立 UI Automation 检查没有桌面窗口，托盘保持可用、服务运行。开启和关闭静默均验证取消、关闭设置、保存失败、正常保存和下一进程加载。再次手动打开会显示已有窗口，保留本次手动停止服务的状态；重复登录启动不显示窗口、不重启服务。另覆盖旧配置、损坏配置、登记丢失、关闭登录启动及系统退出请求。

单元测试覆盖缺失／无效静默字段回退、设置往返保存、部分修改保留其他选项、静默保存不写登录登记，以及显示窗口与服务启动策略的独立性。

## 验收入口与产物

在 Windows 独立源码目录内，使用 SDK 10.0.403 执行锁定还原、Release 构建和测试，然后自包含发布 Debug 应用。桌面验收入口：

```powershell
./scripts/invoke-startup-qa-windows.ps1 -AppPath <Debug.exe> -OutputDirectory <新目录> -SkipSystemLists
```

本地原始证据位于 `dist/qa/silent-start-20261028/`，包括 `windows.trx`、发布日志、GUI JSON 报告和设置／错误截图。安装包位于 `dist/HerdrAutoContinue-win-x64.zip`。

ZIP SHA256：`b3f6afa79285982501b990688773982423a479d1860547033135f63488ffc25c`。

## 验证边界

登录启动通过实际 Run 登记命令触发，未重启或注销远程机器。系统启动列表同时存在同名正式版和测试版，原有列表脚本拒绝选择不明确的条目；本次通过新增 `-SkipSystemLists` 跳过列表开关操作，实际 QA 登录项仍正常登记、使用并清理。未进行 macOS 原生登录事件或 GUI 实机验收。

本机新版已安装到 `/Applications/Herdr Auto Continue.app` 并重新启动。覆盖前退出旧进程，原应用备份为 `/Applications/Herdr Auto Continue.app.backup.20261028-181111`；保留原有用户设置。

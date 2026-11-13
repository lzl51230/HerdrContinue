# 应用更新验证（2026-11-13）

## 已验证

- macOS Release 全量测试：315 通过，0 失败。Release / Debug 构建均无警告和错误；macOS 发布包生成及严格签名校验通过。
- Windows：在 `alex@192.168.100.123` 的独立目录 `herdr-update-20261113` 使用用户目录中的 .NET 10.0.403 SDK，Release 全量测试 300 通过，0 失败，发布包生成通过。
- 两端运行 `qa-update-worker.py`，实际复制发布包至临时目录，验证正常替换、回滚及杀死 worker 后的恢复，分别返回 `complete`、`rolled-back`、`rolled-back`。Windows 额外用户文件保留。
- 两端 Debug 隔离 GUI 检查通过。Windows 使用已登录桌面的 Limited 计划任务。主窗口与设置均显示新版本和更新入口，截图已检查。
- 真实更新客户端通过固定 `127.0.0.1:7890` 代理获取 GitHub latest，返回当前版本 1.6.1；同一 HTTP 请求实现跟随 HTTPS 重定向下载正式 osx-arm64 包，SHA-256 为 `7b9ac5fd8b9d2ce2b96abd6bfdc4d30d946814b46d1c086ba8e8c4dbcf34993f`，与 Release 资产一致。此检查未安装旧发布包。
- 新增自动化测试覆盖数字版本比较、预发布排除、并发检查合并、网络失败重试、显式代理与 NO_PROXY、代理错误不直连，以及 ZIP 越界路径拒绝。
- `git diff --check` 通过。

## 验证中发现并处理

macOS 普通文件复制会丢失部分 .NET 文件签名所依赖的扩展属性；安装复制改用 ditto，并在复制后重新验证签名。Windows 一次全量运行中已有冷却计时测试 `EachSwitchWaitsRemainingTimeAndSettingsWakeWaiter(extend: True)` 失败，之后完整复跑 300 项通过；未修改该测试或冷却逻辑。

## 证据位置

- macOS：`dist/qa/update-ui/{main,settings}.png`、`dist/qa/update-worker/result.json`。
- Windows 回传截图：`dist/qa/update-windows/ui/{main,settings}.png`。
- Windows 远端完整记录：`herdr-update-20261113/dist/qa/update-windows/` 下的 tests.log、build.log、worker/result.json、ui/result.json。
- 可重复运行 Windows 验收：`scripts/qa-updates-windows.ps1`；凭据不保存在脚本或报告中。

## 补充端到端验收

使用隔离构建的 Debug 1.6.1 / 1.6.2 安装包，通过真实应用中的“更新并重启”按钮触发全流程。Release 元数据、重定向和包字节流由仅 Debug 编译的测试处理器提供；其后下载、哈希、解包、校验、辅助进程、替换、重启和健康确认均执行实际实现。测试源代码副本独立于工作区，不修改正式版本号或锁文件。

| 场景 | macOS | Windows |
| --- | --- | --- |
| 服务运行时升级至 1.6.2，重启后继续运行 | 通过 | 通过 |
| 服务停止时升级至 1.6.2，重启后保持停止 | 通过 | 通过 |
| SHA-256 错误，保留 1.6.1 和服务状态 | 通过 | 通过 |
| 新版在健康确认前退出，回滚并重启 1.6.1 | 通过 | 通过 |
| 受保护目录，用户实际允许系统授权，完成升级 | 通过 | 通过 |
| 用户实际取消系统授权，保留旧版、设置及服务状态 | 通过，系统返回 -128 | 通过，系统返回 Win32 1223 |

各场景设置文件逐字节保持一致。Windows 通过 Limited 交互式计划任务启动，升级及回滚后的 GUI 报告 `administrator: false`，包括需要 UAC 的 Program Files 安装场景。macOS 授权通过、取消及 Windows UAC 的允许、拒绝由用户亲自操作系统窗口，未关闭或绕过系统安全保护。

新增证据目录：

- macOS 普通场景：`dist/qa/update-e2e-v4/cases/*/result.json`、`result.png`。
- macOS 允许授权：`dist/qa/update-e2e-auth-v4/cases/success-stopped/`。
- macOS 取消授权：`dist/qa/update-e2e-cancel-final/cases/cancel-authorization/`，包含系统取消输出。
- Windows 远端：`dist/qa/update-e2e-v4/`、`dist/qa/update-e2e-elevated/`、`dist/qa/update-e2e-cancel/`；报告与截图已回传至本机 `dist/qa/windows-e2e-evidence/`。
- 复现脚本：`scripts/qa-update-e2e.py`、`scripts/qa-update-e2e-windows.ps1`。

## 端到端测试发现的修复

1. 新版启动报告健康后，原协调器尚在完成事务时，启动恢复扫描可能与其竞争并删除通信标记。恢复扫描现在跳过启动当前实例的更新事务，并在读取协调器状态后再次确认事务仍需恢复。Windows 上复现并通过完整链路复测。
2. 本机 macOS 提权 worker 在托管的子进程退出等待中卡住，系统校验命令已经成为退出进程。系统命令改由 `posix_spawn` / `waitpid` 启动和回收，设置两分钟超时，超时强制结束并回收子进程；用户再次实际授权后，全链路通过。测试中残留的旧版本进程已清理，Windows Program Files 下的隔离安装副本也已移除。
3. 测试驱动及时回收原应用进程，避免 macOS 中未回收退出进程影响等待；测试会话按用例及输出目录区分，避免旧测试实例占用新用例。重启后的服务状态断言等待 UI 状态通知完成。

## 测试边界

未为测试创建或发布新的 GitHub Release，也未替换用户的正式应用。GitHub 生产端点和真实安装包下载已通过固定代理独立验证；高版本安装重启使用隔离测试包。现有 GitHub v1.6.1 不包含更新清单，首次仍需手动安装包含更新器的版本，之后使用修改后的打包脚本发布新版本。

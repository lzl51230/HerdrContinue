# 多订阅源验收

日期：2026-10-28。验收环境为 Apple Silicon macOS 与 `alex@192.168.100.123` 的 Windows 11 x64 已登录桌面。

## 实现

- 切换设置提供订阅源复选和地区排序。首次默认当前源，订阅 ID 与显示名称分离，原地区设置迁移后保留。来源没有排序，同地区跨源节点一起并行测速，统一选择延迟最低的有效节点。
- 来源使用 Clash Party 订阅条目；`GLOBAL`、自动选择等兼容 provider/代理组不作为独立订阅。当前订阅以有效运行配置为基础，其余订阅读取本地缓存。
- 聚合节点以稳定的来源命名空间区分重名，地区解析使用原始名称。设置保存失败回滚运行策略；仅修改地区时不重载内核。原订阅、客户端登记及基础运行配置文件保持原样。
- 服务运行期间重新检查来源内容和当前基础订阅，变化后重建聚合并放弃旧测速。停止或退出保留运行配置；恢复原配置会将来源选择恢复为基础订阅，并保留地区顺序。
- 现有主/备用地址规则、当前节点检查和连续两次超过 800 ms 的超时判定继续适用。未勾选的来源不参与自动恢复。

## 验证

| 项目 | 结果 |
| --- | --- |
| macOS Release | 215 项测试通过；构建与签名打包通过 |
| Windows Release | 201 项测试通过；零警告、零错误 |
| 双端真实窗口 | 默认仅 A；勾选 B 后取消不保存；保存 A/B 后选择 B 的日本节点；更快但未勾选的 C 不参与；显示源名称 |
| 重启恢复 | 已保存源复选恢复；双端窗口复选状态恢复检查通过 |
| HTTP 接入 | Unix socket / Windows 命名管道上的来源发现、聚合加载、跨源同名节点选择回读和还原通过 |
| 真实 Mihomo 隔离内核 | 双端聚合和同名节点选择成功；无效配置返回错误但保留原选择；恢复基础配置移除聚合节点；源文件未改变 |

新增测试覆盖来源持久化、旧格式兼容、同地区跨源并发屏障、跨源候补、未选源排除、原始名称地区解析、空源、聚合名称、HTTP 加载/还原及保存失败恢复。两端数量差异来自 Unix socket 传输组合。

本地证据位于忽略提交的 `dist/qa/sources/`：`macos-final-tests/`、`sources-windows-final-tests/`、`macos-gui-final/`、`macos-gui-reload/`、`watchdog-qa-sources-windows-verified/`、`watchdog-qa-sources-windows-reload/`、`macos-core/`、`sources-windows-core-final/`。GUI 报告包含复选、地区汇总与恢复后截图，已检查双端布局。真实内核使用独立临时目录及 IPC 接口，混合端口为 0，不操作生产 7890 服务。

## 复现入口

- `dotnet test Tests/Watchdog.Tests -c Release`。
- `dotnet run --project Tests/Watchdog.ClashRuntime -- <mihomo路径> <新报告目录>`：启动并清理隔离内核，验证真实配置加载和选择。
- `dotnet run --project Tests/Watchdog.ClashRuntime -- --catalogue`：只读显示本机来源目录；不会加载配置、测速或切换节点。
- Debug GUI：设置 `WATCHDOG_QA_SESSION=watchdog-qa-<名称>`、`WATCHDOG_QA_SETTINGS_DIR=<watchdog-qa-独立目录>`、`WATCHDOG_QA_AUTOMATION_DIR=<报告目录>`、`WATCHDOG_QA_SCENARIO=sources`，运行 Debug 应用。`sources-reload` 使用同一设置目录检查重启恢复。
- Windows：在已登录桌面运行 `scripts/qa-sources-windows.ps1 -AppPath <Debug.exe> -OutputDirectory <watchdog-qa-新目录>`；重启检查增加 `-Reload -SettingsDirectory <上轮目录>`。远程使用临时 Limited 交互任务运行，任务结束后清理。

## 边界

支持本地缓存内联节点及已有缓存的 provider。provider 自定义覆写/过滤、缺失缓存或无法解析的节点依赖会显示不可用原因；其他源继续参与。订阅下载、远程订阅更新及脚本覆写继续由 Clash Party 管理，不执行未启用订阅的脚本。若当前正在使用的跨源节点丢失缓存，则保留运行配置，并提示恢复原配置后重新应用，避免重载时移除当前出口。

只在全局模式和唯一管理 7890 的内核上启用聚合。配置加载前复核选择与配置指纹，变化后放弃旧操作；外部修改与 HTTP 提交之间仍存在很短的竞争窗口，提交后继续读取实际状态确认。

隔离验收期间未修改真实订阅或切换生产节点，后台生产应用保持运行。测试确认的是跨源节点选择，不会把 Clash Party 的单份“当前订阅”登记改成获胜源。

## 本机覆盖安装

按用户要求于 2026-10-28 15:35 覆盖安装到 `/Applications/Herdr Auto Continue.app`，旧版正常退出并保留带时间戳的备份。新版签名校验通过，实际运行文件与已验证的构建产物一致，并已恢复运行续接服务。

地区顺序仍为日本、新加坡、美国；通用设置（含主题与自启动）保持原样。地区设置升级到新版格式，订阅源默认仅选中当前源。安装前后 Clash 配置文件哈希与实际选中节点均保持一致。证据位于 `dist/qa/sources/local-install/result.json`。

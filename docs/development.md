# 开发与验证说明

[返回中文使用说明](../README.md) · [English user guide](../README.en.md)

这里保留运行依赖、恢复规则、工程结构、构建和验收入口，供开发与排障时查阅。日常安装和使用请先看 README。下文命令均从仓库根目录执行。

项目使用 C# / .NET 10 与 Avalonia 12，macOS 和 Windows 共用业务核心、系统接入层及中文图形界面。

## 运行依赖与平台支持

- Windows 10 22H2 / Windows 11 x64：解压 `HerdrAutoContinue-win-x64.zip`，运行 `HerdrAutoContinue.exe`。包中包含 .NET 运行时，无需单独安装 .NET。
- macOS 14+：打开 `Herdr Auto Continue.app`。默认发布 Apple Silicon 版，可指定 `RUNTIME=osx-x64` 构建 Intel 版。
- 需要本机 Herdr；当前协议验收版本为 Herdr 0.9.3。优先使用 `HERDR_BINARY` 指定的完整路径，其次搜索 PATH；macOS GUI 另有 `~/.local/bin/herdr` 回退路径。
- Clash 监控使用本机 `127.0.0.1:7890`。实际代理可用时，即使控制接口不可用，也允许 agent 续接；实际代理不可用时暂停续接。
- Windows 11 x64 与 Apple Silicon macOS 已完成实机验收。Windows 10 和 Intel macOS 是兼容/构建目标，尚未进行实机验证。

窗口展示 agent 状态、判定原因、独立倒计时、续接次数，以及代理状态、已选地区、当前测速地区和本轮结果。“打开日志文件”分别调用资源管理器和 Finder。

## 行为边界

- 每 2 秒扫描一次，通过 Herdr CLI 发现会话，通过本地换行 JSON 协议读取和提交。macOS 使用 Unix socket；Windows 使用命名管道，不需要辅助功能权限或切换 Herdr 焦点。
- 当前可靠解析 Codex 和 OMP。正常完成、人工中断、授权对话、认证、证书验证、额度、限流和上下文问题不会触发续接。其他 agent 显示在列表中，但不自动输入。
- Codex 仅识别当前空输入框前的原生错误块。OMP 还会核对当前会话 JSONL 的最后一条消息。草稿、未知布局、缺少进程身份或无法读取证据时暂停输入。
- 发送前再次核对终端、进程、pane、会话身份及当前失败证据。Herdr 0.9.3 没有原子条件提交参数，复核和提交之间仍存在短暂外部状态竞争窗口。
- 提交请求使用服务端 working 等待。明确未发送时重新冷却；发送结果不明时保留已尝试状态，等待新的运行或失败证据，避免重复输入。停止再启动保留进程内的该状态，应用退出后不持久化它。
- 监控范围是本机 Herdr 会话，不修改 Herdr。多源功能仅热加载 Clash 运行配置，保留原始订阅文件和客户端登记。自启动由用户在设置中开启，登记在当前用户的登录环境中；SSH 仅用于开发和验收。

## Clash 故障恢复

- 每 10 秒并行检查 Google 和 Cloudflare 的 HTTPS `generate_204`，任一地址返回 204 且延迟不超过 800 ms 即视为可用。强制通过 7890 代理，不采用环境变量绕过代理，也不回退直连。
- 全局统一规则：首次成功但延迟超过 800 ms，立即复测同一地址；两次均超过 800 ms 视为节点超时。复测不超过 800 ms 则通过，采用第二次延迟。两个探测地址分别判断，任一通过即健康；800 ms 本身通过，保留每次请求 5 秒的等待时限。
- 一轮失败即暂停续接；当前节点确认连续高延迟超时后立即按已选地区恢复，普通请求失败仍需连续两轮确认。切换前后的实际连通性验证也遵循同一规则。代理恢复后，符合条件的 agent 重新等待完整的 10 秒。
- 自动发现 Clash Party 的 Mihomo 控制接口：macOS 查找当前用户的 Unix socket，Windows 枚举 MihomoParty 命名管道。校验 Mihomo、7890 混合端口和唯一控制接口。
- 只在全局模式的 GLOBAL 选择器中切换，不修改模式。只选择已勾选地区内、GLOBAL 可直接选择的代理叶子节点；DIRECT、REJECT、组节点和未识别地区的条目不参与。
- 先尝试当前已勾选地区；该地区全部不可用后，从保存的排序首位继续，跳过本轮已经尝试失败的地区。当前地区未勾选或未知时，直接从排序首位开始。
- 逐地区测速，同地区全部节点同时开始，各节点独立复测慢响应；全部完成后选择通过节点中延迟最低的节点。主地址无通过节点时，备用地址只重试请求失败的节点；已确认连续高延迟超时的节点本轮直接排除，下一恢复轮可重新参与。首次慢响应后复测请求失败，仍属于请求失败。只比较当前地区本轮不超过 800 ms 的有效结果，进度按节点数统计。
- 切换后读取实际选择并验证代理；失败则尝试同地区下一节点，该地区全部失败后才进入下一个地区。所有已选地区失败后等待 10 秒开始新一轮，允许重试此前失败的地区。
- 手动选择、节点列表或控制接口变化会使旧结果失效。停止服务或退出应用取消待执行操作，保留已经选中的节点。

## 多订阅源恢复

“切换设置 → 订阅源”支持复选 Clash Party 订阅管理中的远程订阅和本地配置，包括当前未启用的订阅。首次升级仅勾选当前订阅，原有地区顺序保持不变；新源默认不勾选。地区页按已勾选源汇总节点，订阅源本身没有优先级。

同地区的已选源节点一次全部并行测速，统一按延迟排名；最快节点实际验证失败时，可尝试另一源的同地区候补。地区回退和连续两次超过 800 ms 的全局超时规则继续生效。未勾选源不参与自动切换；不勾选任何源或地区时只检查当前代理连通性。

应用读取 Clash Party 本地缓存，以当前有效配置为基础追加所选源节点，通过内核热加载运行配置。节点使用独立运行名称避免跨源重名，界面与测速结果显示原节点名及订阅来源。不会执行其他订阅的脚本覆写，也不会合并它们的 DNS 或路由规则。

服务运行期间，订阅更新、Clash 重启或手动切换订阅会触发重新聚合；代理健康时保留当前节点。停止服务或退出应用保留运行配置和当前节点。“恢复原配置”恢复当前基础订阅，并将源选择恢复为该订阅，保留地区顺序。

订阅下载继续由 Clash Party 管理。支持内联节点与已缓存 provider；缓存缺失、provider 覆写/过滤、未缓存的链式依赖等无法可靠聚合的配置会显示不可用原因，不采用不完整节点定义。普通安装与便携版均自动发现；仅对唯一匹配内核的目录操作。备份位于应用设置目录中的 `ClashRuntime/`，包含配置凭据，不写入操作日志。

双端隔离验收见 [多源验收记录](validation-sources-2026-10-28.md)。

## 地区识别与偏好设置

点击代理卡片中的“切换设置 → 地区与排序”，勾选允许自动恢复的地区，用 ↑ / ↓ 调整已选地区优先级，最后点击“应用并保存”。取消或关闭设置窗口会放弃尚未应用的修改。

- 根据节点名称的国旗、常见中文地区名、英文地区名及代码识别，支持 `HK01`、`JP-02` 等编号。国旗优先，其他标记冲突时列为未识别。地区按国家／地区分组，不按城市拆分。
- 展示可切换节点覆盖的地区数、各地区节点数和未识别数量。当前选择了组时沿 `now` 解析实际叶子节点；无法确定时按未知地区处理。
- 例如排序为“香港 → 日本 → 新加坡”，当前在日本，则本轮依次尝试“日本 → 香港 → 新加坡”，不会重复尝试日本。
- 首次升级默认仅勾选日本。新发现的地区默认不勾选；已选地区暂时消失时保留其排序，显示 0 个节点，恢复时跳过。
- 没有勾选地区时继续检查代理健康，不自动测速或切换。代理健康时保留当前节点，即使其地区已取消勾选。
- 应用新设置会取消并等待旧测速任务结束，重新检查后按新顺序恢复；保存失败保留当前配置并显示错误。
- 自动运行未启用时，启动应用只读加载地区列表，服务默认停止。地区设置窗口提供手动刷新，刷新失败会保留已知列表并显示错误。

选择及排序保存位置：

- macOS：`~/Library/Application Support/HerdrAutoContinue/settings.json`
- Windows：`%LOCALAPPDATA%/HerdrAutoContinue/settings.json`

设置损坏时使用日本默认值并显示提示，不自动覆盖原文件；正常保存采用原子替换。

## 登录启动与应用生命周期

主窗口右上角点击“设置”，勾选“登录后自动启动并运行续接服务”，再点击“应用并保存”。首次安装默认关闭，升级保留已有设置；取消不保存修改。

- 开启后，当前用户登录系统桌面时静默启动应用，保留托盘／菜单栏入口，并自动运行续接服务。macOS 静默启动时隐藏 Dock 图标，Windows 不弹出主窗口或控制台。
- 开启后，手动启动新的应用进程也自动运行服务，并显示主窗口；如果后台实例已运行，再次打开只显示已有窗口，保留本次手动停止状态。
- 关闭主窗口会继续后台运行。托盘／菜单栏提供打开主窗口、设置、启动／停止服务和退出；主窗口底部也可点击“退出应用”。退出会取消待发送任务并停止代理监控。
- 开关变更不改变当前服务的启停状态。关闭自启动后，下一次登录不再启动应用；手动启动新进程时恢复默认停止。
- Herdr 或 Clash 尚未启动时，服务在后台等待并重试；代理健康判定、错误识别和完整 10 秒冷却继续生效。
- 应用按用户／桌面会话保持单实例。macOS 使用固定用户目录中的独占文件锁和固定短路径的本地管道，登录启动与手动启动使用同一实例；Windows 按用户桌面会话隔离。
- Windows 使用当前用户的 `Run` 注册表项，可在“设置 → 应用 → 启动”和“任务管理器 → 启动应用”中查看；Windows 启动命令受系统 260 字符限制，路径过长时会提示缩短路径。
- macOS 使用 `SMAppService.mainApp` 登记原生登录项，显示在“系统设置 → 通用 → 登录项 → 登录时打开”。开启自启动且旧 LaunchAgent 确认为本应用所有时，会迁移到原生登记并清理旧文件；迁移失败保留原文件并提示。
- macOS 设置面板显示系统的已启用、等待许可或已关闭状态。需要许可时，明确开启自启动或点击“处理登录项许可”会弹出原生说明框；选择“前往设置”打开系统登录项页面。说明框本身不授予权限。用户在系统关闭或移除登录项后，应用不会自动重新登记，可在应用中明确重新开启。
- 请将应用保存在固定位置；移动或覆盖升级后检查系统登记状态。macOS 登录启动通过原生 Apple event 识别，保持主窗口隐藏、不激活 Dock 且不抢焦点；保留 `--autostart` 作为显式静默启动入口。

通用设置单独保存在地区设置同目录的 `app-settings.json`。文件损坏、启动项丢失或应用位置变化时暂停自动运行并提示；保存失败会尝试回滚系统登记。地区设置和通用设置分别保存。

macOS 自启动原生验收使用 `python3 scripts/qa-startup-macos.py --app-bundle <Debug.app> --output-directory <新报告目录>`，会在独立签名 QA 应用中验证真实系统列表及原生登录事件。Windows 使用 `scripts/invoke-startup-qa-windows.ps1 -AppPath <Debug/HerdrAutoContinue.exe> -OutputDirectory <新报告目录>`；应用本身以普通用户权限运行，独立 UI 检查进程读取系统设置和任务管理器的启动列表，保存开启、禁用和移除截图。

最新结果与验证边界见 [原生登录项验收](validation-native-startup-2026-10-27.md)，其中登录事件检查未实际重启或注销系统。

## 主题状态与持久化

“设置”中的“颜色主题”提供 Light Modern、Dark Modern、Light+、Dark+、Monokai 和 Solarized Dark 六套 VS Code 风格配色，以及默认的“跟随系统”。跟随系统会自动在现代浅色与现代深色之间切换；手动指定主题后保持所选配色。

选择主题后立即预览主窗口和设置面板，点击“应用并保存”持久化，无需重启。取消或关闭设置面板恢复已保存主题；保存失败也恢复并显示错误。地区设置、测速结果和控件下拉菜单使用同一套主题，系统原生菜单保持系统外观。

主题保存在 `app-settings.json` 的 `theme` 字段，旧设置缺少该字段时默认跟随系统。无法识别的主题值仅回退配色，不会关闭有效的自启动设置。只修改主题不重写系统登录启动项，也不改变续接服务或代理监控的运行状态。

配色来源及调整见 [主题说明](theme-colors.md)。隔离 Debug GUI 验收可使用 `scripts/qa-themes-macos.py`（`--app-bundle`、`--output-directory`）或在 Windows 交互桌面运行 `scripts/qa-themes-windows.ps1`（`-AppPath`、`-OutputDirectory`），两者均保存截图及 JSON 报告。

## 日志

- macOS：`~/Library/Logs/HerdrAutoContinue/events.jsonl`
- Windows：`%LOCALAPPDATA%/HerdrAutoContinue/Logs/events.jsonl`

日志仅保存操作、状态和必要错误摘要，不保存完整对话。单文件约 2 MB 时轮换，保留一份 `.previous`；界面最多保留 500 条事件。

## 构建与测试

安装 .NET 10 SDK；`global.json` 固定 10.0.403，并允许同补丁系列更新。依赖版本及解析结果记录在各工程的 `packages.lock.json` 中。

```sh
dotnet restore HerdrContinue.slnx --locked-mode
dotnet build HerdrContinue.slnx -c Release --no-restore
dotnet test HerdrContinue.slnx -c Release --no-build
dotnet run --project src/HerdrAutoContinue
```

Windows 打包（PowerShell）：

```powershell
./scripts/build-windows.ps1
# 非默认 SDK 路径可通过 -DotnetPath 指定。
```

macOS 打包：

```sh
bash scripts/build-install.sh
# 可选：RUNTIME=osx-x64 bash scripts/build-install.sh
# 可选安装：bash scripts/build-install.sh --install
```

产物位于 `dist/`，两端均包含 `WatchdogProbe`。macOS 应用使用本地签名，尚未进行 Apple 公证；Windows 包尚未进行商业代码签名。

## 只读探针与隔离验收

```sh
dotnet run --project src/Watchdog.Probe -c Release
dotnet run --project src/Watchdog.Probe -c Release -- --clash-readonly
```

默认探针只读取会话、agent 和错误判定，不发送输入。`--clash-readonly` 只读取控制接口和实际代理连通性，不测速或切换。

`--monitor-test --socket ENDPOINT` 仅接受包含 `watchdog-qa-` 的隔离端点，在该会话执行 15 秒续接测试，并使用测试代理状态；不会读取或切换真实 Clash。Windows 的 ENDPOINT 是管道标识。

`Tests/Watchdog.FakeAgent` 发布为 `codex` / `codex.exe`，只在隔离会话中运行，记录实际收到的输入和重复输入。Debug GUI 可用 `WATCHDOG_QA_SESSION` 只监控指定的 `watchdog-qa-` 会话，并注入健康测试代理；Release 不包含此入口。

Windows GUI 验收由 `scripts/qa-windows.ps1` 驱动真实 UI Automation；通过 SSH 时使用 `scripts/invoke-windows-qa.ps1 -BuildDirectory <目录>`，在已登录桌面中创建临时交互式计划任务。构建目录应包含：

- `repo/`：此仓库及 `dist/HerdrAutoContinue-win-x64` Release 产物；
- `qa/app/`：Debug GUI 的自包含发布；
- `qa/fake-agent/`：测试 agent 的自包含发布。

脚本保存截图和 JSON 结果，清理自己创建的 Herdr 会话及计划任务。实际 Herdr 会话和 Clash 节点只做读取。macOS Debug 窗口可通过 `WATCHDOG_QA_AUTOMATION_DIR` 和 `WATCHDOG_QA_SCENARIO=cancel|resume` 执行相同生命周期检查。

## 工程结构

| 工程 | 职责 |
| --- | --- |
| Watchdog.Core | 错误分类、恢复状态机、地区识别和优先级策略、可取消监控协调器及状态快照；仅依赖 .NET 基础库 |
| Watchdog.Infrastructure | 进程发现、Unix socket / 命名管道、JSON RPC、HTTP、路径边界、设置保存及日志 |
| HerdrAutoContinue | Avalonia 窗口与 MVVM，UI 线程只负责显示和操作入口 |
| Watchdog.Probe | 只读探针及受限的隔离监控测试 |
| Watchdog.Tests | 原有 53 个测试场景的迁移及额外的平台、协议和竞争回归 |
| Watchdog.FakeAgent | 跨平台真实终端验收夹具 |

系统 I/O 在核心之外实现；操作系统判断集中在接入层、桌面入口和打包/验收脚本中。核心使用单调时钟，所有状态变更受串行协调保护，网络及进程操作支持超时和取消。

GitHub Actions 在 macOS 与 Windows 上执行锁定还原、构建、测试和打包。`.build/`、`bin/`、`obj/`、`dist/` 和测试结果为本地生成目录，不提交到 Git。

本次迁移的环境、测试结果和交付文件校验值见 [验收记录](validation-2026-10-27.md)。

多地区功能的双端验收见 [1.3.0 验收记录](validation-regions-2026-10-27.md)。

多地区 GUI 验收：macOS Debug 使用 `WATCHDOG_QA_SCENARIO=regions` / `regions-reload`，配合 `WATCHDOG_QA_REGIONS=recovery`；Windows 使用 `scripts/invoke-region-qa.ps1 -BuildDirectory <目录>`。两端均使用独立的 `watchdog-qa-` 设置目录和模拟 Clash，不改变用户的地区配置或真实节点。

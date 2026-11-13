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
- 提交请求使用服务端 working 等待。明确未发送时按上次尝试时间等待剩余冷却；发送结果不明时保留已尝试状态，等待新的运行或失败证据，避免重复输入。停止再启动保留进程内的该状态，应用退出后不持久化它。
- 监控范围是本机 Herdr 会话，不修改 Herdr。多源功能仅热加载 Clash 运行配置，保留原始订阅文件和客户端登记。自启动由用户在设置中开启，登记在当前用户的登录环境中；SSH 仅用于开发和验收。

## Clash 故障恢复

- 检测目标固定为 `https://chatgpt.com/backend-api/codex/models`，强制使用 7890 代理，不回退直连。禁用 Cookie 和自动认证，不读取 Codex 凭据，不发送模型请求。401 为预期未认证响应；其他状态和重定向均失败。
- 复用一条 HTTP/1.1 TCP/TLS 连接，每 2 秒发送心跳，完整读取有大小上限的响应。记录物理 TCP 隧道建立次数；HttpClient 透明重连也算断线，不累计不同连接的时间。保留默认 TLS 验证。
- 维持阶段：连接未满 60 秒断开计一次，连续两次触发恢复。保持满 60 秒清零并继续复用；已达标连接后来断开从零开始新一轮。首次失败后暂停续接，1 秒后重连；有效心跳后可续接，但失败次数要等满 60 秒才清零。心跳延迟不用于淘汰当前节点。
- 建链最多等待 10 秒；从最近有效心跳起连续 10 秒无有效响应即断线，响应头和响应体读取都受此期限约束。agent 续接资格依赖当前有效心跳，不再沿用旧的 20 秒健康租期。
- 自动发现唯一管理 7890 的 Mihomo 控制接口：macOS Unix socket，Windows 命名管道。只在全局 GLOBAL 选择器中切换，只使用已选订阅和地区中的代理叶子节点。
- 当前已选地区优先，再按保存的地区顺序恢复。同地区所有候选并行测速一次，经 Codex 目标返回预期 401，不超过 800 ms 才入选，按延迟排序；不复测高延迟节点。Mihomo 测速上限为 10 秒。首轮排除触发恢复的当前节点，后续轮可重试。
- 临时选中候选并读回实际选择后，创建新长连接；持续满 10 秒且收到有效心跳才接受节点并恢复续接，中途断线立即尝试下一候选。通过后复用该连接，已保持时间计入 60 秒考核。
- 全部地区失败后等待 2、4、8、16、30 秒重试，之后保持 30 秒。现有可配置切换冷却独立生效，每次提交选择前复核配置和节点。
- 心跳和候选验收结果发布前核对节点上下文；手动变更、配置或控制接口变化使旧结果失效。停止、关闭恢复及退出时取消待执行操作并关闭长连接，保留已经选中的节点。

## 多订阅源恢复

“切换设置 → 订阅源”支持复选 Clash Party 订阅管理中的远程订阅和本地配置，包括当前未启用的订阅。首次升级仅勾选当前订阅，原有地区顺序保持不变；新源默认不勾选。地区页按已勾选源汇总节点，订阅源本身没有优先级。

同地区的已选源节点一次全部并行测速，统一按延迟排名；最快节点实际验证失败时，可尝试另一源的同地区候补。候选维持 800 ms 测速门槛，并须通过 10 秒长连接验收。未勾选源不参与自动切换；不勾选任何源或地区时只检查当前代理连通性。

应用读取 Clash Party 本地缓存，以当前有效配置为基础追加所选源节点，通过内核热加载运行配置。节点使用独立运行名称避免跨源重名，界面与测速结果显示原节点名及订阅来源。不会执行其他订阅的脚本覆写，也不会合并它们的 DNS 或路由规则。

服务运行期间，订阅更新、Clash 重启或手动切换订阅会触发重新聚合；代理健康时保留当前节点。停止服务或退出应用保留运行配置和当前节点。“恢复原配置”恢复当前基础订阅，并将源选择恢复为该订阅，保留地区顺序。

订阅配置操作串行执行。切换订阅源或恢复原配置会临时挂起代理监控；期间手动停止服务或关闭代理恢复后，配置完成及失败回滚都不会重新启动监控。明确重新启动的请求会等到配置和旧连接清理结束后生效。退出应用会取消配置操作并等待必要回滚及清理完成；已经保存成功的设置仍会完成应用。

订阅下载继续由 Clash Party 管理。支持内联节点与已缓存 provider；缓存缺失、provider 覆写/过滤、未缓存的链式依赖等无法可靠聚合的配置会显示不可用原因，不采用不完整节点定义。普通安装与便携版均自动发现；仅对唯一匹配内核的目录操作。备份位于应用设置目录中的 `ClashRuntime/`，包含配置凭据，不写入操作日志。

本地工作配置、订阅和 provider 缓存的单文件上限为 **32 MiB（33,554,432 字节，包含边界值）**，按文件字节数计算。有界读取也会检查读取期间增长的文件。当前工作配置超限时停止加载；其他订阅或其 provider 缓存超限时仅将对应订阅标记为不可用。错误提示包含文件名、大小和上限，不包含配置内容。此限制不用于 JSON 转义后的热加载请求或恢复备份。

双端隔离验收见 [多源验收记录](validation-sources-2026-10-28.md)。

## 地区识别与偏好设置

点击代理卡片中的“切换设置 → 地区与排序”，勾选允许自动恢复的地区，用 ↑ / ↓ 调整已选地区优先级，最后点击“应用并保存”。取消或关闭设置窗口会放弃尚未应用的修改。

- 根据节点名称的国旗、常见中文地区名、英文地区名及代码识别，支持 `HK01`、`JP-02` 等编号。国旗优先，其他标记冲突时列为未识别。地区按国家／地区分组，不按城市拆分。
- 展示可切换节点覆盖的地区数、各地区节点数和未识别数量。当前选择了组时沿 `now` 解析实际叶子节点；无法确定时按未知地区处理。
- 例如排序为“香港 → 日本 → 新加坡”，当前在日本，则本轮依次尝试“日本 → 香港 → 新加坡”，不会重复尝试日本。
- 首次升级默认仅勾选日本。新发现的地区默认不勾选；已选地区暂时消失时保留其排序，显示 0 个节点，恢复时跳过。
- 没有勾选地区时继续检查代理健康，不自动测速或切换。代理健康时保留当前节点，即使其地区已取消勾选。
- 应用新设置会取消并等待旧测速任务结束，重新检查后按新顺序恢复；保存失败保留当前配置并显示错误。
- 手动打开新进程后自动运行服务；登录启动仅在登录自启动有效时运行服务。地区设置窗口提供手动刷新，刷新失败会保留已知列表并显示错误。

选择及排序保存位置：

- macOS：`~/Library/Application Support/HerdrAutoContinue/settings.json`
- Windows：`%LOCALAPPDATA%/HerdrAutoContinue/settings.json`

设置损坏时使用日本默认值并显示提示，不自动覆盖原文件；正常保存采用原子替换。

## 登录启动与应用生命周期

主窗口右上角点击“设置”，勾选“登录后自动启动并运行续接服务”，再点击“应用并保存”。首次安装默认关闭，升级保留已有设置；取消不保存修改。

- 开启后，当前用户登录系统桌面时启动应用，保留托盘／菜单栏入口，并自动运行续接服务。“静默启动”默认关闭，旧配置缺少该字段时也关闭；勾选后，手动和登录启动新进程都不显示主窗口。macOS 静默启动时隐藏 Dock 图标，Windows 不弹出主窗口或控制台。
- 无论是否开启登录自启动，手动启动新的应用进程都会自动运行服务，是否显示主窗口由“静默启动”控制；如果后台实例已运行，再次打开只显示已有窗口，保留本次手动停止状态。
- 关闭主窗口会继续后台运行。托盘／菜单栏提供打开主窗口、设置、启动／停止服务和退出；主窗口底部也可点击“退出应用”。退出会取消待发送任务并停止代理监控。
- 开关变更不改变当前服务的启停状态。关闭自启动后，下一次登录不再启动应用；手动启动新进程仍会自动运行服务。
- Herdr 或 Clash 尚未启动时，服务在后台等待并重试；代理健康判定、错误识别和介入间隔继续生效。
- 应用按用户／桌面会话保持单实例。macOS 使用固定用户目录中的独占文件锁和固定短路径的本地管道，登录启动与手动启动使用同一实例；Windows 按用户桌面会话隔离。
- Windows 使用当前用户的 `Run` 注册表项，可在“设置 → 应用 → 启动”和“任务管理器 → 启动应用”中查看；Windows 启动命令受系统 260 字符限制，路径过长时会提示缩短路径。
- macOS 使用 `SMAppService.mainApp` 登记原生登录项，显示在“系统设置 → 通用 → 登录项 → 登录时打开”。开启自启动且旧 LaunchAgent 确认为本应用所有时，会迁移到原生登记并清理旧文件；迁移失败保留原文件并提示。
- macOS 设置面板显示系统的已启用、等待许可或已关闭状态。需要许可时，明确开启自启动或点击“处理登录项许可”会弹出原生说明框；选择“前往设置”打开系统登录项页面。说明框本身不授予权限。用户在系统关闭或移除登录项后，应用不会自动重新登记，可在应用中明确重新开启。
- 请将应用保存在固定位置；移动或覆盖升级后检查系统登记状态。macOS 登录启动通过原生 Apple event 识别，根据静默设置决定是否显示主窗口；`--autostart` 仅标识登录启动来源，不强制隐藏窗口。

通用设置单独保存在地区设置同目录的 `app-settings.json`。文件损坏、启动项丢失或应用位置变化时暂停登录自启动并提示，手动打开新进程仍运行服务；保存失败会尝试回滚系统登记。地区设置和通用设置分别保存。

静默选项保存在 `app-settings.json` 的 `silentStartEnabled` 字段，保存后影响下次新进程的初始显示，不立即隐藏当前窗口。只修改该选项不会重写系统登录项。重复手动打开仍可唤出窗口，且保留本次手动停止服务的状态。

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

默认探针只读取会话、agent 和错误判定，不发送输入。`--clash-readonly` 只读取控制接口和实际代理连通性，不测速或切换。加 `--observe-seconds 60` 可连续观察同一连接 60 秒，逐次输出心跳与持续时间；断线或手动换节点时结束。

`--monitor-test --socket ENDPOINT` 仅接受包含 `watchdog-qa-` 的隔离端点，在该会话执行 15 秒续接测试，并使用测试代理状态；不会读取或切换真实 Clash。Windows 的 ENDPOINT 是管道标识。

`Tests/Watchdog.FakeAgent` 发布为 `codex` / `codex.exe`，只在隔离会话中运行，记录实际收到的输入和重复输入。Debug GUI 可用 `WATCHDOG_QA_SESSION` 只监控指定的 `watchdog-qa-` 会话，并注入健康测试代理；Release 不包含此入口。

Windows GUI 验收由 `scripts/qa-windows.ps1` 驱动真实 UI Automation；通过 SSH 时使用 `scripts/invoke-windows-qa.ps1 -BuildDirectory <目录>`，在已登录桌面中创建临时交互式计划任务。构建目录应包含：

- `repo/`：此仓库及 `dist/HerdrAutoContinue-win-x64` Release 产物；
- `qa/app/`：Debug GUI 的自包含发布；
- `qa/fake-agent/`：测试 agent 的自包含发布。

脚本保存截图和 JSON 结果，清理自己创建的 Herdr 会话及计划任务。实际 Herdr 会话和 Clash 节点只做读取。macOS Debug 窗口可通过 `WATCHDOG_QA_AUTOMATION_DIR` 和 `WATCHDOG_QA_SCENARIO=cancel|resume` 执行相同生命周期检查。

## 长期运行内存回归

macOS 构建会自动重建带无障碍引用生命周期补丁的 Avalonia.Native；托管依赖仍为 12.1.4。首次构建需要 Python 3.12+、Xcode Command Line Tools 和网络，后续使用 `.tools/` 缓存。补丁、固定源码版本、许可证和构建方式见 [原生库说明](../native/avalonia/README.md)。Windows 使用官方原生库。

```sh
# 隔离回归：官方库应失败，修复库应通过。
python3 scripts/qa-memory-macos.py --output-directory dist/memory-stock --stock
python3 scripts/qa-memory-macos.py --output-directory dist/memory-fixed
# 真实窗口显示／最小化／隐藏循环，普通 GC，500 条滚动日志。
python3 scripts/qa-memory-macos.py --output-directory dist/memory-soak --seconds 1800
# 只读观察指定的已运行进程 8 小时，不重启或改变服务状态。
python3 scripts/observe-memory-macos.py --pid PID --output-directory dist/memory-observation
```

Debug GUI 的内存验收使用独立应用生命周期，不加载用户设置、不发现真实 Herdr 会话、不连接或切换 Clash。结果写入指定目录中的 `result.json` 和 `samples.jsonl`。进程观察的 `summary.json` 会实时更新，结束后状态为 `complete`；进程退出或 PID 被复用会记录 `interrupted`。观察程序只记录指标，不会自动重启应用。

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

## 介入冷却

通用设置的 `cooldownSeconds` 默认 5，范围 1～300 整数秒；缺失时兼容旧文件，非法字段单独回退并提示。保存后立即影响待处理任务。首次 agent 错误立即复核并提交；后续从提交尝试开始计时，错误变化、重连及代理恢复不重置时间。每个 agent 进程独立计时，旧证据去重和提交前复核继续生效。网络连续两次未满 60 秒断线才测速恢复，每次自动节点切换前按上次切换请求开始时间等待剩余间隔，等待后重新核对配置和连通性。长连接心跳间隔为 2 秒，agent 扫描仍为 2 秒；服务停止后保留进程内计时，退出后清空。

## 独立功能开关

通用设置新增 `proxyRecoveryEnabled` 和 `agentRecoveryEnabled`，默认均为 true，旧配置缺失字段时保持开启，非法字段分别回退并提示。设置保存后异步应用，等待旧的待发送任务或代理任务退出。已发出的请求继续记录结果。

代理关闭时停止自动探测、测速及切换，启动仅加载本地地区偏好；agent 续接不再依赖代理健康检查。手动地区设置操作仍可按需读取 Clash。agent 关闭时继续监测状态，行内标记自动续接关闭，不提交续接；两者都关闭时服务保持只读监测。全局启停优先于功能开关。重新开启保留去重与上次介入时间。代理监控自身也拒绝在功能关闭时启动，避免其他配置流程重新拉起。

## 应用更新实现与验证

更新请求使用独立 HTTP 客户端，显式固定 HTTP 代理 `127.0.0.1:7890`，清空绕过列表并禁止自动重定向；每次 HTTPS 重定向仍由同一客户端请求。SHA-256 来源为 GitHub asset digest 或同一 Release 的 SHA256SUMS.txt。

发布包包含自包含 Watchdog.Updater 和 update-manifest.json；版本取自 Directory.Build.props。macOS 解包及复制使用 ditto 保留 .NET 文件代码签名所需的扩展属性。不要使用普通逐文件复制替代。Windows 清单用于清除旧版包文件，额外用户文件予以保留。

辅助程序在安装目录之外运行；普通权限协调进程负责重启，必要时提权的 worker 负责文件操作。事务和步骤标记保存在用户设置目录的 Updates 下，旧版备份位于安装路径加 `.update-backup`。发生系统断电或辅助进程异常退出时，应保留事务与备份进行恢复，不能删除唯一可用副本。

测试：`dotnet test HerdrContinue.slnx -c Release`；安装替换和回滚：`python3 scripts/qa-update-worker.py --app <正式包解压路径> --output <报告目录>`。测试在临时副本中运行，不替换正式安装。

完整按钮到重启测试使用 `scripts/qa-update-e2e.py --prepare --base-app <发布包解压目录> --output <全新测试目录>` 构建隔离 1.6.1/1.6.2，再用 `--case success-stopped`、`success-running`、`corrupt`、`startup-failure` 运行各场景。Windows 可用 `scripts/qa-update-e2e-windows.ps1 -Prepare` 在普通用户交互桌面执行。`--target` 只用于预先准备的受保护测试安装副本，系统授权仍由用户操作；不要指向正式应用。

恢复扫描不得干预启动当前进程的更新事务，协调器需要先消费健康确认。macOS 提权环境下系统命令使用原生 spawn/waitpid 和有界超时，避免托管退出通知停滞。具体证据见 [更新验收记录](validation-updates-2026-11-13.md)。

# Herdr Auto Continue

跨平台桌面应用，使用 C# / .NET 10 与 Avalonia 12。macOS 和 Windows 共用业务核心、系统接入层及中文图形界面。

点击“启动服务”监控本机所有 Herdr 会话。容量或临时网络错误停止后，每个 agent 独立等待至少 10 秒，复核后提交“继续”并回车。关闭窗口或点击“停止服务”取消待发送任务；再次打开默认停止。快捷键为 macOS 的 ⌘Enter、Windows 的 Ctrl+Enter。

## 运行与交付

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
- 不监控 SSH 远程机器，不安装登录项或常驻服务，不修改 Herdr 或 Clash 配置。SSH 仅用于开发和验收。

## Clash 故障恢复

- 每 10 秒检查一次 Google 和 Cloudflare 的 HTTPS `generate_204`，任一返回 204 即视为可用。强制通过 7890 代理，不采用环境变量绕过代理，也不回退直连。
- 一轮失败即暂停续接，连续两轮失败后开始日本节点测速。代理恢复后，符合条件的 agent 重新等待完整的 10 秒。
- 自动发现 Clash Party 的 Mihomo 控制接口：macOS 查找当前用户的 Unix socket，Windows 枚举 MihomoParty 命名管道。校验 Mihomo、7890 混合端口和唯一控制接口。
- 只在全局模式的 GLOBAL 选择器中切换，不修改模式。只选择已勾选地区内、GLOBAL 可直接选择的代理叶子节点；DIRECT、REJECT、组节点和未识别地区的条目不参与。
- 先尝试当前已勾选地区；该地区全部不可用后，从保存的排序首位继续，跳过本轮已经尝试失败的地区。当前地区未勾选或未知时，直接从排序首位开始。
- 逐地区测速，每地区最多并发 4 个节点，单节点超时 5 秒。主地址全部失败后使用备用地址重测，只比较当前地区本轮有效延迟。
- 切换后读取实际选择并验证代理；失败则尝试同地区下一节点，该地区全部失败后才进入下一个地区。所有已选地区失败后等待 10 秒开始新一轮，允许重试此前失败的地区。
- 手动选择、节点列表或控制接口变化会使旧结果失效。停止或关闭窗口取消待执行操作，保留已经选中的节点。

## 多地区设置（1.3.0）

点击代理卡片中的“地区设置”，勾选允许自动恢复的地区，用 ↑ / ↓ 调整已选地区优先级，最后点击“应用并保存”。取消或关闭设置窗口会放弃尚未应用的修改。

- 根据节点名称的国旗、常见中文地区名、英文地区名及代码识别，支持 `HK01`、`JP-02` 等编号。国旗优先，其他标记冲突时列为未识别。地区按国家／地区分组，不按城市拆分。
- 展示可切换节点覆盖的地区数、各地区节点数和未识别数量。当前选择了组时沿 `now` 解析实际叶子节点；无法确定时按未知地区处理。
- 例如排序为“香港 → 日本 → 新加坡”，当前在日本，则本轮依次尝试“日本 → 香港 → 新加坡”，不会重复尝试日本。
- 首次升级默认仅勾选日本。新发现的地区默认不勾选；已选地区暂时消失时保留其排序，显示 0 个节点，恢复时跳过。
- 没有勾选地区时继续检查代理健康，不自动测速或切换。代理健康时保留当前节点，即使其地区已取消勾选。
- 应用新设置会取消并等待旧测速任务结束，重新检查后按新顺序恢复；保存失败保留当前配置并显示错误。
- 启动应用时只读加载地区列表，服务仍默认停止。设置窗口提供手动刷新，刷新失败会保留已知列表并显示错误。

选择及排序保存位置：

- macOS：`~/Library/Application Support/HerdrAutoContinue/settings.json`
- Windows：`%LOCALAPPDATA%/HerdrAutoContinue/settings.json`

设置损坏时使用日本默认值并显示提示，不自动覆盖原文件；正常保存采用原子替换。

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

本次迁移的环境、测试结果和交付文件校验值见 [验收记录](docs/validation-2026-10-27.md)。

多地区功能的双端验收见 [1.3.0 验收记录](docs/validation-regions-2026-10-27.md)。

多地区 GUI 验收：macOS Debug 使用 `WATCHDOG_QA_SCENARIO=regions` / `regions-reload`，配合 `WATCHDOG_QA_REGIONS=recovery`；Windows 使用 `scripts/invoke-region-qa.ps1 -BuildDirectory <目录>`。两端均使用独立的 `watchdog-qa-` 设置目录和模拟 Clash，不改变用户的地区配置或真实节点。

# Herdr Auto Continue

原生 macOS 应用：点击“启动服务”监控本机所有运行中的 Herdr 会话。容量或网络错误停止后，每个 agent 独立等待 10 秒，复核后发送“继续”并回车。关闭窗口或点击“停止服务”立即取消待发送任务。再次打开默认停止。

## 使用

打开 `/Applications/Herdr Auto Continue.app`，点击“启动服务”。窗口显示发现的 agent、判断结果、倒计时和续接次数。容量或网络故障可以持续重试；正常完成、人工中断、授权对话、认证/额度/上下文问题不会触发续接。

同一按钮也启停 Clash 监控。窗口显示代理连通性、当前节点、日本节点数量，以及故障恢复时的测速进度与结果。代理正常时保留当前选择，包括非日本节点；仅在代理故障后自动选择日本节点。

## Clash 故障恢复

- 每 10 秒通过 `http://127.0.0.1:7890` 发起 HTTPS 连通检查，使用 Google 和 Cloudflare 的 `generate_204` 地址；任一返回 204 即视为可用。请求强制使用代理，不允许绕过代理直连。
- 一轮检查失败即暂停 agent 自动续接；连续两轮失败后开始日本节点测速。代理恢复后，符合条件的 agent 重新等待完整的 10 秒再复核发送。
- 自动发现本机 Clash Party 的 Mihomo Unix 控制 socket，并核对其管理的混合端口为 7890。当前支持全局模式下的 `GLOBAL` 选择器；其他模式暂停自动切换，不修改 Clash 模式。
- 仅选择 `GLOBAL` 可直接选择、名称含 `🇯🇵`、`日本`、`Japan` 或独立 `JP` 标识的叶子节点。地区根据节点名称识别；不会选择其他国家、DIRECT 或自动选择组。
- “最快”指同一 HTTPS 地址的 URL 延迟最低。每次最多并发测试 4 个日本节点，单节点超时 5 秒；主地址全部失败后使用备用地址重测。只比较本轮有效结果，不使用历史延迟。
- 切换后读取实际选择，再验证 7890 代理连通性；验证失败则尝试下一可用日本节点。全部超时或实际连通性均失败，等待 10 秒重新测速，持续重试。
- 测速期间发现手动选择、节点列表或控制接口变化时，放弃旧结果并重新检查。控制接口断开时持续重新发现；实际代理仍可用时不阻断 agent 续接。
- 停止服务或关闭窗口会取消检查和待执行切换，保留已经选中的节点。

日志位于 `~/Library/Logs/HerdrAutoContinue/events.jsonl`，只保留操作、状态和必要的错误摘要，不保存完整对话。单文件约 2 MB 时轮换，保留一份旧日志。

## 构建与验证

源码仓库：[lzl51230/HerdrContinue](https://github.com/lzl51230/HerdrContinue)。本机开发根目录为 `/Volumes/ggg/HerdrContinue`，后续开发和 PKG 打包均在此目录进行。以下命令从项目根目录执行；`.build/` 和 `dist/` 为本地生成目录，不提交到 Git。

需要 macOS 14+、Swift 6 工具链及本机 Herdr。当前验证环境为 Apple Silicon、macOS 26.6、Herdr 0.9.3。Herdr 默认路径为 `~/.local/bin/herdr`。

```sh
DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer swift test
bash scripts/build-install.sh --install
swift run WatchdogProbe
swift run WatchdogProbe --clash-readonly
```

`WatchdogProbe` 默认只读取 agent 信息，不发送任何输入。`--monitor-test --socket PATH` 仅接受路径含 `watchdog-qa-` 的隔离测试 socket，在该会话中执行 15 秒测试监控。

`--clash-readonly` 只读取 Clash 配置和节点，并检查实际代理连通性，不测速或切换节点。控制接口参考 [Mihomo 官方 API 文档](https://wiki.metacubex.one/en/api/)。

## 实现边界

- 通过 Herdr CLI 发现会话，通过 Unix socket 的换行分隔 JSON 协议读取和提交。无需辅助功能权限，不切换 Herdr 焦点。
- 每 2 秒扫描一次；10 秒是最短冷却时间，实际发送还受扫描与复核耗时影响。
- 当前错误解析支持 Codex 和 OMP。其他 Herdr agent 会显示在列表中，未能可靠识别停止原因时不发送。
- Codex 仅识别当前输入框前的原生错误块；OMP 同时核对屏幕、空编辑器和当前会话 JSONL 中最后一条消息。布局变化、输入草稿或无法读取证据时暂停自动续接并显示原因。
- 网络规则覆盖连接重置/拒绝/中断、流中断、请求超时、DNS、TLS 握手等临时故障。排除认证、证书验证、额度、限流、上下文和人工取消等非目标错误。
- 发送前再次核对进程组、终端与会话身份。Herdr 0.9.3 的 `agent.prompt` 没有“仅当状态/进程仍匹配时发送”的原子参数，因此复核和提交之间仍存在极短的外部状态竞争窗口。
- 接口确认提交不等于模型已经恢复。结果不明确时保留已尝试状态，等待新的运行或失败证据，避免重复输入。
- 本版不监控 SSH 远程机器，不安装登录项或独立常驻服务，也不修改 Herdr 配置。

## 本机验收（2026-10-15）

- 1.1.0 共 53 项自动化测试通过，覆盖错误识别、每 agent 独立冷却、40 轮重复失败、草稿和身份变化、停止/重连、socket 分帧、超时和取消，以及日本节点筛选/排序、持续重测、手动切换竞争、切换确认、代理恢复后的完整冷却和隔离 Unix HTTP 接口传输。
- 隔离 Herdr 会话中的测试 agent 在约 10.38 秒后收到一次完整的“继续”加回车；服务端确认进入 working，随后恢复 idle，无重复输入。测试会话已删除。
- 原生窗口验证通过：默认停止、启动后发现本机 4 个 agent 和 15 个日本节点；代理正常且保持原节点，停止清除冷却，运行中关闭窗口同时停止两项监控并退出。
- Clash 故障、测速和切换由模拟服务及隔离 Unix HTTP 服务验证；本机 Clash 只进行读取和连通检查，未人为断网或切换实际节点。
- 已对 `/Applications/Herdr Auto Continue.app` 完成本地签名与 bundle 校验。

# 颜色主题验收

## 实现与兼容

- 设置提供 Light Modern、Dark Modern、Light+、Dark+、Monokai、Solarized Dark，以及默认的跟随系统。
- 主窗口、通用设置、地区设置、测速结果、下拉菜单和提示使用动态主题资源。选择即时预览，应用后保存；取消、关闭面板和保存失败恢复已保存主题，无需重启。
- 跟随系统在现代浅色与深色之间切换，手动指定的主题保持不变。主题在主窗口创建前加载，静默启动不创建主窗口。
- 通用设置增加 `theme` 字段，保持版本 1；旧文件缺少主题时跟随系统。无效主题值独立回退，不会丢失有效的自启动偏好。
- 仅修改主题不写系统自启动登记。登记失效或无法读取时仍可保存主题；同时修改自启动时保留原有失败回滚机制。

## 构建及自动化

| 环境 | 锁定依赖还原 | Release 构建 | Release 测试 | 主题 GUI | 自启动 GUI 回归 |
| --- | --- | --- | --- | --- | --- |
| 本机 macOS / Apple Silicon | 通过 | 0 警告、0 错误 | 192 通过，0 失败 | 通过 | 7 场景通过 |
| 远程 Windows x64 | 通过 | 0 警告、0 错误 | 179 通过，0 失败 | 通过 | 7 场景通过 |

新增 17 项设置用例：七项主题读写、旧格式及四种无效值回退、系统登记失效或拒绝读取时的主题单独保存、主题保存失败、主题与自启动组合保存失败的登记回滚，以及并发部分更新。macOS 多出的 13 项为既有 Unix socket 传输组合。

主题 GUI 在实际 Avalonia 桌面窗口中执行，遍历六套主题并保存主窗口、设置、地区设置、测速结果、下拉弹层、悬停、禁用和最小尺寸下的焦点截图。检查主要按钮模板的悬停配色，以及正文、次要文字、警告、错误、按钮和选中项的颜色对比度至少 4.5:1。人工抽查了两端浅色、深色及 Solarized／Monokai 截图。

主题 GUI 同时验证：预览期间服务继续运行；主题保存不改变系统启动项；取消和关闭恢复主题；保存失败通过隔离配置路径的实际 I/O 错误触发并确认恢复。随后分别启动普通新进程与 `--autostart` 新进程，验证已保存 Dark Modern 的恢复和静默启动后显示窗口的配色。

系统明暗切换使用注入的系统外观事件源验证同一主题管理器的响应，包括固定主题忽略变化、取消预览时按最新系统模式恢复。未修改两台机器的全局系统外观；生产接入使用 Avalonia 的 `IPlatformSettings.ColorValuesChanged`。悬停状态由 Avalonia 伪类触发，截图不依赖测试鼠标位置。

自启动回归使用既有脚本，验证取消设置、启用、静默启动、手动启动、单实例唤醒、关闭窗口继续运行、手动停止状态保持、系统退出请求、关闭自启动后的普通及静默启动。macOS 使用真实隔离 LaunchAgent；Windows 以普通用户权限在已登录桌面 Session 1 执行真实登记命令。本次未实际重启或注销系统。

## 证据与环境

- 主题截图及结果：`dist/qa/watchdog-qa-theme-macos-ui-states/`、`dist/qa/watchdog-qa-theme-windows-ui-states/`。
- Release TRX：`dist/qa/themes-macos-tests/`、`dist/qa/theme-windows-tests/`。
- 自启动回归：`dist/qa/watchdog-qa-theme-startup-macos/`、`dist/qa/watchdog-qa-theme-startup-windows/`。
- GUI 测试均使用 Debug 自包含应用、独立 `watchdog-qa-` 设置与单实例作用域，以及模拟 Clash。未创建续接测试 agent，未修改生产服务、地区配置、真实代理节点或自启动设置。
- 临时 LaunchAgent、Windows 自启动项及计划任务均在验收后清理，隔离验收期间本机生产应用保持运行。
- 验收后按用户要求覆盖安装至 `/Applications/Herdr Auto Continue.app`，签名校验通过，服务恢复运行，自启动及地区配置文件校验值保持不变。设置面板已确认显示“颜色主题”，当前为“跟随系统”。本次未发布 GitHub Release。

配色来源、保存标识与可复用脚本见 [主题说明](theme-colors.md) 和 [README](../README.md#颜色主题)。

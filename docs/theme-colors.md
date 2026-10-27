# 颜色主题

应用内置六套参考 VS Code 的配色。主题颜色集中在 `AppThemeManager`，控件使用动态资源；保留现有业务界面布局。

| 设置选项 | 保存标识 | 基础控件模式 | 参考来源 |
| --- | --- | --- | --- |
| 跟随系统 | `system` | 系统明暗模式 | 明亮时 Light Modern，暗色时 Dark Modern |
| Light Modern | `light-modern` | Light | [VS Code Light Modern](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/light_modern.json) |
| Dark Modern | `dark-modern` | Dark | [VS Code Dark Modern](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/dark_modern.json) |
| Light+ | `light-plus` | Light | [VS Code Light+](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/light_plus.json) 及其继承的 Light 配色 |
| Dark+ | `dark-plus` | Dark | [VS Code Dark+](https://github.com/microsoft/vscode/blob/main/extensions/theme-defaults/themes/dark_plus.json) 及其继承的 Dark 配色 |
| Monokai | `monokai` | Dark | [VS Code Monokai](https://github.com/microsoft/vscode/blob/main/extensions/theme-monokai/themes/monokai-color-theme.json) |
| Solarized Dark | `solarized-dark` | Dark | [VS Code Solarized Dark](https://github.com/microsoft/vscode/blob/main/extensions/theme-solarized-dark/themes/solarized-dark-color-theme.json) |

主背景、面板、基础文字、按钮和选择状态参考 workbench 配色；次要文字、浅色错误提示、强调文字及 Solarized 按钮和选中文字做了对比度调整。主题 GUI 验收检查正文、次要文字、警告、错误、主要按钮及选中项文本的前景／背景对比度至少为 4.5:1。禁用控件保持视觉上的弱化。

系统外观接入通过 `ISystemThemeSource` 封装 Avalonia 的 `IPlatformSettings.ColorValuesChanged`；界面更新统一在 UI 线程执行。固定主题忽略系统变化，预览取消时重新解析已保存的跟随系统选择。启动时在主窗口创建前加载主题；静默启动仅初始化主题，不创建窗口。

主题随发布包内置，运行时无需联网。现阶段提供固定主题选择，不支持导入 VS Code 扩展、编辑器语法配色或任意主题 JSON。

# Herdr Auto Continue

[简体中文](README.md) | English

When you're drafting a plan, working through requirements, or leaving an AI assistant on a long task, a temporary “service busy” or network error can bring everything to a halt. You have to come back and tell it to continue.

Herdr Auto Continue takes care of that follow-up. It watches tasks in Herdr on your computer. When it recognizes a retryable error, it waits at least 10 seconds, checks that the task is still stuck on the same error, and sends “继续” (“continue”). If the proxy connection fails, it can also look for a working node among your chosen subscriptions and regions, then resume eligible tasks once the connection recovers.

Automatic continuation currently supports **Codex and OMP**. Completed tasks, tasks you stopped yourself, confirmation prompts, and login, quota, or rate-limit issues are left for you to handle.

## Download and get ready

Open the [latest release](https://github.com/lzl51230/HerdrContinue/releases/latest) and choose the package for your computer:

| Your computer | Download | How to open it |
| --- | --- | --- |
| macOS 14 or later, Apple Silicon (M-series chip) | `HerdrAutoContinue-osx-arm64.zip` | Unzip it and move `Herdr Auto Continue.app` to Applications |
| Windows 10 22H2 / Windows 11, 64-bit | `HerdrAutoContinue-win-x64.zip` | Unzip it to a permanent folder and run `HerdrAutoContinue.exe` |

The packages include the components needed to run the app; you don't need to install .NET separately. Windows 11 and Apple Silicon Macs have been tested on real machines. Windows 10 has not. The macOS package is not Apple-notarized, and the Windows package has no commercial code signature, so your system may show an app-source warning.

Before you start, make sure **Herdr is installed and can open your AI sessions**. Proxy recovery uses **Clash Party** with its local proxy port set to **7890**. Automatic node switching also requires Clash Party's **Global mode**.

## Your first run

The app's interface is currently in Chinese. The labels below include the text you'll see on screen.

1. Open Herdr and Clash Party, and check that your usual connection works.
2. Open Herdr Auto Continue and click **Switch settings (切换设置)** on the proxy card.
3. Under **Subscriptions (订阅源)**, check the subscriptions the app may use. Under **Regions & order (地区与排序)**, choose your regions and arrange them with ↑ / ↓. Click **Apply and save (应用并保存)**.
4. Back in the main window, click **Start service (启动服务)**. The app watches all Herdr sessions on this computer and shows task status, countdowns, and continuation counts.

On first use, only the **current subscription and Japan** are selected. Change these to suit your needs. Upgrading preserves your existing region choices and order; newly discovered subscriptions and regions need to be selected manually.

## How does proxy recovery choose a node?

It first looks for a replacement in the current region, provided you've selected that region. If none works, it tries the other regions in your saved order. For example, with Japan, Hong Kong, and Singapore selected in that order, a failed Japanese node leads to attempts in Japan first, then Hong Kong, then Singapore.

Within one region, **nodes from all selected subscriptions are tested at the same time**. The app tries the fastest node that passes testing and checks that it actually works as your proxy. If it doesn't, it tries the next node in that region before moving to another region. Nodes with the same name in different subscriptions are kept distinct, and the interface shows which subscription each node belongs to.

Two rules are useful to know:

- Two consecutive measurements above **800 ms** to the same test address count as a timeout. This also applies when checking your current node.
- **A working proxy stays selected.** The app doesn't keep switching just to chase a lower latency. If no subscriptions or no regions are selected, it only checks the connection and won't switch nodes automatically.

The app combines nodes from your selected subscriptions for use together while keeping the original subscription files. To use just the subscription currently selected in Clash Party, click **Restore original configuration (恢复原配置)**; your region order is preserved. Stopping the service or quitting the app also leaves the current proxy configuration and node in place.

## Everyday controls

| What you want to do | What to click |
| --- | --- |
| Put the window away and let the app keep working | Close the window. The app stays in the menu bar or system tray |
| Pause automatic continuation and proxy monitoring | **Stop service (停止服务)**. Your current proxy node stays selected |
| Close the app completely | **Quit app (退出应用)** at the bottom of the window, or **Quit (退出)** in the menu bar / tray menu |
| Run automatically when you log in | Open **Settings (设置)**, check **Launch at login and run the continuation service (登录后自动启动并运行续接服务)**, then save |
| Change the colors | Open **Settings → Color theme (设置 → 颜色主题)**. Choose the system theme or one of six VS Code-inspired themes |

With startup enabled, the app and continuation service start quietly in the background when you log in. Manually launching a new instance also starts the service. If the app is already running in the background, reopening it only shows the window; it won't restart a service you manually stopped.

Theme changes preview immediately. Click **Apply and save (应用并保存)** to keep them, or **Cancel (取消)** to restore the previous colors. You can also start or stop the service with **⌘Enter** on Mac or **Ctrl+Enter** on Windows.

## Common questions

**Why isn't it doing anything after I open it?**

The service is stopped on first launch. Click **Start service (启动服务)**, or enable startup in Settings for future launches. If Herdr or Clash Party isn't open yet, the app waits and retries. The main window shows its current status.

**Why didn't it continue after an error?**

It only handles recognized temporary capacity and network errors. It leaves completed tasks, manual stops, confirmation prompts, and login or quota issues alone. It also won't send anything while there's a draft in the input box. Check the explanation next to the task.

**Why is a subscription unavailable or showing no nodes?**

Update it in Clash Party first, then click **Refresh list (刷新列表)** in this app. The app uses nodes Clash has already downloaded to your computer. Some subscriptions with custom processing or missing dependencies can't be combined yet; the interface explains why. Subscription downloads and updates are still managed in Clash Party.

**macOS says startup needs permission. What should I do?**

Click **Go to Settings (前往设置)** when prompted, then allow the app under **General → Login Items** in System Settings. Keep the app in Applications, and check its startup status after moving or upgrading it.

**How can I see what it just did?**

The activity log at the bottom of the main window shows waiting periods, node changes, and continuation results. For troubleshooting, click **Open log file (打开日志文件)**. Logs don't store full conversations.

## More details

For build instructions, troubleshooting, and detailed behavior, see the [development and verification guide](docs/development.md) (in Chinese).

import SwiftUI
import AppKit
import WatchdogCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    var monitor: Monitor?
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) {
        monitor?.stop()
        #if DEBUG
        UICheck.didTerminate(monitor: monitor)
        #endif
    }
}

@main
struct HerdrAutoContinueApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var monitor = Monitor()
    var body: some Scene {
        Window("Herdr Auto Continue", id: "main") {
            ContentView(monitor: monitor)
                .onAppear {
                    delegate.monitor = monitor; NSApp.activate(ignoringOtherApps: true)
                    #if DEBUG
                    Task { await UICheck.run(monitor: monitor) }
                    #endif
                }
                .onDisappear { monitor.stop() }
                .frame(minWidth: 900, minHeight: 720)
        }
        .defaultSize(width: 1080, height: 850)
        .commands { CommandGroup(replacing: .newItem) {} }
    }
}

struct ContentView: View {
    @ObservedObject var monitor: Monitor
    @State private var selection: AgentRow.ID?
    private var activeCount: Int { monitor.rows.filter { $0.deadline != nil }.count }
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: 16) {
                Image(systemName: "arrow.trianglehead.2.clockwise.rotate.90.circle.fill")
                    .font(.system(size: 42)).foregroundStyle(.teal)
                VStack(alignment: .leading, spacing: 5) {
                    Text("Herdr 自动续接").font(.system(size: 25, weight: .semibold))
                    Text("代理故障自动切换日本节点，恢复后续接 agent").foregroundStyle(.secondary)
                }
                Spacer()
                Button {
                    if monitor.running { monitor.stop() } else { monitor.start() }
                } label: {
                    Label(monitor.running ? "停止服务" : "启动服务", systemImage: monitor.running ? "stop.fill" : "play.fill")
                        .frame(width: 100)
                }
                .buttonStyle(.borderedProminent)
                .tint(monitor.running ? .red : .teal)
                .controlSize(.large)
                .keyboardShortcut(.return, modifiers: .command)
                .accessibilityIdentifier("service-toggle")
            }
            .padding(24)

            HStack(spacing: 24) {
                Label(monitor.running ? "监控中" : "已停止", systemImage: monitor.running ? "circle.fill" : "pause.circle")
                    .foregroundStyle(monitor.running ? .teal : .secondary)
                Text("\(monitor.rows.count) 个 agent")
                Text("\(activeCount) 个冷却中")
                Spacer()
                Label("独立冷却 10 秒", systemImage: "timer")
            }
            .font(.system(size: 13, weight: .medium))
            .padding(.horizontal, 24).padding(.vertical, 14)
            .background(Color.teal.opacity(0.08))

            ProxyCard(status: monitor.proxyStatus)
                .padding(.horizontal, 24).padding(.top, 16)

            HStack {
                Text(monitor.connection).lineLimit(1).help(monitor.connection)
                Spacer()
                if let lastScan = monitor.lastScan { Text("更新于 \(lastScan.formatted(date: .omitted, time: .standard))") }
            }
            .font(.caption).foregroundStyle(.secondary).padding(.horizontal, 24).padding(.vertical, 12)

            if monitor.rows.isEmpty {
                ContentUnavailableView(monitor.running ? "正在等待 agent" : "准备就绪", systemImage: "terminal",
                                       description: Text(monitor.running ? "运行中的本机 Herdr agent 会自动出现在这里。" : "点击“启动服务”以发现并监控本机所有 Herdr 会话。"))
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Table(monitor.rows, selection: $selection) {
                    TableColumn("Agent") { row in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(row.title).fontWeight(.medium).lineLimit(1).help(row.title)
                            Text("\(row.kind) · \(row.location)").font(.caption).foregroundStyle(.secondary)
                        }.padding(.vertical, 5)
                    }.width(min: 220, ideal: 320)
                    TableColumn("状态") { row in
                        Text(stateLabel(row.state)).foregroundStyle(row.state == "working" ? .teal : .secondary)
                    }.width(70)
                    TableColumn("监控结果") { row in
                        VStack(alignment: .leading, spacing: 3) {
                            Text(row.detail).lineLimit(2).help(row.detail)
                            if let deadline = row.deadline {
                                TimelineView(.periodic(from: .now, by: 1)) { _ in
                                    Text("\(max(0, Int(ceil(deadline - ProcessInfo.processInfo.systemUptime)))) 秒后复核")
                                        .font(.caption).monospacedDigit().foregroundStyle(.orange)
                                }
                            }
                        }
                    }.width(min: 200, ideal: 300)
                    TableColumn("续接") { row in Text("\(row.attempts)").monospacedDigit() }.width(45)
                }
                .tableStyle(.inset(alternatesRowBackgrounds: true))
            }

            Divider()
            HStack {
                Text("操作日志").fontWeight(.semibold)
                Spacer()
                Button("打开日志文件") { NSWorkspace.shared.activateFileViewerSelecting([monitor.logURL]) }
                    .buttonStyle(.link)
            }.padding(.horizontal, 24).padding(.top, 14).padding(.bottom, 8)
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 7) {
                    if monitor.events.isEmpty { Text("尚无操作记录").foregroundStyle(.tertiary) }
                    ForEach(monitor.events.reversed()) { event in
                        HStack(alignment: .top, spacing: 10) {
                            Text(event.date.formatted(date: .omitted, time: .standard)).foregroundStyle(.secondary).frame(width: 65, alignment: .leading)
                            Text(event.target).foregroundStyle(.secondary).frame(width: 140, alignment: .leading).lineLimit(1).help(event.target)
                            Text(event.message).frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled)
                        }
                    }
                }.font(.system(size: 11, design: .monospaced)).padding(.horizontal, 24).padding(.vertical, 6)
            }.frame(height: 145)
            HStack {
                Image(systemName: "info.circle")
                Text("关闭窗口即停止服务。正常完成、人工中断和等待确认的对话不会自动续接。")
                Spacer()
            }.font(.caption).foregroundStyle(.secondary).padding(.horizontal, 24).padding(.vertical, 14)
        }.background(Color(nsColor: .windowBackgroundColor))
    }
    private func stateLabel(_ state: String) -> String {
        ["working": "运行中", "idle": "空闲", "done": "已完成", "blocked": "待确认"][state] ?? "未知"
    }
}

private struct ProxyCard: View {
    let status: ProxyStatus
    @State private var showingResults = false
    private var tint: Color {
        switch status.phase {
        case .healthy: return .teal
        case .stopped: return .secondary
        case .checking, .testing, .switching: return .blue
        default: return .orange
        }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack {
                Label(status.phase.rawValue, systemImage: "network").fontWeight(.semibold).foregroundStyle(tint)
                Text("127.0.0.1:7890").font(.caption.monospaced()).foregroundStyle(.secondary)
                Spacer()
                if status.phase == .testing { Text("\(status.completed) / \(status.candidateCount)").monospacedDigit() }
                else if status.phase == .healthy, let delay = status.latencyMS { Text("连通检查 \(delay) ms").font(.caption).foregroundStyle(.secondary) }
                Text("日本节点 \(status.candidateCount)").font(.caption).foregroundStyle(.secondary)
                if !status.delays.isEmpty {
                    Button("测速结果") { showingResults = true }
                        .buttonStyle(.link)
                        .popover(isPresented: $showingResults) {
                            VStack(alignment: .leading, spacing: 10) {
                                Text("第 \(status.round) 轮 · 可用日本节点").font(.headline)
                                ScrollView {
                                    VStack(spacing: 8) {
                                        ForEach(status.delays, id: \.name) { result in
                                            HStack { Text(result.name); Spacer(); Text("\(result.milliseconds) ms").monospacedDigit() }
                                        }
                                    }
                                }.frame(maxHeight: 240)
                                Text("只比较本轮成功结果；超时节点不参与选择。").font(.caption).foregroundStyle(.secondary)
                            }.padding(18).frame(width: 440)
                        }
                }
            }
            Text("当前节点：\(status.selected ?? "等待读取")").lineLimit(1).help(status.selected ?? "等待读取")
            HStack(alignment: .top) {
                Text(status.detail).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                Spacer()
                if [.retrying, .suspect, .unavailable].contains(status.phase), let next = status.nextCheck {
                    TimelineView(.periodic(from: .now, by: 1)) { _ in
                        Text("\(max(0, Int(ceil(next - ProcessInfo.processInfo.systemUptime)))) 秒后重试")
                            .font(.caption).monospacedDigit().foregroundStyle(.orange)
                    }
                }
            }
        }.padding(14).background(tint.opacity(0.06), in: RoundedRectangle(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(tint.opacity(0.2)))
    }
}

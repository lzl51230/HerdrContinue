using System.Xml.Linq;

namespace Watchdog.Infrastructure;

public sealed class MacLoginItemRegistration(AutoStartCommand command, IMacLoginItemService service,
    string? legacyPath = null, string legacyLabel = "local.alex.herdr-auto-continue") : IAutoStartRegistration
{
    private readonly string path = legacyPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", legacyLabel + ".plist");
    private sealed record Snapshot(AutoStartState State, string? Legacy);
    public string? AvailabilityError => command.AvailabilityError;
    public bool CanOpenSettings => true;
    public void OpenSettings() => service.OpenSettings();
    public void PromptForApproval() => service.PromptForApproval();
    public async Task<AutoStartEntry> ReadAsync(CancellationToken ct = default)
    {
        var legacy = await ReadOwnedLegacyAsync(ct);
        var status = service.Status;
        return new(status == AutoStartState.NotRegistered && legacy is null ? null : new Snapshot(status, legacy),
            status == AutoStartState.Enabled, State: status);
    }
    private async Task<string?> ReadOwnedLegacyAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new IOException("旧登录启动项过大，无法迁移。");
        var text = await File.ReadAllTextAsync(path, ct);
        try
        {
            var dict = XDocument.Parse(text).Root?.Element("dict") ?? throw new IOException("旧启动项格式无效。");
            var values = dict.Elements("key").ToDictionary(e => e.Value, e => e.NextNode as XElement);
            if (!values.TryGetValue("Label", out var label) || label?.Value != legacyLabel ||
                !values.TryGetValue("ProgramArguments", out var arguments) || arguments is null ||
                !arguments.Elements("string").Select(e => e.Value).SequenceEqual(new[] { command.Executable }.Concat(command.Arguments)))
                throw new IOException("旧启动项与当前应用不匹配，已保留原文件；请检查后重新开启自启动。");
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException)
        { throw new IOException("旧启动项无法识别，已保留原文件。", e); }
        return text;
    }
    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var legacy = await ReadOwnedLegacyAsync(ct);
        if (enabled) command.Validate();
        service.SetEnabled(enabled);
        var state = service.Status;
        if (enabled && state is not (AutoStartState.Enabled or AutoStartState.RequiresApproval))
            throw new IOException("系统未确认登录项登记成功。");
        if (!enabled && state != AutoStartState.NotRegistered) throw new IOException("系统未确认登录项关闭成功。");
        // Do not leave a legacy launch path that could bypass a user's denied approval.
        if (legacy is not null) File.Delete(path);
    }
    public async Task RestoreAsync(AutoStartEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var previous = entry.Value as Snapshot;
        service.SetEnabled(previous?.State is AutoStartState.Enabled or AutoStartState.RequiresApproval);
        var legacy = new LegacyMacAutoStartRegistration(command, path, legacyLabel);
        await legacy.RestoreAsync(new(previous?.Legacy, false), ct);
    }
    public async Task MigrateLegacyAsync(bool requested, CancellationToken ct = default)
    {
        if (!requested || !File.Exists(path)) return;
        var previous = await ReadAsync(ct);
        try { await SetEnabledAsync(true, ct); }
        catch
        {
            await RestoreAsync(previous, CancellationToken.None);
            throw;
        }
    }
}

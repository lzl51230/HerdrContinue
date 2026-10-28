namespace Watchdog.Core;

// Editing is separate from the active recovery policy until Apply succeeds.
public sealed class RegionSelectionDraft(ProxyRegionPreferences preferences)
{
    private readonly List<string> selected = preferences.PriorityRegionIds.ToList();
    public IReadOnlyList<string> Selected => selected.AsReadOnly();
    public List<string>? Sources { get; set; } = preferences.SelectedSourceIds?.ToList();
    public ProxyRegionPreferences ToPreferences() => new(selected, Sources);
    public void SetSelected(string id, bool enabled)
    {
        var canonical = RegionResolver.ForId(id)?.Id ?? throw new ArgumentException("未知地区代码");
        if (enabled && !selected.Contains(canonical)) selected.Add(canonical);
        if (!enabled) selected.Remove(canonical);
    }
    public void Move(string id, int direction)
    {
        if (direction is not -1 and not 1) throw new ArgumentOutOfRangeException(nameof(direction));
        var index = selected.IndexOf(id); var next = index + direction;
        if (index >= 0 && next >= 0 && next < selected.Count) (selected[index], selected[next]) = (selected[next], selected[index]);
    }
    public IReadOnlyList<RegionAvailability> Rows(IReadOnlyList<RegionAvailability> catalogue)
    {
        var available = catalogue.ToDictionary(r => r.Region.Id);
        return selected.Select(id => available.GetValueOrDefault(id) ?? new RegionAvailability(RegionResolver.ForId(id)!, 0))
            .Concat(catalogue.Where(r => !selected.Contains(r.Region.Id)).OrderBy(r => r.Region.Name, StringComparer.Ordinal)).ToArray();
    }
}

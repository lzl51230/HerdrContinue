using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchdog.Core;

public sealed record ProxyRegion(string Id, string Name)
{
    public string Flag => string.Concat(Id.Select(c => char.ConvertFromUtf32(0x1F1E6 + c - 'A')));
}
public sealed record RegionAvailability(ProxyRegion Region, int NodeCount);

public static class RegionResolver
{
    private static readonly Dictionary<string, string[]> CommonNames = new(StringComparer.Ordinal)
    {
        ["JP"] = ["日本", "Japan"], ["HK"] = ["香港", "中国香港", "中國香港", "Hong Kong"],
        ["TW"] = ["台湾", "台灣", "中国台湾", "中國台灣", "Taiwan"], ["MO"] = ["澳门", "澳門", "Macao", "Macau"],
        ["US"] = ["美国", "美國", "United States", "USA"], ["SG"] = ["新加坡", "Singapore"],
        ["GB"] = ["英国", "英國", "United Kingdom", "Britain", "UK"], ["CN"] = ["中国", "中國", "大陆", "大陸", "China"],
        ["KR"] = ["韩国", "韓國", "South Korea", "Korea"], ["DE"] = ["德国", "德國", "Germany"],
        ["FR"] = ["法国", "法國", "France"], ["CA"] = ["加拿大", "Canada"], ["AU"] = ["澳大利亚", "澳大利亞", "澳洲", "Australia"],
        ["NZ"] = ["新西兰", "紐西蘭", "New Zealand"], ["NL"] = ["荷兰", "荷蘭", "Netherlands"],
        ["RU"] = ["俄罗斯", "俄羅斯", "Russia"], ["IN"] = ["印度", "India"], ["ID"] = ["印度尼西亚", "印度尼西亞", "印尼", "Indonesia"],
        ["MY"] = ["马来西亚", "馬來西亞", "Malaysia"], ["TH"] = ["泰国", "泰國", "Thailand"],
        ["VN"] = ["越南", "Vietnam"], ["PH"] = ["菲律宾", "菲律賓", "Philippines"],
        ["TR"] = ["土耳其", "Turkey", "Türkiye"], ["AR"] = ["阿根廷", "Argentina"], ["BR"] = ["巴西", "Brazil"],
        ["CH"] = ["瑞士", "Switzerland"], ["SE"] = ["瑞典", "Sweden"], ["FI"] = ["芬兰", "芬蘭", "Finland"],
        ["NO"] = ["挪威", "Norway"], ["DK"] = ["丹麦", "丹麥", "Denmark"], ["IE"] = ["爱尔兰", "愛爾蘭", "Ireland"],
        ["IT"] = ["意大利", "義大利", "Italy"], ["ES"] = ["西班牙", "Spain"], ["PT"] = ["葡萄牙", "Portugal"],
        ["AT"] = ["奥地利", "奧地利", "Austria"], ["BE"] = ["比利时", "比利時", "Belgium"],
        ["PL"] = ["波兰", "波蘭", "Poland"], ["CZ"] = ["捷克", "Czechia", "Czech Republic"],
        ["HU"] = ["匈牙利", "Hungary"], ["RO"] = ["罗马尼亚", "羅馬尼亞", "Romania"],
        ["UA"] = ["乌克兰", "烏克蘭", "Ukraine"], ["IS"] = ["冰岛", "冰島", "Iceland"],
        ["AE"] = ["阿联酋", "阿聯酋", "United Arab Emirates"], ["IL"] = ["以色列", "Israel"],
        ["ZA"] = ["南非", "South Africa"], ["EG"] = ["埃及", "Egypt"], ["MX"] = ["墨西哥", "Mexico"],
        ["CL"] = ["智利", "Chile"], ["PE"] = ["秘鲁", "秘魯", "Peru"], ["CO"] = ["哥伦比亚", "哥倫比亞", "Colombia"],
        ["BD"] = ["孟加拉", "Bangladesh"], ["PK"] = ["巴基斯坦", "Pakistan"], ["NP"] = ["尼泊尔", "尼泊爾", "Nepal"]
    };
    private static readonly IReadOnlyDictionary<string, ProxyRegion> Regions = BuildRegions();
    private static readonly (string Id, Regex Pattern)[] Names = BuildAliases();
    private static readonly Regex Codes = new(@"(?<![a-z0-9])(" + string.Join('|', Regions.Keys.Append("UK")) + @")(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Notices = new(@"如果无法使用|请更新订阅|請更新訂閱|剩余流量|剩餘流量|到期时间|到期時間|订阅到期|訂閱到期|traffic remaining|update subscription|expiry|expires", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly record struct Resolution(ProxyRegion? Region);
    private static readonly ConcurrentDictionary<string, Resolution> Cache = new(StringComparer.Ordinal);

    public static ProxyRegion? ForId(string id) => string.IsNullOrWhiteSpace(id) ? null : Regions.GetValueOrDefault(id.ToUpperInvariant() == "UK" ? "GB" : id.ToUpperInvariant());
    public static ProxyRegion? Resolve(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached.Region;
        var region = ResolveName(name);
        if (Cache.Count >= 8192) Cache.Clear();
        Cache.TryAdd(name, new(region));
        return region;
    }
    private static ProxyRegion? ResolveName(string name)
    {
        name = name.Normalize(NormalizationForm.FormKC);
        if (Notices.IsMatch(name)) return null;
        var runes = name.EnumerateRunes().ToArray();
        HashSet<string> flags = [];
        for (var i = 0; i + 1 < runes.Length; i++)
        {
            if (runes[i].Value is < 0x1F1E6 or > 0x1F1FF || runes[i + 1].Value is < 0x1F1E6 or > 0x1F1FF) continue;
            flags.Add(new string([(char)('A' + runes[i].Value - 0x1F1E6), (char)('A' + runes[++i].Value - 0x1F1E6)]));
        }
        if (flags.Count > 0) return flags.Count == 1 ? ForId(flags.Single()) : null;
        List<(string Id, int Start, int Length)> matches = [];
        foreach (var (id, pattern) in Names)
            foreach (Match match in pattern.Matches(name)) matches.Add((id, match.Index, match.Length));
        foreach (Match match in Codes.Matches(name)) matches.Add((match.Value.Equals("UK", StringComparison.OrdinalIgnoreCase) ? "GB" : match.Value.ToUpperInvariant(), match.Index, match.Length));
        List<(string Id, int Start, int Length)> accepted = [];
        foreach (var match in matches.OrderByDescending(m => m.Length))
            if (!accepted.Any(a => match.Start < a.Start + a.Length && a.Start < match.Start + match.Length)) accepted.Add(match);
        var ids = accepted.Select(a => a.Id).Distinct().ToArray();
        return ids.Length == 1 ? ForId(ids[0]) : null;
    }
    private static Dictionary<string, ProxyRegion> BuildRegions()
    {
        Dictionary<string, ProxyRegion> result = [];
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var info = new RegionInfo(culture.Name); var id = info.TwoLetterISORegionName;
            if (id.Length == 2 && id.All(char.IsAsciiLetter)) result.TryAdd(id, new(id, CommonNames.TryGetValue(id, out var names) ? names[0] : info.EnglishName));
        }
        foreach (var (id, names) in CommonNames) result[id] = new(id, names[0]);
        return result;
    }
    private static (string, Regex)[] BuildAliases()
    {
        var english = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Select(c => new RegionInfo(c.Name))
            .Where(r => Regions.ContainsKey(r.TwoLetterISORegionName)).GroupBy(r => r.TwoLetterISORegionName)
            .ToDictionary(g => g.Key, g => g.Select(r => r.EnglishName).Distinct().ToArray());
        return Regions.Keys.SelectMany(id => (CommonNames.GetValueOrDefault(id) ?? []).Concat(english.GetValueOrDefault(id) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(alias => (id, new Regex(
                alias.Any(char.IsAsciiLetter) ? @"(?<![a-z])" + Regex.Escape(alias) + @"(?![a-z])" : Regex.Escape(alias),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))).ToArray();
    }
}

public sealed class ProxyRegionPreferences
{
    public static ProxyRegionPreferences Default { get; } = new(["JP"]);
    public IReadOnlyList<string> PriorityRegionIds { get; }
    public ProxyRegionPreferences(IEnumerable<string> ids)
    {
        PriorityRegionIds = Array.AsReadOnly(ids.Select(id => RegionResolver.ForId(id)?.Id ?? throw new ArgumentException("未知地区代码：" + id))
            .Distinct(StringComparer.Ordinal).ToArray());
    }
    public bool Contains(string? regionId) => regionId is not null && PriorityRegionIds.Contains(regionId);
    public bool HasSameOrder(ProxyRegionPreferences other) => PriorityRegionIds.SequenceEqual(other.PriorityRegionIds);
    public IReadOnlyList<string> RecoveryOrder(string? currentRegionId) => Contains(currentRegionId)
        ? new[] { currentRegionId! }.Concat(PriorityRegionIds.Where(id => id != currentRegionId)).ToArray() : PriorityRegionIds;
}

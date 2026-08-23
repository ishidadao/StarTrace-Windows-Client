namespace StarTrace.Client.Models;

public static class GachaPools
{
    private static readonly IReadOnlyDictionary<string, string> WutheringWavesAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1"] = "1", ["角色活动"] = "1", ["角色活动唤取"] = "1", ["角色精准调谐"] = "1",
            ["2"] = "2", ["武器活动"] = "2", ["武器活动唤取"] = "2", ["武器精准调谐"] = "2",
            ["3"] = "3", ["角色常驻"] = "3", ["角色常驻唤取"] = "3", ["角色调谐（常驻池）"] = "3",
            ["4"] = "4", ["武器常驻"] = "4", ["武器常驻唤取"] = "4", ["武器调谐（常驻池）"] = "4",
            ["5"] = "5", ["新手唤取"] = "5", ["新手调谐"] = "5",
            ["6"] = "6", ["新手自选"] = "6", ["新手自选唤取"] = "6",
            ["7"] = "7", ["感恩定向"] = "7", ["感恩定向唤取"] = "7",
            ["8"] = "8", ["角色新旅"] = "8", ["角色新旅唤取"] = "8",
            ["9"] = "9", ["武器新旅"] = "9", ["武器新旅唤取"] = "9",
            ["10"] = "10", ["角色联动"] = "10", ["角色联动唤取"] = "10",
            ["11"] = "11", ["武器联动"] = "11", ["武器联动唤取"] = "11",
            ["12"] = "12", ["角色忆旅"] = "12", ["角色忆旅唤取"] = "12",
            ["13"] = "13", ["武器忆旅"] = "13", ["武器忆旅唤取"] = "13",
        };

    private static readonly IReadOnlyDictionary<string, string> WutheringWavesNames =
        new Dictionary<string, string>
        {
            ["1"] = "角色活动",
            ["2"] = "武器活动",
            ["3"] = "角色常驻",
            ["4"] = "武器常驻",
            ["5"] = "新手唤取",
            ["6"] = "新手自选",
            ["7"] = "感恩定向",
            ["8"] = "角色新旅",
            ["9"] = "武器新旅",
            ["10"] = "角色联动",
            ["11"] = "武器联动",
            ["12"] = "角色忆旅",
            ["13"] = "武器忆旅",
        };

    private static readonly IReadOnlyDictionary<string, string> GenshinNames =
        new Dictionary<string, string>
        {
            ["100"] = "新手祈愿",
            ["200"] = "常驻祈愿",
            ["301"] = "角色活动",
            ["302"] = "武器活动",
            ["500"] = "集录祈愿",
        };

    public static string Normalize(string game, string poolType)
    {
        var value = poolType.Trim();
        return game == "wuwa" && WutheringWavesAliases.TryGetValue(value, out var normalized)
            ? normalized
            : value;
    }

    public static string DisplayName(string game, string poolType)
    {
        var normalized = Normalize(game, poolType);
        var names = game == "wuwa" ? WutheringWavesNames : GenshinNames;
        return names.TryGetValue(normalized, out var name) ? name : string.IsNullOrWhiteSpace(normalized) ? "未知卡池" : normalized;
    }
}

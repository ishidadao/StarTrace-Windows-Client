using System.Text.Json;
using StarTrace.Client.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace StarTrace.Client.Services;

public sealed class UigfService
{
    public async Task<IReadOnlyList<FetchResult>> PickAndImportAsync(Microsoft.UI.Xaml.Window window)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return [];
        return Parse(await File.ReadAllTextAsync(file.Path));
    }

    public async Task<bool> PickAndExportAsync(Microsoft.UI.Xaml.Window window, FetchResult data)
    {
        if (data.Game != "genshin") throw new InvalidOperationException("UIGF 仅用于原神祈愿记录。");
        var picker = new FileSavePicker { SuggestedFileName = $"UIGF_v42_{data.Uid}_{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("UIGF JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;

        var payload = new
        {
            hkrpg = Array.Empty<object>(),
            hk4e_ugc = Array.Empty<object>(),
            info = new
            {
                export_timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                export_app = "StarTrace.Client",
                export_app_version = "1.0.0",
                version = "v4.2",
            },
            hk4e = new[]
            {
                new
                {
                    uid = data.Uid,
                    timezone = 8,
                    lang = "zh-cn",
                    list = data.Records.Select(record => new
                    {
                        uigf_gacha_type = NormalizeGachaType(record.PoolType),
                        gacha_type = record.PoolType,
                        item_id = record.ItemId,
                        count = "1",
                        time = record.PulledAt,
                        name = record.ItemName,
                        item_type = record.ItemType,
                        rank_type = record.Rarity.ToString(),
                        id = record.RecordId,
                    }),
                },
            },
        };
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    public IReadOnlyList<FetchResult> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var groups = new List<FetchResult>();
        if (root.TryGetProperty("hk4e", out var hk4e) && hk4e.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in hk4e.EnumerateArray()) groups.Add(ParseEntry(entry));
        }
        else if (root.TryGetProperty("list", out var legacyList) && legacyList.ValueKind == JsonValueKind.Array)
        {
            var info = root.TryGetProperty("info", out var infoNode) ? infoNode : default;
            var uid = info.ValueKind == JsonValueKind.Object ? Text(info, "uid") : "";
            groups.Add(Build(uid, legacyList));
        }
        else throw new InvalidDataException("不是受支持的 UIGF v2/v3/v4 文件。");

        if (groups.Count == 0 || groups.Any(group => string.IsNullOrWhiteSpace(group.Uid) || group.Records.Count == 0))
            throw new InvalidDataException("UIGF 文件缺少 UID 或有效祈愿记录。");
        return groups;
    }

    private static FetchResult ParseEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("UIGF 账号条目缺少 list。");
        return Build(Text(entry, "uid"), list);
    }

    private static FetchResult Build(string uid, JsonElement list)
    {
        var records = new List<GachaRecord>();
        foreach (var item in list.EnumerateArray())
        {
            var recordId = Text(item, "id");
            if (string.IsNullOrWhiteSpace(recordId)) throw new InvalidDataException("UIGF 记录缺少 id。");
            records.Add(new GachaRecord
            {
                Uid = uid,
                RecordId = recordId,
                PoolType = Text(item, "gacha_type", Text(item, "uigf_gacha_type", "unknown")),
                ItemId = Text(item, "item_id"),
                ItemName = Text(item, "name", "未知物品"),
                ItemType = Text(item, "item_type", "未知"),
                Rarity = int.TryParse(Text(item, "rank_type", "3"), out var rarity) ? rarity : 3,
                PulledAt = Text(item, "time"),
            });
        }
        return new FetchResult("genshin", uid, records.GroupBy(item => item.RecordId).Select(group => group.First())
            .OrderByDescending(item => item.PulledAt).ToList());
    }

    private static string Text(JsonElement node, string name, string fallback = "") =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString()
            : fallback;

    private static string NormalizeGachaType(string type) => type == "400" ? "301" : type;
}

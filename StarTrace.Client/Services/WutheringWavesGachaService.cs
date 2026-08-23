using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

public sealed class WutheringWavesGachaService(HttpClient client)
{
    private const string ChinaEndpoint = "https://gmserver-api.aki-game2.com/gacha/record/query";
    private const string GlobalEndpoint = "https://gmserver-api.aki-game2.net/gacha/record/query";

    public async Task<FetchResult> FetchAsync(string recordUrl, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!Uri.TryCreate(recordUrl.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("请输入鸣潮唤取记录页的完整链接。");
        var queryStart = recordUrl.IndexOf('?');
        var query = ParseQuery(queryStart >= 0 ? recordUrl[(queryStart + 1)..] : uri.Query);
        var uid = Required(query, "player_id");
        var cardPoolId = RequiredAny(query, "resources_id", "gacha_id");
        var serverId = Required(query, "svr_id");
        var language = query.GetValueOrDefault("lang", "zh-Hans");
        var recordId = Required(query, "record_id");
        // The official client uses separate API hosts for mainland China and
        // global accounts. A mainland player ID begins with 1.
        var endpoint = uid.StartsWith('1') ? ChinaEndpoint : GlobalEndpoint;
        var records = new List<GachaRecord>();
        string? apiError = null;

        // Types 12/13 are the character/weapon "忆旅" pools added after the
        // original eleven pool types.
        for (var poolType = 1; poolType <= 13; poolType++)
        {
            progress?.Report($"正在获取鸣潮卡池 {poolType} / 13");
            var payload = new { playerId = uid, cardPoolId, serverId, languageCode = language, recordId, cardPoolType = poolType };
            using var response = await client.PostAsJsonAsync(endpoint, payload, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = json.RootElement;
            if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue) && codeValue != 0)
            {
                apiError ??= Text(root, "message");
                continue;
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;

            var drawsAtTimestamp = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                var resourceId = Text(item, "resourceId");
                var time = Text(item, "time");
                var name = Text(item, "name", "未知物品");
                var rarity = int.TryParse(Text(item, "qualityLevel", "3"), out var parsedRarity) ? parsedRarity : 3;
                var count = int.TryParse(Text(item, "count", "1"), out var parsedCount)
                    ? WutheringWavesRecordIdentity.NormalizeCount(parsedCount)
                    : 1;
                var drawNumberAtTimestamp = drawsAtTimestamp.GetValueOrDefault(time) + 1;
                drawsAtTimestamp[time] = drawNumberAtTimestamp;
                records.Add(new GachaRecord
                {
                    Uid = uid,
                    RecordId = WutheringWavesRecordIdentity.CreateRecordId(
                        uid, serverId, poolType.ToString(), resourceId, rarity, count, time, drawNumberAtTimestamp),
                    // The API may return a localized display name (for example
                    // "角色精准调谐") in cardPoolType. The requested numeric type is
                    // the stable protocol identifier and must be persisted instead.
                    PoolType = poolType.ToString(),
                    ItemId = resourceId,
                    ItemName = name,
                    ItemType = Text(item, "resourceType", "未知"),
                    Rarity = rarity,
                    Count = count,
                    PulledAt = time,
                    ServerId = serverId,
                });
            }
        }

        if (records.Count == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(apiError)
                ? "没有读取到唤取记录，链接可能已失效。请在鸣潮中重新打开唤取记录页后立即重试。"
                : $"鸣潮接口未返回记录：{apiError} 请在游戏运行时重新打开唤取记录页后立即重试。");
        return new FetchResult("wuwa", uid, records
            .GroupBy(record => record.RecordId).Select(group => group.First())
            .OrderByDescending(record => record.PulledAt).ToList());
    }

    private static string Text(JsonElement item, string name, string fallback = "") => item.TryGetProperty(name, out var value) ? value.ToString() : fallback;
    private static string Required(Dictionary<string, string> query, string key) => query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"鸣潮链接缺少 {key}。");
    private static string RequiredAny(Dictionary<string, string> query, params string[] keys)
    {
        foreach (var key in keys)
            if (query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        throw new ArgumentException($"鸣潮链接缺少 {string.Join(" / ", keys)}。");
    }
    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('#')[0]
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => WebUtility.UrlDecode(parts[0]), parts => WebUtility.UrlDecode(parts[1]));
}

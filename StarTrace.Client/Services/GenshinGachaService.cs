using System.Net;
using System.Text.Json;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

public sealed class GenshinGachaService(HttpClient client)
{
    private static readonly string[] PoolTypes = ["301", "302", "200", "100", "500"];

    public async Task<FetchResult> FetchAsync(string inputUrl, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!Uri.TryCreate(inputUrl.Trim(), UriKind.Absolute, out var source))
            throw new ArgumentException("请输入游戏祈愿历史中的完整地址。");

        if (!source.AbsolutePath.Contains("getGachaLog", StringComparison.OrdinalIgnoreCase))
            source = ConvertHistoryPageToApi(source);

        var authQuery = ParseQuery(FullQuery(source));
        foreach (var required in new[] { "authkey" })
            if (!authQuery.ContainsKey(required)) throw new ArgumentException("链接缺少 authkey，可能已经失效。");

        var allowed = new[] { "authkey", "authkey_ver", "sign_type", "region", "lang", "game_biz" };
        var baseQuery = authQuery.Where(pair => allowed.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var records = new List<GachaRecord>();
        string uid = "";

        foreach (var poolType in PoolTypes)
        {
            var endId = "0";
            var page = 1;
            while (true)
            {
                progress?.Report($"正在获取原神卡池 {poolType} · 第 {page} 页");
                var query = new Dictionary<string, string>(baseQuery)
                {
                    ["gacha_type"] = poolType,
                    ["page"] = page.ToString(),
                    ["size"] = "20",
                    ["end_id"] = endId,
                };
                var builder = new UriBuilder(source) { Fragment = "", Query = BuildQuery(query) };
                using var response = await client.GetAsync(builder.Uri, token);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = json.RootElement;
                if (root.GetProperty("retcode").GetInt32() != 0)
                    throw new InvalidOperationException(root.TryGetProperty("message", out var message) ? message.GetString() : "原神接口返回失败");

                var list = root.GetProperty("data").GetProperty("list");
                if (list.GetArrayLength() == 0) break;
                foreach (var item in list.EnumerateArray())
                {
                    var itemUid = Text(item, "uid");
                    uid = string.IsNullOrWhiteSpace(uid) ? itemUid : uid;
                    if (!string.Equals(uid, itemUid, StringComparison.Ordinal))
                        throw new InvalidDataException("同一次抓取返回了不同 UID，已停止以避免混合账号数据。");
                    records.Add(new GachaRecord
                    {
                        Uid = itemUid,
                        RecordId = Text(item, "id"),
                        PoolType = Text(item, "gacha_type", poolType),
                        ItemId = Text(item, "item_id"),
                        ItemName = Text(item, "name", "未知物品"),
                        ItemType = Text(item, "item_type", "未知"),
                        Rarity = int.TryParse(Text(item, "rank_type", "3"), out var rarity) ? rarity : 3,
                        PulledAt = Text(item, "time"),
                    });
                }
                endId = Text(list[list.GetArrayLength() - 1], "id");
                page++;
                await Task.Delay(350, token);
            }
        }

        if (string.IsNullOrWhiteSpace(uid) || records.Count == 0) throw new InvalidOperationException("没有读取到抽卡记录。");
        return new FetchResult("genshin", uid, records
            .GroupBy(record => record.RecordId).Select(group => group.First())
            .OrderByDescending(record => record.PulledAt).ToList());
    }

    private static string Text(JsonElement item, string name, string fallback = "") =>
        item.TryGetProperty(name, out var value) ? value.ToString() : fallback;

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => WebUtility.UrlDecode(parts[0]), parts => WebUtility.UrlDecode(parts[1]));

    private static string BuildQuery(Dictionary<string, string> query) => string.Join("&",
        query.Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}"));

    private static Uri ConvertHistoryPageToApi(Uri source)
    {
        var query = ParseQuery(FullQuery(source));
        var gameBiz = query.GetValueOrDefault("game_biz", "");
        if (!source.Host.Contains("webstatic.mihoyo.com", StringComparison.OrdinalIgnoreCase) ||
            !gameBiz.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请输入游戏祈愿历史页或完整 getGachaLog 地址。");

        var host = gameBiz.Equals("hk4e_global", StringComparison.OrdinalIgnoreCase)
            ? "hk4e-api-os.hoyoverse.com"
            : "public-operation-hk4e.mihoyo.com";
        return new UriBuilder(Uri.UriSchemeHttps, host)
        {
            Path = "/gacha_info/api/getGachaLog",
            Query = BuildQuery(query),
        }.Uri;
    }

    private static string FullQuery(Uri source)
    {
        if (!string.IsNullOrWhiteSpace(source.Query)) return source.Query;
        var fragment = source.Fragment;
        var queryStart = fragment.IndexOf('?');
        return queryStart >= 0 ? fragment[(queryStart + 1)..] : "";
    }
}

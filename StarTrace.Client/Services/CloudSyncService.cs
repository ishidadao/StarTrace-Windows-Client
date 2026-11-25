using System.Net.Http.Json;
using System.Text.Json;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

public sealed class CloudSyncService(HttpClient client, HttpClient? authenticatedClient = null) : IDisposable
{
    private readonly HttpClient _apiClient = authenticatedClient ??
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    public static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    // Kept separate from upload so the existing offline queue and sync flow are
    // unchanged. When a server account is read, icon_url is retained verbatim.
    public async Task<FetchResult?> ReadAccountAsync(
        string baseUrl,
        string accessToken,
        string game,
        string uid,
        CancellationToken token = default)
    {
        if (game is not ("genshin" or "wuwa")) throw new ArgumentException("游戏来源无效。");
        if (!TryNormalizeBaseUrl(baseUrl, out var normalizedBaseUrl) ||
            !Uri.TryCreate($"{normalizedBaseUrl}/api/records?game={Uri.EscapeDataString(game)}&uid={Uri.EscapeDataString(uid)}", UriKind.Absolute, out var endpoint))
            throw new ArgumentException("网站地址无效。");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("请先登录星迹账号。");

        RequireOfficialOrigin(normalizedBaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Trim());
        using var response = await _apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var responseText = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadError(responseText, "读取云端记录失败。"));
        using var json = JsonDocument.Parse(responseText);
        if (!json.RootElement.TryGetProperty("records", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;

        var records = new List<GachaRecord>();
        foreach (var row in rows.EnumerateArray())
        {
            var recordId = Field(row, "recordId", "record_id", "id");
            var pulledAt = Field(row, "pulledAt", "pulled_at", "time");
            if (string.IsNullOrWhiteSpace(recordId) || string.IsNullOrWhiteSpace(pulledAt)) continue;
            records.Add(new GachaRecord
            {
                Uid = Field(row, "uid", "playerId") is { Length: > 0 } rowUid ? rowUid : uid,
                RecordId = recordId,
                PoolType = Field(row, "poolType", "pool_type", "gacha_type", "cardPoolType"),
                ItemId = Field(row, "itemId", "item_id", "resourceId"),
                ItemName = Field(row, "itemName", "item_name", "name"),
                ItemType = Field(row, "itemType", "item_type", "resourceType"),
                Rarity = int.TryParse(Field(row, "rarity", "rank_type", "qualityLevel"), out var rarity) ? rarity : 3,
                Count = int.TryParse(Field(row, "count"), out var count) ? count : 1,
                PulledAt = pulledAt,
                ServerId = Field(row, "serverId", "server_id"),
                IconUrl = Field(row, "icon_url", "iconUrl"),
            });
        }
        return new FetchResult(game, uid, records);
    }

    public async Task<bool> IsServerReachableAsync(string baseUrl, CancellationToken token = default)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var normalizedBaseUrl) ||
            !Uri.TryCreate(normalizedBaseUrl + "/", UriKind.Absolute, out var endpoint)) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<int> UploadAsync(string baseUrl, string accessToken, string game, IEnumerable<GachaRecord> records, CancellationToken token = default)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var normalizedBaseUrl) ||
            !Uri.TryCreate(normalizedBaseUrl + "/api/records", UriKind.Absolute, out var endpoint))
            throw new ArgumentException("网站地址无效。");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("请先登录星迹账号。");

        RequireOfficialOrigin(normalizedBaseUrl);
        if (game is not ("genshin" or "wuwa")) throw new ArgumentException("游戏来源无效。");
        var groups = records.GroupBy(record => (Game: game, record.Uid));
        var total = 0;
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Key.Uid)) throw new InvalidDataException("记录中存在空 UID，已停止上传。");
            foreach (var batch in UploadBatches(game, group))
            {
                var payload = new { game = group.Key.Game, uid = group.Key.Uid, source = "winui-client", records = batch };
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(payload) };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Trim());
                using var response = await _apiClient.SendAsync(request, token);
                var responseText = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                        throw new CloudAuthenticationException("云端拒绝了登录凭据，请重新统一认证；待上传记录已保留。");
                    throw new InvalidOperationException(ReadError(responseText, "同步失败。"));
                }
                using var json = JsonDocument.Parse(responseText);
                var written = json.RootElement.GetProperty("written").GetInt32();
                var ignored = json.RootElement.TryGetProperty("ignoredDuplicates", out var duplicates) ? duplicates.GetInt32() : 0;
                if (written + ignored != batch.Length)
                    throw new InvalidDataException("云端未确认全部记录，已保留待上传队列供重试。");
                total += batch.Length;
            }
        }
        return total;
    }

    private static IEnumerable<GachaRecord[]> UploadBatches(string game, IEnumerable<GachaRecord> records)
    {
        if (game != "wuwa")
        {
            foreach (var chunk in records.Chunk(1000)) yield return chunk;
            yield break;
        }
        // The server deduplicates WuWa by natural identity and multiplicity.
        // Splitting a same-second ten-pull across requests can lose repeat items.
        var buffer = new List<GachaRecord>();
        foreach (var timestamp in records.GroupBy(record => record.PulledAt))
        {
            var group = timestamp.ToArray();
            if (group.Length > 10000) throw new InvalidDataException("同一时间的记录超过服务器批次上限，已保留待上传数据。");
            if (buffer.Count > 0 && buffer.Count + group.Length > 1000)
            {
                yield return buffer.ToArray();
                buffer.Clear();
            }
            buffer.AddRange(group);
        }
        if (buffer.Count > 0) yield return buffer.ToArray();
    }

    private static string ReadError(string responseText, string fallback)
    {
        try { return JsonDocument.Parse(responseText).RootElement.GetProperty("error").GetString() ?? fallback; }
        catch { return string.IsNullOrWhiteSpace(responseText) ? fallback : responseText; }
    }

    private static string Field(JsonElement row, params string[] names)
    {
        foreach (var name in names)
            if (row.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.ToString();
        return "";
    }

    private static void RequireOfficialOrigin(string baseUrl)
    {
        if (baseUrl != IdentityLoginService.ApiOrigin)
            throw new InvalidOperationException("统一认证令牌只能发送到正式星迹服务。");
    }

    public void Dispose() => _apiClient.Dispose();
}

public sealed class CloudAuthenticationException(string message) : InvalidOperationException(message);

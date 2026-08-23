using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

public sealed class ItemIconService
{
    private const string GenshinAvatarEndpoint = "https://api-takumi.mihoyo.com/event/e20200928calculate/v1/avatar/list";
    private const string GenshinWeaponEndpoint = "https://api-takumi.mihoyo.com/event/e20200928calculate/v1/weapon/list";
    private const string WutheringWavesAvatarEndpoint = "https://mc.appfeng.com/json/avatar.json";
    private const string WutheringWavesWeaponEndpoint = "https://mc.appfeng.com/json/weapon.json";

    private readonly HttpClient _client;
    private readonly string _cacheRoot;
    private readonly SemaphoreSlim _metadataGate = new(1, 1);
    private readonly SemaphoreSlim _imageGate = new(4, 4);
    private readonly ConcurrentDictionary<string, byte[]> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IconCatalog> _catalogs = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public ItemIconService(HttpClient client, string? cacheRoot = null)
    {
        _client = client;
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarTrace", "item-icons");
    }

    public async Task<string?> ResolveIconUrlAsync(string game, GachaRecord record, CancellationToken token = default)
    {
        if (IsHttpUrl(record.IconUrl)) return record.IconUrl;
        if (game is not ("genshin" or "wuwa")) return null;

        var catalog = await GetCatalogAsync(game, token);
        var item = catalog.Find(record, IsWeapon(record));
        return item?.IconUrl;
    }

    public async Task<byte[]?> GetImageBytesAsync(string url, CancellationToken token = default)
    {
        if (!IsHttpUrl(url)) return null;
        if (_images.TryGetValue(url, out var memory)) return memory;

        var path = ImagePath(url);
        try
        {
            if (File.Exists(path))
            {
                var cached = await File.ReadAllBytesAsync(path, token);
                if (cached.Length > 0)
                {
                    _images.TryAdd(url, cached);
                    return cached;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        await _imageGate.WaitAsync(token);
        try
        {
            if (_images.TryGetValue(url, out memory)) return memory;
            try
            {
                if (File.Exists(path))
                {
                    var cached = await File.ReadAllBytesAsync(path, token);
                    if (cached.Length > 0)
                    {
                        _images.TryAdd(url, cached);
                        return cached;
                    }
                }

                using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) return null;
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                if (bytes.Length == 0) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, token);
                _images.TryAdd(url, bytes);
                return bytes;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
        finally { _imageGate.Release(); }
    }

    public static bool IsWeapon(GachaRecord record) =>
        record.ItemType.Contains("武器", StringComparison.OrdinalIgnoreCase) ||
        record.ItemType.Contains("weapon", StringComparison.OrdinalIgnoreCase);

    private async Task<IconCatalog> GetCatalogAsync(string game, CancellationToken token)
    {
        lock (_catalogs)
            if (_catalogs.TryGetValue(game, out var existing)) return existing;

        await _metadataGate.WaitAsync(token);
        try
        {
            lock (_catalogs)
                if (_catalogs.TryGetValue(game, out var existing)) return existing;

            var catalog = game == "genshin"
                ? new IconCatalog(
                    await LoadMetadataAsync(game, false, GenshinAvatarEndpoint, token),
                    await LoadMetadataAsync(game, true, GenshinWeaponEndpoint, token))
                : new IconCatalog(
                    await LoadMetadataAsync(game, false, WutheringWavesAvatarEndpoint, token),
                    await LoadMetadataAsync(game, true, WutheringWavesWeaponEndpoint, token));
            lock (_catalogs) _catalogs[game] = catalog;
            return catalog;
        }
        finally { _metadataGate.Release(); }
    }

    private async Task<List<IconMetadata>> LoadMetadataAsync(string game, bool weapon, string endpoint, CancellationToken token)
    {
        var cachePath = MetadataPath(game, weapon);
        var cached = await ReadMetadataCacheAsync(cachePath, token);
        try
        {
            using var request = new HttpRequestMessage(game == "genshin" ? HttpMethod.Post : HttpMethod.Get, endpoint);
            if (game == "genshin")
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://act.mihoyo.com");
                request.Headers.Referrer = new Uri("https://act.mihoyo.com/ys/event/calculator/index.html");
                // Mirrors FufuLauncher's full-list calculator requests. An empty
                // body currently returns only a small, current-item subset.
                request.Content = weapon
                    ? JsonContent.Create(new { page = 1, size = 1000, weapon_levels = new[] { 1, 2, 3, 4, 5 } })
                    : JsonContent.Create(new { page = 1, size = 1000, is_all = true });
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return cached;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var parsed = ParseMetadata(json.RootElement, game, weapon);
            if (parsed.Count == 0) return cached;
            await WriteMetadataCacheAsync(cachePath, parsed, token);
            return parsed;
        }
        catch (OperationCanceledException) { throw; }
        catch { return cached; }
    }

    private async Task<List<IconMetadata>> ReadMetadataCacheAsync(string path, CancellationToken token)
    {
        try
        {
            if (!File.Exists(path)) return [];
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<IconMetadata>>(stream, _json, token) ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task WriteMetadataCacheAsync(string path, List<IconMetadata> data, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, data, _json, token);
            File.Move(temporary, path, true);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static List<IconMetadata> ParseMetadata(JsonElement root, string game, bool weapon)
    {
        var list = root;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("list", out var nestedList))
            list = nestedList;
        if (list.ValueKind != JsonValueKind.Array) return [];

        var results = new List<IconMetadata>();
        foreach (var item in list.EnumerateArray())
        {
            var id = Text(item, "id");
            var name = Text(item, "name");
            var icon = Text(item, "icon");
            var url = game == "wuwa"
                ? WutheringWavesIconUrl(icon, weapon)
                : NormalizeUrl(icon);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            results.Add(new IconMetadata(id, name, url, weapon));
        }
        return results;
    }

    private static string? WutheringWavesIconUrl(string icon, bool weapon)
    {
        if (IsHttpUrl(icon)) return icon;
        if (string.IsNullOrWhiteSpace(icon)) return null;
        return $"https://mc.appfeng.com/ui/{(weapon ? "weapon" : "avatar")}/{Uri.EscapeDataString(icon)}.png";
    }

    private static string? NormalizeUrl(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        return IsHttpUrl(value) ? value : null;
    }

    private static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http";

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.ToString() : "";

    private string MetadataPath(string game, bool weapon) => Path.Combine(
        _cacheRoot, $"{game}-{(weapon ? "weapon" : "avatar")}.json");

    private string ImagePath(string url) => Path.Combine(
        _cacheRoot,
        "images",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img");

    private sealed record IconMetadata(string Id, string Name, string IconUrl, bool IsWeapon);

    private sealed class IconCatalog(List<IconMetadata> avatars, List<IconMetadata> weapons)
    {
        private readonly List<IconMetadata> _avatars = avatars;
        private readonly List<IconMetadata> _weapons = weapons;

        public IconMetadata? Find(GachaRecord record, bool weapon)
        {
            var preferred = weapon ? _weapons : _avatars;
            var secondary = weapon ? _avatars : _weapons;
            if (!string.IsNullOrWhiteSpace(record.ItemId))
            {
                var byId = preferred.FirstOrDefault(item => item.Id == record.ItemId) ??
                    secondary.FirstOrDefault(item => item.Id == record.ItemId);
                if (byId is not null) return byId;
            }

            // Old UIGF and early local records can be missing ItemId. Name matching
            // intentionally remains exact to avoid assigning a same-named item icon.
            return preferred.FirstOrDefault(item => item.Name == record.ItemName) ??
                secondary.FirstOrDefault(item => item.Name == record.ItemName);
        }
    }
}

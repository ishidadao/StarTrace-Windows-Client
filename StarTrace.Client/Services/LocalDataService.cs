using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

public sealed class LocalDataService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public LocalDataService(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarTrace", "client-data.json");
    }

    public async Task<FetchResult?> LoadLatestAsync(CancellationToken token = default)
    {
        var state = await ReadAsync(token);
        var account = state.Accounts.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        return account is null ? null : new FetchResult(account.Game, account.Uid, account.Records);
    }

    public async Task<FetchResult?> LoadLatestForGameAsync(string game, CancellationToken token = default)
    {
        if (game is not ("genshin" or "wuwa")) throw new ArgumentException("游戏来源无效。");
        var state = await ReadAsync(token);
        var account = state.Accounts
            .Where(item => item.Game == game)
            .OrderByDescending(item => item.UpdatedAt)
            .FirstOrDefault();
        return account is null ? null : new FetchResult(account.Game, account.Uid, account.Records);
    }

    public async Task<FetchResult?> LoadAccountAsync(string game, string uid, CancellationToken token = default)
    {
        var state = await ReadAsync(token);
        var account = state.Accounts.FirstOrDefault(item => item.Game == game && item.Uid == uid);
        return account is null ? null : new FetchResult(account.Game, account.Uid, account.Records);
    }

    public async Task<FetchResult> MergeAsync(FetchResult incoming, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(incoming.Uid) || incoming.Records.Any(record => record.Uid != incoming.Uid))
            throw new InvalidDataException("本地保存前 UID 校验失败，已拒绝混合记录。");

        await _gate.WaitAsync(token);
        try
        {
            var state = await ReadUnlockedAsync(token);
            var account = state.Accounts.FirstOrDefault(item => item.Game == incoming.Game && item.Uid == incoming.Uid);
            if (account is null)
            {
                account = new LocalAccount { Game = incoming.Game, Uid = incoming.Uid };
                state.Accounts.Add(account);
            }
            var normalizedIncoming = incoming.Records
                .Select(record => NormalizeRecord(incoming.Game, record))
                .ToList();
            account.Records = RecordMergePolicy.Merge(
                incoming.Game,
                account.Records,
                normalizedIncoming);
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await WriteUnlockedAsync(state, token);
            return new FetchResult(account.Game, account.Uid, account.Records);
        }
        finally { _gate.Release(); }
    }

    public async Task QueueSyncAsync(string game, string uid, CancellationToken token = default)
    {
        await UpdateAsync(state =>
        {
            if (!state.Pending.Any(item => item.Game == game && item.Uid == uid))
                state.Pending.Add(new PendingAccount { Game = game, Uid = uid });
        }, token);
    }

    public async Task MarkSyncedAsync(string game, string uid, CancellationToken token = default) =>
        await UpdateAsync(state => state.Pending.RemoveAll(item => item.Game == game && item.Uid == uid), token);

    public async Task<IReadOnlyList<PendingAccount>> GetPendingAsync(CancellationToken token = default) =>
        (await ReadAsync(token)).Pending.ToList();

    public async Task SaveIdentitySettingsAsync(IdentityLogin login, CancellationToken token = default)
    {
        var access = ProtectedData.Protect(Encoding.UTF8.GetBytes(login.AccessToken), null, DataProtectionScope.CurrentUser);
        var refresh = ProtectedData.Protect(Encoding.UTF8.GetBytes(login.RefreshToken), null, DataProtectionScope.CurrentUser);
        await UpdateAsync(state =>
        {
            state.ServerUrl = IdentityLoginService.ApiOrigin;
            state.ProtectedAccessToken = Convert.ToBase64String(access);
            state.ProtectedRefreshToken = Convert.ToBase64String(refresh);
            state.AccessTokenExpiresAt = login.ExpiresAt;
            state.Username = login.Username;
            state.ProtectedSyncKey = "";
        }, token);
    }

    public Task ClearCloudLoginAsync(CancellationToken token = default) => UpdateAsync(state =>
    {
        state.ProtectedAccessToken = "";
        state.ProtectedRefreshToken = "";
        state.AccessTokenExpiresAt = null;
        state.ProtectedSyncKey = "";
        state.Username = "";
    }, token);

    public async Task<CloudSettings> GetCloudSettingsAsync(CancellationToken token = default)
    {
        var state = await ReadAsync(token);
        var accessToken = "";
        var refreshToken = "";
        if (!string.IsNullOrWhiteSpace(state.ProtectedAccessToken))
        {
            try
            {
                accessToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(state.ProtectedAccessToken), null, DataProtectionScope.CurrentUser));
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(state.ProtectedRefreshToken))
        {
            try
            {
                refreshToken = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    Convert.FromBase64String(state.ProtectedRefreshToken), null, DataProtectionScope.CurrentUser));
            }
            catch { }
        }
        return new CloudSettings(
            CloudSyncService.TryNormalizeBaseUrl(state.ServerUrl, out var normalizedBaseUrl) ? normalizedBaseUrl : "",
            accessToken,
            state.Username, refreshToken, state.AccessTokenExpiresAt);
    }

    private async Task<LocalState> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadUnlockedAsync(token); }
        finally { _gate.Release(); }
    }

    private async Task UpdateAsync(Action<LocalState> update, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = await ReadUnlockedAsync(token);
            update(state);
            await WriteUnlockedAsync(state, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<LocalState> ReadUnlockedAsync(CancellationToken token)
    {
        if (!File.Exists(_filePath)) return new LocalState();
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var state = await JsonSerializer.DeserializeAsync<LocalState>(stream, _json, token) ?? new LocalState();
            foreach (var account in state.Accounts)
                account.Records = account.Records.Select(record => NormalizeRecord(account.Game, record)).ToList();
            return state;
        }
        catch (JsonException)
        {
            var backup = _filePath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(_filePath, backup, true);
            return new LocalState();
        }
    }

    private async Task WriteUnlockedAsync(LocalState state, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporary = _filePath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, state, _json, token);
        File.Move(temporary, _filePath, true);
    }

    private static GachaRecord NormalizeRecord(string game, GachaRecord record)
    {
        var poolType = GachaPools.Normalize(game, record.PoolType);
        if (poolType == record.PoolType) return record;
        return new GachaRecord
        {
            Uid = record.Uid,
            RecordId = record.RecordId,
            PoolType = poolType,
            ItemId = record.ItemId,
            ItemName = record.ItemName,
            ItemType = record.ItemType,
            Rarity = record.Rarity,
            Count = game == "wuwa" ? WutheringWavesRecordIdentity.NormalizeCount(record.Count) : record.Count,
            PulledAt = record.PulledAt,
            ServerId = record.ServerId,
            IconUrl = record.IconUrl,
        };
    }
}

public sealed record CloudSettings(string BaseUrl, string AccessToken, string Username, string RefreshToken = "", DateTimeOffset? ExpiresAt = null)
{
    public bool UsesUnifiedIdentity => BaseUrl.TrimEnd('/') == IdentityLoginService.ApiOrigin &&
        !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(RefreshToken) && ExpiresAt is not null;
}
public sealed class PendingAccount { public string Game { get; set; } = ""; public string Uid { get; set; } = ""; }
public sealed class LocalAccount
{
    public string Game { get; set; } = "";
    public string Uid { get; set; } = "";
    public List<GachaRecord> Records { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}
public sealed class LocalState
{
    public string ServerUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string ProtectedAccessToken { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    // Kept only so older local files can be read; legacy sync keys are never reused.
    public string ProtectedSyncKey { get; set; } = "";
    public List<LocalAccount> Accounts { get; set; } = [];
    public List<PendingAccount> Pending { get; set; } = [];
}

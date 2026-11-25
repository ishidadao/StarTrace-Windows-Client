namespace StarTrace.Client.Services;

public sealed class CloudTransferService(LocalDataService local, CloudSyncService cloud)
{
    public async Task<int> UploadPendingAsync(CloudSettings settings, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        if (!settings.UsesUnifiedIdentity)
            throw new CloudAuthenticationException("请先统一认证登录，记录已保留在待上传队列。");
        var total = 0;
        foreach (var pending in await local.GetPendingAsync(token))
        {
            var account = await local.LoadAccountAsync(pending.Game, pending.Uid, token);
            if (account is null || account.Records.Count == 0)
            {
                await local.MarkSyncedAsync(pending.Game, pending.Uid, token);
                continue;
            }
            progress?.Report($"正在上传 {(account.Game == "genshin" ? "原神" : "鸣潮")} · UID {account.Uid} · {account.Records.Count} 条");
            total += await cloud.UploadAsync(settings.BaseUrl, settings.AccessToken, account.Game, account.Records, token);
            await local.MarkSyncedAsync(account.Game, account.Uid, token);
        }
        return total;
    }
}

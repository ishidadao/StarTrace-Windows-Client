using System.Text.Json;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

public static class BackupService
{
    public static string Serialize(IReadOnlyList<FetchResult> accounts) =>
        JsonSerializer.Serialize(new
        {
            format = "startrace-backup",
            version = 1,
            exportedAt = DateTimeOffset.UtcNow,
            accounts = accounts.Select(account => new
            {
                game = account.Game,
                uid = account.Uid,
                records = account.Records,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
}

using System.Security.Cryptography;
using System.Text;
using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

internal static class WutheringWavesRecordIdentity
{
    private const string Version = "wuwa-v2";

    public static string CreateRecordId(
        string uid,
        string serverId,
        string poolType,
        string resourceId,
        int rarity,
        int count,
        string pulledAt,
        int drawNumberAtTimestamp)
    {
        var source = Join(
            Version,
            uid,
            serverId,
            poolType,
            resourceId,
            rarity.ToString(),
            NormalizeCount(count).ToString(),
            pulledAt,
            drawNumberAtTimestamp.ToString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))
            .ToLowerInvariant()[..32];
    }

    public static string ContentKey(GachaRecord record) => Join(
        record.Uid,
        record.ServerId ?? "",
        record.PoolType,
        record.ItemId,
        record.Rarity.ToString(),
        NormalizeCount(record.Count).ToString(),
        record.PulledAt);

    public static int NormalizeCount(int count) => count > 0 ? count : 1;

    private static string Join(params string[] values) => string.Concat(
        values.Select(value => $"{value.Length}:{value}"));
}

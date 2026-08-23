using System.Text.Json.Serialization;

namespace StarTrace.Client.Models;

public sealed class GachaRecord
{
    [JsonPropertyName("uid")] public string Uid { get; init; } = "";
    [JsonPropertyName("recordId")] public string RecordId { get; init; } = "";
    [JsonPropertyName("poolType")] public string PoolType { get; init; } = "";
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";
    [JsonPropertyName("itemName")] public string ItemName { get; init; } = "";
    [JsonPropertyName("itemType")] public string ItemType { get; init; } = "";
    [JsonPropertyName("rarity")] public int Rarity { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; } = 1;
    [JsonPropertyName("pulledAt")] public string PulledAt { get; init; } = "";
    [JsonPropertyName("serverId")] public string? ServerId { get; init; }
    [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }

    [JsonIgnore] public string RarityText => new('★', Math.Clamp(Rarity, 0, 5));
    [JsonIgnore] public string AccountKey => $"{Uid}";
}

public sealed record FetchResult(string Game, string Uid, IReadOnlyList<GachaRecord> Records);

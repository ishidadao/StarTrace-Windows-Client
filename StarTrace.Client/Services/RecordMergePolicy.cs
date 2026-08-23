using StarTrace.Client.Models;

namespace StarTrace.Client.Services;

internal static class RecordMergePolicy
{
    public static List<GachaRecord> Merge(
        string game,
        IReadOnlyList<GachaRecord> existing,
        IReadOnlyList<GachaRecord> incoming)
    {
        IEnumerable<GachaRecord> retained = existing;
        if (game == "wuwa")
        {
            // Old client releases included the API array index in RecordId. When
            // new pulls were prepended, every historical ID changed. Replace all
            // locally cached versions of records covered by the current official
            // response; this also removes duplicate legacy-ID batches while
            // preserving expired records that are no longer returned by the API.
            var refreshedContent = incoming
                .Select(WutheringWavesRecordIdentity.ContentKey)
                .ToHashSet(StringComparer.Ordinal);
            retained = existing.Where(record =>
                !refreshedContent.Contains(WutheringWavesRecordIdentity.ContentKey(record)));
        }

        return incoming.Concat(retained)
            .GroupBy(record => record.RecordId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(record => record.PulledAt, StringComparer.Ordinal)
            .ToList();
    }
}

namespace DictationApp.Core.History;

public interface IHistoryRepository
{
    Task InitialiseAsync(CancellationToken ct = default);

    Task<long> InsertAsync(DictationRecord record, CancellationToken ct = default);

    Task UpdateAsync(DictationRecord record, CancellationToken ct = default);

    Task<DictationRecord?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Newest first. A null or blank query lists everything; otherwise FTS5 full-text search.</summary>
    Task<IReadOnlyList<DictationRecord>> SearchAsync(string? query, int limit = 200, CancellationToken ct = default);

    Task<DictationRecord?> GetLatestAsync(CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Deletes records created before <paramref name="olderThan"/>; returns the audio paths they referenced.</summary>
    Task<IReadOnlyList<string>> DeleteOlderThanAsync(DateTimeOffset olderThan, CancellationToken ct = default);

    /// <summary>Clears the audio path on records created before <paramref name="olderThan"/>; returns the paths cleared.</summary>
    Task<IReadOnlyList<string>> ClearAudioOlderThanAsync(DateTimeOffset olderThan, CancellationToken ct = default);

    Task<HistoryStats> GetStatsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<string>> DeleteAllAsync(CancellationToken ct = default);
}

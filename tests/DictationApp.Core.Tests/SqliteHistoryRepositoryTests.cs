using DictationApp.Core.Cleanup;
using DictationApp.Core.History;
using Microsoft.Extensions.Logging.Abstractions;

namespace DictationApp.Core.Tests;

public sealed class SqliteHistoryRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteHistoryRepository _repo;

    public SqliteHistoryRepositoryTests()
    {
        Directory.CreateDirectory(_dir);
        _repo = new SqliteHistoryRepository(Path.Combine(_dir, "history.db"), NullLogger<SqliteHistoryRepository>.Instance);
    }

    public void Dispose()
    {
        _repo.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DictationRecord Record(string raw, string inserted, DateTimeOffset? at = null, string? audio = null, RecordStatus status = RecordStatus.Inserted) => new()
    {
        CreatedAt = at ?? DateTimeOffset.UtcNow,
        UpdatedAt = at ?? DateTimeOffset.UtcNow,
        RawTranscript = raw,
        CleanedText = inserted,
        InsertedText = inserted,
        Tone = Tone.Formal,
        Level = CleanupLevel.Medium,
        ProcessName = "OUTLOOK",
        WindowTitle = "Inbox",
        DurationMs = 4200,
        AudioPath = audio,
        CostEstimate = 0.0005m,
        Status = status,
        LlmModel = "gemini-2.5-flash-lite",
    };

    [Fact]
    public async Task Insert_get_update_delete_round_trip()
    {
        var id = await _repo.InsertAsync(Record("raw words", "Raw words."));
        var loaded = await _repo.GetAsync(id);

        Assert.NotNull(loaded);
        Assert.Equal("Raw words.", loaded.InsertedText);
        Assert.Equal(Tone.Formal, loaded.Tone);
        Assert.Equal(CleanupLevel.Medium, loaded.Level);
        Assert.Equal(RecordStatus.Inserted, loaded.Status);
        Assert.Equal(0.0005m, loaded.CostEstimate);
        Assert.Equal("gemini-2.5-flash-lite", loaded.LlmModel);

        loaded.Status = RecordStatus.CopiedOnly;
        loaded.AiEditUndone = true;
        await _repo.UpdateAsync(loaded);
        var again = await _repo.GetAsync(id);
        Assert.Equal(RecordStatus.CopiedOnly, again!.Status);
        Assert.True(again.AiEditUndone);

        await _repo.DeleteAsync(id);
        Assert.Null(await _repo.GetAsync(id));
    }

    [Fact]
    public async Task Search_uses_full_text_index_with_prefix_matching()
    {
        await _repo.InsertAsync(Record("the isoniazid dose", "The isoniazid dose."));
        await _repo.InsertAsync(Record("meeting notes", "Meeting notes."));
        await _repo.InsertAsync(Record("rifapentine schedule", "Rifapentine schedule."));

        var all = await _repo.SearchAsync(null);
        Assert.Equal(3, all.Count);
        Assert.Equal("Rifapentine schedule.", all[0].InsertedText); // newest first

        var hits = await _repo.SearchAsync("isoni");
        Assert.Single(hits);
        Assert.Contains("isoniazid", hits[0].RawTranscript);

        var byTitle = await _repo.SearchAsync("Inbox");
        Assert.Equal(3, byTitle.Count);

        Assert.Empty(await _repo.SearchAsync("nothing-here"));
        Assert.Empty(await _repo.SearchAsync("\"quoted\" OR (weird"));
    }

    [Fact]
    public async Task Search_index_follows_updates()
    {
        var id = await _repo.InsertAsync(Record("alpha", "Alpha."));
        var r = await _repo.GetAsync(id);
        r!.InsertedText = "Bravo.";
        r.CleanedText = "Bravo.";
        r.RawTranscript = "bravo";
        await _repo.UpdateAsync(r);

        Assert.Empty(await _repo.SearchAsync("alpha"));
        Assert.Single(await _repo.SearchAsync("bravo"));
    }

    [Fact]
    public async Task Retention_deletes_old_records_and_reports_audio_paths()
    {
        var now = DateTimeOffset.UtcNow;
        await _repo.InsertAsync(Record("old", "Old.", now.AddDays(-20), audio: "old.wav"));
        await _repo.InsertAsync(Record("recent", "Recent.", now.AddHours(-1), audio: "recent.wav"));
        await _repo.InsertAsync(Record("no audio", "No audio.", now.AddDays(-20)));

        var deleted = await _repo.DeleteOlderThanAsync(now.AddDays(-14));
        Assert.Equal(["old.wav"], deleted);
        Assert.Equal(1, (await _repo.GetStatsAsync()).Count);

        var cleared = await _repo.ClearAudioOlderThanAsync(now.AddMinutes(-30));
        Assert.Equal(["recent.wav"], cleared);
        var remaining = await _repo.GetLatestAsync();
        Assert.Null(remaining!.AudioPath);
        Assert.Equal("Recent.", remaining.InsertedText);
    }

    [Fact]
    public async Task Stats_and_delete_all()
    {
        await _repo.InsertAsync(Record("a", "A.", audio: "a.wav"));
        await _repo.InsertAsync(Record("b", "B."));
        var stats = await _repo.GetStatsAsync();
        Assert.Equal(2, stats.Count);
        Assert.Equal(1, stats.WithAudio);
        Assert.Equal(0.001m, stats.TotalCost);

        var paths = await _repo.DeleteAllAsync();
        Assert.Equal(["a.wav"], paths);
        Assert.Equal(0, (await _repo.GetStatsAsync()).Count);
    }

    [Fact]
    public async Task Migration_is_idempotent_across_instances()
    {
        await _repo.InsertAsync(Record("x", "X."));
        using var second = new SqliteHistoryRepository(_repo.DatabasePath, NullLogger<SqliteHistoryRepository>.Instance);
        await second.InitialiseAsync();
        Assert.Equal(1, (await second.GetStatsAsync()).Count);
    }

    [Fact]
    public void Fts_query_builder_quotes_tokens()
    {
        Assert.Equal("\"hello\"* \"wor\"\"ld\"*", SqliteHistoryRepository.ToFtsQuery("hello wor\"ld"));
    }
}

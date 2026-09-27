using System.Buffers.Binary;
using DictationApp.Core.Abstractions;
using DictationApp.Core.Cleanup;
using DictationApp.Core.History;
using DictationApp.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace DictationApp.Core.Tests;

/// <summary>Orphan sweep, "Delete all" and the recovery of interrupted recordings, on real files and a real database.</summary>
public sealed class HistoryMaintenanceTests : IDisposable
{
    /// <summary>The app's logger: its file handle is closed while the logs are deleted.</summary>
    private sealed class FakeLogFiles : ILogFileControl
    {
        public IDisposable? OpenFile { get; set; }

        public int Closes { get; private set; }

        public T WhileClosed<T>(Func<T> action)
        {
            Closes++;
            OpenFile?.Dispose();
            OpenFile = null;
            return action();
        }
    }

    private readonly FakeLogFiles _logFiles = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DictationAppTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly AppPaths _paths;
    private readonly SqliteHistoryRepository _repo;
    private readonly JsonSettingsStore _settings;
    private readonly HistoryMaintenance _maintenance;

    public HistoryMaintenanceTests()
    {
        _paths = new AppPaths(_dir);
        _paths.EnsureDirectories();
        _repo = new SqliteHistoryRepository(_paths.HistoryDb, NullLogger<SqliteHistoryRepository>.Instance);
        _settings = new JsonSettingsStore(_paths.SettingsFile, NullLogger<JsonSettingsStore>.Instance);
        _maintenance = new HistoryMaintenance(_repo, _settings, _paths, _clock, NullLogger<HistoryMaintenance>.Instance, _logFiles);
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _repo.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A recording as the app writes it; <paramref name="finished"/> false leaves the sizes at zero.</summary>
    private string Wav(string name, DateTime writtenUtc, int pcmBytes = WavHeader.BytesPerSecond, bool finished = true)
    {
        var path = Path.Combine(_paths.AudioDirectory, name);
        using (var stream = File.Create(path))
        {
            stream.Write(WavHeader.Create(finished ? pcmBytes : 0));
            stream.Write(new byte[pcmBytes]);
        }

        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private Task<long> Record(string? audio, DateTimeOffset at) => _repo.InsertAsync(new DictationRecord
    {
        CreatedAt = at,
        UpdatedAt = at,
        RawTranscript = "words",
        Tone = Tone.Neutral,
        Level = CleanupLevel.Light,
        Status = RecordStatus.Inserted,
        AudioPath = audio,
    });

    private async Task AddNewerRecordsAsync(int count)
    {
        await using var conn = new SqliteConnection($"Data Source={_paths.HistoryDb}");
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        for (var i = 0; i < count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = "INSERT INTO dictations (created_at, updated_at, raw_transcript, tone, level, status) VALUES ($at, $at, 'newer', 'Neutral', 'Light', 'Inserted');";
            cmd.Parameters.AddWithValue("$at", _clock.GetUtcNow().AddMinutes(-i).ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    [Fact]
    public async Task Sweep_keeps_the_audio_of_records_beyond_the_newest_5000_and_removes_old_orphans()
    {
        var referenced = Wav("20260901-120000-aaaaaaaa.wav", Now.AddDays(-20));
        await Record(referenced, _clock.GetUtcNow().AddDays(-20));
        await AddNewerRecordsAsync(5000);
        var oldOrphan = Wav("20260920-120000-bbbbbbbb.wav", Now.AddDays(-2));
        var youngOrphan = Wav("20260926-110000-cccccccc.wav", Now.AddHours(-1));

        var deleted = await _maintenance.SweepOrphansAsync(CancellationToken.None);

        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(youngOrphan));
        Assert.Equal(1, deleted);
        Assert.Equal([Path.GetFullPath(referenced)], (await _repo.GetReferencedAudioPathsAsync()).Select(Path.GetFullPath));
    }

    [Fact]
    public async Task Sweep_is_skipped_when_history_is_empty()
    {
        // A lost or recreated database must not turn every recording into an orphan.
        var recording = Wav("20260920-120000-bbbbbbbb.wav", Now.AddDays(-2));

        Assert.Equal(0, await _maintenance.SweepOrphansAsync(CancellationToken.None));
        Assert.True(File.Exists(recording));
    }

    [Fact]
    public async Task Delete_all_removes_every_recording_and_the_logs_and_keeps_only_what_is_in_use()
    {
        var referenced = Wav("20260926-115900-aaaaaaaa.wav", Now.AddSeconds(-5)); // saved moments ago
        await Record(referenced, _clock.GetUtcNow());
        var orphan = Wav("20260926-100000-bbbbbbbb.wav", Now.AddHours(-2));      // left by a crash
        var recording = Wav("20260926-115958-cccccccc.wav", Now.AddSeconds(-2)); // a dictation in progress
        var locked = Wav("20260926-090000-dddddddd.wav", Now.AddHours(-3));
        var oldLog = Path.Combine(_paths.LogDirectory, "dictation-20260925.log");
        var todayLog = Path.Combine(_paths.LogDirectory, "dictation-20260926.log");
        await File.WriteAllTextAsync(oldLog, "yesterday");
        await File.WriteAllTextAsync(todayLog, "today");

        DeleteAllResult result;
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) // playing, say
        {
            // The logger's handle on today's log, closed while the logs are deleted.
            _logFiles.OpenFile = new FileStream(todayLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            result = await _maintenance.DeleteAllAsync(CancellationToken.None);
        }

        // Only Windows refuses to delete a file another handle has open; on Linux (the CI's Core job) it goes too.
        var lockHolds = OperatingSystem.IsWindows();
        Assert.Empty(await _repo.SearchAsync(null));
        Assert.False(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(recording));
        Assert.Equal(lockHolds, File.Exists(locked));
        Assert.False(File.Exists(oldLog));
        Assert.False(File.Exists(todayLog));
        Assert.Equal(1, _logFiles.Closes);
        Assert.Equal(new DeleteAllResult(RecordingsRemoved: lockHolds ? 2 : 3, RecordingsKept: lockHolds ? 2 : 1, LogsCleared: 2, LogsKept: 0), result);
    }

    [Fact]
    public async Task A_database_error_during_recovery_leaves_the_recording_recoverable()
    {
        // The sizes are written only once History lists the recording: after a failed insert the next pass recovers
        // it, instead of the sweep later taking a repaired but unlisted file for an orphan.
        var runStart = Now.AddMinutes(-1);
        var interrupted = Wav("20260926-100000-aaaaaaaa.wav", runStart.AddHours(-1), finished: false);
        var locked = Substitute.For<IHistoryRepository>();
        locked.GetReferencedAudioPathsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>([]));
        locked.InsertAsync(Arg.Any<DictationRecord>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<long>(new InvalidOperationException("database is locked")));
        var failing = new HistoryMaintenance(locked, _settings, _paths, _clock, NullLogger<HistoryMaintenance>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.RecoverInterruptedAsync(runStart, CancellationToken.None));
        Assert.True(WavHeader.IsUnfinished(interrupted));

        Assert.Equal(1, (await _maintenance.RecoverInterruptedAsync(runStart, CancellationToken.None)).Added);
        Assert.False(WavHeader.IsUnfinished(interrupted));
        Assert.Equal(interrupted, Assert.Single(await _repo.SearchAsync(null)).AudioPath);
    }

    [Fact]
    public async Task A_recording_that_cannot_be_read_is_tried_again_at_the_next_pass()
    {
        // A read error must not look like a finished file: recovery reports itself incomplete, so the next pass (not the
        // next start) checks the recording again.
        var runStart = Now.AddMinutes(-1);
        var interrupted = Wav("20260926-100000-aaaaaaaa.wav", runStart.AddHours(-1), finished: false);
        _maintenance.InspectUnfinished = _ => throw new IOException("too many open files");

        Assert.Equal(new RetentionPass(Recovered: 0, RemovedFiles: 0, RecoveryComplete: false), await _maintenance.RunRetentionPassAsync(runStart, CancellationToken.None));

        _maintenance.InspectUnfinished = path => WavHeader.IsUnfinished(path, out var pcm) ? pcm : null;
        Assert.Equal(new RetentionPass(Recovered: 1, RemovedFiles: 0), await _maintenance.RunRetentionPassAsync(runStart, CancellationToken.None));
        Assert.Equal(interrupted, Assert.Single(await _repo.SearchAsync(null)).AudioPath);
    }

    [Fact]
    public async Task The_orphan_sweep_leaves_unfinished_recordings_for_recovery()
    {
        // A failed save leaves its recording unfinished and unlisted until the next start lists it; the hourly sweep of
        // a long-running app must not delete it first.
        await Record(null, _clock.GetUtcNow());
        var unfinished = Wav("20260924-100000-aaaaaaaa.wav", Now.AddDays(-2), finished: false);
        var orphan = Wav("20260924-110000-bbbbbbbb.wav", Now.AddDays(-2));

        Assert.Equal(1, await _maintenance.SweepOrphansAsync(CancellationToken.None));
        Assert.True(File.Exists(unfinished));
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task A_lost_database_skips_the_sweep_even_after_recovery_lists_a_recording()
    {
        // An empty History (lost or recreated) with an unfinished recording from an earlier run: the pass recovers it,
        // and the other recordings, which no record lists any more, are not swept as orphans.
        var runStart = Now.AddMinutes(-1);
        var interrupted = Wav("20260924-100000-aaaaaaaa.wav", runStart.AddDays(-2), finished: false);
        var unlisted = Wav("20260924-110000-bbbbbbbb.wav", runStart.AddDays(-2));

        var pass = await _maintenance.RunRetentionPassAsync(runStart, CancellationToken.None);

        Assert.Equal(new RetentionPass(Recovered: 1, RemovedFiles: 0), pass);
        Assert.True(File.Exists(interrupted));
        Assert.True(File.Exists(unlisted));
        Assert.Equal(interrupted, Assert.Single(await _repo.SearchAsync(null)).AudioPath);
    }

    [Fact]
    public async Task Unfinished_recordings_from_an_earlier_run_are_repaired_and_listed_for_retry()
    {
        var runStart = Now.AddMinutes(-1);
        var interrupted = Wav("20260926-100000-aaaaaaaa.wav", runStart.AddHours(-1), finished: false);
        var tiny = Wav("20260926-100500-bbbbbbbb.wav", runStart.AddHours(-1), pcmBytes: 3200, finished: false);
        var pastRetention = Wav("20260901-100000-cccccccc.wav", runStart.AddDays(-25), finished: false);
        var thisRun = Wav("20260926-115930-dddddddd.wav", runStart.AddSeconds(30), finished: false);
        var finishedOrphan = Wav("20260926-101000-eeeeeeee.wav", runStart.AddHours(-1));
        var alreadyListed = Wav("20260926-102000-ffffffff.wav", runStart.AddHours(-1), finished: false);
        await Record(alreadyListed, new DateTimeOffset(2026, 9, 26, 10, 20, 0, TimeSpan.Zero));

        var recovery = await _maintenance.RecoverInterruptedAsync(new DateTimeOffset(runStart, TimeSpan.Zero), CancellationToken.None);

        Assert.Equal(new RecoveryResult(Added: 1, Complete: true), recovery);
        var record = Assert.Single(await _repo.SearchAsync(null), r => r.AudioPath == interrupted);
        Assert.Equal(RecordStatus.Failed, record.Status);
        Assert.Equal(HistoryMaintenance.RecoveredReason, record.FailureReason);
        Assert.Equal(1000, record.DurationMs);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero), record.CreatedAt);
        Assert.False(WavHeader.TryRepairUnfinished(interrupted, out _)); // finished now
        Assert.False(WavHeader.TryRepairUnfinished(alreadyListed, out _));
        Assert.False(File.Exists(tiny));
        Assert.False(File.Exists(pastRetention));
        Assert.True(WavHeader.TryRepairUnfinished(thisRun, out _)); // untouched: this run is still writing it
        Assert.True(File.Exists(finishedOrphan));
    }

    [Fact]
    public void An_unfinished_recording_written_by_older_versions_is_repaired_too()
    {
        // NAudio wrote an 18-byte format chunk (46-byte header) with both sizes zero until the file was closed.
        var path = Path.Combine(_paths.AudioDirectory, "20260920-120000-aaaaaaaa.wav");
        var header = new byte[46];
        "RIFF"u8.CopyTo(header);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 18);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 16000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(38));
        File.WriteAllBytes(path, [.. header, .. new byte[6401]]); // an odd byte: cut to whole samples

        Assert.True(WavHeader.TryRepairUnfinished(path, out var pcm));
        Assert.Equal(6400, pcm);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(46 + 6400, bytes.Length);
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(6400u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(42)));
    }
}

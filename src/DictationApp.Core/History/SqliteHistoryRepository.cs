using System.Globalization;
using DictationApp.Core.Cleanup;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DictationApp.Core.History;

/// <summary>
/// SQLite in WAL mode with an FTS5 external-content index kept in sync by triggers. Migrations are
/// versioned SQL scripts applied in order; the schema version lives in <c>schema_version</c>.
/// </summary>
public sealed class SqliteHistoryRepository : IHistoryRepository, IDisposable
{
    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE IF NOT EXISTS dictations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            raw_transcript TEXT NOT NULL,
            cleaned_text TEXT NOT NULL DEFAULT '',
            inserted_text TEXT NOT NULL DEFAULT '',
            tone TEXT NOT NULL,
            level TEXT NOT NULL,
            process_name TEXT,
            window_title TEXT,
            url TEXT,
            duration_ms INTEGER NOT NULL DEFAULT 0,
            audio_path TEXT,
            cost_estimate REAL,
            status TEXT NOT NULL,
            llm_model TEXT,
            failure_reason TEXT,
            ai_edit_undone INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS ix_dictations_created ON dictations(created_at);
        CREATE VIRTUAL TABLE IF NOT EXISTS dictations_fts USING fts5(
            raw_transcript, cleaned_text, inserted_text, window_title, process_name,
            content='dictations', content_rowid='id', tokenize='unicode61'
        );
        CREATE TRIGGER IF NOT EXISTS dictations_ai AFTER INSERT ON dictations BEGIN
            INSERT INTO dictations_fts(rowid, raw_transcript, cleaned_text, inserted_text, window_title, process_name)
            VALUES (new.id, new.raw_transcript, new.cleaned_text, new.inserted_text, new.window_title, new.process_name);
        END;
        CREATE TRIGGER IF NOT EXISTS dictations_ad AFTER DELETE ON dictations BEGIN
            INSERT INTO dictations_fts(dictations_fts, rowid, raw_transcript, cleaned_text, inserted_text, window_title, process_name)
            VALUES ('delete', old.id, old.raw_transcript, old.cleaned_text, old.inserted_text, old.window_title, old.process_name);
        END;
        CREATE TRIGGER IF NOT EXISTS dictations_au AFTER UPDATE ON dictations BEGIN
            INSERT INTO dictations_fts(dictations_fts, rowid, raw_transcript, cleaned_text, inserted_text, window_title, process_name)
            VALUES ('delete', old.id, old.raw_transcript, old.cleaned_text, old.inserted_text, old.window_title, old.process_name);
            INSERT INTO dictations_fts(rowid, raw_transcript, cleaned_text, inserted_text, window_title, process_name)
            VALUES (new.id, new.raw_transcript, new.cleaned_text, new.inserted_text, new.window_title, new.process_name);
        END;
        """,
    ];

    private const string Columns = "id, created_at, updated_at, raw_transcript, cleaned_text, inserted_text, tone, level, process_name, window_title, url, duration_ms, audio_path, cost_estimate, status, llm_model, failure_reason, ai_edit_undone";

    private readonly string _connectionString;
    private readonly ILogger<SqliteHistoryRepository> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialised;

    public SqliteHistoryRepository(string databasePath, ILogger<SqliteHistoryRepository> logger)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        _logger = logger;
    }

    public string DatabasePath { get; }

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_initialised)
        {
            return;
        }

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialised)
            {
                return;
            }

            var dir = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await ExecAsync(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", ct).ConfigureAwait(false);
            await ExecAsync(conn, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);", ct).ConfigureAwait(false);
            var version = Convert.ToInt32(await ScalarAsync(conn, "SELECT COALESCE(MAX(version), 0) FROM schema_version;", ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            for (var i = version; i < Migrations.Length; i++)
            {
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await ExecAsync(conn, Migrations[i], ct, tx).ConfigureAwait(false);
                await ExecAsync(conn, $"INSERT INTO schema_version(version) VALUES ({i + 1});", ct, tx).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("History database migrated to schema v{Version}", i + 1);
            }

            _initialised = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<long> InsertAsync(DictationRecord record, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dictations (created_at, updated_at, raw_transcript, cleaned_text, inserted_text, tone, level, process_name, window_title, url, duration_ms, audio_path, cost_estimate, status, llm_model, failure_reason, ai_edit_undone)
            VALUES ($created, $updated, $raw, $cleaned, $inserted, $tone, $level, $process, $title, $url, $duration, $audio, $cost, $status, $model, $failure, $undone);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, record);
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        record.Id = id;
        return id;
    }

    public async Task UpdateAsync(DictationRecord record, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE dictations SET updated_at=$updated, raw_transcript=$raw, cleaned_text=$cleaned, inserted_text=$inserted, tone=$tone, level=$level,
                process_name=$process, window_title=$title, url=$url, duration_ms=$duration, audio_path=$audio, cost_estimate=$cost, status=$status,
                llm_model=$model, failure_reason=$failure, ai_edit_undone=$undone
            WHERE id=$id;
            """;
        Bind(cmd, record);
        cmd.Parameters.AddWithValue("$id", record.Id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<DictationRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM dictations WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<DictationRecord?> GetLatestAsync(CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM dictations ORDER BY id DESC LIMIT 1;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<DictationRecord>> SearchAsync(string? query, int limit = 200, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
        {
            cmd.CommandText = $"SELECT {Columns} FROM dictations ORDER BY id DESC LIMIT $limit;";
        }
        else
        {
            cmd.CommandText = $"""
                SELECT {Columns} FROM dictations d
                WHERE d.id IN (SELECT rowid FROM dictations_fts WHERE dictations_fts MATCH $q)
                ORDER BY d.id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$q", ToFtsQuery(query));
        }

        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<DictationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(Read(reader));
        }

        return list;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dictations WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DeleteOlderThanAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var paths = await AudioPathsWhereAsync(conn, "created_at < $cutoff AND audio_path IS NOT NULL", olderThan, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM dictations WHERE created_at < $cutoff;";
        cmd.Parameters.AddWithValue("$cutoff", Iso(olderThan));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return paths;
    }

    public async Task<IReadOnlyList<string>> ClearAudioOlderThanAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var paths = await AudioPathsWhereAsync(conn, "created_at < $cutoff AND audio_path IS NOT NULL", olderThan, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE dictations SET audio_path=NULL WHERE created_at < $cutoff AND audio_path IS NOT NULL;";
        cmd.Parameters.AddWithValue("$cutoff", Iso(olderThan));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return paths;
    }

    public async Task<IReadOnlyList<string>> DeleteAllAsync(CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var paths = await AudioPathsWhereAsync(conn, "audio_path IS NOT NULL", null, ct).ConfigureAwait(false);
        await ExecAsync(conn, "DELETE FROM dictations;", ct).ConfigureAwait(false);
        return paths;
    }

    public async Task<HistoryStats> GetStatsAsync(CancellationToken ct = default)
    {
        await InitialiseAsync(ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(cost_estimate), 0), COALESCE(SUM(CASE WHEN audio_path IS NOT NULL THEN 1 ELSE 0 END), 0) FROM dictations;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new HistoryStats(0, 0, 0, 0);
        }

        var count = reader.GetInt32(0);
        var cost = (decimal)reader.GetDouble(1);
        var withAudio = reader.GetInt32(2);
        return new HistoryStats(count, Math.Round(cost, 4), 0, withAudio);
    }

    public void Dispose()
    {
        _initGate.Dispose();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Turns free text into a safe FTS5 query: each word becomes a quoted prefix term.</summary>
    internal static string ToFtsQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", words.Select(w => "\"" + w.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));
    }

    private static async Task<IReadOnlyList<string>> AudioPathsWhereAsync(SqliteConnection conn, string where, DateTimeOffset? cutoff, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT audio_path FROM dictations WHERE {where};";
        if (cutoff is { } c)
        {
            cmd.Parameters.AddWithValue("$cutoff", Iso(c));
        }

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                list.Add(reader.GetString(0));
            }
        }

        return list;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql, CancellationToken ct, System.Data.Common.DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = (SqliteTransaction?)tx;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand cmd, DictationRecord r)
    {
        cmd.Parameters.AddWithValue("$created", Iso(r.CreatedAt));
        cmd.Parameters.AddWithValue("$updated", Iso(r.UpdatedAt));
        cmd.Parameters.AddWithValue("$raw", r.RawTranscript);
        cmd.Parameters.AddWithValue("$cleaned", r.CleanedText);
        cmd.Parameters.AddWithValue("$inserted", r.InsertedText);
        cmd.Parameters.AddWithValue("$tone", r.Tone.ToString());
        cmd.Parameters.AddWithValue("$level", r.Level.ToString());
        cmd.Parameters.AddWithValue("$process", (object?)r.ProcessName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)r.WindowTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)r.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$duration", r.DurationMs);
        cmd.Parameters.AddWithValue("$audio", (object?)r.AudioPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cost", r.CostEstimate is { } c ? (double)c : DBNull.Value);
        cmd.Parameters.AddWithValue("$status", r.Status.ToString());
        cmd.Parameters.AddWithValue("$model", (object?)r.LlmModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$failure", (object?)r.FailureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$undone", r.AiEditUndone ? 1 : 0);
    }

    private static DictationRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
        RawTranscript = reader.GetString(3),
        CleanedText = reader.GetString(4),
        InsertedText = reader.GetString(5),
        Tone = Enum.TryParse<Tone>(reader.GetString(6), out var tone) ? tone : Tone.Neutral,
        Level = Enum.TryParse<CleanupLevel>(reader.GetString(7), out var level) ? level : CleanupLevel.None,
        ProcessName = reader.IsDBNull(8) ? null : reader.GetString(8),
        WindowTitle = reader.IsDBNull(9) ? null : reader.GetString(9),
        Url = reader.IsDBNull(10) ? null : reader.GetString(10),
        DurationMs = reader.GetInt32(11),
        AudioPath = reader.IsDBNull(12) ? null : reader.GetString(12),
        CostEstimate = reader.IsDBNull(13) ? null : (decimal)reader.GetDouble(13),
        Status = Enum.TryParse<RecordStatus>(reader.GetString(14), out var status) ? status : RecordStatus.Pending,
        LlmModel = reader.IsDBNull(15) ? null : reader.GetString(15),
        FailureReason = reader.IsDBNull(16) ? null : reader.GetString(16),
        AiEditUndone = reader.GetInt32(17) != 0,
    };

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

using DictationApp.Core.Cleanup;

namespace DictationApp.Core.History;

public enum RecordStatus
{
    Pending,
    Inserted,
    CopiedOnly,
    Failed,
}

public enum RetentionPolicy
{
    Hours24,
    Days14,
    Forever,
}

public static class RetentionPolicyExtensions
{
    public static TimeSpan? ToTimeSpan(this RetentionPolicy policy) => policy switch
    {
        RetentionPolicy.Hours24 => TimeSpan.FromHours(24),
        RetentionPolicy.Days14 => TimeSpan.FromDays(14),
        _ => null,
    };
}

public sealed class DictationRecord
{
    public long Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string RawTranscript { get; set; } = string.Empty;

    /// <summary>LLM output, or empty when cleanup did not run / was skipped.</summary>
    public string CleanedText { get; set; } = string.Empty;

    /// <summary>What actually went into the target (cleaned text, or raw when cleanup was skipped or undone).</summary>
    public string InsertedText { get; set; } = string.Empty;

    public Tone Tone { get; set; }

    public CleanupLevel Level { get; set; }

    public string? ProcessName { get; set; }

    public string? WindowTitle { get; set; }

    public string? Url { get; set; }

    public int DurationMs { get; set; }

    public string? AudioPath { get; set; }

    public decimal? CostEstimate { get; set; }

    public RecordStatus Status { get; set; }

    public string? LlmModel { get; set; }

    public string? FailureReason { get; set; }

    public bool AiEditUndone { get; set; }

    public bool HasAudio => !string.IsNullOrEmpty(AudioPath);

    public bool CanUndoAiEdit => !AiEditUndone && CleanedText.Length > 0 && !string.Equals(CleanedText, RawTranscript, StringComparison.Ordinal);
}

public sealed record HistoryStats(int Count, decimal TotalCost, long TotalAudioBytes, int WithAudio);

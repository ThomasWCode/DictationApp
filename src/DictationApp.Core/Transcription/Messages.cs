using System.Text.Json;
using System.Text.Json.Serialization;

namespace DictationApp.Core.Transcription;

/// <summary>Server → client messages of the AssemblyAI streaming v3 protocol.</summary>
public abstract class StreamingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public sealed class BeginMessage : StreamingMessage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; init; }
}

public sealed class WordInfo
{
    [JsonPropertyName("start")]
    public double Start { get; init; }

    [JsonPropertyName("end")]
    public double End { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("word_is_final")]
    public bool WordIsFinal { get; init; }
}

public sealed class TurnMessage : StreamingMessage
{
    [JsonPropertyName("turn_order")]
    public int TurnOrder { get; init; }

    [JsonPropertyName("turn_is_formatted")]
    public bool TurnIsFormatted { get; init; }

    [JsonPropertyName("end_of_turn")]
    public bool EndOfTurn { get; init; }

    /// <summary>Finalised words of the turn so far.</summary>
    [JsonPropertyName("transcript")]
    public string? Transcript { get; init; }

    /// <summary>Full text of the turn including unfinalised words (Universal-3.5 Pro).</summary>
    [JsonPropertyName("utterance")]
    public string? Utterance { get; init; }

    [JsonPropertyName("end_of_turn_confidence")]
    public double? EndOfTurnConfidence { get; init; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; init; }

    [JsonPropertyName("words")]
    public List<WordInfo>? Words { get; init; }

    /// <summary>Best available text: utterance, else transcript, else the joined word list.</summary>
    [JsonIgnore]
    public string BestText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Utterance))
            {
                return Utterance.Trim();
            }

            if (!string.IsNullOrWhiteSpace(Transcript))
            {
                return Transcript.Trim();
            }

            return Words is { Count: > 0 } ? string.Join(' ', Words.Select(w => w.Text)).Trim() : string.Empty;
        }
    }
}

public sealed class TerminationMessage : StreamingMessage
{
    [JsonPropertyName("audio_duration_seconds")]
    public double AudioDurationSeconds { get; init; }

    [JsonPropertyName("session_duration_seconds")]
    public double SessionDurationSeconds { get; init; }
}

public sealed class ErrorMessage : StreamingMessage
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public static class StreamingMessageParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = false };

    /// <summary>Returns a typed message, or null for message types we do not model (e.g. SpeechStarted).</summary>
    public static StreamingMessage? Parse(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String ? typeProp.GetString() : null;

        // Errors arrive both untyped ({"error": ...}) and typed ({"type":"Error","error": ...}); either must fail
        // the session at once rather than leave it waiting for a timeout.
        if (root.TryGetProperty("error", out var errorProp) || string.Equals(type, "Error", StringComparison.OrdinalIgnoreCase))
        {
            var detail = errorProp.ValueKind != JsonValueKind.Undefined ? errorProp : root.TryGetProperty("message", out var messageProp) ? messageProp : default;
            var text = detail.ValueKind switch
            {
                JsonValueKind.String => detail.GetString(),
                JsonValueKind.Undefined or JsonValueKind.Null => null,
                _ => detail.GetRawText(),
            };
            return new ErrorMessage { Type = "Error", Error = text ?? "unknown error" };
        }

        return type switch
        {
            "Begin" => root.Deserialize<BeginMessage>(Options),
            "Turn" => root.Deserialize<TurnMessage>(Options),
            "Termination" => root.Deserialize<TerminationMessage>(Options),
            _ => null,
        };
    }
}

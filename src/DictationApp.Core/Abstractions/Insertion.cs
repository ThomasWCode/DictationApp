namespace DictationApp.Core.Abstractions;

/// <summary>Snapshot of where the user was when the chord went down.</summary>
public sealed record ForegroundContext(
    nint WindowHandle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string? Url,
    bool IsEditable,
    bool IsElevated,
    string EditableReason)
{
    public static ForegroundContext Unknown { get; } = new(0, 0, string.Empty, string.Empty, null, true, false, "unknown");

    /// <summary>Host part of <see cref="Url"/> in lower case, or null.</summary>
    public string? UrlHost
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                return null;
            }

            var candidate = Url.Contains("://", StringComparison.Ordinal) ? Url : "https://" + Url;
            return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
        }
    }
}

public interface IForegroundContextProvider
{
    ForegroundContext Capture();
}

public enum PasteMode
{
    CtrlV,
    CtrlShiftV,
}

public enum InsertionOutcome
{
    Inserted,
    CopiedOnly,
    Failed,
}

public sealed record InsertionResult(InsertionOutcome Outcome, string? Reason = null);

public interface ITextInserter
{
    Task<InsertionResult> InsertAsync(string text, ForegroundContext target, PasteMode pasteMode, CancellationToken ct);
}

public interface IClipboard
{
    Task SetTextAsync(string text, CancellationToken ct = default);

    Task<string?> GetTextAsync(CancellationToken ct = default);
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public interface INotifier
{
    /// <param name="actionUri">Optional URI opened when the toast is clicked, e.g. <c>ms-settings:privacy-microphone</c>.</param>
    void Toast(string title, string message, ToastKind kind = ToastKind.Info, string? actionUri = null);
}

/// <summary>Reversible protection for secrets at rest (DPAPI on Windows).</summary>
public interface ISecretStore
{
    string Protect(string plaintext);

    /// <summary>Returns null when the value cannot be unprotected (different user, corrupt).</summary>
    string? Unprotect(string protectedValue);
}

public interface IApiKeyProvider
{
    /// <summary>AssemblyAI key for streaming transcription.</summary>
    string? GetApiKey();

    /// <summary>Groq (OpenAI-compatible) key for cleanup and tone. Null when cleanup is unavailable.</summary>
    string? GetLlmApiKey();
}

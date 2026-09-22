namespace DictationApp.Core.Cleanup;

public enum CleanupLevel
{
    /// <summary>Spoken-command normalisation only; no LLM call unless the tone is not Neutral.</summary>
    None,

    /// <summary>Fillers, false starts and self-corrections removed; punctuation fixed. No rephrasing.</summary>
    Light,

    /// <summary>Light plus grammar fixes, redundancy removal and run-on splitting.</summary>
    Medium,

    /// <summary>Medium plus tightened wording, merged fragments and paragraphing.</summary>
    High,
}

public enum Tone
{
    Neutral,
    Formal,
    Casual,
}

public static class CleanupEnumExtensions
{
    public static CleanupLevel Next(this CleanupLevel level) => (CleanupLevel)(((int)level + 1) % 4);

    public static CleanupLevel Previous(this CleanupLevel level) => (CleanupLevel)(((int)level + 3) % 4);

    public static Tone Next(this Tone tone) => (Tone)(((int)tone + 1) % 3);

    public static Tone Previous(this Tone tone) => (Tone)(((int)tone + 2) % 3);
}

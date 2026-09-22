namespace DictationApp.Core.Dictionary;

/// <summary>A personal-dictionary entry sent to AssemblyAI as a keyterm and to the LLM as a spelling to preserve.</summary>
public sealed class DictionaryTerm
{
    public const int MaxLength = 50;

    public string Term { get; set; } = string.Empty;

    public bool Starred { get; set; }

    public int UseCount { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }

    public DictionaryTerm Clone() => (DictionaryTerm)MemberwiseClone();
}

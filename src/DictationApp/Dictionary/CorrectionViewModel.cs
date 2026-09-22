using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DictationApp.Core.Dictionary;
using DictationApp.Core.History;
using DictationApp.Core.Settings;

namespace DictationApp.Dictionary;

public sealed partial class CandidateTerm : ObservableObject
{
    [ObservableProperty] private bool _selected = true;

    public required string From { get; init; }

    public required string To { get; init; }
}

/// <summary>
/// "Correct last dictation": the user fixes the inserted text, we diff it word by word and offer the
/// changed words as dictionary terms so they transcribe correctly next time.
/// </summary>
public sealed partial class CorrectionViewModel : ObservableObject
{
    private readonly IHistoryRepository _history;
    private readonly ISettingsStore _settings;
    private DictationRecord? _record;

    [ObservableProperty] private string _originalText = string.Empty;
    [ObservableProperty] private string _editedText = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _hasRecord;

    public CorrectionViewModel(IHistoryRepository history, ISettingsStore settings)
    {
        _history = history;
        _settings = settings;
    }

    public event Action? RequestClose;

    public ObservableCollection<CandidateTerm> Candidates { get; } = [];

    public async Task LoadAsync()
    {
        _record = await _history.GetLatestAsync();
        HasRecord = _record is not null;
        if (_record is null)
        {
            Status = "No dictation in history yet.";
            return;
        }

        OriginalText = _record.InsertedText.Length > 0 ? _record.InsertedText : _record.RawTranscript;
        EditedText = OriginalText;
        Status = $"Last dictation from {_record.CreatedAt.ToLocalTime():HH:mm} in {_record.ProcessName}. Fix any misheard words below.";
    }

    [RelayCommand]
    private void FindCorrections()
    {
        Candidates.Clear();
        foreach (var c in CorrectionDiffer.Diff(OriginalText, EditedText))
        {
            var to = CorrectionDiffer.SuggestTerms([c]).FirstOrDefault();
            if (to is null)
            {
                continue;
            }

            Candidates.Add(new CandidateTerm { From = c.From, To = to });
        }

        Status = Candidates.Count == 0 ? "No word changes found." : $"{Candidates.Count} candidate term{(Candidates.Count == 1 ? string.Empty : "s")}. Untick any you do not want.";
    }

    [RelayCommand]
    private async Task AddSelectedAsync()
    {
        var terms = Candidates.Where(c => c.Selected).Select(c => c.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (terms.Count == 0)
        {
            Status = "Nothing selected.";
            return;
        }

        await _settings.UpdateAsync(s =>
        {
            foreach (var term in terms)
            {
                if (!s.Dictionary.Any(t => string.Equals(t.Term, term, StringComparison.OrdinalIgnoreCase)))
                {
                    s.Dictionary.Add(new DictionaryTerm { Term = term, AddedAt = DateTimeOffset.UtcNow, Starred = true });
                }
            }
        });

        if (_record is not null && !string.Equals(EditedText, OriginalText, StringComparison.Ordinal))
        {
            _record.InsertedText = EditedText;
            _record.UpdatedAt = DateTimeOffset.UtcNow;
            await _history.UpdateAsync(_record);
        }

        Status = $"Added {terms.Count} term{(terms.Count == 1 ? string.Empty : "s")} to the dictionary.";
        RequestClose?.Invoke();
    }
}

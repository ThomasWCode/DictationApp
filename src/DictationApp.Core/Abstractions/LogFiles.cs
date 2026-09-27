namespace DictationApp.Core.Abstractions;

/// <summary>
/// Closes the log file the logger is writing for the duration of an action, so "Delete all history" can delete every
/// log file. Emptying the open file instead broke the logger's appends for the rest of the day.
/// </summary>
public interface ILogFileControl
{
    T WhileClosed<T>(Func<T> action);
}

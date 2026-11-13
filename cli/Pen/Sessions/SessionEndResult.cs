namespace Pen.Sessions;

/// <summary>How ending a session went.</summary>
/// <param name="CheckFinished">Whether its .git check finished.</param>
/// <param name="ReminderDir">
/// The dir to give a signing reminder for, or null if the check failed, moved something or put a file back.
/// </param>
internal readonly record struct SessionEndResult(bool CheckFinished, string? ReminderDir);

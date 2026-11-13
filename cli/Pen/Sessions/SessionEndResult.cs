namespace Pen.Sessions;

/// <summary>
/// How ending a session went: whether its .git check finished, and the dir to
/// give a signing reminder for (null if the check failed or moved something).
/// </summary>
internal readonly record struct SessionEndResult(bool CheckFinished, string? ReminderDir);

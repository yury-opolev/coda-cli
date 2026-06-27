namespace Coda.Agent;

/// <summary>
/// One todo. <see cref="Content"/> is the imperative form ("Fix the bug");
/// <see cref="ActiveForm"/> is the present-continuous form shown while in progress
/// ("Fixing the bug").
/// </summary>
public sealed record TodoItem(string Content, string ActiveForm, TodoStatus Status);

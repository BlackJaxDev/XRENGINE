namespace XREngine.Editor.Tools;

public readonly record struct DependencyTaskLogLine(string Text, bool IsError, bool IsCommand);

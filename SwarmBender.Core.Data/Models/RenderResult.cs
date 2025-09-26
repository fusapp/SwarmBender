namespace SwarmBender.Core.Data.Models;

public sealed class RenderResult
{
    public required string OutFile { get; init; }
    public string? HistoryFile { get; init; }
    public string? LogFile { get; init; }

    public override string ToString() => OutFile;
}
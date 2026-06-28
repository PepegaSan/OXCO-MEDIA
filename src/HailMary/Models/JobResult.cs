namespace HailMary.Models;

public sealed class JobResult
{
    public bool Success { get; init; }

    public int ExitCode { get; init; }

    public string? OutputPath { get; init; }

    public string Message { get; init; } = string.Empty;
}

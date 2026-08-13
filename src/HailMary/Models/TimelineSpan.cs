namespace HailMary.Models;

public enum TimelineSpanKind
{
    Scene,
    Pending,
    Loop,
}

public sealed class TimelineSpan
{
    public TimelineSpan(double startSeconds, double endSeconds, TimelineSpanKind kind, string? label = null)
    {
        StartSeconds = startSeconds;
        EndSeconds = endSeconds;
        Kind = kind;
        Label = label;
    }

    public double StartSeconds { get; }

    public double EndSeconds { get; }

    public TimelineSpanKind Kind { get; }

    public string? Label { get; }
}

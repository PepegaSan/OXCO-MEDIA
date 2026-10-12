namespace HailMary.Models;

public sealed class MarkerQuickPreset
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string PrimaryTag { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>1–9 for Shift+digit instant create; 0 = none.</summary>
    public int InstantSlot { get; set; }

    /// <summary>Shortcut badge for the preset chip ("⇧3"), empty without slot.</summary>
    public string SlotText => InstantSlot is >= 1 and <= 9 ? $"⇧{InstantSlot}" : string.Empty;

    public string Display => InstantSlot is >= 1 and <= 9
        ? $"{Label}  (Shift+{InstantSlot})"
        : Label;
}

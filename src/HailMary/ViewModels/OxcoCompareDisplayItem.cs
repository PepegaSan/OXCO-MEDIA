using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Models;
using Microsoft.UI.Xaml;

namespace HailMary.ViewModels;

public partial class OxcoCompareDisplayItem : ObservableObject
{
    public bool IsGroupHeader { get; init; }

    public string GroupLabel { get; init; } = string.Empty;

    public OxcoCompareFileEntry? Entry { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Rel { get; init; } = string.Empty;

    public string Duration { get; init; } = string.Empty;

    public string Resolution { get; init; } = string.Empty;

    public string Size { get; init; } = string.Empty;

    public string SortTime { get; init; } = string.Empty;

    public string? BackgroundColor { get; init; }

    public string ForegroundColor { get; init; } = "#E8E8E8";

    public bool IsMatchHighlight { get; init; }

    public bool IsBestMatch { get; init; }

    public bool IsWorkflowActive { get; init; }

    public bool ShowBestBadge { get; init; }

    public string? BaseRowBorderColor { get; init; }

    public double BaseRowBorderThickness { get; init; } = 2;

    [ObservableProperty] private string? _rowBorderColor;

    [ObservableProperty] private double _rowBorderThickness = 2;

    public Thickness UniformRowBorderThickness => new(RowBorderThickness);

    partial void OnRowBorderThicknessChanged(double value) =>
        OnPropertyChanged(nameof(UniformRowBorderThickness));

    public void ApplyListSelection(bool selected)
    {
        if (selected)
        {
            RowBorderColor = "#0078D4";
            RowBorderThickness = 4;
            return;
        }

        RowBorderColor = BaseRowBorderColor;
        RowBorderThickness = BaseRowBorderThickness;
    }
}

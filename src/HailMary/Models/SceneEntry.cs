using CommunityToolkit.Mvvm.ComponentModel;
using HailMary.Services;

namespace HailMary.Models;

public partial class SceneEntry : ObservableObject
{
    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _positionInput = string.Empty;

    [ObservableProperty]
    private string _startInput = "0";

    [ObservableProperty]
    private string _endInput = "0";

    public double StartSeconds { get; private set; }

    public double EndSeconds { get; private set; }

    public string Display =>
        $"{TimecodeHelper.FormatDisplay(StartSeconds)} → {TimecodeHelper.FormatDisplay(EndSeconds)}";

    public static SceneEntry FromSeconds(double start, double end, int number)
    {
        var entry = new SceneEntry
        {
            Number = number,
            StartSeconds = start,
            EndSeconds = end,
            StartInput = TimecodeHelper.FormatForEditor(start),
            EndInput = TimecodeHelper.FormatForEditor(end),
        };
        return entry;
    }

    public bool TryApplyTimes(out string error)
    {
        if (!TimecodeHelper.TryParseFlexible(StartInput, out var start, out error))
        {
            return false;
        }

        if (!TimecodeHelper.TryParseFlexible(EndInput, out var end, out error))
        {
            return false;
        }

        if (end <= start)
        {
            error = "Ende muss nach Start liegen";
            return false;
        }

        StartSeconds = start;
        EndSeconds = end;
        StartInput = TimecodeHelper.FormatForEditor(start);
        EndInput = TimecodeHelper.FormatForEditor(end);
        OnPropertyChanged(nameof(Display));
        return true;
    }

    partial void OnNumberChanged(int value) => OnPropertyChanged(nameof(Display));

    partial void OnStartInputChanged(string value) => OnPropertyChanged(nameof(Display));

    partial void OnEndInputChanged(string value) => OnPropertyChanged(nameof(Display));
}

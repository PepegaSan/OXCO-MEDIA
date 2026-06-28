using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HailMary.Services;

public sealed class JobProgressService : INotifyPropertyChanged
{
    private bool _isVisible;
    private double _value;
    private bool _isIndeterminate;
    private string _label = string.Empty;
    private bool _batchActive;
    private int _batchTotal = 1;
    private int _batchItemIndex;
    private string _batchLabel = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBatchActive => _batchActive;

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    public double Value
    {
        get => _value;
        private set => SetField(ref _value, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetField(ref _isIndeterminate, value);
    }

    public string Label
    {
        get => _label;
        private set => SetField(ref _label, value);
    }

    public string DisplayText =>
        string.IsNullOrWhiteSpace(Label)
            ? $"{Value:0}%"
            : $"{Label} {Value:0}%";

    public void BeginBatch(int total, string label)
    {
        _batchActive = true;
        _batchTotal = Math.Max(1, total);
        _batchItemIndex = 0;
        _batchLabel = label.Trim();
        IsVisible = true;
        ApplyBatchSlice(0, indeterminate: true);
    }

    public void SetBatchItem(int index)
    {
        if (!_batchActive)
        {
            return;
        }

        _batchItemIndex = Math.Clamp(index, 0, _batchTotal - 1);
        ApplyBatchSlice(0, indeterminate: true);
    }

    public void EndBatch()
    {
        _batchActive = false;
        _batchTotal = 1;
        _batchItemIndex = 0;
        _batchLabel = string.Empty;
        EndJob();
    }

    public void BeginJob()
    {
        if (_batchActive)
        {
            BeginSubJob();
            return;
        }

        IsVisible = true;
        Value = 0;
        IsIndeterminate = true;
        Label = string.Empty;
    }

    public void EndJob()
    {
        if (_batchActive)
        {
            EndSubJob();
            return;
        }

        IsVisible = false;
        Value = 0;
        IsIndeterminate = false;
        Label = string.Empty;
    }

    public void BeginSubJob()
    {
        if (!_batchActive)
        {
            IsVisible = true;
            Value = 0;
            IsIndeterminate = true;
            Label = string.Empty;
            return;
        }

        ApplyBatchSlice(0, indeterminate: true);
    }

    public void EndSubJob()
    {
        if (!_batchActive)
        {
            IsVisible = false;
            Value = 0;
            IsIndeterminate = false;
            Label = string.Empty;
            return;
        }

        ApplyBatchSlice(100, indeterminate: false);
    }

    public void TryApplyFromLogLine(string line)
    {
        if (!JobLogProgressParser.TryParse(line, out var parsed))
        {
            return;
        }

        if (parsed.Kind == JobProgressParseKind.End)
        {
            if (_batchActive)
            {
                ApplyBatchSlice(100, indeterminate: false);
                return;
            }

            IsVisible = false;
            IsIndeterminate = false;
            return;
        }

        if (_batchActive)
        {
            ApplyBatchSlice(parsed.Percent, indeterminate: false, subLabel: parsed.Label);
            return;
        }

        IsVisible = true;
        IsIndeterminate = false;
        Value = parsed.Percent;
        Label = parsed.Label;
    }

    private void ApplyBatchSlice(double subPercent, bool indeterminate, string? subLabel = null)
    {
        var clamped = Math.Clamp(subPercent, 0, 100);
        var overall = ((_batchItemIndex + clamped / 100.0) / _batchTotal) * 100.0;
        Value = Math.Clamp(overall, 0, 100);
        IsVisible = true;
        IsIndeterminate = indeterminate && clamped <= 0;

        var batchPart = $"{_batchLabel} {_batchItemIndex + 1}/{_batchTotal}".Trim();
        Label = string.IsNullOrWhiteSpace(subLabel)
            ? batchPart
            : $"{batchPart} · {subLabel}";
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        NotifyPropertyChanged(propertyName);
        if (propertyName is nameof(Value) or nameof(Label))
        {
            NotifyPropertyChanged(nameof(DisplayText));
        }
    }

    private void NotifyPropertyChanged(string? propertyName)
    {
        UiDispatcher.Run(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }
}

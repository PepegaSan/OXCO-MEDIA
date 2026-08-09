using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _tool;
    private int _selectedSegmentIndex = -1;
    private int _videoWidth;
    private int _videoHeight;
    private double _probeFps;
    private double _probeDurationSec;
    private string? _suggestedBitrate;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _exportCts;
    private TextOverlaySegment? _editorSegmentSubscribed;
    private bool _isOverlayDragging;
    private bool _syncingSelectedFont;

    public event Action<int>? RequestSegmentListSelection;

    public IReadOnlyList<WindowsFontChoice> AvailableFonts { get; } = WindowsFontCatalog.GetFonts();

    [ObservableProperty] private WindowsFontChoice? _selectedSystemFont;

    public TextToVideoViewModel(ToolDefinition tool)
    {
        _tool = tool;
        var settings = TextToVideoConfigReader.Load();
        VideoPath = settings.LastVideoPath;
        Codec = settings.Codec;
        Bitrate = settings.Bitrate;
        ExportContainer = settings.ExportContainer;
        GifFps = settings.GifFps;
        TargetFps = settings.TargetFps;
        GifMaxWidth = settings.GifMaxWidth;
        GifPaletteColors = settings.GifPaletteColors;
        AudioCopy = settings.AudioCopy;
        SrtPath = settings.SrtPath;
        DavinciPreset = settings.DavinciPreset;
        DavinciOutputDir = settings.DavinciOutputDir;
        UseLastFfmpegForResolve = settings.UseLastFfmpegForResolve;
        BatchOutputDir = string.IsNullOrWhiteSpace(settings.LastVideoDir) ? "" : settings.LastVideoDir;
        TextToVideoConfigReader.ApplyToObservable(settings, Segments);
        BatchRows.CollectionChanged += BatchRows_OnCollectionChanged;

        if (Segments.Count > 0)
        {
            _selectedSegmentIndex = 0;
            EditorSegment = Segments[0].Clone();
            RequestSegmentListSelection?.Invoke(0);
        }
        else
        {
            EditorSegment = new TextOverlaySegment();
        }

        SyncSelectedFontFromEditor();
        UpdateSegmentEditorChrome();
    }

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public bool CanInteract => !IsBusy;

    public bool IsEditingExistingSegment =>
        _selectedSegmentIndex >= 0 && _selectedSegmentIndex < Segments.Count;

    public string SegmentEditorTitle =>
        IsEditingExistingSegment
            ? Loc.F("texttovideo.editSegment", _selectedSegmentIndex + 1)
            : Loc.T("texttovideo.newSegmentTitle");

    public string SegmentModeHint =>
        IsEditingExistingSegment
            ? Loc.F("texttovideo.modeEditingHint", _selectedSegmentIndex + 1)
            : Loc.T("texttovideo.modeNewHint");

    public string CommitSegmentLabel =>
        IsEditingExistingSegment ? Loc.T("texttovideo.commitEdit") : Loc.T("texttovideo.commitNew");

    public string EditorFromTimeHint => TimeFieldHelper.FormatDetailedHint(EditorSegment.From);

    public string EditorToTimeHint => TimeFieldHelper.FormatDetailedHint(EditorSegment.To);

    partial void OnEditorSegmentChanged(TextOverlaySegment value)
    {
        if (_editorSegmentSubscribed is not null)
        {
            _editorSegmentSubscribed.PropertyChanged -= EditorSegment_PropertyChanged;
        }

        _editorSegmentSubscribed = value;
        value.PropertyChanged += EditorSegment_PropertyChanged;
        UpdateTimeHints();
        UpdateSegmentEditorChrome();
        SyncSelectedFontFromEditor();
    }

    public void ApplySystemFont(WindowsFontChoice choice)
    {
        _syncingSelectedFont = true;
        try
        {
            SelectedSystemFont = choice;
        }
        finally
        {
            _syncingSelectedFont = false;
        }

        var path = choice.Path ?? string.Empty;
        if (!string.Equals(EditorSegment.FontPath, path, StringComparison.OrdinalIgnoreCase))
        {
            EditorSegment.FontPath = path;
        }
        else
        {
            SchedulePreviewRefresh();
        }
    }

    partial void OnSelectedSystemFontChanged(WindowsFontChoice? value)
    {
        if (_syncingSelectedFont || value is null)
        {
            return;
        }

        if (!string.Equals(EditorSegment.FontPath, value.Path, StringComparison.OrdinalIgnoreCase))
        {
            EditorSegment.FontPath = value.Path;
        }
    }

    private void SyncSelectedFontFromEditor()
    {
        _syncingSelectedFont = true;
        try
        {
            var match = WindowsFontCatalog.FindByPath(EditorSegment.FontPath);
            if (match is not null)
            {
                SelectedSystemFont = match;
            }
            else if (!string.IsNullOrWhiteSpace(EditorSegment.FontPath))
            {
                SelectedSystemFont = new WindowsFontChoice(
                    System.IO.Path.GetFileName(EditorSegment.FontPath),
                    EditorSegment.FontPath);
            }
            else
            {
                SelectedSystemFont = AvailableFonts[0];
            }
        }
        finally
        {
            _syncingSelectedFont = false;
        }
    }

    private void EditorSegment_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextOverlaySegment.FontPath))
        {
            SyncSelectedFontFromEditor();
        }

        if (e.PropertyName is nameof(TextOverlaySegment.Text)
            or nameof(TextOverlaySegment.From)
            or nameof(TextOverlaySegment.To)
            or nameof(TextOverlaySegment.Fontsize)
            or nameof(TextOverlaySegment.Color)
            or nameof(TextOverlaySegment.PosX)
            or nameof(TextOverlaySegment.PosY)
            or nameof(TextOverlaySegment.LineSpacing)
            or nameof(TextOverlaySegment.BoxBorder)
            or nameof(TextOverlaySegment.BoxEnabled)
            or nameof(TextOverlaySegment.Bold)
            or nameof(TextOverlaySegment.Italic)
            or nameof(TextOverlaySegment.Strike)
            or nameof(TextOverlaySegment.FontPath)
            or nameof(TextOverlaySegment.ItalicFontPath))
        {
            if (e.PropertyName is nameof(TextOverlaySegment.From) or nameof(TextOverlaySegment.To))
            {
                UpdateTimeHints();
                EnsurePlayheadShowsEditorSegment();
            }
            else if (e.PropertyName is nameof(TextOverlaySegment.PosX) or nameof(TextOverlaySegment.PosY))
            {
                if (_isOverlayDragging)
                {
                    return;
                }
            }
            else if (e.PropertyName is nameof(TextOverlaySegment.Text)
                     or nameof(TextOverlaySegment.Fontsize)
                     or nameof(TextOverlaySegment.Color)
                     or nameof(TextOverlaySegment.Bold)
                     or nameof(TextOverlaySegment.Italic)
                     or nameof(TextOverlaySegment.Strike)
                     or nameof(TextOverlaySegment.BoxEnabled)
                     or nameof(TextOverlaySegment.BoxBorder)
                     or nameof(TextOverlaySegment.LineSpacing)
                     or nameof(TextOverlaySegment.FontPath)
                     or nameof(TextOverlaySegment.ItalicFontPath))
            {
                // Timed overlays are invisible outside From/To — jump into range so edits show up.
                EnsurePlayheadShowsEditorSegment();
            }

            SchedulePreviewRefresh();
        }
    }

    /// <summary>
    /// If the editor segment is time-bounded and the playhead is outside that window,
    /// move to the start of the window so preview/export-looking edits are visible.
    /// </summary>
    private void EnsurePlayheadShowsEditorSegment()
    {
        if (!HasVideo || _probeDurationSec <= 0)
        {
            return;
        }

        var hasFrom = TimeFieldHelper.TryParseSeconds(EditorSegment.From, out var fromSec);
        var hasTo = TimeFieldHelper.TryParseSeconds(EditorSegment.To, out var toSec);
        if (!hasFrom && !hasTo)
        {
            return;
        }

        var t = SliderValue;
        var visible = (!hasFrom || t >= fromSec) && (!hasTo || t <= toSec);
        if (visible)
        {
            return;
        }

        double target;
        if (hasFrom && hasTo && toSec > fromSec)
        {
            target = fromSec + Math.Min(0.35, (toSec - fromSec) * 0.1);
        }
        else if (hasFrom)
        {
            target = fromSec;
        }
        else
        {
            target = Math.Max(0, toSec - 0.35);
        }

        SetPreviewPosition(Math.Clamp(target, 0, SliderMaximum));
    }

    private void UpdateTimeHints()
    {
        OnPropertyChanged(nameof(EditorFromTimeHint));
        OnPropertyChanged(nameof(EditorToTimeHint));
    }

    private void UpdateSegmentEditorChrome()
    {
        OnPropertyChanged(nameof(IsEditingExistingSegment));
        OnPropertyChanged(nameof(SegmentEditorTitle));
        OnPropertyChanged(nameof(SegmentModeHint));
        OnPropertyChanged(nameof(CommitSegmentLabel));
    }

    public string Description => ToolText.Description(_tool);

    public ObservableCollection<TextOverlaySegment> Segments { get; } = [];

    public static IReadOnlyList<string> ContainerOptions { get; } = ["mp4", "gif"];

    public static IReadOnlyList<string> CodecOptions { get; } =
    [
        "H.264 (libx264)",
        "H.265 / HEVC (libx265)",
        "VP9 (libvpx-vp9)",
        "AV1 (libsvtav1)",
    ];

    public static IReadOnlyList<string> ColorPalette { get; } =
    [
        "FFFFFF", "000000", "FF0000", "00CC00", "0066FF", "FFFF00", "FF00CC", "00DDDD", "FF8800", "888888",
    ];

    [ObservableProperty] private TextOverlaySegment _editorSegment;

    [ObservableProperty] private string _videoPath = string.Empty;

    [ObservableProperty] private string _codec = "H.264 (libx264)";

    [ObservableProperty] private string _bitrate = "5000k";

    [ObservableProperty] private string _exportContainer = "mp4";

    [ObservableProperty] private int _gifFps = 15;

    [ObservableProperty] private double _targetFps;

    [ObservableProperty] private int _gifMaxWidth = 720;

    [ObservableProperty] private int _gifPaletteColors = 128;

    [ObservableProperty] private bool _audioCopy = true;

    [ObservableProperty] private string _srtPath = string.Empty;

    [ObservableProperty] private string _davinciPreset = "YouTube - 1080p";

    [ObservableProperty] private string _davinciOutputDir = string.Empty;

    [ObservableProperty] private string _status = Loc.T("common.ready");

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _hasVideo;

    [ObservableProperty] private double _sliderValue;

    [ObservableProperty] private double _sliderMaximum = 1;

    [ObservableProperty] private string _timeDisplay = "0:00 / 0:00";

    [ObservableProperty] private string _videoInfoLine = string.Empty;

    [ObservableProperty] private byte[]? _previewImageBytes;

    [ObservableProperty] private bool _isPreviewPlaying;

    public bool IsGifExport => string.Equals(ExportContainer, "gif", StringComparison.OrdinalIgnoreCase);

    public bool IsMp4Export => !IsGifExport;

    public IReadOnlyList<string> ContainerChoices => ContainerOptions;

    public IReadOnlyList<string> CodecChoices => CodecOptions;

    public int? SelectedSegmentIndex => _selectedSegmentIndex >= 0 ? _selectedSegmentIndex : null;

    partial void OnExportContainerChanged(string value)
    {
        OnPropertyChanged(nameof(IsGifExport));
        OnPropertyChanged(nameof(IsMp4Export));
        PersistSettings();
    }

    partial void OnVideoPathChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
        {
            _ = LoadVideoAsync();
        }
        else
        {
            HasVideo = false;
            PreviewImageBytes = null;
        }
    }

    partial void OnSrtPathChanged(string value)
    {
        PersistSettings();
        SchedulePreviewRefresh();
    }

    partial void OnCodecChanged(string value) => PersistSettings();

    partial void OnBitrateChanged(string value) => PersistSettings();

    partial void OnGifFpsChanged(int value) => PersistSettings();
    partial void OnTargetFpsChanged(double value) => PersistSettings();

    partial void OnGifMaxWidthChanged(int value) => PersistSettings();

    partial void OnGifPaletteColorsChanged(int value) => PersistSettings();

    partial void OnAudioCopyChanged(bool value) => PersistSettings();

    partial void OnSliderValueChanged(double value)
    {
        UpdateTimeDisplay();
        SchedulePreviewRefresh();
    }

    public void SetSelectedSegmentIndex(int index)
    {
        if (index < 0)
        {
            // NewSegment already set index to -1 before clearing the list selection.
            if (_selectedSegmentIndex < 0)
            {
                return;
            }

            // Ignore accidental ListView deselection while editing a saved segment
            // (collection churn often fires SelectedIndex = -1 briefly).
            if (_selectedSegmentIndex < Segments.Count)
            {
                RequestSegmentListSelection?.Invoke(_selectedSegmentIndex);
                return;
            }

            _selectedSegmentIndex = -1;
            OnPropertyChanged(nameof(SelectedSegmentIndex));
            UpdateSegmentEditorChrome();
            return;
        }

        if (index >= Segments.Count)
        {
            return;
        }

        // Same item already loaded into the editor — keep the in-progress draft.
        if (index == _selectedSegmentIndex)
        {
            return;
        }

        _selectedSegmentIndex = index;
        OnPropertyChanged(nameof(SelectedSegmentIndex));
        EditorSegment = Segments[index].Clone();
        UpdateSegmentEditorChrome();
        EnsurePlayheadShowsEditorSegment();
        SchedulePreviewRefresh();
        Status = Loc.F("texttovideo.status.editingSegment", index + 1);
    }

    private static void NormalizeSegmentTimes(TextOverlaySegment segment)
    {
        if (!string.IsNullOrWhiteSpace(segment.From)
            && TimeFieldHelper.TryParseSeconds(segment.From, out var fromSec))
        {
            segment.From = TimeFieldHelper.FormatForField(fromSec);
        }

        if (!string.IsNullOrWhiteSpace(segment.To)
            && TimeFieldHelper.TryParseSeconds(segment.To, out var toSec))
        {
            segment.To = TimeFieldHelper.FormatForField(toSec);
        }
    }

    private bool TryValidateAndNormalizeEditorTimes()
    {
        NormalizeSegmentTimes(EditorSegment);

        var hasFrom = TimeFieldHelper.TryParseSeconds(EditorSegment.From, out var fromSec);
        var hasTo = TimeFieldHelper.TryParseSeconds(EditorSegment.To, out var toSec);

        if (hasFrom && hasTo && toSec <= fromSec)
        {
            Status = Loc.T("texttovideo.status.invalidTimeRange");
            return false;
        }

        if (_probeDurationSec > 0)
        {
            if (hasFrom && fromSec > _probeDurationSec)
            {
                EditorSegment.From = TimeFieldHelper.FormatForField(_probeDurationSec);
                fromSec = _probeDurationSec;
            }

            if (hasTo && toSec > _probeDurationSec)
            {
                EditorSegment.To = TimeFieldHelper.FormatForField(_probeDurationSec);
            }
        }

        return true;
    }

    private TextToVideoSettings CollectSettings() => new()
    {
        LastVideoDir = string.IsNullOrWhiteSpace(VideoPath) ? "" : Path.GetDirectoryName(VideoPath) ?? "",
        LastVideoPath = VideoPath,
        Codec = Codec,
        Bitrate = Bitrate,
        ExportContainer = ExportContainer,
        GifFps = GifFps,
        TargetFps = TargetFps,
        GifMaxWidth = GifMaxWidth,
        GifPaletteColors = GifPaletteColors,
        AudioCopy = AudioCopy,
        SrtPath = SrtPath,
        DavinciPreset = DavinciPreset,
        DavinciOutputDir = DavinciOutputDir,
        UseLastFfmpegForResolve = UseLastFfmpegForResolve,
        OverlaySegments = Segments.Select(s => s.Clone()).ToList(),
    };

    public void PersistSettings() => TextToVideoConfigReader.Save(CollectSettings());

    [RelayCommand]
    private void SaveSettings()
    {
        PersistSettings();
        Status = Loc.T("common.settingsSaved");
    }

    [RelayCommand]
    private async Task PickVideoAsync()
    {
        var path = await FilePickerHelper.PickVideoAsync(VideoPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            VideoPath = path;
            PersistSettings();
        }
    }

    [RelayCommand]
    private void OpenFullGui() => AppServices.Launcher.Launch(_tool);

    public int PreviewVideoWidth => _videoWidth;

    public int PreviewVideoHeight => _videoHeight;

    public void CancelPendingPreview()
    {
        _previewDebounceCts?.Cancel();
        _previewCts?.Cancel();
        Interlocked.Increment(ref _previewGeneration);
    }

    public void SetPreviewPosition(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, SliderMaximum);
        if (Math.Abs(SliderValue - seconds) < 0.02)
        {
            return;
        }

        SliderValue = seconds;
    }

    public void SetPositionFromPreviewClick(double normalizedX, double normalizedY, bool deferPreviewRefresh = false)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        var x = (int)Math.Clamp(Math.Round(normalizedX * _videoWidth), 0, _videoWidth - 1);
        var y = (int)Math.Clamp(Math.Round(normalizedY * _videoHeight), 0, _videoHeight - 1);

        if (deferPreviewRefresh)
        {
            _isOverlayDragging = true;
        }

        EditorSegment.PosX = x;
        EditorSegment.PosY = y;

        if (!deferPreviewRefresh)
        {
            _isOverlayDragging = false;
            SchedulePreviewRefresh();
        }
    }

    public void BeginOverlayPositionDrag() => _isOverlayDragging = true;

    public void EndOverlayPositionDrag()
    {
        if (!_isOverlayDragging)
        {
            SchedulePreviewRefresh();
            return;
        }

        _isOverlayDragging = false;
        SchedulePreviewRefresh();
    }

    public void CancelOverlayPositionDrag() => _isOverlayDragging = false;

    public void ApplyPlayheadToFrom() => EditorSegment.From = TimeFieldHelper.FormatForField(SliderValue);

    public void ApplyPlayheadToTo() => EditorSegment.To = TimeFieldHelper.FormatForField(SliderValue);

    private void UpdateTimeDisplay()
    {
        var cur = SliderValue;
        var tot = SliderMaximum;
        TimeDisplay = $"{TimeFieldHelper.FormatClock(cur)} / {TimeFieldHelper.FormatClock(tot)}";
    }
}

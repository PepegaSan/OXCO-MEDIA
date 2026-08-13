using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;

namespace HailMary.ViewModels;

public partial class TextToVideoViewModel : ObservableObject, IToolShellHost, ISplitPaneToolHost, ILocalizable
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
        Codec = FfmpegEncoderCatalog.LabelFor(FfmpegEncoderCatalog.TextToVideo, settings.Codec);
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
        Segments.CollectionChanged += (_, _) => RebuildTimeline();

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
            or nameof(TextOverlaySegment.LayoutWidth)
            or nameof(TextOverlaySegment.LayoutHeight)
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
            else if (e.PropertyName is nameof(TextOverlaySegment.PosX)
                     or nameof(TextOverlaySegment.PosY)
                     or nameof(TextOverlaySegment.LayoutWidth)
                     or nameof(TextOverlaySegment.LayoutHeight)
                     or nameof(TextOverlaySegment.Fontsize))
            {
                if (_isOverlayDragging)
                {
                    return;
                }
            }
            else if (e.PropertyName is nameof(TextOverlaySegment.Text)
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

            if (_isOverlayDragging)
            {
                return;
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
        FfmpegEncoderCatalog.Labels(FfmpegEncoderCatalog.TextToVideo);

    public static IReadOnlyList<string> ColorPalette { get; } =
    [
        "FFFFFF", "000000", "FF0000", "00CC00", "0066FF", "FFFF00", "FF00CC", "00DDDD", "FF8800", "888888",
    ];

    [ObservableProperty] private TextOverlaySegment _editorSegment;

    [ObservableProperty] private string _videoPath = string.Empty;

    [ObservableProperty] private string _codec = FfmpegEncoderCatalog.LabelFor(
        FfmpegEncoderCatalog.TextToVideo,
        "libx264");

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

    [ObservableProperty] private float[]? _waveformPeaks;

    public IReadOnlyList<TimelineSpan> TimelineRanges { get; private set; } = [];

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
        if (deferPreviewRefresh)
        {
            BeginOverlayPositionDrag();
            ApplyMoveFromPreview(normalizedX, normalizedY);
            return;
        }

        BeginOverlayPositionDrag();
        ApplyMoveFromPreview(normalizedX, normalizedY);
        EndOverlayPositionDrag();
    }

    /// <summary>Rough drawtext/Pillow box in source pixels (prefer slightly large so the frame contains glyphs).</summary>
    public (int Width, int Height) EstimateEditorOverlayBox()
    {
        var seg = EditorSegment;
        // Same split as overlay_core._overlay_visual_lines (keeps empty lines after Enter).
        var lines = (seg.Text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (lines.Length == 0)
        {
            lines = [""];
        }

        var fs = Math.Clamp(seg.Fontsize, 8, 400);
        var ls = Math.Clamp(seg.LineSpacing, -120, 120);
        var lineStep = Math.Max(4, fs + ls);
        var lineCount = Math.Max(1, lines.Length);
        // drawtext y-stack + a little descent so glyphs don't clip the bottom edge
        var h = fs + (lineCount - 1) * lineStep + Math.Max(2, fs / 6);

        var maxUnits = 1.0;
        foreach (var line in lines)
        {
            maxUnits = Math.Max(maxUnits, EstimateTextWidthUnits(line));
        }

        // FreeType advances vary a lot by face; keep the base generous, then add font-mismatch slack.
        var em = seg.Bold ? 0.70 : 0.62;
        var w = (int)Math.Max(fs * 2.0, Math.Ceiling(maxUnits * fs * em));
        // ~2.5 glyph widths: WinUI measure ≠ FFmpeg/Pillow face for many fonts
        w += Math.Max(12, (int)Math.Ceiling(fs * em * 2.5));
        if (seg.Italic)
        {
            w += Math.Max(6, fs / 4);
        }

        if (seg.BoxEnabled)
        {
            // FFmpeg boxborderw pads around the glyph box
            var bb = Math.Max(0, seg.BoxBorder);
            w += bb * 2 + Math.Max(8, fs / 6);
            h += bb * 2 + Math.Max(6, fs / 8);
        }

        // Pillow raster path (custom font / styles) adds per-line pad + optional shear
        if (seg.Bold || seg.Italic || seg.Strike || !string.IsNullOrWhiteSpace(seg.FontPath))
        {
            w += Math.Max(12, fs / 3);
            h += Math.Max(6, fs / 8);
        }

        if (seg.LayoutWidth > 0)
        {
            w = seg.LayoutWidth;
        }

        if (seg.LayoutHeight > 0)
        {
            h = seg.LayoutHeight;
        }

        if (_videoWidth > 0)
        {
            w = Math.Min(w, _videoWidth);
        }

        if (_videoHeight > 0)
        {
            h = Math.Min(h, _videoHeight);
        }

        return (Math.Max(8, w), Math.Max(8, h));
    }

    private static double EstimateTextWidthUnits(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return 1.0;
        }

        double units = 0;
        foreach (var ch in line)
        {
            units += ch switch
            {
                ' ' or '\t' => 0.33,
                'i' or 'l' or 'I' or 'j' or 't' or 'f' or 'r' or '\'' or '.' or ',' or ':' or ';' => 0.40,
                'm' or 'w' or 'M' or 'W' or '@' => 1.35,
                >= '\u1100' and <= '\u11FF' or >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF'
                    or >= '\uAC00' and <= '\uD7AF' => 1.0,
                // Most Latin letters ≈ 1em unit (scaled by em factor below)
                _ => 1.0,
            };
        }

        return Math.Max(1.0, units);
    }

    public void BeginOverlayPositionDrag()
    {
        _isOverlayDragging = true;
        _previewDebounceCts?.Cancel();
    }

    public void EndOverlayPositionDrag()
    {
        _isOverlayDragging = false;
        SchedulePreviewRefresh(immediate: true);
    }

    public void CancelOverlayPositionDrag()
    {
        _isOverlayDragging = false;
        SchedulePreviewRefresh(immediate: true);
    }

    /// <summary>Normalized preview top-left (0–1) → PosX/PosY, clamped so the estimated box stays on screen.</summary>
    public void ApplyMoveFromPreview(double nx, double ny)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        var (w, h) = EstimateEditorOverlayBox();
        var x = (int)Math.Clamp(Math.Round(nx * _videoWidth), 0, Math.Max(0, _videoWidth - w));
        var y = (int)Math.Clamp(Math.Round(ny * _videoHeight), 0, Math.Max(0, _videoHeight - h));
        EditorSegment.PosX = x;
        EditorSegment.PosY = y;
    }

    /// <summary>SE corner in normalized preview space; width and height are independent.</summary>
    public void ApplyResizeFromPreview(double nRight, double nBottom)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        var left = EditorSegment.PosX / (double)_videoWidth;
        var top = EditorSegment.PosY / (double)_videoHeight;
        var maxW = Math.Max(8, _videoWidth - EditorSegment.PosX);
        var maxH = Math.Max(8, _videoHeight - EditorSegment.PosY);
        var newW = (int)Math.Clamp(
            Math.Round((Math.Clamp(nRight, 0, 1) - left) * _videoWidth),
            8,
            maxW);
        var newH = (int)Math.Clamp(
            Math.Round((Math.Clamp(nBottom, 0, 1) - top) * _videoHeight),
            8,
            maxH);

        EditorSegment.LayoutWidth = newW;
        EditorSegment.LayoutHeight = newH;
    }

    public void NudgeOverlay(int dxPx, int dyPx, int dFontsize)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        if (dFontsize != 0)
        {
            EditorSegment.Fontsize = Math.Clamp(EditorSegment.Fontsize + dFontsize, 12, 200);
        }

        var (w, h) = EstimateEditorOverlayBox();
        EditorSegment.PosX = Math.Clamp(EditorSegment.PosX + dxPx, 0, Math.Max(0, _videoWidth - w));
        EditorSegment.PosY = Math.Clamp(EditorSegment.PosY + dyPx, 0, Math.Max(0, _videoHeight - h));
        SchedulePreviewRefresh();
    }

    public void ApplyPlayheadToFrom() => EditorSegment.From = TimeFieldHelper.FormatForField(SliderValue);

    public void ApplyPlayheadToTo() => EditorSegment.To = TimeFieldHelper.FormatForField(SliderValue);

    private void UpdateTimeDisplay()
    {
        var cur = SliderValue;
        var tot = SliderMaximum;
        TimeDisplay = $"{TimeFieldHelper.FormatClock(cur)} / {TimeFieldHelper.FormatClock(tot)}";
    }
}

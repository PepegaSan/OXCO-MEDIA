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

    public event Action<int>? RequestSegmentListSelection;

    public TextToVideoViewModel(ToolDefinition tool)
    {
        _tool = tool;
        var settings = TextToVideoConfigReader.Load();
        VideoPath = settings.LastVideoPath;
        Codec = settings.Codec;
        Bitrate = settings.Bitrate;
        ExportContainer = settings.ExportContainer;
        GifFps = settings.GifFps;
        GifMaxWidth = settings.GifMaxWidth;
        GifPaletteColors = settings.GifPaletteColors;
        AudioCopy = settings.AudioCopy;
        SrtPath = settings.SrtPath;
        DavinciPreset = settings.DavinciPreset;
        DavinciOutputDir = settings.DavinciOutputDir;
        BatchOutputDir = string.IsNullOrWhiteSpace(settings.LastVideoDir) ? "" : settings.LastVideoDir;
        TextToVideoConfigReader.ApplyToObservable(settings, Segments);
        EditorSegment = new TextOverlaySegment();
    }

    public bool IsEditingExistingSegment =>
        _selectedSegmentIndex >= 0
        && _selectedSegmentIndex < Segments.Count
        && ReferenceEquals(EditorSegment, Segments[_selectedSegmentIndex]);

    public string SegmentEditorTitle =>
        IsEditingExistingSegment
            ? Loc.F("texttovideo.editSegment", _selectedSegmentIndex + 1)
            : Loc.T("texttovideo.newSegment");

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
    }

    private void EditorSegment_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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
            }

            SchedulePreviewRefresh();
        }
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

    partial void OnSrtPathChanged(string value) => SchedulePreviewRefresh();

    partial void OnSliderValueChanged(double value)
    {
        UpdateTimeDisplay();
        SchedulePreviewRefresh();
    }

    partial void OnIsPreviewPlayingChanged(bool value)
    {
        if (value)
        {
            SchedulePreviewRefresh();
        }
    }

    public void SetSelectedSegmentIndex(int index)
    {
        if (index == _selectedSegmentIndex
            && index >= 0
            && index < Segments.Count
            && ReferenceEquals(EditorSegment, Segments[index]))
        {
            return;
        }

        _selectedSegmentIndex = index;
        OnPropertyChanged(nameof(SelectedSegmentIndex));
        UpdateSegmentEditorChrome();

        if (index >= 0 && index < Segments.Count)
        {
            EditorSegment = Segments[index];
        }
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

    private TextToVideoSettings CollectSettings() => new()
    {
        LastVideoDir = string.IsNullOrWhiteSpace(VideoPath) ? "" : Path.GetDirectoryName(VideoPath) ?? "",
        LastVideoPath = VideoPath,
        Codec = Codec,
        Bitrate = Bitrate,
        ExportContainer = ExportContainer,
        GifFps = GifFps,
        GifMaxWidth = GifMaxWidth,
        GifPaletteColors = GifPaletteColors,
        AudioCopy = AudioCopy,
        SrtPath = SrtPath,
        DavinciPreset = DavinciPreset,
        DavinciOutputDir = DavinciOutputDir,
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

    public void SetPreviewPosition(double seconds)
    {
        seconds = Math.Clamp(seconds, 0, SliderMaximum);
        if (Math.Abs(SliderValue - seconds) < 0.02)
        {
            return;
        }

        SliderValue = seconds;
    }

    public void SetPositionFromPreviewClick(double normalizedX, double normalizedY)
    {
        if (_videoWidth <= 0 || _videoHeight <= 0)
        {
            return;
        }

        EditorSegment.PosX = (int)Math.Clamp(Math.Round(normalizedX * _videoWidth), 0, _videoWidth - 1);
        EditorSegment.PosY = (int)Math.Clamp(Math.Round(normalizedY * _videoHeight), 0, _videoHeight - 1);
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

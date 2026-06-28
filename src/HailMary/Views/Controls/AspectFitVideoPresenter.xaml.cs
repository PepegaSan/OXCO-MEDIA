using HailMary.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views.Controls;

public sealed partial class AspectFitVideoPresenter : UserControl
{
    public static readonly DependencyProperty MinPreviewHeightProperty =
        DependencyProperty.Register(
            nameof(MinPreviewHeight),
            typeof(double),
            typeof(AspectFitVideoPresenter),
            new PropertyMetadata(120d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxPreviewHeightProperty =
        DependencyProperty.Register(
            nameof(MaxPreviewHeight),
            typeof(double),
            typeof(AspectFitVideoPresenter),
            new PropertyMetadata(720d, OnLayoutPropertyChanged));

    private int _videoWidth;
    private int _videoHeight;

    public AspectFitVideoPresenter()
    {
        InitializeComponent();
        Loaded += OnLoadedLayout;
    }

    private void OnLoadedLayout(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        AspectFitLayoutHelper.WatchParentWidthChanges(this, ApplyLayout);
    }

    public MediaPlayerElement Player => PreviewPlayer;

    public double MinPreviewHeight
    {
        get => (double)GetValue(MinPreviewHeightProperty);
        set => SetValue(MinPreviewHeightProperty, value);
    }

    public double MaxPreviewHeight
    {
        get => (double)GetValue(MaxPreviewHeightProperty);
        set => SetValue(MaxPreviewHeightProperty, value);
    }

    public void ClearVideoDimensions()
    {
        _videoWidth = 0;
        _videoHeight = 0;
        ApplyLayout();
    }

    public async Task SetVideoDimensionsFromPathAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ClearVideoDimensions();
            return;
        }

        var fromProbe = await FfprobeHelper.ProbeVideoSizeAsync(path);
        if (fromProbe is { Width: > 0, Height: > 0 })
        {
            SetVideoDimensions(fromProbe.Value.Width, fromProbe.Value.Height);
            return;
        }

        var fromProps = await TryReadStorageVideoSizeAsync(path);
        if (fromProps is { Width: > 0, Height: > 0 })
        {
            SetVideoDimensions(fromProps.Value.Width, fromProps.Value.Height);
            return;
        }

        SetVideoDimensions(16, 9);
    }

    public void SetVideoDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            ClearVideoDimensions();
            return;
        }

        _videoWidth = width;
        _videoHeight = height;
        ApplyLayout();
    }

    private static async Task<(int Width, int Height)?> TryReadStorageVideoSizeAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            var props = await file.Properties.GetVideoPropertiesAsync();
            if (props.Width > 0 && props.Height > 0)
            {
                return ((int)props.Width, (int)props.Height);
            }
        }
        catch
        {
            // fallback
        }

        return null;
    }

    private void Root_OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AspectFitVideoPresenter presenter)
        {
            presenter.ApplyLayout();
        }
    }

    private void ApplyLayout()
    {
        var availW = AspectFitLayoutHelper.GetAvailableWidth(this);
        var (displayW, displayH, rootMin) = AspectFitLayoutHelper.Compute(
            availW,
            MaxPreviewHeight,
            MinPreviewHeight,
            _videoWidth,
            _videoHeight);

        VideoChrome.Width = displayW;
        VideoChrome.Height = displayH;
        Root.MinHeight = rootMin;
        Root.Width = _videoWidth > 0 ? displayW : double.NaN;
    }
}

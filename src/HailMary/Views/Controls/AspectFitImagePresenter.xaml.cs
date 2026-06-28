using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HailMary.Views.Controls;

public sealed partial class AspectFitImagePresenter : UserControl
{
    public static readonly DependencyProperty MinPreviewHeightProperty =
        DependencyProperty.Register(
            nameof(MinPreviewHeight),
            typeof(double),
            typeof(AspectFitImagePresenter),
            new PropertyMetadata(120d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxPreviewHeightProperty =
        DependencyProperty.Register(
            nameof(MaxPreviewHeight),
            typeof(double),
            typeof(AspectFitImagePresenter),
            new PropertyMetadata(720d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ImageSourceProperty =
        DependencyProperty.Register(
            nameof(ImageSource),
            typeof(ImageSource),
            typeof(AspectFitImagePresenter),
            new PropertyMetadata(null, OnImageSourceChanged));

    private int _contentWidth;
    private int _contentHeight;

    public AspectFitImagePresenter()
    {
        InitializeComponent();
        Loaded += OnLoadedLayout;
    }

    private void OnLoadedLayout(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        AspectFitLayoutHelper.WatchParentWidthChanges(this, ApplyLayout);
    }

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

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public void ClearContentDimensions()
    {
        _contentWidth = 0;
        _contentHeight = 0;
        ApplyLayout();
    }

    public void SetContentDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            ClearContentDimensions();
            return;
        }

        _contentWidth = width;
        _contentHeight = height;
        ApplyLayout();
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AspectFitImagePresenter presenter)
        {
            presenter.ApplyLayout();
        }
    }

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AspectFitImagePresenter presenter)
        {
            return;
        }

        presenter.PreviewImage.Source = e.NewValue as ImageSource;
        if (e.NewValue is BitmapImage bitmap)
        {
            presenter.TryApplyBitmapDimensions(bitmap);
        }
        else if (e.NewValue is null)
        {
            presenter.ClearContentDimensions();
        }
    }

    private void PreviewImage_OnImageOpened(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is BitmapImage bitmap)
        {
            TryApplyBitmapDimensions(bitmap);
        }
    }

    private void TryApplyBitmapDimensions(BitmapImage bitmap)
    {
        if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            SetContentDimensions(bitmap.PixelWidth, bitmap.PixelHeight);
        }
    }

    private void Root_OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var availW = AspectFitLayoutHelper.GetAvailableWidth(this);
        var (displayW, displayH, rootMin) = AspectFitLayoutHelper.Compute(
            availW,
            MaxPreviewHeight,
            MinPreviewHeight,
            _contentWidth,
            _contentHeight);

        FrameChrome.Width = displayW;
        FrameChrome.Height = displayH;
        Root.MinHeight = rootMin;
        Root.Width = _contentWidth > 0 ? displayW : double.NaN;
    }
}

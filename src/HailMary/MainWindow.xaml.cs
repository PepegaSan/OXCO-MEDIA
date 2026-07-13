using HailMary.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace HailMary;

public sealed partial class MainWindow : Window
{
    private const int MinWindowWidth = 900;
    private const int MinWindowHeight = 600;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        RestoreWindowState();
        RefreshTitle();
        AppServices.Localization.LanguageChanged += RefreshTitle;

        Closed += (_, _) => SaveWindowState();
        RootFrame.Navigate(typeof(MainPage));
    }

    private void RefreshTitle()
    {
        var title = AppServices.Localization.Get("app.title", "Hail Mary");
        Title = title;
        AppTitleBar.Title = title;
    }

    private void RestoreWindowState()
    {
        var settings = AppServices.Settings.Current;
        var width = (int)Math.Clamp(settings.WindowWidth, MinWindowWidth, 3840);
        var height = (int)Math.Clamp(settings.WindowHeight, MinWindowHeight, 2160);
        AppWindow.Resize(new SizeInt32(width, height));

        var position = new PointInt32((int)settings.WindowX, (int)settings.WindowY);
        try
        {
            var display = DisplayArea.GetFromPoint(position, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            position.X = Math.Clamp(position.X, work.X, Math.Max(work.X, work.X + work.Width - width));
            position.Y = Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        }
        catch
        {
            // Fallback: Standardposition beibehalten.
        }

        AppWindow.Move(position);
    }

    private void SaveWindowState()
    {
        var size = AppWindow.Size;
        var position = AppWindow.Position;
        AppServices.Settings.Current.WindowWidth = size.Width;
        AppServices.Settings.Current.WindowHeight = size.Height;
        AppServices.Settings.Current.WindowX = position.X;
        AppServices.Settings.Current.WindowY = position.Y;
        AppServices.Settings.Save();
    }
}

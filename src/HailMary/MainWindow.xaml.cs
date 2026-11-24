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
        var width = Math.Max(MinWindowWidth, (int)settings.WindowWidth);
        var height = Math.Max(MinWindowHeight, (int)settings.WindowHeight);

        var position = new PointInt32((int)settings.WindowX, (int)settings.WindowY);
        try
        {
            var display = DisplayArea.GetFromPoint(position, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
            position.X = Math.Clamp(position.X, work.X, Math.Max(work.X, work.X + work.Width - width));
            position.Y = Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        }
        catch
        {
            // Fallback: Standardposition beibehalten.
        }

        AppWindow.MoveAndResize(new RectInt32(position.X, position.Y, width, height));
    }

    private void SaveWindowState()
    {
        var size = AppWindow.Size;
        var position = AppWindow.Position;
        AppServices.Settings.Current.WindowWidth = size.Width;
        AppServices.Settings.Current.WindowHeight = size.Height;
        AppServices.Settings.Current.WindowX = position.X;
        AppServices.Settings.Current.WindowY = position.Y;
        try
        {
            AppServices.Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StartupLog.Write($"Could not save window position and size: {ex.Message}");
        }
    }
}

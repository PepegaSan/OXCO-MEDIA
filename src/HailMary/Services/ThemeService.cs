using HailMary.Models;
using Microsoft.UI.Xaml;

namespace HailMary.Services;

public sealed class ThemeService
{
    public UiTheme CurrentTheme { get; private set; } = UiTheme.System;

    public event Action? ThemeChanged;

    /// <summary>Applies the theme from settings. Call after <see cref="App.Window"/> is created.</summary>
    public void ApplyFromSettings() => Apply(AppServices.Settings.Current.UiTheme);

    public void Apply(UiTheme theme)
    {
        CurrentTheme = theme;
        var elementTheme = theme switch
        {
            UiTheme.Light => ElementTheme.Light,
            UiTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        if (App.Window?.Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = elementTheme;
        }

        ThemeChanged?.Invoke();
    }

    public void SaveTheme(UiTheme theme)
    {
        AppServices.Settings.Current.UiTheme = theme;
        AppServices.Settings.Save();
        Apply(theme);
    }
}

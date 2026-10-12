using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HailMary.Views;

public sealed partial class TextToVideoPanel : UserControl
{
    public TextToVideoViewModel ViewModel { get; }

    public TextToVideoPanel(TextToVideoViewModel viewModel)
    {
        ViewModel = viewModel;
        ViewModel.ConfirmAsync = ConfirmAsync;
        InitializeComponent();
        ViewModel.RequestSegmentListSelection += _ => { };

        BuildColorPalette();
        RebuildStylePresets();
        InitFontCombo();
        SyncAlignButtons();
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        AppServices.Localization.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            AppServices.Localization.LanguageChanged -= OnLanguageChanged;
            ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        };
    }

    private bool _fontComboSyncing;

    private void InitFontCombo()
    {
        FontCombo.ItemsSource = ViewModel.AvailableFonts;
        SyncFontComboSelection();
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextToVideoViewModel.SelectedSystemFont)
            or nameof(TextToVideoViewModel.EditorSegment))
        {
            SyncFontComboSelection();
        }

        if (e.PropertyName is nameof(TextToVideoViewModel.EditorAlignIndex)
            or nameof(TextToVideoViewModel.EditorSegment))
        {
            SyncAlignButtons();
        }
    }

    private void AlignButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var index))
        {
            ViewModel.EditorAlignIndex = index;
        }

        // Toggle buttons act as a radio group — re-sync even when the value did not change.
        SyncAlignButtons();
    }

    private void SyncAlignButtons()
    {
        var index = ViewModel.EditorAlignIndex;
        AlignLeftButton.IsChecked = index == 0;
        AlignCenterButton.IsChecked = index == 1;
        AlignRightButton.IsChecked = index == 2;
    }

    private void SyncFontComboSelection()
    {
        _fontComboSyncing = true;
        try
        {
            var selected = ViewModel.SelectedSystemFont;
            if (selected is null)
            {
                FontCombo.SelectedIndex = -1;
                return;
            }

            // Prefer exact instance from ItemsSource list.
            var match = ViewModel.AvailableFonts.FirstOrDefault(f =>
                string.Equals(f.Path, selected.Path, StringComparison.OrdinalIgnoreCase));
            FontCombo.SelectedItem = match ?? selected;
        }
        finally
        {
            _fontComboSyncing = false;
        }
    }

    private void FontCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fontComboSyncing || FontCombo.SelectedItem is not WindowsFontChoice choice)
        {
            return;
        }

        ViewModel.ApplySystemFont(choice);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords },
            PrimaryButtonText = Loc.T("common.yes"),
            CloseButtonText = Loc.T("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        return (await dialog.ShowAsync()) == ContentDialogResult.Primary;
    }

    private void OnLanguageChanged() => UiDispatcher.Run(RebuildStylePresets);

    private void RebuildStylePresets()
    {
        TextColorField.Header = Loc.T("texttovideo.color");
        StylePresetPanel.Children.Clear();
        foreach (var preset in TextOverlayStylePresets.All)
        {
            var btn = new Button
            {
                Content = preset.Label,
                Tag = preset.Id,
                Width = 160,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            btn.Click += StylePresetButton_OnClick;
            StylePresetPanel.Children.Add(btn);
        }
    }

    private void StylePresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            ViewModel.ApplyStylePresetCommand.Execute(id);
        }
    }

    private void BuildColorPalette()
    {
        foreach (var hex in TextToVideoViewModel.ColorPalette)
        {
            var color = ParseHexColor(hex);
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                Tag = hex,
            };
            btn.Click += ColorButton_OnClick;
            ColorPalettePanel.Children.Add(btn);
        }
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6)
        {
            return Microsoft.UI.Colors.White;
        }

        return Color.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    private void ColorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            ViewModel.EditorSegment.Color = hex;
            ViewModel.SchedulePreviewRefresh();
        }
    }

    private void SegmentItem_Click(object sender, RoutedEventArgs e)
    {
        TextOverlaySegment? segment = null;
        if (sender is FrameworkElement el)
        {
            segment = el.Tag as TextOverlaySegment ?? el.DataContext as TextOverlaySegment;
        }

        if (segment is null)
        {
            return;
        }

        var index = ViewModel.Segments.IndexOf(segment);
        if (index >= 0)
        {
            ViewModel.SetSelectedSegmentIndex(index);
        }
    }

    private void InsertFrom_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPlayheadToFrom();

    private void InsertTo_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPlayheadToTo();
}

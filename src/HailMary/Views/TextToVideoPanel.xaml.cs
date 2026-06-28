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
        InitializeComponent();
        ViewModel.RequestSegmentListSelection += index => SegmentList.SelectedIndex = index;
        BuildColorPalette();
        RebuildStylePresets();
        AppServices.Localization.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => AppServices.Localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => UiDispatcher.Run(RebuildStylePresets);

    private void RebuildStylePresets()
    {
        StylePresetPanel.Children.Clear();
        foreach (var preset in TextOverlayStylePresets.All)
        {
            var btn = new Button
            {
                Content = preset.Label,
                Tag = preset.Id,
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

    private void SegmentList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedSegmentIndex(SegmentList.SelectedIndex);
    }

    private void InsertFrom_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPlayheadToFrom();

    private void InsertTo_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPlayheadToTo();
}

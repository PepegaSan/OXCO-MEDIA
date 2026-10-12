using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HailMary.Views;

public sealed partial class MarkerAutocutPanel : UserControl
{
    public MarkerAutocutViewModel ViewModel { get; }

    public MarkerAutocutPanel(MarkerAutocutViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        Loaded += (_, _) => RenderStrip();
    }

    private void ViewModel_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MarkerAutocutViewModel.StripBlocks))
        {
            RenderStrip();
        }
    }

    /// <summary>Proportional blocks: how long each clip (or video) is in the result.</summary>
    private void RenderStrip()
    {
        ResultStrip.Children.Clear();
        ResultStrip.ColumnDefinitions.Clear();
        var blocks = ViewModel.StripBlocks;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            ResultStrip.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.05, block.Seconds), GridUnitType.Star),
            });
            var rect = new Border
            {
                Background = new SolidColorBrush(ParseHex(block.ColorHex)),
                CornerRadius = new CornerRadius(3),
                MinWidth = 2,
            };
            ToolTipService.SetToolTip(rect, block.Tooltip);
            Grid.SetColumn(rect, i);
            ResultStrip.Children.Add(rect);
        }
    }

    private static Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length == 6
            ? Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16))
            : Color.FromArgb(255, 136, 136, 136);
    }

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    /// <summary>
    /// File header checkbox: display-only binding, a real click toggles the whole file.
    /// (A TwoWay binding wrote the "partly selected" state back and re-/deselected every marker.)
    /// </summary>
    private void GroupCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.Tag is not MarkerAutocutFileGroup group)
        {
            return;
        }

        group.AllSelected = group.AllSelected != true;
        box.IsChecked = group.AllSelected;
    }

    private void FileCardUp_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MoveFileCardUpCommand.Execute(TagOf<MarkerAutocutOrderFileCard>(sender));

    private void FileCardDown_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MoveFileCardDownCommand.Execute(TagOf<MarkerAutocutOrderFileCard>(sender));

    private void FileCardRemove_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveFileCardCommand.Execute(TagOf<MarkerAutocutOrderFileCard>(sender));

    private void ClipUp_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MoveClipUpCommand.Execute(TagOf<MarkerAutocutOrderRowViewModel>(sender));

    private void ClipDown_Click(object sender, RoutedEventArgs e) =>
        ViewModel.MoveClipDownCommand.Execute(TagOf<MarkerAutocutOrderRowViewModel>(sender));

    private void ClipRemove_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveClipCommand.Execute(TagOf<MarkerAutocutOrderRowViewModel>(sender));

    // Rebuild after the drop finished — never while the list is still dragging.
    private void FileCardList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        DispatcherQueue.TryEnqueue(ViewModel.ApplyFileCardOrder);

    private void ClipCardList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.OnClipsReordered();
}

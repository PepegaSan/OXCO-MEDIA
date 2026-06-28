using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HailMary.Views.Helpers;

/// <summary>
/// Drag &amp; Drop für Video-Dateien (optional Ordner) auf TextBox/Grid/Border.
/// Setzt ViewModel-Eigenschaft per Name oder ToolIoViewModel.ApplyDroppedInput.
/// </summary>
public static class VideoDrop
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(VideoDrop),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty AllowFoldersProperty =
        DependencyProperty.RegisterAttached(
            "AllowFolders",
            typeof(bool),
            typeof(VideoDrop),
            new PropertyMetadata(false));

    public static readonly DependencyProperty PathPropertyNameProperty =
        DependencyProperty.RegisterAttached(
            "PathPropertyName",
            typeof(string),
            typeof(VideoDrop),
            new PropertyMetadata(string.Empty));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static bool GetAllowFolders(DependencyObject obj) => (bool)obj.GetValue(AllowFoldersProperty);

    public static void SetAllowFolders(DependencyObject obj, bool value) => obj.SetValue(AllowFoldersProperty, value);

    public static string GetPathPropertyName(DependencyObject obj) => (string)obj.GetValue(PathPropertyNameProperty);

    public static void SetPathPropertyName(DependencyObject obj, string value) => obj.SetValue(PathPropertyNameProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;

        if (e.NewValue is true)
        {
            element.AllowDrop = true;
            element.DragOver += OnDragOver;
            element.Drop += OnDrop;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.Caption = "Video ablegen";
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    private static async void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not DependencyObject element || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(item => item.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var allowFolders = GetAllowFolders(element);
        if (FindPanelViewModel(element) is IVideoBatchHost batchHost)
        {
            batchHost.AddDroppedPaths(paths);
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
            AppServices.Log.Info($"Drop: {paths.Count} Element(e) zur Batch-Liste");
            return;
        }

        var path = VideoPathDropHelper.PickFirstVideoOrFolder(paths, allowFolders);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;

        if (!TryAssignPath(element, path, allowFolders))
        {
            AppServices.Log.Info($"Drop ignoriert: {path}");
        }
    }

    private static bool TryAssignPath(DependencyObject element, string path, bool allowFolders)
    {
        var propertyName = GetPathPropertyName(element);
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            var vm = FindPanelViewModel(element);
            if (vm is null)
            {
                return false;
            }

            var prop = vm.GetType().GetProperty(propertyName);
            if (prop is null || !prop.CanWrite || prop.PropertyType != typeof(string))
            {
                return false;
            }

            prop.SetValue(vm, path);
            AppServices.Log.Info($"Drop: {path}");
            return true;
        }

        if (FindPanelViewModel(element) is ToolIoViewModel toolIo)
        {
            toolIo.ApplyDroppedInput(path, allowFolders);
            AppServices.Log.Info($"Drop: {path}");
            return true;
        }

        if (element is TextBox textBox)
        {
            textBox.Text = path;
            AppServices.Log.Info($"Drop: {path}");
            return true;
        }

        return false;
    }

    private static object? FindPanelViewModel(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement fe)
            {
                var prop = fe.GetType().GetProperty("ViewModel");
                if (prop?.GetValue(fe) is { } vm)
                {
                    return vm;
                }
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}

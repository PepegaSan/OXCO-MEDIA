using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HailMary.Services;

public static class FolderPickerHelper
{
    public static async Task<string?> PickFolderAsync(string? suggestedPath = null)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);

        if (!string.IsNullOrWhiteSpace(suggestedPath) && Directory.Exists(suggestedPath))
        {
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(App.Window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }
}

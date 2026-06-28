using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HailMary.Services;

public static class FilePickerHelper
{
    public static async Task<string?> PickVideoAsync(string? suggestedPath = null)
    {
        var picker = CreateVideoPicker();
        InitializePicker(picker);

        if (!string.IsNullOrWhiteSpace(suggestedPath) && File.Exists(suggestedPath))
        {
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        }

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        var picker = CreateVideoPicker();
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(f => f.Path).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    private static FileOpenPicker CreateVideoPicker()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mkv");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".avi");
        picker.FileTypeFilter.Add(".webm");
        picker.FileTypeFilter.Add(".m4v");
        picker.FileTypeFilter.Add(".wmv");
        picker.FileTypeFilter.Add(".mpg");
        picker.FileTypeFilter.Add(".mpeg");
        picker.FileTypeFilter.Add(".mts");
        picker.FileTypeFilter.Add(".m2ts");
        picker.FileTypeFilter.Add(".flv");
        return picker;
    }

    public static async Task<string?> PickAnyFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickSrtAsync(string? suggestedPath = null)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".srt");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickFontFileAsync(string? suggestedPath = null)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".ttf");
        picker.FileTypeFilter.Add(".otf");
        picker.FileTypeFilter.Add(".ttc");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public static async Task<string?> PickExportVideoAsync(string? sourceVideoPath, bool gif)
    {
        var picker = new FileSavePicker();
        if (gif)
        {
            picker.FileTypeChoices.Add("GIF", [".gif"]);
            picker.SuggestedFileName = "export.gif";
        }
        else
        {
            picker.FileTypeChoices.Add("MP4", [".mp4"]);
            picker.SuggestedFileName = "export.mp4";
        }

        if (!string.IsNullOrWhiteSpace(sourceVideoPath) && File.Exists(sourceVideoPath))
        {
            picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(sourceVideoPath) + (gif ? "_text.gif" : "_text.mp4");
        }

        InitializePicker(picker);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static void InitializePicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(App.Window);
        InitializeWithWindow.Initialize(picker, hwnd);
    }
}

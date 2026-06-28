using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HailMary.Models;
using HailMary.Services;
using Windows.ApplicationModel.DataTransfer;

namespace HailMary.ViewModels;

public partial class ToolTabViewModel : ObservableObject, IToolShellHost, ILocalizable
{
    private readonly ToolDefinition _definition;

    public ToolTabViewModel(ToolDefinition definition)
    {
        _definition = definition;
        Status = Loc.T("common.ready");
    }

    public string Id => _definition.Id;

    public string Label => _definition.Label;

    public string Description => _definition.Description;

    [ObservableProperty]
    private string _status = Loc.T("common.ready");

    [RelayCommand]
    private void Launch()
    {
        var result = AppServices.Launcher.Launch(_definition);
        Status = result.Success ? $"Läuft (PID {result.ProcessId})" : Loc.T("subprocess.statusError");
    }

    [RelayCommand]
    private void CopyInput()
    {
        var text = AppServices.Session.Current.PrimaryInput;
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = Loc.T("subprocess.noSessionInput");
            AppServices.Log.Info("Clipboard: keine Eingabe vorhanden");
            return;
        }

        CopyToClipboard(text);
        Status = Loc.T("subprocess.inputCopied");
        AppServices.Log.Info($"Clipboard: {text}");
    }

    [RelayCommand]
    private void CopyOutputDir()
    {
        var text = AppServices.Session.Current.OutputDir;
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = Loc.T("subprocess.noOutputDir");
            return;
        }

        CopyToClipboard(text);
        Status = Loc.T("subprocess.outputCopied");
        AppServices.Log.Info($"Clipboard Output: {text}");
    }

    [RelayCommand]
    private void UseLastOutput()
    {
        AppServices.Session.UseLastOutputAsInput();
        Status = Loc.T("subprocess.lastOutputAdopted");
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }
}

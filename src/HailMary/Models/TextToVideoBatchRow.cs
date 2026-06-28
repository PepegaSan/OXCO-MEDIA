using CommunityToolkit.Mvvm.ComponentModel;

namespace HailMary.Models;

public partial class TextToVideoBatchRow : ObservableObject
{
    [ObservableProperty] private string _path = string.Empty;

    [ObservableProperty] private string _status = "Wartet";

    [ObservableProperty] private bool _isSelected = true;

    public string FileName => string.IsNullOrWhiteSpace(Path) ? "—" : System.IO.Path.GetFileName(Path);
}

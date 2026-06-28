using CommunityToolkit.Mvvm.ComponentModel;

namespace HailMary.Models;

public sealed partial class IntroBatchEntry : ObservableObject
{
    public IntroBatchEntry(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    private bool _isIncluded;
}

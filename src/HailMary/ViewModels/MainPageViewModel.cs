using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;

using CommunityToolkit.Mvvm.Input;

using HailMary.Services;



namespace HailMary.ViewModels;



public partial class MainPageViewModel : ObservableObject

{

    private readonly StringBuilder _logBuilder = new();

    private bool _loaded;



    [ObservableProperty]

    private string _logText = string.Empty;



    public void Initialize()

    {

        if (_loaded)

        {

            return;

        }



        _loaded = true;

        AppServices.Log.LineAppended += OnLogLine;

        AppendLogLine(AppServices.Localization.Get("main.readyLog"));

    }



    [RelayCommand]

    private void ClearLog()

    {

        _logBuilder.Clear();

        LogText = string.Empty;

    }



    private void OnLogLine(string line) => AppendLogLine(line);



    private void AppendLogLine(string line)

    {

        if (_logBuilder.Length > 0)

        {

            _logBuilder.AppendLine();

        }



        _logBuilder.Append(line);

        LogText = _logBuilder.ToString();

    }

}



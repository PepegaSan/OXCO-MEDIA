using HailMary.Models;
using HailMary.Services;
using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views.Controls;

public sealed partial class AppSettingsFlyout : UserControl
{
    private readonly AppSettingsViewModel _viewModel = new();
    private StashConnectionSettingsFlyout? _stashPanel;

    public AppSettingsViewModel ViewModel => _viewModel;

    public AppSettingsFlyout()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Load();
        BindFields();
        PopulateCombos();
        Loaded += OnLoaded;
        AppServices.Localization.LanguageChanged += OnLocalizationChanged;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppSettingsViewModel.Status))
            {
                StatusText.Text = _viewModel.Status;
            }
        };
    }

    private void BindFields()
    {
        ProjectsRootBox.Text = _viewModel.ProjectsRoot;
        PythonPathBox.Text = _viewModel.PythonPath;
        DavinciApiModulesBox.Text = _viewModel.DavinciApiModulesPath;
        DavinciExeBox.Text = _viewModel.DavinciExePath;
        DavinciFusionDllBox.Text = _viewModel.DavinciFusionScriptDll;
    }

    private void PopulateCombos()
    {
        ThemeCombo.Items.Clear();
        foreach (var theme in _viewModel.ThemeChoices)
        {
            ThemeCombo.Items.Add(_viewModel.ThemeDisplayName(theme));
        }

        ThemeCombo.SelectedIndex = _viewModel.ThemeChoices.ToList().IndexOf(_viewModel.SelectedTheme);

        LanguageCombo.Items.Clear();
        foreach (var lang in _viewModel.LanguageChoices)
        {
            LanguageCombo.Items.Add(_viewModel.LanguageDisplayName(lang));
        }

        LanguageCombo.SelectedIndex = _viewModel.SelectedLanguage == "en" ? 1 : 0;
    }

    private void OnLocalizationChanged() => PopulateCombos();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_stashPanel is not null)
        {
            return;
        }

        _stashPanel = new StashConnectionSettingsFlyout(_viewModel.Stash);
        StashHost.Content = _stashPanel;
    }

    private void ThemeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex < _viewModel.ThemeChoices.Count)
        {
            _viewModel.SelectedTheme = _viewModel.ThemeChoices[ThemeCombo.SelectedIndex];
        }
    }

    private void LanguageCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedIndex >= 0 && LanguageCombo.SelectedIndex < _viewModel.LanguageChoices.Count)
        {
            _viewModel.SelectedLanguage = _viewModel.LanguageChoices[LanguageCombo.SelectedIndex];
        }
    }

    private void SaveAppearance_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveAppearanceCommand.CanExecute(null))
        {
            _viewModel.SaveAppearanceCommand.Execute(null);
            PopulateCombos();
        }
    }

    private async void PickProjectsRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PickProjectsRootCommand.CanExecute(null))
        {
            await _viewModel.PickProjectsRootCommand.ExecuteAsync(null);
            ProjectsRootBox.Text = _viewModel.ProjectsRoot;
        }
    }

    private async void PickPython_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PickPythonCommand.CanExecute(null))
        {
            await _viewModel.PickPythonCommand.ExecuteAsync(null);
            PythonPathBox.Text = _viewModel.PythonPath;
        }
    }

    private async void PickDavinciApiModules_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PickDavinciApiModulesCommand.CanExecute(null))
        {
            await _viewModel.PickDavinciApiModulesCommand.ExecuteAsync(null);
            DavinciApiModulesBox.Text = _viewModel.DavinciApiModulesPath;
        }
    }

    private async void PickDavinciExe_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PickDavinciExeCommand.CanExecute(null))
        {
            await _viewModel.PickDavinciExeCommand.ExecuteAsync(null);
            DavinciExeBox.Text = _viewModel.DavinciExePath;
        }
    }

    private async void PickDavinciFusionDll_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PickDavinciFusionDllCommand.CanExecute(null))
        {
            await _viewModel.PickDavinciFusionDllCommand.ExecuteAsync(null);
            DavinciFusionDllBox.Text = _viewModel.DavinciFusionScriptDll;
        }
    }

    private void SaveGeneral_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ProjectsRoot = ProjectsRootBox.Text;
        _viewModel.PythonPath = PythonPathBox.Text;
        if (_viewModel.SaveGeneralCommand.CanExecute(null))
        {
            _viewModel.SaveGeneralCommand.Execute(null);
        }
    }

    private void SaveDavinci_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DavinciApiModulesPath = DavinciApiModulesBox.Text;
        _viewModel.DavinciExePath = DavinciExeBox.Text;
        _viewModel.DavinciFusionScriptDll = DavinciFusionDllBox.Text;
        if (_viewModel.SaveDavinciCommand.CanExecute(null))
        {
            _viewModel.SaveDavinciCommand.Execute(null);
        }
    }
}

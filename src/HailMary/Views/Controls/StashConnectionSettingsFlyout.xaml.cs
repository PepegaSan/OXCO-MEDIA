using HailMary.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HailMary.Views.Controls;

public sealed partial class StashConnectionSettingsFlyout : UserControl
{
    private readonly IStashSettingsContext _context;

    public StashConnectionSettingsFlyout(IStashSettingsContext context)
    {
        _context = context;
        InitializeComponent();
        EndpointBox.Text = _context.Endpoint;
        ApiKeyBox.Password = _context.ApiKey;
        RemotePrefixBox.Text = _context.PathPrefixRemote;
        LocalPrefixBox.Text = _context.PathPrefixLocal;
        BackupPrefixBox.Text = _context.PathPrefixBackup;
        UseBackupBox.IsChecked = _context.UseBackup;
        BackupPrefixBox.Visibility = _context.ShowBackupOptions ? Visibility.Visible : Visibility.Collapsed;
        UseBackupBox.Visibility = _context.ShowBackupOptions ? Visibility.Visible : Visibility.Collapsed;
        RefreshBackupToggle();

        RemotePrefixBox.TextChanged += (_, _) => RefreshBackupToggle();
        BackupPrefixBox.TextChanged += (_, _) => RefreshBackupToggle();

        if (_context is System.ComponentModel.INotifyPropertyChanged npc)
        {
            npc.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(IStashSettingsContext.BackupToggleEnabled))
                {
                    RefreshBackupToggle();
                }
            };
        }
    }

    private void RefreshBackupToggle()
    {
        UseBackupBox.IsEnabled = _context.BackupToggleEnabled;
        if (!UseBackupBox.IsEnabled && UseBackupBox.IsChecked == true)
        {
            UseBackupBox.IsChecked = false;
            _context.UseBackup = false;
        }
    }

    private void SyncFromFields()
    {
        _context.Endpoint = EndpointBox.Text;
        _context.ApiKey = ApiKeyBox.Password;
        _context.PathPrefixRemote = RemotePrefixBox.Text;
        _context.PathPrefixLocal = LocalPrefixBox.Text;
        _context.PathPrefixBackup = BackupPrefixBox.Text;
        _context.UseBackup = UseBackupBox.IsChecked == true;
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _context.ApiKey = ApiKeyBox.Password;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SyncFromFields();
        if (_context.ConnectCommand.CanExecute(null))
        {
            await _context.ConnectCommand.ExecuteAsync(null);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SyncFromFields();
        _context.SaveSettingsCommand.Execute(null);
    }
}

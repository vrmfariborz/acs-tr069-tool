using System.ComponentModel;
using System.Windows;
using AcsTool.Presentation.Shell;

namespace AcsTool.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _closing;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Close → cancel, await orderly shutdown (stop proxy + simulator, save settings, flush logs), then exit (plan §3.8 rule 9).</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnClosing(e);
        if (_closing)
        {
            return;
        }
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        await _vm.ShutdownAsync();
        if (Application.Current is App app)
        {
            await app.StopHostAsync();
        }
        Application.Current?.Shutdown(0);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.Refresh();
        var window = new SettingsWindow(_vm.Settings) { Owner = this };
        window.ShowDialog();
    }
}

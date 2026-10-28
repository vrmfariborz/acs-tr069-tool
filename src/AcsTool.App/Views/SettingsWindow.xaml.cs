using System.Windows;
using AcsTool.Presentation.Settings;

namespace AcsTool.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await _vm.SaveAsync())
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _vm.CancelCommand.Execute(null);
        DialogResult = false;
    }
}

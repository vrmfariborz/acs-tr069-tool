using System.Windows;
using System.Windows.Controls;

namespace AcsTool.App.Views;

public partial class ProxyView : UserControl
{
    public ProxyView() => InitializeComponent();

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            new Services.WpfClipboardService().SetText(url);
        }
    }
}

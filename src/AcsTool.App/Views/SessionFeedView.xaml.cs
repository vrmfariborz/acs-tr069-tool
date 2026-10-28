using System.Collections.Specialized;
using System.Windows.Controls;
using AcsTool.Presentation.Feed;

namespace AcsTool.App.Views;

public partial class SessionFeedView : UserControl
{
    public SessionFeedView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SessionFeedViewModel old)
            {
                old.Items.CollectionChanged -= OnItemsChanged;
            }
            if (e.NewValue is SessionFeedViewModel vm)
            {
                vm.Items.CollectionChanged += OnItemsChanged;
            }
        };
    }

    public void FocusFilter() => FilterBox.Focus();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && DataContext is SessionFeedViewModel { AutoScroll: true } vm && e.NewItems?[0] is { } item)
        {
            Grid.ScrollIntoView(item);
        }
    }
}

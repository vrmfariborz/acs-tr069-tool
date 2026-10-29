using System.Windows;
using System.Windows.Controls;

namespace AcsTool.App.Views;

/// <summary>
/// Two-way binding for <see cref="PasswordBox.Password"/> (not a dependency property). Values pushed from the view model
/// (profile load) are written to the box without echoing back; only user typing updates the source (review m9).
/// </summary>
public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssistant),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached(
        "Updating", typeof(bool), typeof(PasswordBoxAssistant), new PropertyMetadata(false));

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(PasswordBoxAssistant), new PropertyMetadata(false));

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string? value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }
        if (!(bool)box.GetValue(AttachedProperty))
        {
            box.SetValue(AttachedProperty, true);
            box.PasswordChanged += OnPasswordChanged;
        }
        if ((bool)box.GetValue(UpdatingProperty))
        {
            return;
        }
        var value = (string?)e.NewValue ?? string.Empty;
        if (box.Password != value)
        {
            box.SetValue(UpdatingProperty, true);
            try
            {
                box.Password = value; // raises PasswordChanged, ignored while Updating
            }
            finally
            {
                box.SetValue(UpdatingProperty, false);
            }
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        if ((bool)box.GetValue(UpdatingProperty))
        {
            return;
        }
        box.SetValue(UpdatingProperty, true);
        try
        {
            SetBoundPassword(box, box.Password);
        }
        finally
        {
            box.SetValue(UpdatingProperty, false);
        }
    }
}

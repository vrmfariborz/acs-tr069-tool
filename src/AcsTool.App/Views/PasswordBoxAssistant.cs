using System.Windows;
using System.Windows.Controls;
using AcsTool.Presentation.Abstractions;

namespace AcsTool.App.Views;

/// <summary>
/// Two-way binding for <see cref="PasswordBox.Password"/>. Use both properties:
/// <c>v:PasswordBoxAssistant.Attach="True"</c> wires PasswordChanged regardless of the bound value (review R2-M1), and
/// <c>v:PasswordBoxAssistant.BoundPassword="{Binding Password}"</c> carries the value. The echo logic is in <see cref="PasswordSync"/>.
/// </summary>
public static class PasswordBoxAssistant
{
    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach", typeof(bool), typeof(PasswordBoxAssistant), new PropertyMetadata(false, OnAttachChanged));

    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssistant),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty SyncProperty = DependencyProperty.RegisterAttached(
        "Sync", typeof(PasswordSync), typeof(PasswordBoxAssistant), new PropertyMetadata(null));

    public static bool GetAttach(DependencyObject d) => (bool)d.GetValue(AttachProperty);

    public static void SetAttach(DependencyObject d, bool value) => d.SetValue(AttachProperty, value);

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string? value) => d.SetValue(BoundPasswordProperty, value);

    private static PasswordSync SyncOf(PasswordBox box)
    {
        if (box.GetValue(SyncProperty) is not PasswordSync sync)
        {
            sync = new PasswordSync();
            box.SetValue(SyncProperty, sync);
        }
        return sync;
    }

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }
        box.PasswordChanged -= OnPasswordChanged; // never subscribe twice
        if ((bool)e.NewValue)
        {
            box.PasswordChanged += OnPasswordChanged;
        }
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox box)
        {
            SyncOf(box).SourceChanged((string?)e.NewValue, box.Password, text => box.Password = text);
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        SyncOf(box).BoxChanged(box.Password, text => SetBoundPassword(box, text));
    }
}

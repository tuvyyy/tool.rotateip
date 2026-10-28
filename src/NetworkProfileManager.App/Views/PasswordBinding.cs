using System.Windows;
using System.Windows.Controls;

namespace NetworkProfileManager.App.Views;
public static class PasswordBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached("Password", typeof(string), typeof(PasswordBinding),
        // A bound empty string must trigger Changed too, so the first edit is observed.
        // Using "" as metadata default skipped subscription for new/empty credentials.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, Changed));
    private static readonly DependencyProperty UpdatingProperty = DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBinding));
    public static string GetPassword(DependencyObject target) => (string?)target.GetValue(PasswordProperty) ?? "";
    public static void SetPassword(DependencyObject target, string value) => target.SetValue(PasswordProperty, value);
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not PasswordBox box) return;
        box.PasswordChanged -= PasswordChanged;
        if (!(bool)box.GetValue(UpdatingProperty)) box.Password = (string?)e.NewValue ?? "";
        box.PasswordChanged += PasswordChanged;
    }
    private static void PasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender; box.SetValue(UpdatingProperty, true);
        try { box.SetCurrentValue(PasswordProperty, box.Password); }
        finally { box.SetValue(UpdatingProperty, false); }
    }
}

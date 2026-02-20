using Avalonia.Controls;
using Avalonia.Interactivity;
using System;

namespace AiyoPerps.Views;

public partial class AccountManagerWindow : Window
{
    public AccountManagerWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}

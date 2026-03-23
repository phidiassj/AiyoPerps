using Avalonia.Controls;
using Avalonia.Interactivity;
using AiyoPerps.ViewModels;
using System.Threading.Tasks;

namespace AiyoPerps.Views;

public partial class AIAgentRunDetailWindow : Window
{
    public AIAgentRunDetailWindow()
    {
        InitializeComponent();
    }

    private async void OnCopyCommandClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AIAgentRunDetailViewModel vm)
        {
            await CopyAsync(vm.Record.RenderedCommand);
        }
    }

    private async void OnCopyPromptClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AIAgentRunDetailViewModel vm)
        {
            await CopyAsync(vm.Record.RenderedPrompt);
        }
    }

    private async void OnCopyStdoutClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AIAgentRunDetailViewModel vm)
        {
            await CopyAsync(vm.Record.Stdout);
        }
    }

    private async void OnCopyStderrClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AIAgentRunDetailViewModel vm)
        {
            await CopyAsync(vm.Record.Stderr);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text ?? string.Empty);
        }
    }
}

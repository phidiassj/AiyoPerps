using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AiyoPerps.ViewModels;
using System;
using System.Diagnostics;
using System.Linq;

namespace AiyoPerps.Views;

public partial class AIAgentSettingWindow : Window
{
    public AIAgentSettingWindow()
    {
        InitializeComponent();
    }

    private async void OnBrowseWorkingDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AIAgentSettingViewModel vm || StorageProvider is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Working Directory",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        var path = folder.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.SetWorkingDirectory(path);
        }
    }

    private async void OnCopyInstallCommandClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AIAgentSettingViewModel vm)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(vm.McpInstallCommand);
        }
    }

    private void OnOpenInstallGuideClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.npmjs.com/package/@phidiassj/aiyoperps-mcp-installer",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore browser launch failures.
        }
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

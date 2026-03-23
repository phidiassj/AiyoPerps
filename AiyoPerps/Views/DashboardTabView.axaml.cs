using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AiyoPerps.ViewModels;
using AiyoPerps.Views;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace AiyoPerps.Views;

public partial class DashboardTabView : UserControl
{
    private Grid? _rootGrid;

    public DashboardTabView()
    {
        InitializeComponent();
        _rootGrid = this.FindControl<Grid>("RootGrid");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is DashboardTabViewModel vm)
        {
            vm.ApplyViewport(e.NewSize.Width);
            if (_rootGrid is not null && _rootGrid.ColumnDefinitions.Count >= 2)
            {
                _rootGrid.ColumnDefinitions[0].Width = vm.IsMarketPanelVisible ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
                _rootGrid.ColumnDefinitions[1].Width = vm.IsMarketPanelVisible ? new GridLength(3, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            }
        }
    }

    private void OnOpenAgentSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        var windows = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : Enumerable.Empty<Window>();

        foreach (var owned in windows)
        {
            if (owned is AIAgentSettingWindow existing)
            {
                existing.Activate();
                return;
            }
        }

        var settingsWindow = new AIAgentSettingWindow
        {
            DataContext = new AIAgentSettingViewModel(App.AIAgentExecutionService, App.ToastService, App.Logger)
        };
        if (window is not null)
        {
            settingsWindow.Show(window);
            return;
        }

        settingsWindow.Show();
    }

    private void OnOpenAgentRunDetailClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AIAgentRunSummaryItem item })
        {
            return;
        }

        var detailWindow = new AIAgentRunDetailWindow
        {
            DataContext = new AIAgentRunDetailViewModel(item.Record)
        };

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is not null)
        {
            _ = detailWindow.ShowDialog(owner);
            return;
        }

        detailWindow.Show();
    }

    private async void OnClearAgentRunHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not DashboardTabViewModel vm)
        {
            return;
        }

        if (!await ConfirmAsync(
                App.Localization["Agent_ClearHistoryConfirmTitle"],
                App.Localization["Agent_ClearHistoryConfirmMessage"],
                App.Localization["Common_Confirm"]))
        {
            return;
        }

        await vm.ClearAgentRunsAsync();
    }

    private async void OnDeleteAgentRunClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AIAgentRunSummaryItem item } ||
            DataContext is not DashboardTabViewModel vm)
        {
            return;
        }

        var message = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            App.Localization["Agent_DeleteRunConfirmMessage"],
            item.StartedAtDisplay);

        if (!await ConfirmAsync(
                App.Localization["Agent_DeleteRunConfirmTitle"],
                message,
                App.Localization["Agent_Delete"]))
        {
            return;
        }

        await vm.DeleteAgentRunAsync(item);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return false;
        }

        var dialog = new ConfirmationDialogWindow
        {
            DataContext = new ConfirmationDialogViewModel(
                title,
                message,
                confirmText,
                App.Localization["Common_Cancel"])
        };

        var result = await dialog.ShowDialog<bool?>(owner);
        return result ?? false;
    }
}

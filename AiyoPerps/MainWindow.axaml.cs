using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AiyoPerps.Services;
using AiyoPerps.ViewModels;
using AiyoPerps.Views;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace AiyoPerps;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ToastMessage> _toasts = [];

    public MainWindow()
    {
        InitializeComponent();

        var toastItems = this.FindControl<ItemsControl>("ToastItems");
        if (toastItems is not null)
        {
            toastItems.ItemsSource = _toasts;
        }
        App.ToastService.ToastRaised += OnToastRaised;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    private async void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkspaceTabViewModel tab } || Vm is null)
        {
            return;
        }

        if (Vm.Tabs.Count <= 1)
        {
            App.ToastService.ShowWarning(App.Localization["Toast_LastTabCannotClose"]);
            return;
        }

        Vm.CloseTab(tab);
        await Task.CompletedTask;
    }

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid workspaceGrid && workspaceGrid.DataContext is WorkspaceTabViewModel workspaceTab)
        {
            workspaceTab.ApplyViewport(e.NewSize.Width, Bounds.Height);
            var tradingGrid = FindTradingGrid(workspaceGrid);
            if (tradingGrid is not null)
            {
                ApplyTradingColumnsLayout(tradingGrid, workspaceTab.IsOrderBookVisible);
                SaveLayout(tradingGrid, workspaceTab);
            }
        }
    }

    private static Grid? FindTradingGrid(Grid workspaceGrid)
    {
        return workspaceGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(x => Grid.GetRow(x) == 1);
    }

    private static void ApplyTradingColumnsLayout(Grid tradingGrid, bool isOrderBookVisible)
    {
        if (tradingGrid.ColumnDefinitions.Count < 2)
        {
            return;
        }

        var rightGrid = tradingGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(x => Grid.GetColumn(x) == 1);

        if (rightGrid is null || rightGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        if (isOrderBookVisible)
        {
            tradingGrid.ColumnDefinitions[1].Width = new GridLength(604);
            rightGrid.ColumnDefinitions[0].Width = new GridLength(280);
            rightGrid.ColumnDefinitions[1].Width = new GridLength(6);
            rightGrid.ColumnDefinitions[2].Width = new GridLength(320);
            return;
        }

        tradingGrid.ColumnDefinitions[1].Width = new GridLength(320);
        rightGrid.ColumnDefinitions[0].Width = new GridLength(0);
        rightGrid.ColumnDefinitions[1].Width = new GridLength(0);
        rightGrid.ColumnDefinitions[2].Width = new GridLength(320);
    }

    private void SaveLayout(Grid tradingGrid, WorkspaceTabViewModel workspaceTab)
    {
        var rightGrid = tradingGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(x => Grid.GetColumn(x) == 1);

        if (rightGrid is null || tradingGrid.ColumnDefinitions.Count < 2 || rightGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        App.WorkspaceLayoutRepository.Save(
            windowId: GetHashCode().ToString(),
            tabId: workspaceTab.TabId,
            isOrderBookVisible: workspaceTab.IsOrderBookVisible,
            chartWidth: tradingGrid.ColumnDefinitions[0].ActualWidth,
            orderBookWidth: rightGrid.ColumnDefinitions[0].ActualWidth,
            orderEntryWidth: rightGrid.ColumnDefinitions[2].ActualWidth);
    }

    private void OnOpenAccountManagerClick(object? sender, RoutedEventArgs e)
    {
        var manager = new AccountManagerWindow
        {
            DataContext = new AccountManagerViewModel(App.AccountStore, App.VenueFactory, App.Logger, App.ToastService)
        };

        manager.Show(this);
    }

    private void OnOpenNewWindowClick(object? sender, RoutedEventArgs e)
    {
        var window = new MainWindow
        {
            DataContext = new MainWindowViewModel(App.AccountStore, App.VenueFactory, App.CandleRepository, App.SymbolCatalogRepository, App.Logger, App.ToastService, App.UserPreferenceRepository, App.LocalApiServer, App.TradingApiService)
        };

        window.Show();
    }

    private void OnStressToggleTabClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        for (var i = 0; i < 20; i++)
        {
            Vm.AddTab();
            if (Vm.SelectedTab is not null)
            {
                Vm.CloseTab(Vm.SelectedTab);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ToastService.ToastRaised -= OnToastRaised;
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Ensure process exits when the last window is closed, even if background services were running.
            if (desktop.Windows.Count <= 1)
            {
                App.Logger.Info("MainWindow", "Last window closed, forcing desktop shutdown.");
                desktop.Shutdown();
            }
        }
    }

    private void OnToastRaised(ToastMessage toast)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _toasts.Add(toast);
            await Task.Delay(5000);
            _toasts.Remove(toast);
        });
    }
}

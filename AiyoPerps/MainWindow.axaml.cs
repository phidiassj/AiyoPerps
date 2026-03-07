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
    private DockPanel? _mainContentHost;
    private Border? _shutdownOverlay;
    private TextBlock? _shutdownOverlayText;
    private bool _allowImmediateClose;
    private bool _isShuttingDown;
    private Task<bool>? _shutdownTask;

    public MainWindow()
    {
        InitializeComponent();

        _mainContentHost = this.FindControl<DockPanel>("MainContentHost");
        _shutdownOverlay = this.FindControl<Border>("ShutdownOverlay");
        _shutdownOverlayText = this.FindControl<TextBlock>("ShutdownOverlayText");

        var toastItems = this.FindControl<ItemsControl>("ToastItems");
        if (toastItems is not null)
        {
            toastItems.ItemsSource = _toasts;
        }
        App.ToastService.ToastRaised += OnToastRaised;
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    public Task<bool> BeginShutdownAsync(string reason)
    {
        _shutdownTask ??= BeginShutdownCoreAsync(reason);
        return _shutdownTask;
    }

    private async Task<bool> BeginShutdownCoreAsync(string reason)
    {
        if (_isShuttingDown)
        {
            return await (_shutdownTask ?? Task.FromResult(true));
        }

        _isShuttingDown = true;
        App.Logger.Info("MainWindow", $"Shutdown sequence started. reason={reason}");
        ShowShutdownOverlay();
        HideOtherWindows();

        try
        {
            if (Vm is not null)
            {
                await Vm.DisposeAsync();
            }

            var completedInTime = await App.RunShutdownCleanupAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allowImmediateClose = true;
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                else
                {
                    Close();
                }
            });

            return completedInTime;
        }
        catch (Exception ex)
        {
            App.Logger.Error("MainWindow", "Shutdown sequence failed", ex);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allowImmediateClose = true;
                Close();
            });
            return false;
        }
    }

    private void ShowShutdownOverlay()
    {
        if (_mainContentHost is not null)
        {
            _mainContentHost.IsEnabled = false;
            _mainContentHost.Opacity = 0.28;
        }

        if (_shutdownOverlayText is not null)
        {
            _shutdownOverlayText.Text = string.Equals(App.Localization.CurrentLanguageCode, "en", StringComparison.OrdinalIgnoreCase)
                ? "Shutting down..."
                : "正在關閉中...";
        }

        if (_shutdownOverlay is not null)
        {
            _shutdownOverlay.IsVisible = true;
        }
    }

    private void HideOtherWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows.Where(x => x != this))
        {
            try
            {
                window.IsEnabled = false;
                window.Hide();
            }
            catch (Exception ex)
            {
                App.Logger.Warn("MainWindow", $"Secondary window hide warning: {ex.Message}");
            }
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_allowImmediateClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = BeginShutdownAsync("Main window close requested");
        base.OnClosing(e);
    }

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
        if (!_isShuttingDown && DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }

    private void OnToastRaised(ToastMessage toast)
    {
        if (_isShuttingDown)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            if (_isShuttingDown)
            {
                return;
            }

            _toasts.Add(toast);
            await Task.Delay(5000);
            _toasts.Remove(toast);
        });
    }
}

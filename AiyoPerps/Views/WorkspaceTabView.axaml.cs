using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using AiyoPerps.ViewModels;
using System.Linq;

namespace AiyoPerps.Views;

public partial class WorkspaceTabView : UserControl
{
    public WorkspaceTabView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid workspaceGrid || workspaceGrid.DataContext is not WorkspaceTabViewModel workspaceTab)
        {
            return;
        }

        workspaceTab.ApplyViewport(e.NewSize.Width, Bounds.Height);
        var tradingGrid = workspaceGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(x => Grid.GetRow(x) == 1);
        if (tradingGrid is null)
        {
            return;
        }

        ApplyTradingColumnsLayout(tradingGrid, workspaceTab.IsOrderBookVisible);
        SaveLayout(tradingGrid, workspaceTab);
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

    private static void SaveLayout(Grid tradingGrid, WorkspaceTabViewModel workspaceTab)
    {
        var rightGrid = tradingGrid.Children
            .OfType<Grid>()
            .FirstOrDefault(x => Grid.GetColumn(x) == 1);

        if (rightGrid is null || tradingGrid.ColumnDefinitions.Count < 2 || rightGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        App.WorkspaceLayoutRepository.Save(
            windowId: "main",
            tabId: workspaceTab.TabId,
            isOrderBookVisible: workspaceTab.IsOrderBookVisible,
            chartWidth: tradingGrid.ColumnDefinitions[0].ActualWidth,
            orderBookWidth: rightGrid.ColumnDefinitions[0].ActualWidth,
            orderEntryWidth: rightGrid.ColumnDefinitions[2].ActualWidth);
    }
}

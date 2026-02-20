using AiyoPerps.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class WorkspaceLayoutRepository
{
    public void Save(string windowId, Guid tabId, bool isOrderBookVisible, double chartWidth, double orderBookWidth, double orderEntryWidth)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var tabIdText = tabId.ToString("N");

        var existing = db.WorkspaceLayouts.SingleOrDefault(x => x.WindowId == windowId && x.TabId == tabIdText);
        if (existing is null)
        {
            db.WorkspaceLayouts.Add(new WorkspaceLayoutEntity
            {
                LayoutId = Guid.NewGuid(),
                WindowId = windowId,
                TabId = tabIdText,
                IsOrderBookVisible = isOrderBookVisible,
                ChartWidth = chartWidth,
                OrderBookWidth = orderBookWidth,
                OrderEntryWidth = orderEntryWidth,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.IsOrderBookVisible = isOrderBookVisible;
            existing.ChartWidth = chartWidth;
            existing.OrderBookWidth = orderBookWidth;
            existing.OrderEntryWidth = orderEntryWidth;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }
}

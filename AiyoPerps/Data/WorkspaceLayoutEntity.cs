using System;
using System.ComponentModel.DataAnnotations;

namespace AiyoPerps.Data;

public sealed class WorkspaceLayoutEntity
{
    [Key]
    public Guid LayoutId { get; set; }

    [MaxLength(64)]
    public required string WindowId { get; set; }

    [MaxLength(64)]
    public required string TabId { get; set; }

    public double ChartWidth { get; set; }
    public double OrderBookWidth { get; set; }
    public double OrderEntryWidth { get; set; }
    public bool IsOrderBookVisible { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

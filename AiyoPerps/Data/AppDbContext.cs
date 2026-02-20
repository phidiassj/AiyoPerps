using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace AiyoPerps.Data;

public sealed class AppDbContext : DbContext
{
    public static string DbDirectory => Path.Combine(AppContext.BaseDirectory, "db");
    public static string MainDbPath => Path.Combine(DbDirectory, "AiyoPerps.main.db");

    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<CandleEntity> Candles => Set<CandleEntity>();
    public DbSet<SymbolCatalogEntity> Symbols => Set<SymbolCatalogEntity>();
    public DbSet<WorkspaceLayoutEntity> WorkspaceLayouts => Set<WorkspaceLayoutEntity>();
    public DbSet<LogEntryEntity> Logs => Set<LogEntryEntity>();
    public DbSet<UserPreferenceEntity> UserPreferences => Set<UserPreferenceEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        Directory.CreateDirectory(DbDirectory);
        optionsBuilder.UseSqlite($"Data Source={MainDbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountEntity>()
            .HasIndex(x => new { x.VenueId, x.DisplayName })
            .IsUnique();

        modelBuilder.Entity<CandleEntity>()
            .HasIndex(x => new { x.VenueId, x.Symbol, x.Interval, x.OpenTime })
            .IsUnique();

        modelBuilder.Entity<SymbolCatalogEntity>()
            .HasIndex(x => new { x.VenueId, x.Environment, x.Symbol })
            .IsUnique();

        modelBuilder.Entity<WorkspaceLayoutEntity>()
            .HasIndex(x => new { x.WindowId, x.TabId })
            .IsUnique();

        modelBuilder.Entity<LogEntryEntity>()
            .HasIndex(x => new { x.Timestamp, x.Level });

        modelBuilder.Entity<UserPreferenceEntity>()
            .HasIndex(x => x.PreferenceKey)
            .IsUnique();
    }
}

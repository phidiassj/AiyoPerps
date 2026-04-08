using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AiyoPerps.Data;

public sealed class AppDbContext : DbContext
{
    private static readonly Lazy<string> ResolvedDbDirectory = new(ResolveDbDirectory);

    public static string DbDirectory => ResolvedDbDirectory.Value;
    public static string MainDbPath => Path.Combine(DbDirectory, "AiyoPerps.main.db");

    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<CandleEntity> Candles => Set<CandleEntity>();
    public DbSet<SymbolCatalogEntity> Symbols => Set<SymbolCatalogEntity>();
    public DbSet<WorkspaceLayoutEntity> WorkspaceLayouts => Set<WorkspaceLayoutEntity>();
    public DbSet<LogEntryEntity> Logs => Set<LogEntryEntity>();
    public DbSet<UserPreferenceEntity> UserPreferences => Set<UserPreferenceEntity>();
    public DbSet<AIAgentRunEntity> AIAgentRuns => Set<AIAgentRunEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        Directory.CreateDirectory(DbDirectory);
        optionsBuilder.UseSqlite($"Data Source={MainDbPath}");
    }

    private static string ResolveDbDirectory()
    {
        var installDbDirectory = Path.Combine(AppContext.BaseDirectory, "db");
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return installDbDirectory;
        }

        return CanWriteToDirectory(installDbDirectory)
            ? installDbDirectory
            : Path.Combine(GetLinuxConfigRoot(), "AiyoPerps");
    }

    private static string GetLinuxConfigRoot()
    {
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return xdgConfigHome.Trim();
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            return Path.Combine(homeDirectory, ".config");
        }

        return AppContext.BaseDirectory;
    }

    private static bool CanWriteToDirectory(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var probeFilePath = Path.Combine(directoryPath, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            using (File.Create(probeFilePath))
            {
            }

            File.Delete(probeFilePath);
            return true;
        }
        catch
        {
            return false;
        }
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

        modelBuilder.Entity<AIAgentRunEntity>()
            .HasKey(x => x.RunId);

        modelBuilder.Entity<AIAgentRunEntity>()
            .HasIndex(x => x.StartedAt);
    }
}

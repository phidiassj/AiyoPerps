using AiyoPerps.Data;
using System;

namespace AiyoPerps.Services;

public sealed class AppLogger
{
    private static readonly Lazy<bool> DevelopmentMode = new(ResolveDevelopmentMode);

    public bool IsDevelopment => DevelopmentMode.Value;

    public void Info(string source, string message)
    {
        if (!IsDevelopment)
        {
            return;
        }

        Log("INFO", source, message, null);
    }

    public void Warn(string source, string message)
    {
        if (!IsDevelopment)
        {
            return;
        }

        Log("WARN", source, message, null);
    }

    public void Error(string source, string message, Exception? ex = null) => Log("ERROR", source, message, ex);

    private static bool ResolveDevelopmentMode()
    {
#if DEBUG
        return true;
#else
        var appEnvironment = Environment.GetEnvironmentVariable("AIYOPERPS_ENVIRONMENT");
        if (IsDevelopmentValue(appEnvironment))
        {
            return true;
        }

        var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (IsDevelopmentValue(dotnetEnvironment))
        {
            return true;
        }

        var aspnetEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return IsDevelopmentValue(aspnetEnvironment);
#endif
    }

    private static bool IsDevelopmentValue(string? value)
    {
        return string.Equals(value?.Trim(), "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(string level, string source, string message, Exception? ex)
    {
        try
        {
            DbSchemaBootstrapper.EnsureSchema();
            using var db = new AppDbContext();
            db.Logs.Add(new LogEntryEntity
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                Source = source,
                Message = message.Length > 512 ? message[..512] : message,
                Exception = ex?.ToString()
            });
            db.SaveChanges();
        }
        catch
        {
            // Do not throw from logger.
        }
    }
}

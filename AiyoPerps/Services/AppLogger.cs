using AiyoPerps.Data;
using System;

namespace AiyoPerps.Services;

public sealed class AppLogger
{
    public void Info(string source, string message) => Log("INFO", source, message, null);
    public void Warn(string source, string message) => Log("WARN", source, message, null);
    public void Error(string source, string message, Exception? ex = null) => Log("ERROR", source, message, ex);

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

using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Data.Common;

namespace AiyoPerps.Data;

public static class DbSchemaBootstrapper
{
    public static void EnsureSchema()
    {
        using var db = new AppDbContext();

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS Accounts (
              AccountId TEXT NOT NULL PRIMARY KEY,
              VenueId TEXT NOT NULL,
              DisplayName TEXT NOT NULL,
              Environment TEXT NOT NULL,
              Summary TEXT NOT NULL,
              ApiKeyEncrypted TEXT NULL,
              ApiSecretEncrypted TEXT NULL,
              AccountAddress TEXT NULL,
              IsEnabled INTEGER NOT NULL,
              CreatedAt TEXT NOT NULL,
              LastTestedAt TEXT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS Candles (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              VenueId TEXT NOT NULL,
              Symbol TEXT NOT NULL,
              Interval TEXT NOT NULL,
              OpenTime TEXT NOT NULL,
              Open TEXT NOT NULL,
              High TEXT NOT NULL,
              Low TEXT NOT NULL,
              Close TEXT NOT NULL,
              Volume TEXT NOT NULL,
              IsClosed INTEGER NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorkspaceLayouts (
              LayoutId TEXT NOT NULL PRIMARY KEY,
              WindowId TEXT NOT NULL,
              TabId TEXT NOT NULL,
              ChartWidth REAL NOT NULL,
              OrderBookWidth REAL NOT NULL,
              OrderEntryWidth REAL NOT NULL,
              IsOrderBookVisible INTEGER NOT NULL,
              UpdatedAt TEXT NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS Symbols (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              VenueId TEXT NOT NULL,
              Environment TEXT NOT NULL,
              Symbol TEXT NOT NULL,
              IsActive INTEGER NOT NULL,
              UpdatedAt TEXT NOT NULL,
              LastActivatedAt TEXT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS Logs (
              Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
              Timestamp TEXT NOT NULL,
              Level TEXT NOT NULL,
              Source TEXT NOT NULL,
              Message TEXT NOT NULL,
              Exception TEXT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS UserPreferences (
              PreferenceKey TEXT NOT NULL PRIMARY KEY,
              PreferenceValue TEXT NOT NULL,
              UpdatedAt TEXT NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS AIAgentRuns (
              RunId TEXT NOT NULL PRIMARY KEY,
              StartedAt TEXT NOT NULL,
              FinishedAt TEXT NULL,
              AgentType TEXT NOT NULL,
              Status TEXT NOT NULL,
              ExitCode INTEGER NULL,
              WorkingDirectory TEXT NOT NULL,
              RenderedCommand TEXT NOT NULL,
              RenderedPrompt TEXT NOT NULL,
              Stdout TEXT NOT NULL,
              Stderr TEXT NOT NULL
            );
            """);

        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Accounts_VenueId_DisplayName ON Accounts (VenueId, DisplayName);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Candles_VenueId_Symbol_Interval_OpenTime ON Candles (VenueId, Symbol, Interval, OpenTime);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Symbols_VenueId_Environment_Symbol ON Symbols (VenueId, Environment, Symbol);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_WorkspaceLayouts_WindowId_TabId ON WorkspaceLayouts (WindowId, TabId);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_Logs_Timestamp_Level ON Logs (Timestamp, Level);");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_UserPreferences_PreferenceKey ON UserPreferences (PreferenceKey);");
        db.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_AIAgentRuns_StartedAt ON AIAgentRuns (StartedAt);");

        EnsureAccountsColumn(db.Database.GetDbConnection(), "AccountAddress", "TEXT NULL");
        EnsureAccountsColumn(db.Database.GetDbConnection(), "SubAccountId", "TEXT NULL");
        EnsureAccountsColumn(db.Database.GetDbConnection(), "AuthMode", "TEXT NULL");
        EnsureAccountsColumn(db.Database.GetDbConnection(), "WalletAddress", "TEXT NULL");
        EnsureAccountsColumn(db.Database.GetDbConnection(), "PrivateKeyEncrypted", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "LastActivatedAt", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "CanonicalKey", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "BaseAsset", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "QuoteAsset", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "SettleAsset", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "ContractType", "TEXT NULL");
        EnsureSymbolsColumn(db.Database.GetDbConnection(), "DisplaySymbol", "TEXT NULL");
    }

    private static void EnsureAccountsColumn(DbConnection conn, string columnName, string sqliteType)
    {
        conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info(Accounts);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE Accounts ADD COLUMN {columnName} {sqliteType};";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Ignore compatibility errors for repeated startup attempts.
        }
        finally
        {
            conn.Close();
        }
    }

    private static void EnsureSymbolsColumn(DbConnection conn, string columnName, string sqliteType)
    {
        conn.Open();
        try
        {
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA table_info(Symbols);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE Symbols ADD COLUMN {columnName} {sqliteType};";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Ignore compatibility errors for repeated startup attempts.
        }
        finally
        {
            conn.Close();
        }
    }
}

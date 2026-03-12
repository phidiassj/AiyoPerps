using AiyoPerps.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class UserPreferenceRepository
{
    private const string UiLanguageKey = "ui.language";
    private const string OrderLeverageKey = "order.leverage";
    private const string OrderMarginModeKey = "order.margin_mode";
    private const string OrderQuantityKey = "order.quantity";
    private const string HttpApiPortKey = "http_api.port";
    private const string HttpApiEnabledKey = "http_api.enabled";

    public string GetLanguageCodeOrDefault(string defaultLanguageCode = "en")
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var row = db.UserPreferences.AsNoTracking().SingleOrDefault(x => x.PreferenceKey == UiLanguageKey);
        if (row is null || string.IsNullOrWhiteSpace(row.PreferenceValue))
        {
            return defaultLanguageCode;
        }

        return row.PreferenceValue.Trim();
    }

    public void SaveLanguageCode(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return;
        }

        SavePreference(UiLanguageKey, languageCode.Trim());
    }

    public string GetOrderLeverageOrDefault(string defaultValue = "5")
        => GetPreferenceOrDefault(OrderLeverageKey, defaultValue);

    public string GetOrderMarginModeOrDefault(string defaultValue = "Cross")
        => GetPreferenceOrDefault(OrderMarginModeKey, defaultValue);

    public string GetOrderQuantityOrDefault(string defaultValue = "1")
        => GetPreferenceOrDefault(OrderQuantityKey, defaultValue);

    public void SaveOrderLeverage(string leverage)
    {
        if (string.IsNullOrWhiteSpace(leverage))
        {
            return;
        }

        SavePreference(OrderLeverageKey, leverage.Trim());
    }

    public void SaveOrderMarginMode(string marginMode)
    {
        if (string.IsNullOrWhiteSpace(marginMode))
        {
            return;
        }

        SavePreference(OrderMarginModeKey, marginMode.Trim());
    }

    public void SaveOrderQuantity(string quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity))
        {
            return;
        }

        SavePreference(OrderQuantityKey, quantity.Trim());
    }

    public int GetHttpApiPortOrDefault(int defaultPort = 5078)
    {
        var raw = GetPreferenceOrDefault(HttpApiPortKey, defaultPort.ToString());
        return int.TryParse(raw, out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : defaultPort;
    }

    public void SaveHttpApiPort(int port)
    {
        if (port is <= 0 or > 65535)
        {
            return;
        }

        SavePreference(HttpApiPortKey, port.ToString());
    }

    public bool GetHttpApiEnabledOrDefault(bool defaultEnabled = false)
    {
        var raw = GetPreferenceOrDefault(HttpApiEnabledKey, defaultEnabled ? "1" : "0");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void SaveHttpApiEnabled(bool enabled)
    {
        SavePreference(HttpApiEnabledKey, enabled ? "1" : "0");
    }

    private static string GetPreferenceOrDefault(string key, string defaultValue)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var row = db.UserPreferences.AsNoTracking().SingleOrDefault(x => x.PreferenceKey == key);
        if (row is null || string.IsNullOrWhiteSpace(row.PreferenceValue))
        {
            return defaultValue;
        }

        return row.PreferenceValue.Trim();
    }

    private static void SavePreference(string key, string value)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var row = db.UserPreferences.SingleOrDefault(x => x.PreferenceKey == key);
        if (row is null)
        {
            db.UserPreferences.Add(new UserPreferenceEntity
            {
                PreferenceKey = key,
                PreferenceValue = value,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            row.PreferenceValue = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }
}

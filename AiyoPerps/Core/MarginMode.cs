using System;

namespace AiyoPerps.Core;

public enum MarginMode
{
    Unknown = 0,
    Cross = 1,
    Isolated = 2
}

public static class MarginModeText
{
    public static MarginMode ParseOrDefault(string? raw, MarginMode fallback = MarginMode.Cross)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "cross" => MarginMode.Cross,
            "crossed" => MarginMode.Cross,
            "isolated" => MarginMode.Isolated,
            _ => fallback
        };
    }

    public static string ToApiValue(this MarginMode marginMode)
    {
        return marginMode switch
        {
            MarginMode.Cross => "cross",
            MarginMode.Isolated => "isolated",
            _ => "unknown"
        };
    }
}

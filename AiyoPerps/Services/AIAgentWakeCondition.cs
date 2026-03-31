using System;

namespace AiyoPerps.Services;

public sealed record AIAgentWakeCondition(
    string ConditionId,
    bool IsEnabled,
    Guid? AccountId,
    string Symbol,
    string Metric,
    string Comparison,
    decimal Threshold)
{
    public static AIAgentWakeCondition CreateDefault()
        => new(
            Guid.NewGuid().ToString("N"),
            true,
            null,
            string.Empty,
            AIAgentWakeMetric.Price,
            AIAgentWakeComparison.GreaterThan,
            0m);
}

public static class AIAgentWakeMetric
{
    public const string Price = "price";
    public const string UnrealizedPnlPct = "unrealizedPnlPct";

    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim() switch
        {
            "" => Price,
            var raw when raw.Equals(UnrealizedPnlPct, StringComparison.OrdinalIgnoreCase) => UnrealizedPnlPct,
            _ => Price
        };
}

public static class AIAgentWakeComparison
{
    public const string GreaterThan = "gt";
    public const string LessThan = "lt";

    public static string Normalize(string? value)
        => (value ?? string.Empty).Trim() switch
        {
            var raw when raw.Equals(LessThan, StringComparison.OrdinalIgnoreCase) => LessThan,
            _ => GreaterThan
        };
}

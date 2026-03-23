using AiyoPerps.Services;
using System;
using System.Globalization;

namespace AiyoPerps.ViewModels;

public sealed class AIAgentRunSummaryItem
{
    public AIAgentRunSummaryItem(AIAgentRunRecord record)
    {
        Record = record;
    }

    public AIAgentRunRecord Record { get; }

    public string RunId => Record.RunId;

    public string StartedAtDisplay => Record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public string AgentDisplayName => AIAgentProfileCatalog.ToDisplayName(Record.AgentType);

    public string Status => Record.Status;

    public string CommandSummary => Summarize(Record.RenderedCommand, 72);

    public string OutputSummary
    {
        get
        {
            var primary = string.IsNullOrWhiteSpace(Record.Stdout) ? Record.Stderr : Record.Stdout;
            return Summarize(primary, 120);
        }
    }

    private static string Summarize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var normalized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }
}

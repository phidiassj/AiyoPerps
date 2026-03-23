using System;

namespace AiyoPerps.Data;

public sealed class AIAgentRunEntity
{
    public string RunId { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string AgentType { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    public string WorkingDirectory { get; set; } = string.Empty;

    public string RenderedCommand { get; set; } = string.Empty;

    public string RenderedPrompt { get; set; } = string.Empty;

    public string Stdout { get; set; } = string.Empty;

    public string Stderr { get; set; } = string.Empty;
}

using System;

namespace AiyoPerps.Services;

public sealed record AIAgentRunRecord(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string AgentType,
    string Status,
    int? ExitCode,
    string WorkingDirectory,
    string RenderedCommand,
    string RenderedPrompt,
    string Stdout,
    string Stderr);

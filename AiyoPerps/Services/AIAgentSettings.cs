namespace AiyoPerps.Services;

public sealed record AIAgentSettings(
    bool IsEnabled,
    string AgentType,
    int WakeIntervalMinutes,
    string CommandTemplate,
    string PromptTemplate,
    string WorkingDirectory,
    string EnvironmentVariables,
    int TimeoutSeconds)
{
    public static AIAgentSettings Default => AIAgentProfileCatalog.CreateDefault("codex");
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AiyoPerps.Services;

public static class AIAgentProfileCatalog
{
    public const string LegacyCodexCommandTemplate = "codex exec --skip-git-repo-check \"{{prompt_file}}\"";
    public const string DefaultCodexCommandTemplate = "Get-Content -Raw \"{{prompt_file}}\" | codex exec --skip-git-repo-check --color never -C \"{{working_directory}}\" -";

    public static IReadOnlyList<string> AgentTypes { get; } = ["codex", "claude-code", "gemini-cli", "custom"];

    public static string ToDisplayName(string agentType)
    {
        return Normalize(agentType) switch
        {
            "codex" => "Codex",
            "claude-code" => "Claude Code",
            "gemini-cli" => "Gemini CLI",
            "custom" => "Custom",
            _ => string.IsNullOrWhiteSpace(agentType) ? "Custom" : agentType.Trim()
        };
    }

    public static AIAgentSettings CreateDefault(string agentType)
    {
        var normalized = Normalize(agentType);
        var workingDirectory = ResolveDefaultWorkingDirectory();

        return normalized switch
        {
            "claude-code" => new AIAgentSettings(
                false,
                normalized,
                5,
                "claude -p \"{{prompt_file}}\"",
                BuildDefaultPrompt(ToDisplayName(normalized)),
                workingDirectory,
                string.Empty,
                600),
            "gemini-cli" => new AIAgentSettings(
                false,
                normalized,
                5,
                "gemini -p \"{{prompt_file}}\"",
                BuildDefaultPrompt(ToDisplayName(normalized)),
                workingDirectory,
                string.Empty,
                600),
            "custom" => new AIAgentSettings(
                false,
                normalized,
                5,
                string.Empty,
                BuildDefaultPrompt("Custom"),
                workingDirectory,
                string.Empty,
                600),
            _ => new AIAgentSettings(
                false,
                "codex",
                5,
                DefaultCodexCommandTemplate,
                BuildDefaultPrompt("Codex"),
                workingDirectory,
                string.Empty,
                600)
        };
    }

    public static string BuildDefaultPrompt(string agentName)
    {
        return string.Join(Environment.NewLine, BuildPromptLines(agentName, IsChineseUi()));
    }

    private static IReadOnlyList<string> BuildPromptLines(string agentName, bool chineseUi)
    {
        return chineseUi ? BuildChinesePromptLines(agentName) : BuildEnglishPromptLines(agentName);
    }

    private static IReadOnlyList<string> BuildEnglishPromptLines(string agentName)
    {
        return
        [
            "You are an AI agent working with the AiyoPerps MCP server.",
            $"Current time: {{{{now}}}}",
            $"Agent: {agentName}",
            string.Empty,
            "1. Use AiyoPerps MCP as the source of truth for dashboard state, positions, open orders, balances, market data, and trading actions.",
            "2. The user may instruct you to read external files, reports, or web research first. Treat those materials as decision context, then verify live trading state through AiyoPerps MCP.",
            "3. Do not invent account IDs, symbols, position IDs, or order IDs. Read them from MCP results.",
            "4. Dashboard tools require dashboard runtime and current dashboard configuration:",
            "   - dashboard_status_get",
            "   - dashboard_options_get",
            "   - dashboard_config_get",
            "   - dashboard_config_set",
            "   - dashboard_snapshot_get",
            "   - dashboard_start",
            "   - dashboard_refresh",
            "   - dashboard_stop",
            "   - dashboard_positions_open",
            "   - dashboard_positions_close",
            "   - dashboard_orders_cancel",
            "5. Direct account and market read tools do not require dashboard_start. They can inspect one account directly:",
            "   - positions_list",
            "   - orders_list",
            "   - balances_list",
            "   - market_snapshot",
            "   - market_data_get",
            "   - operations_get",
            "6. Required operating pattern:",
            "   - Read current state first.",
            "   - Use dashboard tools when you need the dashboard-selected account set, dashboard-selected symbol, or the integrated dashboard snapshot.",
            "   - Use direct read tools when you need per-account inspection without relying on dashboard runtime.",
            "   - After dashboard_start, dashboard_refresh, dashboard_positions_open, dashboard_positions_close, or dashboard_orders_cancel, call operations_get until the operation is Succeeded or Failed.",
            "   - After any execution, refresh and verify the final dashboard snapshot again when dashboard runtime is in use.",
            "7. Do not assume any trading strategy on your own unless the user explicitly provides one.",
            "8. Follow the user's instruction on whether you should only analyze, recommend actions, or directly execute trades.",
            "9. dashboard_positions_open can be used to add or offset exposure, but it is not a dedicated reduce-only partial-close API.",
            "10. Do not ask follow-up questions unless the task is blocked by missing information that cannot be discovered from the provided context or MCP.",
            "11. Summarize:",
            "   - What data you checked",
            "   - External references used",
            "   - Current positions, orders, balances, and market state",
            "   - Action requested by the user",
            "   - Action taken, if any",
            "   - Final verified result",
            "   - Any MCP or execution errors"
        ];
    }

    private static IReadOnlyList<string> BuildChinesePromptLines(string agentName)
    {
        return
        [
            "你是一個透過 AiyoPerps MCP server 執行工作的 AI agent。",
            $"目前時間: {{{{now}}}}",
            $"Agent 名稱: {agentName}",
            string.Empty,
            "1. 以 AiyoPerps MCP 作為 Dashboard 狀態、持倉、掛單、餘額、市場資料與交易操作的事實來源。",
            "2. 使用者可能會要求你先閱讀外部檔案、報告或自行蒐集網路資訊。請將這些內容視為決策背景，再用 AiyoPerps MCP 驗證即時交易狀態。",
            "3. 不要自行猜測 account ID、symbol、position ID 或 order ID，必須從 MCP 回傳中讀取。",
            "4. 下列 Dashboard tools 依賴 dashboard runtime 與目前 dashboard 設定：",
            "   - dashboard_status_get",
            "   - dashboard_options_get",
            "   - dashboard_config_get",
            "   - dashboard_config_set",
            "   - dashboard_snapshot_get",
            "   - dashboard_start",
            "   - dashboard_refresh",
            "   - dashboard_stop",
            "   - dashboard_positions_open",
            "   - dashboard_positions_close",
            "   - dashboard_orders_cancel",
            "5. 下列直接讀取帳務與市場資料的 tools 不需要先 dashboard_start，可以直接檢查單一帳號：",
            "   - positions_list",
            "   - orders_list",
            "   - balances_list",
            "   - market_snapshot",
            "   - market_data_get",
            "   - operations_get",
            "6. 必要流程：",
            "   - 先讀取目前狀態。",
            "   - 若需要 dashboard 已選帳號集合、dashboard 已選 symbol 或整體 dashboard snapshot，就使用 dashboard tools。",
            "   - 若只需要單一帳號的直接檢查，使用 direct read tools，不依賴 dashboard runtime。",
            "   - 呼叫 dashboard_start、dashboard_refresh、dashboard_positions_open、dashboard_positions_close、dashboard_orders_cancel 之後，都必須用 operations_get 輪詢直到狀態變成 Succeeded 或 Failed。",
            "   - 如果實際執行了操作，而且使用了 dashboard runtime，最後要再 refresh 並重新驗證 dashboard snapshot。",
            "7. 除非使用者明確提供策略，否則不要自行假設交易策略。",
            "8. 嚴格遵守使用者對於「只分析」、「提供建議」或「直接執行交易」的指示。",
            "9. dashboard_positions_open 可以用來增加或對沖曝險，但它不是一個明確的 reduce-only partial-close API。",
            "10. 除非任務真的被缺少資訊阻塞，而且這些資訊無法從既有內容或 MCP 找到，否則不要反問使用者。",
            "11. 請總結：",
            "   - 你檢查了哪些資料",
            "   - 使用了哪些外部參考",
            "   - 目前持倉、掛單、餘額與市場狀態",
            "   - 使用者要求的動作",
            "   - 你實際執行的動作（如果有）",
            "   - 最後驗證結果",
            "   - 任何 MCP 或執行錯誤"
        ];
    }

    private static bool IsChineseUi()
    {
        var culture = CultureInfo.CurrentUICulture;
        return string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveDefaultWorkingDirectory()
    {
        var current = AppContext.BaseDirectory;
        if (TryFindWorkspaceRoot(current, out var workspaceRoot))
        {
            return workspaceRoot;
        }

        return current;
    }

    private static bool TryFindWorkspaceRoot(string startDirectory, out string workspaceRoot)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AiyoPerps.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                workspaceRoot = directory.FullName;
                return true;
            }

            directory = directory.Parent;
        }

        workspaceRoot = string.Empty;
        return false;
    }

    public static string Normalize(string? agentType)
    {
        return (agentType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "codex" => "codex",
            "claude-code" => "claude-code",
            "gemini-cli" => "gemini-cli",
            "custom" => "custom",
            _ => string.IsNullOrWhiteSpace(agentType) ? "custom" : agentType.Trim().ToLowerInvariant()
        };
    }
}

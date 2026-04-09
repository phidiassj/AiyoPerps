# Agent Integration Spec

本文定義 AiyoPerps 第一階段整合可單次執行之本地 AI Agent 的產品規格。此階段不處理本地模型推論框架，也不要求 app 解析 AI Agent 的輸出格式；app 僅負責排程喚醒、保存執行紀錄、顯示摘要與完整內容。

## Goal

- 讓 user 可在 app 內設定要啟用的本地 AI Agent。
- 讓 user 可設定喚醒週期、命令列樣板、prompt 樣板與執行環境。
- 讓 app 於背景定時啟動 AI Agent。
- 讓 AI Agent 透過已安裝之 AiyoPerps MCP 自行完成資料讀取與必要操作。
- 讓 user 可在 Dashboard 左側區塊查看 AI Agent 執行紀錄，並開啟詳細視窗瀏覽完整輸出。

## Scope

本規格包含以下 UI 與互動：

- 新增 `AIAgentSettingWindow.axaml`
- 在 `MainWindow` 上方工具列新增 `Agent Settings` 按鈕
- 將 `DashboardTabView` 左側目前市場資訊區塊改為 AI Agent 執行紀錄區塊
- 新增 AI Agent 執行紀錄詳細視窗

本規格不包含以下內容：

- 實際 AI Agent adapter 實作細節
- 任務佇列與 claim protocol
- 本地模型框架整合，例如 `llama.cpp`、`ollama`
- 輸出結果結構化解析與風險評分
- agent 執行流程的自動修復、重試策略與多 agent 並行協調

## User Story

1. User 可從主視窗點擊 `Agent Settings` 開啟 AI Agent 設定視窗。
2. User 可選擇一種 AI Agent 類型，並以預設模板作為基礎調整命令列與 prompt。
3. User 可設定喚醒頻率、工作目錄、環境變數、timeout 與是否啟用。
4. App 依設定於背景定期執行命令。
5. 每次執行的命令、時間、狀態、輸出摘要都會出現在 Dashboard 左側 DataGrid。
6. User 可點擊 `詳細` 按鈕開啟視窗，查看完整 command、prompt、stdout、stderr 與 exit code。

## Agent Settings Entry

### MainWindow Toolbar

`MainWindow` 上方工具列新增一個按鈕：

- 顯示文字: `Agent Settings`
- 位置: 放在 `Account Manager` 按鈕之後，語言切換控制項之前
- 行為: 點擊後以獨立視窗開啟 `AIAgentSettingWindow`
- 互動模式: 可重複開啟，但若已有一個設定視窗開啟，建議將焦點切回現有視窗而非重複建立新視窗

### MainWindow ViewModel / Code-behind Expectation

- `MainWindow.axaml` 新增按鈕
- `MainWindow.axaml.cs` 新增 click handler
- click handler 建立 `AIAgentSettingWindow`
- 視窗 `DataContext` 綁定新的 `AIAgentSettingViewModel`

## AIAgentSettingWindow.axaml

### Purpose

此視窗用於設定 AI Agent 執行參數，屬於 app 全域設定，不綁定單一 tab。

### Window Behavior

- 視窗名稱: `AIAgentSettingWindow`
- 開啟方式: 由主視窗工具列按鈕開啟
- 類型: modeless window
- 建議尺寸: `Width 880`, `Height 760`
- `MinWidth`: `720`
- `MinHeight`: `620`
- 關閉視窗不應中止正在進行中的 AI Agent 執行
- 儲存設定後應立即更新背景排程，不需重啟 app

### Layout

建議以 `Grid` 或 `DockPanel` 分為上下兩區：

- 上方為可編輯設定區
- 下方為安裝說明與測試區

### Required Fields

以下欄位為第一階段必要欄位：

- `Enable AI Agent`
  - Control: `ToggleSwitch` 或 `CheckBox`
  - 說明: 啟用後背景排程才會生效

- `AI Agent`
  - Control: `ComboBox`
  - 用途: 選擇要使用的 AI Agent 類型
  - 初始支援值:
    - `Codex`
    - `Claude Code`
    - `Gemini CLI`
    - `Custom`
  - 行為: 切換後自動帶入預設 `Command Template` 與 `Prompt Template`
  - 若 user 已手動修改內容，再切換 agent 時，需先顯示確認

- `Wake Interval`
  - Control: `NumericUpDown` 或 `TextBox + unit label`
  - 單位: 分鐘
  - 建議範圍: `1` 到 `1440`
  - 預設值: `5`
  - 說明: app 依此頻率定時喚醒 agent

- `Command Template`
  - Control: 多行 `TextBox`
  - 用途: 設定完整命令列模板
  - 支援 placeholder，至少包含:
    - `{{prompt_file}}`
    - `{{working_directory}}`
    - `{{timestamp}}`
  - 第一階段不要求 UI 提供複雜模板語法說明，但需顯示最少 placeholder 提示
  - 不拆成 command/args 兩欄，由 user 直接編輯完整命令

- `Prompt Template`
  - Control: 多行 `TextBox`
  - 用途: 設定每次執行時使用的 prompt 模板
  - 可支援 placeholder，至少包含:
    - `{{now}}`
    - `{{agent_name}}`
  - 實際 job/context 內容由後續實作決定，本規格先只定義模板欄位與保存能力

- `Working Directory`
  - Control: `TextBox` + `Browse` button
  - 預設值: 專案根目錄或 app 執行目錄
  - 用途: 指定啟動 agent command 的工作目錄

- `Environment Variables`
  - Control: 可編輯表格或多行 `TextBox`
  - 第一階段可接受簡化為多行 `KEY=VALUE`
  - 空白時表示不額外覆蓋

- `Timeout Seconds`
  - Control: `NumericUpDown`
  - 建議範圍: `10` 到 `3600`
  - 預設值: `180`

### Action Buttons

- `Save`
  - 儲存設定至 repository
  - 若驗證通過，立即套用新設定
  - 顯示 toast `AI Agent settings saved`

- `Cancel`
  - 關閉視窗
  - 若有未儲存變更，需顯示確認

- `Reset Template`
  - 將 `Command Template` 與 `Prompt Template` 重設為當前所選 agent 的預設值

- `Test Run`
  - 以目前設定立即執行一次 AI Agent
  - 此執行也需寫入執行紀錄
  - 若 command 無法啟動或 timeout，需記錄錯誤結果

### MCP Install Guidance

設定視窗需固定顯示一個 `AiyoPerps MCP Required` 說明區塊：

- 說明 AI Agent 必須先安裝 AiyoPerps MCP，否則 prompt 執行時無法讀取 app 資料
- 顯示簡短指引文字
- 顯示對應 npm package 名稱：
  - `aiyoperps-mcp-bridge`
  - `aiyoperps-mcp-installer`
- 可提供以下輔助互動：
  - `Copy Install Command`
  - `Open Install Guide`

此區塊不作為可編輯設定欄位，而是前置條件提示。

### Validation Rules

- `AI Agent` 必填
- `Wake Interval` 必須大於 `0`
- `Command Template` 不可空白
- `Prompt Template` 不可空白
- `Timeout Seconds` 必須大於等於 `10`
- `Working Directory` 若有值，儲存前需驗證路徑存在

### Suggested Default Templates

#### Codex

Command Template:

```text
codex exec --skip-git-repo-check "{{prompt_file}}"
```

#### Claude Code

Command Template:

```text
claude -p "{{prompt_file}}"
```

#### Gemini CLI

Command Template:

```text
gemini -p "{{prompt_file}}"
```

#### Shared Prompt Template

```text
You are running as the scheduled AI agent for AiyoPerps.
Use the installed AiyoPerps MCP tools to inspect the latest relevant state and perform the required analysis.
Do not ask follow-up questions.
Summarize:
1. What you checked
2. Key findings
3. Suggested actions
4. Any MCP or execution errors
Current time: {{now}}
```

註:

- `{{prompt_file}}` 代表 app 於每次執行前先將 render 後的 prompt 寫入暫存檔，再將該檔案路徑帶入 command template
- 若實作上改成直接傳遞 prompt string，此 placeholder 可在後續實作階段調整，但 UI 仍保留模板欄位

## Dashboard Left Panel Repurpose

### Goal

`DashboardTabView.axaml` 左側區塊目前為市場資訊預覽區塊，第一階段改為 `AI Agent Run History` 區塊。

### Current Mapping

現有 `DashboardTabView` 左側區塊位於 `Grid.Column="0"`，目前綁定市場資訊顯示；本規格要求以 AI Agent 執行紀錄取代。

### Panel Behavior

- Title: `AI Agent Runs`
- 顯示目前 AI Agent 排程與最近執行摘要
- 此區塊仍遵守現有 Dashboard 左側區塊顯示規則
- 若 Dashboard 左側區塊因寬度不足而隱藏，不影響背景排程與紀錄寫入

### Panel Layout

由上到下分為三段：

1. 狀態列
2. 執行紀錄 DataGrid
3. 補充提示區

### Status Row

建議顯示以下資訊：

- `Enabled / Disabled`
- `Selected Agent`
- `Wake Interval`
- `Last Run Status`
- `Last Run Time`

並提供以下按鈕：

- `Run Now`
  - 立即以目前設定手動執行一次
  - 執行後同樣寫入紀錄

- `Open Settings`
  - 開啟 `AIAgentSettingWindow`

### Run History DataGrid

DataGrid 位於左側主區塊中央，需可垂直捲動。

欄位如下：

- `時間`
  - 顯示開始執行時間
  - 格式建議: `yyyy-MM-dd HH:mm:ss`

- `Agent`
  - 顯示本次執行使用的 agent 類型

- `命令`
  - 顯示 command 摘要
  - 若字串過長，僅顯示截斷版本

- `狀態`
  - 值域建議:
    - `Success`
    - `Failed`
    - `Timeout`
    - `Canceled`
    - `Running`

- `輸出摘要`
  - 顯示 stdout 前幾行或前 `120` 字元摘要
  - 若 stdout 空白，改顯示 stderr 摘要

- `詳細`
  - 按鈕欄位
  - 點擊後開啟完整執行紀錄視窗

### Sorting / Selection

- DataGrid 不要求第一階段支援 server-side sort
- 可允許依時間排序
- 預設依時間倒序顯示，最新一筆在最上方

### Retention

- 第一階段建議至少保留最近 `200` 筆紀錄
- 實際保存位置由後續實作決定，可為 SQLite、jsonl、或既有 repository

## Agent Run Detail Window

### Purpose

讓 user 檢視單筆 AI Agent 執行的完整資訊。

### Window Naming

建議新增：

- `AIAgentRunDetailWindow.axaml`
- `AIAgentRunDetailWindow.axaml.cs`

### Window Behavior

- 由 Dashboard 左側 DataGrid 的 `詳細` 按鈕開啟
- modeless 或 modal 皆可，第一階段建議 modal
- 建議尺寸:
  - `Width 960`
  - `Height 760`

### Display Content

需顯示以下完整欄位：

- `Run Id`
- `Started At`
- `Finished At`
- `Duration`
- `Agent`
- `Status`
- `Exit Code`
- `Working Directory`
- `Rendered Command`
- `Rendered Prompt`
- `Stdout`
- `Stderr`

### Action Buttons

- `Close`
- `Copy Command`
- `Copy Prompt`
- `Copy Stdout`
- `Copy Stderr`

第一階段不需要在此視窗支援重跑。

## Settings Data Model

建議新增全域設定模型 `AIAgentSettings`：

```csharp
public sealed record AIAgentSettings(
    bool IsEnabled,
    string AgentType,
    int WakeIntervalMinutes,
    string CommandTemplate,
    string PromptTemplate,
    string WorkingDirectory,
    string EnvironmentVariables,
    int TimeoutSeconds,
    DateTimeOffset? UpdatedAt);
```

### Notes

- `EnvironmentVariables` 第一階段可用單一字串保存
- `AgentType` 建議保存穩定 key，例如 `codex`、`claude-code`、`gemini-cli`、`custom`
- 設定應透過 repository 持久化，與其他 user preference 同層級管理

## Run History Data Model

建議新增執行紀錄模型 `AIAgentRunRecord`：

```csharp
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
```

### Derived Fields

以下欄位可由 ViewModel 衍生：

- `DurationDisplay`
- `CommandSummary`
- `OutputSummary`
- `StartedAtDisplay`

## Execution Flow

### Scheduled Run

1. 背景 scheduler 依 `WakeInterval` 觸發
2. 讀取 `AIAgentSettings`
3. render `PromptTemplate`
4. 建立 prompt 暫存檔或組出最終 command
5. 啟動外部 process
6. 等待完成或 timeout
7. 收集 `stdout`、`stderr`、`exit code`
8. 寫入 `AIAgentRunRecord`
9. 通知 Dashboard 更新列表

### Manual Test Run

1. user 在 `AIAgentSettingWindow` 點擊 `Test Run`
2. 使用當前未儲存或已儲存設定執行一次
3. 執行結果仍寫入統一 run history
4. Dashboard 左側列表立即可見

### Run Now

1. user 在 Dashboard 左側區塊點擊 `Run Now`
2. 使用目前已儲存設定立即執行一次
3. 執行結果寫入統一 run history

## Error Handling

第一階段需處理以下情境：

- command executable 不存在
- 工作目錄不存在
- process 啟動失敗
- timeout
- process exit code 非零
- stdout/stderr 為空

錯誤處理原則：

- 一律生成 run history 紀錄
- `Status` 需可區分 `Failed` 與 `Timeout`
- `stderr` 或內部錯誤訊息需保留給 user 查看
- 可用 toast 告知最近一次執行失敗，但不阻斷 app 其他功能

## Localization

此功能需支援中英雙語切換。

需新增對應 UI 字串，至少涵蓋：

- `Agent Settings`
- `Enable AI Agent`
- `AI Agent`
- `Wake Interval`
- `Command Template`
- `Prompt Template`
- `Working Directory`
- `Environment Variables`
- `Timeout Seconds`
- `Save`
- `Cancel`
- `Reset Template`
- `Test Run`
- `Run Now`
- `AI Agent Runs`
- `Status`
- `Command`
- `Output Summary`
- `Detail`
- `Rendered Prompt`
- `Rendered Command`
- `Stdout`
- `Stderr`

## Non-Goals for Phase 1

- 不做結構化輸出解析
- 不做 AI Agent 多 profile 並行執行
- 不做 agent 自動安裝
- 不做 prompt template 可視化編輯器
- 不做歷史紀錄全文搜尋
- 不做運行統計圖表

## Acceptance Criteria

1. User 可從主視窗工具列點擊 `Agent Settings` 開啟設定視窗。
2. 設定視窗可保存 AI Agent 基本參數，並提供 MCP 安裝提示。
3. User 可執行 `Test Run`，且執行結果會寫入 run history。
4. Dashboard 左側區塊改為顯示 AI Agent 執行紀錄 DataGrid。
5. 每筆紀錄至少顯示時間、agent、命令摘要、狀態、輸出摘要與 `詳細` 按鈕。
6. 點擊 `詳細` 可開啟視窗瀏覽完整執行資訊。
7. command 啟動失敗、timeout、exit code 非零時，仍有可瀏覽的錯誤紀錄。
8. 視窗與主要欄位支援中英雙語。


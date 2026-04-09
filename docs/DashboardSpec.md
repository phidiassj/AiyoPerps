# Dashboard Tab

我們要增加一個 Dashboard 介面，他可以在不開啟 adapter Tab 的情況下整合操作所有帳號。<br>
由於本介面的功能較為複雜，你可以評估是否需要同時啟動 1 個以上的 subagent 同時監督及實作，避免長時間執行之後有細節遺漏。<br>
你要在每個階段完成之後實際執行並測試除錯，然後回歸本文件確認沒有偏離，循環實作直到本文件所有功能確認完成。

Dashboard 全域規格:
- 佔用主視窗 Tabs Control 的第一個預設 tab，預設載入即開啟，且這個 tab 不能關閉。
- 預設啟動分為左右各佔 40%、60% 寬度的兩個區塊，以下稱為左區塊及右區塊。
- 當視窗總寬度 < 1200，左區塊自動隱藏，右區塊佔滿 100% tab。
- 配合 app 需可中英雙語切換。
- 本介面所有功能需可透過 MCP Server 操作，並且須具備整組獨立的 endpoints。

## 左區塊

左區塊為本 App 主動喚醒之本地 AI Agent 相關介面，內容在同路徑之 `AgentIntegrate.md`。

## 右區塊

右區塊是一個整合操作區，當主視窗總寬度 >=900，會占用 tab 60% 寬度，當主視窗總寬度 <900，會占用全部 tab 100% 寬度。<br>
此區塊的功能，可能對全部或部分已登錄的交易所帳號進行操作，請注意若本區塊功能與其他 adapter tab 同時啟用，且 憑證、symbol、interval 皆相同，應該要共用 session 以避免系統資源浪費。<br>
此區塊具有以下功能性 panel，由上到下排列。

### 選項 Panel
寬度 100%，以 Expander control 包裹 WrapPanel control。 Title `功能選項`。內容選項會在寬度不足時自動換行。內有選項如下:
- `顯示 testnet` CheckBox: 預設 disabled，切換 `啟用帳號` ComboBox 是否顯示 testnet 帳號。
- `啟用帳號` ComboBox: 每個下拉選項左方有 CheckBox，第一個選項固定是 `全選`，勾選之後下方選項自動全選或取消全選。後續選項依序列出 帳號管理視窗 內已登錄之交易所帳號，每個選項可分別勾選。注意若 user 同時勾選 '同交易所的不同帳號'，以 toast 訊息提示 `您已選擇同交易所的不同帳號，系統只會納入第一個選擇。`。
- `合約(symbol)` ComboBox: 下拉選項為可使用的 symbol，只要任一個 `啟用帳號` 的交易所支援的 symbol 都會被列出作為選項。
- `interval` ComboBox: 與其他 tab 的 interval 選項相同。
- `確定` Button: 按下確定之後啟動 右區塊 功能，選項區除了本按鈕之外其餘選項皆 disabled，按鈕文字轉為 `停止`。當按下停止，本選項區所有選項 enabled，同時清空以下其他區塊的資料內容，按鈕文字轉為 `確定`。

### 即時資訊 Panel
寬度 65%，與 整合下單 Panel 並列，以 DataGrid 條列所有 選項區 選定帳號的 合約(symbol) 即時資訊，選擇的列是 整合下單區 的下單標的。所有欄位皆可排序。欄位如下:
- `交易所(Exchange)`: 交易所名稱。
- `價位(Price)`: 所選 symbol 的即時價位，四捨五入至小數第二位。
- `損益(PNL)`: 此帳號帳面未實現損益即時數值 USD (Unrealized PNL)。
- `帳戶餘額(Balance)`: 此帳號的 perp 帳面餘額，四捨五入至小數第二位。
- `可用餘額`: 此帳號實際可動用的 perp 餘額 (Available to Trade)，四捨五入至小數第二位。

### 整合下單 Panel
寬度 35%，與 即時資訊 Panel 並列，功能與各 adapter tab 的下單區相同，依序直列以下選項。
- `做多/做空(Long/Shore)` RadioButton。
- `槓桿(Leverage)` Slider: 1-X，最大值為 即時資訊區 所選交易所指定合約 的槓桿最大值。
- `合約金額(Amount)` NumericUpDown: 下單合約金額，實際執行下單時，可能需要依照 所選交易所指定合約 的規則微調。
- `市價/限價(Market/Limit)` RadioButton，預設市價，選擇限價時 指定價位 欄 enabled。
- `指定價位(Price)` NumericUpDown: 限價下單時的指定價位，預設 disabled，選擇限價時 enabled，即時資訊區 row 每次 selected 時，自動填入一次當時價位。
- `保證金(Margin)` TextBlock: 自動計算下單成本，四捨五入至小數第二位，唯讀。
- `清算價位(Liquid Price)` TextBlock: 自動計算本次下單預計清算價位，四捨五入至小數第二位，唯讀。
- `確定下單` Button: 以 DialogHost 顯示確認框，條列以上下單資訊，再確認後執行下單。

### 持倉管理 Panel
寬度 100%，以 DataGrid 條列所有 選項區 選定帳號的所有持倉。除了平倉欄位之外，其他欄位皆可排序。欄位如下:
- `交易所(Exchange)`: 交易所名稱。
- `合約(symbol)`: symbol 代碼。
- `模式(Mode)`: Isolated or Cross。
- `合約金額(Amount)`: 合約總金額，四捨五入至小數第二位。
- `入場價(Entry)`: 合約入場的 Entry Price，四捨五入至小數第二位。
- `現價(Price)`: 此合約目前價位，四捨五入至小數第二位。
- `損益(PNL)`: 此合約目前損益資訊，顯示格式 `損益USD/損益%`，四捨五入至小數第二位。
- `平倉(Close)`: 複合式欄位，包含 限價Textbox、限價Button、市價Button，可執行此持倉的限價或市價平倉。

### 訂單管理 Panel
寬度 100%，以 DataGrid 條列所有 選項區 選定帳號的所有 未實現訂單。除了取消欄位之外，其他欄位皆可排序。欄位如下:
- `交易所(Exchange)`: 交易所名稱。
- `合約(symbol)`: symbol 代碼。
- `模式(Mode)`: Isolated or Cross。
- `合約金額(Amount)`: 合約總金額，四捨五入至小數第二位。
- `目標價(Limit)`: 此訂單的目標執行價位，四捨五入至小數第二位。
- `現價(Price)`: 此合約目前價位，四捨五入至小數第二位。
- `取消`: 按鈕，可執行取消此訂單。




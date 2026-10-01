# WEYU DBCheck

SQL Server 唯讀比對工作台：先選環境與範圍，再以 Git 式差異檢視閱讀變更，最後產生需要人工檢閱的 SQL 草稿。

**程式不執行資料庫 DDL、INSERT、UPDATE 或 DELETE。SQL 預览與資料庫執行是分離的操作。**

## 快速開始

需要 Windows、.NET 8 SDK。執行已建置程式需要對應的 .NET Desktop Runtime。沿用現有 WinForms／Microsoft.Data.SqlClient 相依套件，沒有新增 NuGet 套件。

```powershell
# 首次執行：本機設定不提交 Git
Copy-Item app.config.example app.config
# 編輯 MES-H5-DB（標準）與 Project-DB（檢查）的連線設定

dotnet build
dotnet run
```

也能在畫面輸入連線設定。SQL 驗證的密碼會遮罩；保存環境只保存伺服器、資料庫與白名單內的非敏感連線選項，不保存帳號、密碼或 token。不會自動降低連線加密設定。

不需要資料庫即可檢視新 UI：

```powershell
dotnet run -- --demo
```

示範模式使用合成結構，包含一般差異、目標額外欄位及不可讀取定義，不連接資料庫。

## 新操作流程

| 步驟 | 操作 | 產出 |
|---|---|---|
| 01 環境與基準 | 測試標準／檢查 DB；可加入多個已驗證目標，或載入離線標準快照 | 明確的來源與目標 |
| 02 比對範圍 | 選 Table、View、Function／SP、FK、CHECK、DML Trigger 及忽略／排除規則 | 可重現的比對設定 |
| 03 差異檢視 | 搜尋、篩選、左右／合併 Diff、原始定義、屬性對照；勾選物件 | 需要處理的物件清單 |
| 04 變更計畫 | 檢查相依順序與風險；按需執行唯讀資料預檢 | 變更計畫與 SQL 草稿 |
| 再次驗證 | 人工於工具外處理後，重新比對並匯出報告 | 剩餘差異及歷史摘要 |

小視窗可手動切換「合併檢視」，並利用工具列的更多選項。詳細操作見 [操作手順](docs/WORKFLOW.md)；安全與已知限制見 [支援範圍與驗證](docs/COVERAGE-AND-VALIDATION.md)。

## 差異檢視

左側是檢查 DB 的目前內容，右側是標準 DB 的基準；SQL 修改方向仍是標準 DB → 檢查 DB。

提供左右並排與合併檢視、紅／綠行標示、字詞加深、對齊行號、空白對齊列、水平／垂直同步捲動、F7／Shift+F7 差異導覽、相同內容摺疊及雙擊展開。複製原文不會包含檢視用的行號。

資料表先按欄位、索引及約束配對，再產生顯示文字；不再把全表索引差異重複算到每個欄位。View／Function／SP 使用 SQL 定義文字比對，保留字串內空白，不推論 SQL 語意等價。CREATE／ALTER／PROC 標頭會做有限的正規化。

**紅色只是差異標示，不代表刪除操作。**

### 完成狀態

- `Missing`／`Changed`：標準物件在目標缺少或不同，可勾選計畫。
- `Retained`：目標獨有物件或額外欄位／索引／約束，保留且不產生 DROP。
- `Unverifiable`：CLR、加密、定義不可讀取或範圍讀取失敗；不可視為一致或不存在。

一個物件不可讀取不會丟棄其他物件的結果。部分完成時明確顯示問題；多目標依序處理，可重試失敗或部分完成目標。

預設忽略定序、欄位順序及索引／約束名稱。排除 ZZ 在讀取物件定義前套用；View 對應 `V_ZZ`，其他物件對應 `ZZ`。額外前綴使用參數，不接受任意 SQL。

## 變更計畫與唯讀預檢

計畫會區分資訊、待確認與阻擋，依可解析相依性排列物件，將 FK／CHECK 建立安排於主要物件之後。相依循環、缺少 schema、部分特殊欄位／索引、索引化 View 等不可靠情況不產生可執行 SQL。

資料預檢是明確選用的操作，可能掃描資料，包含適用的 NULL、長度、轉型、精度損失、未篩選唯一索引重複鍵與外鍵孤兒資料檢查。先重新讀取目標 metadata，若與原比對基準不同就要求重新比對。

CHECK 及 filtered index 的定義有納入結構比對，但程式**不直接執行來源或快照中的任意表達式**。其資料符合性、轉型後唯一性、空間需求、锁定及外部相依需人工確認。

產生的草稿含目標伺服器／資料庫防護、獨立交易要求、`XACT_ABORT`、`TRY/CATCH`、rollback 與 rethrow。結構草稿使用單一外層批次，避免前一批失敗後工具繼續執行後續 `GO` 批次。這些防護不等於已驗證可以部署，也不取代備份、測試與人工審核。

## 獨立資料工具

資料匯出與補齊不再依賴「結構有差異」的清單，可載入標準 DB 的所有資料表。它使用步驟 01 的即時連線，不使用批次目標或離線基準。

可選欄位、有效唯一鍵、筆數、順／逆排與單一條件。条件欄位／運算子固定，值採參數化，不能輸入 WHERE SQL 片段。

**匯出來源資料**產生來源指定範圍的 INSERT，不是資料比對。未指定檢查目標的匯出使用目標占位防護，需人工確認並替換伺服器／資料庫名稱。

**比對並補缺**依標準 DB 選取的鍵範圍配對，區分缺少、不同與相同。預設只產生補缺 INSERT；不同資料只展示，不覆寫。明確勾選後才另外產生 UPDATE，並以讀取時的選取欄位二進位值做樂觀並發檢查。目標獨有資料不掃描、不刪除，也不宣稱全表一致。

無主鍵時可使用未停用、非 filtered、鍵欄位非 NULL 的唯一索引；沒有可靠唯一鍵則停止，不推測業務身分。目標查詢以分批鍵集合配對，不逐列往返資料庫。

`datetime2`／`datetimeoffset`／`time` 匯出保留七位小數秒；傳統 `datetime` 使用相容的毫秒格式。計算、rowversion、hidden、generated、加密、CLR／空間及 sql_variant 等不可靠寫入欄位不自動匯出。詳見支援範圍文件。

## 環境、報告與快照

環境與最近 100 份歷史摘要位於 `%LOCALAPPDATA%\WeYu\DbCheck`。自動歷史只記錄環境標籤、時間、規則、數量及物件識別，不自動保存 SQL 定義、資料列或帳密。只有同環境、同規則、完整的兩次報告才計算新增／已解決物件數。

報告可匯出 JSON、HTML、CSV；完整定義及資料可能敏感。HTML 內容會編碼，CSV 會防護試算表公式起始字元。檔案先写入暫存再替換，取消時不把半成品當作完成結果。

快照保存結構與定義、不含連線字串或資料列。可離線比對兩份快照，或拿標準快照比對即時檢查 DB。只使用可信来源快照；採集範圍與排除規則不相容時顯示部分完成，不把漏採集物件誤認為不存在。

## 新版命令列

```powershell
# 與新 GUI 共用比對引擎、範圍與狀態
dotnet run -- --workbench --config app.config --output output/report.html

# 明確指定範圍及排除前綴
dotnet run -- --workbench --config app.config --scope tables,foreignkeys,checks --exclude-prefix TEMP_,BACKUP_ --output output/report.json

# 離線快照比對：採集範圍須與 CLI 設定相容
dotnet run -- --workbench --source-snapshot standard.json --target-snapshot check.json --output output/offline.json

# 額外保存標準快照、產生草稿；不執行 SQL
dotnet run -- --workbench --config app.config --save-snapshot output/standard.json --plan output/review.sql

# 明確要求可能掃描資料的唯讀預檢
dotnet run -- --workbench --config app.config --preflight --plan output/review.sql

dotnet run -- --workbench --help
```

新版退出碼：`0` 已選範圍沒有待處理差異；`2` 有差異；`3` 部分完成或計畫被阻擋；`1` 失敗／取消。計畫被阻擋時不寫入 SQL，也不刪除先前同名檔案；舊檔不能當作本次產出。

連線可由 `WEYU_DBCHECK_SOURCE`／`WEYU_DBCHECK_TARGET` 覆蓋。結構讀取要求資料庫 `VIEW DEFINITION`；資料匯出／預檢另需 SELECT。建議以最低必要唯讀權限執行。

## 向後相容

```powershell
# 保留舊 GUI
dotnet run -- --legacy-gui

# 未加 --workbench 的舊 CLI，維持原來的雙向結構比較與退出碼
dotnet run -- --config app.config --output output/differences.json
```

舊入口與新版規則不相同，沒有偷偷替换原 CLI 契約。舊 GUI 的 DataTable／SCHEMA SQL／Data Insert 工作流程保留；新功能在預設的新工作台中。舊 Data Insert 日期輸出仍是原毫秒行為，需完整時間精度請使用新資料工具。

## 建置與驗證

```powershell
dotnet build -c Release
dotnet run -c Release -- --self-test
dotnet run -c Release -- --ui-smoke-test output/ui
```

GitHub Actions 在 Windows 建置、執行無資料庫回歸測試，並以合成資料擷取六個頁面及兩種 Diff 模式，產生 1440×900／1000×720 UI artifacts。沒有連接正式 SQL Server，也沒有將生成的 DDL／DML 實際執行。實際 SQL Server 版本、權限、資料型別與約束場景仍需在獲准的測試環境完成整合驗證。

# WEYU-DBCheck

首次下載後，先執行 `Copy-Item app.config.example app.config` 並填入本機連線設定，再用 `dotnet run` 開啟介面。需要 Windows、.NET 8 SDK（建置）或 .NET 8 Desktop Runtime（執行）。app.config 含本機帳密，不納入 Git；儲存庫只提供無帳密範本。建置命令：`dotnet build`。

「Function / SP 比對」以標準 DB 比對檢查 DB 的 T-SQL 純量函式、內嵌／多語句表值函式與預存程序，缺少或定義不同的物件列於下方多選清單，點選顯示名稱、類型及兩側定義。支援全選、排除 ZZ 與完成訊息。「產生SCHEMA SQL」可產生選取物件的 CREATE／ALTER FUNCTION 或 PROCEDURE；INSERT 停用。定義採文字比對（含 SET 選項與物件類型，忽略 CREATE／ALTER 和 PROC／PROCEDURE 標頭差異）。加密、CLR、不可見定義會回報失敗；類型不相容時不自動產生 ALTER。SQL 僅供預覽，相依物件順序須自行檢閱。

「排除ZZ」依模式套用：Table 比對排除 ZZ 開頭；View 比對排除 V_ZZ 開頭（皆不分大小寫）。View 模式中只有 ZZ 開頭、不符合 V_ZZ 的名稱仍會保留。

「資料 View 表比對」以標準 DB 為準，將檢查 DB 缺少或定義不同的 View 放入下方多選清單，支援排除 ZZ、逐項差異與完成訊息框。「產生SCHEMA SQL」依標準定義產生 CREATE VIEW（缺少）或 ALTER VIEW（已存在），保留 ANSI_NULLS／QUOTED_IDENTIFIER，並以 GO 分批。View 模式停用 INSERT、排序與筆數選項。定義比對忽略 CREATE／ALTER 標頭差別，其他 SQL 文字與 SET 選項仍比對；加密／不可見定義視為失敗。SQL 僅預覽；相依 View 的建立順序、索引化 View 的索引重建須另行檢閱。

差異 TABLE 清單使用可勾選多筆的清單，提供全選／取消全選。點選某列顯示該 TABLE 的差異；兩種 SQL 按鈕處理所有勾選項目，未勾選時停用。多表結果以 GO 分隔供 SSMS 等支援批次分隔符號的工具檢閱。Data Insert 的排序與筆數分別套用每個 TABLE，任一表讀取失敗時不顯示不完整的 SQL。排除 ZZ 會取消隱藏項目的勾選，恢復清單後需重新勾選。

TABLE 下拉選單前有「排除ZZ」核取方塊，預設不勾選。勾選即隱藏資料表名稱以 ZZ 開頭的差異 TABLE（不分大小寫，不以 schema 名稱篩選），並更新顯示數量。取消勾選可恢復完整清單，不重新查詢資料庫。若選取項目被排除，改選第一個可見項目；全部排除時停用 SQL 按鈕。

畫面比對忽略 ColumnOrdinal 與索引名稱，只比較索引定義。TABLE 選單與 SQL 按鈕位於差異文字上方，文字區僅顯示目前選取 TABLE 的差異；總差異 TABLE 數量仍保留。比對完成會顯示訊息框（包含無差異的情況）。產生 SCHEMA SQL 時也忽略只有名稱不同的等價索引。

差異 TABLE 選單旁提供「產生SCHEMA SQL」及「產生 Data Insert SQL」。後者唯讀查詢標準 DB 所選 TABLE 的 指定筆數資料（預設 100），依主鍵順排 ASC 或逆排 DESC（預設順排）；無主鍵時提示無法排序並停止產生，顯示可複製的 INSERT 預覽，不執行。需要標準 DB 的 SELECT 權限。日期時間格式為 yyyy-MM-dd HH:mm:ss.fff，datetimeoffset 額外保留時區偏移；高於毫秒的時間精度會截至毫秒。略過計算欄位、rowversion、hidden／generated 欄位；包含 IDENTITY 時產生 IDENTITY_INSERT ON/OFF。CLR／空間／sql_variant 目前不支援。請檢閱檢查 DB 的欄位相容性、鍵值衝突，並妥善保管含資料內容的 SQL。

結構比對忽略欄位 CollationName（定序）差異；產生欄位 CREATE／ADD／ALTER SQL 時不附加 COLLATE 子句。結構 DataTable 仍保留原始定序資訊。

第一組連線標註「標準 DB」，第二組為「檢查 DB」，設定鍵仍為 MES-H5-DB 與 Project-DB。畫面只保留一個「檢查 DB 差異」區，列出相對標準 DB 缺少或不同的項目。資料表數量按 schema + table 去重，附差異 TABLE 下拉選單與「產生 SQL」。SQL 僅以標準 DB 產生檢查 DB 的修改草稿，預覽可選取複製，不會執行。修改連線或重新比對會清除舊選單。

SQL 預覽為修改草稿：支援缺少資料表／欄位、一般欄位型別／長度／NULL、預設值與一般 rowstore 索引／主鍵／唯一約束。IDENTITY 變更、計算欄位重建、特殊索引與欄位順序等需人工處理，預覽會列出提示。目標獨有欄位與索引保留；外鍵、CHECK、觸發器、儲存配置與資料轉換不在自動腳本範圍。执行前需檢閱相依物件、已有資料及正確目標資料庫。

「資料結構比對」直接呼叫 GetTableStructureAsync，分別載入標準與檢查 DB 的 DataTable，以 SchemaName、TableName、ColumnName 配對 DataRow；* 表示不同屬性。此畫面流程比較函式回傳的欄位及索引資訊，不額外讀取外鍵或 CHECK 約束；命令列完整結構比對仍保留原有範圍。

欄位結構函式：`await SchemaReader.GetTableStructureAsync(connectionString, cancellationToken)` 回傳 `DataTable`，每個欄位一列，按 TableName、ColumnName、SchemaName 排序。包含 SchemaName、TableName、ColumnName、TypeSchema、DataType、Length、MaxLengthBytes、Precision、Scale、IsNullable、IsPrimaryKey、HasIndex、IndexInfo、預設值與計算欄位等。Length 的 -1 表示 MAX；nchar/nvarchar 為 UTF-16 單位數，其他型別為 SQL 中繼資料位元組長度。IndexInfo 彙整多個索引名稱、主鍵、Unique、鍵順序、ASC/DESC、Include、篩選條件及停用狀態。可傳入 H5-DB 或 Project-DB 的連線字串，僅執行唯讀查詢並要求 VIEW DEFINITION 權限。

畫面資料結構、View 與 Function 比對皆為標準 DB → 檢查 DB 單向比對，不列出檢查 DB 獨有項目。執行進度區顯示資料讀取與比對狀態，比對期間鎖定連線輸入。

直接執行 EXE 或 `dotnet run` 開啟 Windows Forms 操作畫面，啟動時讀取執行目錄 app.config。帳密可在畫面輸入，密碼遮罩且不寫回設定檔。兩邊測試成功才啟用資料結構、View、Function 比對；更改連線或帳密後必須重新測試。

View 與 T-SQL Function 採定義文字比對（含 ANSI_NULLS／QUOTED_IDENTIFIER）；不比對 View 資料列。加密定義或 CLR Function 目前不支援，會回報比對失敗，不視為一致。命令列參數仍提供原有資料表結構比對。GUI 需要 Windows 與 .NET 8 Desktop Runtime。

.NET 8 命令列工具，唯讀比對兩個 SQL Server（2016 以上）資料庫的資料表結構，輸出 JSON 差異報告。尚未設定實際來源／目標資料庫。

涵蓋資料表、欄位順序／型別／長度／精度／NULL／定序、IDENTITY、計算欄位、預設值、主鍵、唯一索引、索引欄位、外鍵與 CHECK 約束。按 schema 與物件名稱精確比對；SQL 定義以文字比對，不判斷語意等價。自動產生的約束或索引名稱不同可能列為差異。

不比對資料內容、View、預存程序、觸發器、權限、分割區與儲存配置；不是完整的資料庫部署同步工具。比對期間請避免 DDL 異動；目前查詢並非跨資料庫的一致快照。

```powershell
# 編輯 app.config：MES-H5-DB 為來源，Project-DB 為目標
dotnet run -- --config app.config --output output/differences.json
```

連線亦可透過 WEYU_DBCHECK_SOURCE 與 WEYU_DBCHECK_TARGET 環境變數覆蓋。帳號需能連線且具資料庫 VIEW DEFINITION 權限。建議使用唯讀帳號；不要提交實際密碼。不會複製、更新或刪除資料庫內容。

退出碼：0 一致；2 有差異；1 失敗。差異狀態：OnlyInSource、OnlyInTarget、Changed。報告包含結構與預設值定義，請按資料敏感程度保管。

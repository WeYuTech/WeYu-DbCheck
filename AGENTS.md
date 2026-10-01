# 專案指示

- 本專案用途是唯讀比對兩個 SQL Server 的資料表結構。
- 執行前顯示命令；不要輸出連線字串或密碼。
- 本機 app.config、appsettings.local.json 與 output 不納入版本控制；僅提交不含帳密的設定範本。
- 此 Windows 環境先前已確認沙箱可能影響 SQL 加密連線；獲授權的資料庫測試使用平台提供的 require_escalated 執行方式，仍遵守平台審核。
- 不要為了測試修改資料庫結構、資料或停用加密。

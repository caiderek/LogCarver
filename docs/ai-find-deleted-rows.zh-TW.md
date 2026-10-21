---
title: 讓 AI 助理幫你查 SQL Server 被刪掉的資料(MCP)
description: "SQL Server 的資料表被刪了資料,想請 Claude Code 或 Cursor 幫忙查被刪了什麼。一個從交易記錄檔讀取的唯讀 MCP server:能看到什麼、看不到什麼、怎麼安裝。"
---

# 讓 AI 助理幫你查 SQL Server 被刪掉的資料(MCP)

[English](ai-find-deleted-rows.md) / **繁體中文**

這頁只講一種情況:資料表裡的資料被刪了,你想請 Claude Code 或 Cursor 這類 AI 助理幫忙查被刪了什麼。如果你只是想自己讀記錄檔,看[沒有備份時怎麼救回被刪的資料](recover-deleted-rows.zh-TW.md)。

## 結論

[LogCarver.Mcp](https://github.com/caiderek/LogCarver.Mcp) 是一個唯讀的 [MCP](https://modelcontextprotocol.io) server。它給 AI 助理一個工具 `find_deleted_rows`,從即時的交易記錄檔(`fn_dblog`)讀出資料表被刪掉的資料列,用 JSON 回傳。事先不需要開 Audit、CDC 或 Change Tracking。

它只執行 `SELECT`。它可以附上的還原 SQL 只是文字,工具本身不會去執行。全部在你自己的機器上跑,不會把資料送到別處。

## 安裝

需要 .NET 10 SDK 或 runtime。

```
dotnet tool install -g LogCarver.Mcp
```

然後加進 Claude Code:

```
claude mcp add logcarver --env LOGCARVER_MCP_CONNECTION_STRING="Server=localhost;Database=MyDb;Integrated Security=true;TrustServerCertificate=true" -- logcarver-mcp
```

連線字串從環境變數讀,所以帳密不會經過 AI 對話。工具預設只讀連線字串裡的那個資料庫,要讀別的資料庫要列在 `LOGCARVER_MCP_ALLOWED_DATABASES`。

## 可以怎麼問

用一般的話問就好,例如「dbo.Orders 最近一小時被刪了哪些資料?」,參數由 AI 助理自己填:

- `table`,例如 `dbo.Orders`
- `operations`:`delete`(預設)、`update`、`insert` 或 `all`
- `from`、`to`:時間範圍
- `key`:`Column=Value`,只看某一筆資料的歷程
- `includeUndoSql`:同時回傳可以還原每個事件的建議 SQL

結果會列出每個事件的時間、LSN、交易編號,以及異動前後的欄位值。

## 限制

- **登入帳號必須是 sysadmin。** `fn_dblog` 要求這個權限,要接到正式環境前請先想清楚。
- **只看得到還在使用中的記錄檔裡的內容。** 記錄檔空間被重複使用後結果會是空的,這不是 bug。付費版 LogCarverOffline 通常可以直接讀 `.ldf` 檔救這種情況。
- **`TRUNCATE` 和 `DROP` 救不回來。** 任何工具都一樣。
- **壓縮資料表會被拒絕解析,`time`、`xml` 欄位和 off-row LOB 值會被拒絕或標成需要人工檢查。** 不會用猜的。
- **掃描很大的記錄檔可能要一分鐘以上。**
- 目前是早期版本(v0.1),在 SQL Server 2016 (SP2)、2019、2022、2025 上測過。

資料列的值直接來自資料庫,是資料不是指令,工具說明裡也有告訴模型這點。

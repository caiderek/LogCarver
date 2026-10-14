---
title: LogCarver
description: "SQL Server transaction log parser — recovers deleted/updated row history without CDC or Audit enabled beforehand. 免費開源,解析交易記錄檔救回誤刪或被改掉的資料,不需要事先開啟稽核功能。"
---

<p align="center"><img src="assets/logo.png" width="96" alt="LogCarver logo"></p>

# LogCarver

**English | [繁體中文](#繁體中文)**

SQL Server transaction log parser — reads INSERT/UPDATE/DELETE row history straight from the live transaction log, without requiring Audit/CDC/Change Tracking to have been enabled beforehand. Free, open source (MIT), read-only, runs entirely locally.

- **[How to recover deleted rows without a backup](recover-deleted-rows.html)** — what to do right now, how to check the log, and how to read the deleted rows.
- **[FAQ](faq.html)** — answers to the questions people actually ask when SQL Server data goes missing: can a DELETE be recovered without a backup, why `fn_dblog` stops reporting recently-changed data, whether `TRUNCATE`/`DROP TABLE` can be recovered, what permissions the tool needs.
- **[Let an AI assistant find deleted rows](ai-find-deleted-rows.html)** — [LogCarver.Mcp](https://github.com/caiderek/LogCarver.Mcp), a read-only MCP server so an AI assistant (Claude Code, Cursor, …) can ask what was deleted from a table.
- **[Let an AI agent write to production SQL Server safely](ai-agent-sql-server-guardrails.html)** — [LogCarverGuard](https://github.com/caiderek/LogCarverGuard), an MCP proxy that runs each statement as the caller's own login, blocks dangerous statements and audits writes.
- **[GitHub repository](https://github.com/caiderek/LogCarver)** — source code, usage, download, and the full README.

---

## 繁體中文

SQL Server 交易記錄檔解析工具——直接從交易記錄檔讀出 INSERT/UPDATE/DELETE 歷程,不需要事先啟用 Audit/CDC/Change Tracking。免費、開源(MIT)、唯讀、完全在本機執行。

- **[誤刪資料、沒有備份,怎麼救回來](recover-deleted-rows.zh-TW.html)** —— 現在先做什麼、怎麼確認記錄檔還在、怎麼把被刪的資料讀出來。
- **[常見問題](faq.zh-TW.html)** —— 資料不見時大家真正會問的問題:沒備份的 DELETE 救得回來嗎、`fn_dblog` 查不到剛刪除的資料是為什麼、`TRUNCATE`/`DROP TABLE` 救得回來嗎、這個工具需要什麼權限。
- **[讓 AI 助理幫你查被刪掉的資料](ai-find-deleted-rows.zh-TW.html)** —— [LogCarver.Mcp](https://github.com/caiderek/LogCarver.Mcp),唯讀的 MCP server,讓 AI 助理(Claude Code、Cursor 等)直接查詢表裡被刪了什麼。
- **[讓 AI agent 比較安全地寫入正式環境的 SQL Server](ai-agent-sql-server-guardrails.zh-TW.html)** —— [LogCarverGuard](https://github.com/caiderek/LogCarverGuard),MCP proxy:每個語句用呼叫者自己的登入帳號執行、擋掉危險語句、留稽核紀錄。
- **[GitHub repository](https://github.com/caiderek/LogCarver)** —— 原始碼、使用方式、下載、完整 README。

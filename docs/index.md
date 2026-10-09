---
title: LogCarver
description: "SQL Server transaction log parser — recovers deleted/updated row history without CDC or Audit enabled beforehand. 免費開源,解析交易記錄檔救回誤刪或被改掉的資料,不需要事先開啟稽核功能。"
---

<p align="center"><img src="assets/logo.png" width="96" alt="LogCarver logo"></p>

# LogCarver

**English | [繁體中文](#繁體中文)**

SQL Server transaction log parser — reads INSERT/UPDATE/DELETE row history straight from the live transaction log, without requiring Audit/CDC/Change Tracking to have been enabled beforehand. Free, open source (MIT), read-only, runs entirely locally.

- **[FAQ](faq.html)** — answers to the questions people actually ask when SQL Server data goes missing: can a DELETE be recovered without a backup, why `fn_dblog` stops reporting recently-changed data, whether `TRUNCATE`/`DROP TABLE` can be recovered, what permissions the tool needs.
- **[GitHub repository](https://github.com/caiderek/LogCarver)** — source code, usage, download, and the full README.

---

## 繁體中文

SQL Server 交易記錄檔解析工具——直接從交易記錄檔讀出 INSERT/UPDATE/DELETE 歷程,不需要事先啟用 Audit/CDC/Change Tracking。免費、開源(MIT)、唯讀、完全在本機執行。

- **[常見問題](faq.zh-TW.html)** —— 資料不見時大家真正會問的問題:沒備份的 DELETE 救得回來嗎、`fn_dblog` 查不到剛刪除的資料是為什麼、`TRUNCATE`/`DROP TABLE` 救得回來嗎、這個工具需要什麼權限。
- **[GitHub repository](https://github.com/caiderek/LogCarver)** —— 原始碼、使用方式、下載、完整 README。

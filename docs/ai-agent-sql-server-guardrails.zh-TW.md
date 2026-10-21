---
title: 怎麼讓 AI agent 比較安全地寫入正式環境的 SQL Server
description: "想讓 Claude Code 或 Cursor 讀寫正式環境的 SQL Server,又不想把密碼和完整權限交出去。一個 MCP proxy:每個語句用呼叫者自己的登入帳號執行、擋掉危險語句、每次寫入都留稽核紀錄。能做到什麼、做不到什麼。"
---

# 怎麼讓 AI agent 比較安全地寫入正式環境的 SQL Server

[English](ai-agent-sql-server-guardrails.md) / **繁體中文**

這頁講的是:AI 寫程式的 agent 已經需要碰正式環境的 SQL Server,但你不想把真正的帳密和不受限的權限交給它。

## 結論

[LogCarverGuard](https://github.com/caiderek/LogCarverGuard) 是一個 [MCP](https://modelcontextprotocol.io) proxy,放在 agent 和 SQL Server 中間:

```
AI agent --(MCP, API key)--> LogCarverGuard proxy --(EXECUTE AS 呼叫者自己的登入)--> SQL Server
```

- agent 看不到資料庫密碼。它只拿到一把 API key,proxy 再把這把 key 對應到那個人自己的 SQL 登入帳號。
- 每個語句都用那個登入帳號執行,所以你原本設的 `GRANT`、`DENY` 照樣決定能做什麼,proxy 不會放寬權限。
- 用真正的 T-SQL parser 判斷要擋什麼,不是比對關鍵字,所以寫在註解或字串裡的假關鍵字不會有作用。
- 每個執行過的語句都寫進稽核紀錄:誰、什麼時候、哪一段 SQL。

## 會擋什麼

一律擋掉:`DROP TABLE`、`DROP DATABASE`、`RESTORE DATABASE` / `RESTORE LOG`、`ALTER DATABASE ... SET`、`sp_executesql`、`xp_cmdshell`、用字串動態 `EXEC`、切換身分(`REVERT`、`EXECUTE AS`、`SETUSER`)、改權限,以及由呼叫端自己管理的交易。

管理員沒有加進白名單就擋:`TRUNCATE TABLE`、`ALTER TABLE ... DROP COLUMN`、`EXEC` 具名的預存程序。

一批 `INSERT` / `UPDATE` / `DELETE` 會在 proxy 控制的同一個交易裡執行,其中任何一句失敗就整批回復。

## 兩種跑法

- **本機模式。** 由你的 AI 工具在自己的機器上用 stdio 啟動。不用架 server、不用 Docker、不用 API key。另外有唯讀模式,只讓 `SELECT` 通過。

  ```
  dotnet tool install -g LogCarverGuard.Local
  ```

- **集中式服務。** 需要一台有 Docker 的 Linux 主機。每個人有自己的 API key 和 SQL 登入帳號,SQL Server 只接受來自 proxy 的連線。想要真正的隔離邊界就用這個。

兩種的設定步驟都在 [README](https://github.com/caiderek/LogCarverGuard)。

## 它沒有涵蓋的

- **本機模式是防止意外的護欄,不是資安邊界。** 帳密就在你的機器上,能執行 shell 指令的 agent 可以直接連 SQL Server。
- **真正的邊界是 SQL 登入帳號自己的權限。** 每把 key 都用你能接受的最小權限,不要用 sysadmin 或 db_owner。封鎖清單刻意縮得很窄:`DROP INDEX`、`sp_rename`、`BACKUP DATABASE`、Linked Server 的 `OPENQUERY`,只要登入帳號有權限就會執行。
- **只管經過這條 MCP 連線送出的 SQL。** agent 手上的其他工具(檔案、API、雲端主控台)不在它的範圍內。
- **沒有讀取時遮罩。** 帳號能 `SELECT` 的敏感資料,agent 就讀得到。
- **只支援 SQL Server。** API key 是單一靜態密鑰,目前是早期版本(v0.1)。

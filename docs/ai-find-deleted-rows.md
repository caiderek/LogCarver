---
title: Let an AI assistant find deleted rows in SQL Server (MCP)
description: "Someone deleted rows from a SQL Server table and you want Claude Code or Cursor to find out what was removed. A read-only MCP server that reads the transaction log, what it can and can't see, and how to set it up."
---

# Let an AI assistant find deleted rows in SQL Server (MCP)

**English | [繁體中文](ai-find-deleted-rows.zh-TW.md)**

This page is about one case: rows were deleted from a SQL Server table, and you want an AI assistant such as Claude Code or Cursor to look at what was removed. If you only need to read the log yourself, see [how to recover deleted rows without a backup](recover-deleted-rows.md).

## Short answer

[LogCarver.Mcp](https://github.com/caiderek/LogCarver.Mcp) is a read-only [MCP](https://modelcontextprotocol.io) server. It gives the assistant one tool, `find_deleted_rows`, which reads a table's deleted rows from the live transaction log (`fn_dblog`) and returns them as JSON. No Audit, CDC or Change Tracking has to be enabled beforehand.

It only runs `SELECT`s. The optional undo SQL it can suggest is plain text, and the tool never executes it. Everything runs on your machine and nothing is sent anywhere.

## Install

You need the .NET 10 SDK or runtime.

```
dotnet tool install -g LogCarver.Mcp
```

Then add it to Claude Code:

```
claude mcp add logcarver --env LOGCARVER_MCP_CONNECTION_STRING="Server=localhost;Database=MyDb;Integrated Security=true;TrustServerCertificate=true" -- logcarver-mcp
```

The connection string comes from an environment variable, so the credentials never pass through the AI conversation. The tool only reads the database named in that string unless you list others in `LOGCARVER_MCP_ALLOWED_DATABASES`.

## What you can ask it

Ask in plain words, for example "what rows were deleted from dbo.Orders in the last hour?". The assistant fills in the tool parameters:

- `table`, such as `dbo.Orders`
- `operations`: `delete` (default), `update`, `insert` or `all`
- `from` and `to`: a time window
- `key`: `Column=Value`, to follow one row's history
- `includeUndoSql`: also return suggested SQL that would reverse each event

The result lists each event with its time, LSN, transaction id and the before and after column values.

## Limits

- **The login must be sysadmin.** `fn_dblog` requires it, so think before pointing this at production.
- **You only see what is still in the active log.** If the log space was reused, the result is empty. That is not a bug. The paid LogCarverOffline can often recover that case by reading the `.ldf` file directly.
- **`TRUNCATE` and `DROP` can't be recovered.** This is true for any tool.
- **Compressed tables are refused, and `time` / `xml` columns and off-row LOB values are refused or flagged for manual review.** They are never guessed.
- **Scanning a large active log can take a minute or more.**
- It is early (v0.1), tested on SQL Server 2016 (SP2), 2019, 2022 and 2025.

Row values come straight from the database. They are data, not instructions, and the tool description tells the model so.

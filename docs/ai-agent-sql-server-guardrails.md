---
title: How to let an AI agent write to a production SQL Server safely
description: "You want Claude Code or Cursor to read and write a production SQL Server without handing it the password. An MCP proxy that runs each statement as the caller's own login, blocks dangerous statements, and audits every write. What it covers and what it doesn't."
---

# How to let an AI agent write to a production SQL Server safely

**English** / [繁體中文](ai-agent-sql-server-guardrails.zh-TW.md)

This page is for the case where an AI coding agent already needs to touch a production SQL Server, and you would rather not give it the real credentials and a free hand.

## Short answer

[LogCarverGuard](https://github.com/caiderek/LogCarverGuard) is an [MCP](https://modelcontextprotocol.io) proxy that sits between the agent and SQL Server:

```
AI agent --(MCP, API key)--> LogCarverGuard proxy --(EXECUTE AS the caller's own login)--> SQL Server
```

- The agent never sees the database password. It holds an API key, and the proxy maps the key to that person's own SQL login.
- Each statement runs as that login, so your existing `GRANT` and `DENY` still decide what is allowed. The proxy never widens them.
- A real T-SQL parser decides what is blocked, not keyword matching, so a fake keyword inside a comment or string does nothing.
- Every executed statement is written to an audit log: who, when, and which SQL.

## What is blocked

Always blocked: `DROP TABLE`, `DROP DATABASE`, `RESTORE DATABASE` / `RESTORE LOG`, `ALTER DATABASE ... SET`, `sp_executesql`, `xp_cmdshell`, dynamic `EXEC` of a string, identity switching (`REVERT`, `EXECUTE AS`, `SETUSER`), permission changes, and caller-managed transactions.

Blocked unless the admin allow-lists it: `TRUNCATE TABLE`, `ALTER TABLE ... DROP COLUMN`, and `EXEC` of a named stored procedure.

A batch of `INSERT` / `UPDATE` / `DELETE` runs in one transaction that the proxy controls, and is rolled back as a whole if any statement fails.

## Two ways to run it

- **Local mode.** Your AI tool starts it over stdio on your own machine. No server, no Docker, no API keys. It also has a read-only mode that lets only `SELECT` through.

  ```
  dotnet tool install -g LogCarverGuard.Local
  ```

- **Central service.** A Linux host with Docker. Each person has their own API key and SQL login, and SQL Server only accepts connections from the proxy. This is the one to use when you want a real boundary.

Setup steps for both are in the [README](https://github.com/caiderek/LogCarverGuard).

## What it does not cover

- **Local mode is a guardrail against accidents, not a security boundary.** The credentials live on your machine, and an agent that can run shell commands can connect to SQL Server directly.
- **The real boundary is the SQL login's own grants.** Give each key the least privilege you can accept, never sysadmin or db_owner. The block list is deliberately narrow: `DROP INDEX`, `sp_rename`, `BACKUP DATABASE` and Linked Server `OPENQUERY` run if the login may run them.
- **It only sees SQL sent through this MCP connection.** Other tools the agent has (files, APIs, cloud consoles) are outside it.
- **No mask-on-read.** If an account can `SELECT` sensitive data, the agent can read it.
- **SQL Server only.** The API key is a single static secret, and it is early (v0.1).

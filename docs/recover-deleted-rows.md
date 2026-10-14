---
title: How to recover deleted rows in SQL Server without a backup
description: "Rows were deleted or overwritten in SQL Server, and you have no backup, CDC or audit. Can you get them back? How to read the deleted rows from the transaction log, and what to avoid before the log gets reused."
---

# How to recover deleted rows in SQL Server without a backup

**English | [繁體中文](recover-deleted-rows.zh-TW.md)**

This page is for the case where you have no backup. If you have a full backup plus an unbroken chain of log backups, restore to a point in time just before the mistake (`RESTORE LOG ... WITH STOPAT`). That is the most reliable way, and you can stop reading here.

## Short answer

DELETE and UPDATE can often be recovered. SQL Server's transaction log can't be turned off. Every INSERT, UPDATE and DELETE is written to it before the data file changes, so the original content of a deleted row is usually still in the log.

The problem is time. The log gets reused, and once a section is reused, SQL Server itself can no longer report what was in it. So the first job is to keep it from being reused.

## What to do right now

I tested each of these on a throwaway database:

1. **Don't run `CHECKPOINT` by hand.** Under the SIMPLE recovery model, once a checkpoint finished, the delete records were gone from `fn_dblog`.
2. **Don't back up the transaction log.** Under FULL recovery, after a log backup and some more writes, the delete records could no longer be found. In my test the first log backup right after the delete left them in place, but writing more data and backing up again removed them. I can't tell you which backup will clear them, so the safe move is to do none. This goes against normal DBA habits, but it is the safer choice here.
3. **Write to the database as little as you can.** The more it writes, the faster the log gets reused.

## Check whether the log still has them

```sql
USE YourDatabase;

SELECT COUNT(*) AS delete_records
FROM fn_dblog(NULL, NULL)
WHERE Operation = 'LOP_DELETE_ROWS'
  AND AllocUnitName LIKE 'dbo.YourTable%';
```

`fn_dblog` needs high privileges, usually sysadmin. A count above 0 means the delete records for that table are still there. If it returns 0, see "Limits" at the bottom.

## Read the deleted rows

`fn_dblog` gives you raw bytes, not column values, so you have to decode them. [LogCarver](https://github.com/caiderek/LogCarver) is a free, open source tool I wrote that does this:

```
LogCarver.exe localhost YourDatabase dbo.YourTable
```

The output looks like this (from a test database where I deleted 3 rows):

```
[0000002F:000001B0:0002 2026-10-13 15:37:42] DELETE: Id=3, Amount=100, Customer=cust-3
[0000002F:000001B0:0004 2026-10-13 15:37:42] DELETE: Id=4, Amount=100, Customer=cust-4
[0000002F:000001B0:0005 2026-10-13 15:37:42] DELETE: Id=5, Amount=100, Customer=cust-5
```

Add `--undo` to print suggested SQL that reverses each change. The tool only prints it and never runs anything. Test the SQL on a non-production copy first.

## Limits

- **TRUNCATE TABLE and DROP TABLE can't be recovered.** Both release whole pages, and the log has no row by row delete records for them. This is true for any tool.
- **Once the log has been reused, the free version can't see the rows.** The paid version, [LogCarverOffline](faq.md#whats-the-difference-between-the-free-logcarver-and-the-paid-logcarveroffline), reads the bytes in the `.ldf` file directly. If those bytes haven't been overwritten yet, it may recover data that `fn_dblog` no longer reports.
- **Compressed tables and off-row LOB columns are refused.** The tool will not decode them. This is a known limit.
- **An UPDATE of a row whose INSERT has already left the log is rebuilt from the row's current state, and is flagged for manual review if that isn't possible.** The tool reads the row from the data page (or from its later DELETE record) and undoes the logged changes, checking every step against the log. It refuses rather than guesses when the row can't be found or doesn't match. This was verified on SQL Server 2025; check the output before relying on it on other versions.
- **It runs on Windows only, and the login needs sysadmin or db_owner.**

The full list of limits and supported column types is in the [README](https://github.com/caiderek/LogCarver) and the [FAQ](faq.md).

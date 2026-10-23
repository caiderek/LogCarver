<p align="center"><img src="docs/assets/logo.png" width="96" alt="LogCarver logo"></p>

# LogCarver

**English | [繁體中文](README.zh-TW.md)**

SQL Server transaction log parser — reads insert/update/delete history straight from the transaction log, without requiring Audit/CDC/Change Tracking to have been enabled beforehand.

Deleted rows and no backup? Start with the [step-by-step guide](docs/recover-deleted-rows.md).

## Download

Grab the latest self-contained `LogCarver.exe` from the [Releases page](https://github.com/caiderek/LogCarver/releases/latest) — no .NET installation required on the target machine.

## Status

**Working MVP.** Connects to a live SQL Server instance, decodes a table's full INSERT/UPDATE (before/after)/DELETE history, guards against the schema-drift trap (decoding old rows with a newer table structure), and supports filtering to an incident time window, single-row history lookup, Undo/Replay SQL generation, and point-in-time snapshots.

## Prerequisites

- SQL Server, with an account that has `sysadmin` or `db_owner`-level rights on the target database — `fn_dblog` requires elevated permissions.
- **Only SQL Server 2025, 2022, 2019, and 2016 (SP2) have been validated so far.** The tool warns rather than silently misdecoding when it detects an unvalidated version, but treat output from other versions with extra caution until they're confirmed.
- Windows integrated authentication by default; SQL authentication is available via `--user`/`--password` (see [Usage](#usage) below) for servers that aren't domain-joined or don't support integrated security.

## Scope (current)

- SQL Server only, connects to a live instance via `fn_dblog`
- Runs entirely locally — no network calls, no data ever leaves the machine it runs on
- **Column types currently decoded:** `int`, `bigint`, `smallint`, `tinyint`, `bit`, `uniqueidentifier`, `date`, `datetime2` (any scale, 0–7), `datetime`, `smalldatetime`, `decimal`/`numeric` (any precision, 1–38), `money`, `smallmoney`, `float`, `real`, `char`, `nchar`, `varchar`, `nvarchar`, `varbinary`, `binary` — including `geography`/`geometry`/`hierarchyid`, which SQL Server stores the same way as `varbinary` internally. Anything else (e.g. plain `time` with no date part, `xml`, `sql_variant`) is explicitly refused, not guessed — you'll see `not shown - ... is not implemented yet` for those rows.
- Also explicitly detected and refused rather than guessed at: compressed tables (ROW/PAGE), off-row LOB values, records predating a schema-changing DDL
- **Known over-refusal, not a wrong-data risk:** `TRUNCATE TABLE` produces the same kind of log record (`LOP_HOBT_DDL`) as a real schema-changing `ALTER TABLE`, and the two aren't currently distinguished — so a `TRUNCATE` gets treated as a schema boundary, and every event before it gets refused too, even though `TRUNCATE` never actually changes column layout. The refused history is real and decodable; this tool just doesn't show it yet.
- Offline `.ldf` file analysis — recovering data `fn_dblog` can no longer see but that hasn't been physically overwritten yet — isn't part of this free tool. That's the paid **LogCarverOffline**'s differentiator; see [Offline recovery](#offline-recovery) below.

## Disclaimer

LogCarver only ever **prints suggested SQL** (for `--undo`/`--replay`) — it never connects with write intent or executes anything itself. Always review generated SQL and test it against a non-production copy first. This tool is provided with no warranty of correctness; verify recovered data independently before relying on it, especially for anything compliance- or finance-sensitive. See [LICENSE](LICENSE) for the full MIT disclaimer.

## Quick start (copy and paste)

LogCarver is a command-line program. Double-clicking `LogCarver.exe` only prints the usage text and closes the window, so run it from a Command Prompt:

1. Download `LogCarver.exe` from the [latest release](https://github.com/caiderek/LogCarver/releases/latest) and put it in a folder, for example `C:\LogCarver`.
2. Press `Win + R`, type `cmd`, press Enter.
3. Go to the folder:

   ```
   cd /d C:\LogCarver
   ```

4. Paste this, then change the values (see the table below):

   ```
   LogCarver.exe 192.168.1.10 MyDatabase dbo.Orders --user myuser --password "mypassword" --from "2026-09-23T09:00" --to "2026-09-23T10:00" --undo --export sql --output undo.sql
   ```

5. Open `undo.sql` in SQL Server Management Studio, read every statement, then run it. LogCarver never runs it for you, and a statement whose row has changed since matches nothing and does nothing.

| Part | Replace with |
|---|---|
| `192.168.1.10` | The server's hostname or IP. A named instance is `host\instance`; a non-default port is `host,1433`. |
| `MyDatabase` | The database the table lives in. |
| `dbo.Orders` | The table, as `schema.table`. |
| `--user myuser --password "mypassword"` | A SQL login. To use your current Windows account instead, leave both out. The password stays in your shell history, so prefer Windows authentication where you can. |
| `--from` / `--to` | The time window you want to look at. Leave both out to see everything still in the log. |
| `--undo --export sql --output undo.sql` | Writes the statements that would undo those changes to `undo.sql` in the current folder. For a spreadsheet-friendly list of the events instead, use `--export csv --output orders.csv`. |

The login needs the right to read the transaction log (`fn_dblog` normally needs `sysadmin`). If it reports `0 row event(s)`, see [Why do I see "0 row event(s)"?](#why-do-i-see-0-row-events) below.

## Usage

```
LogCarver.exe <server> <database> <schema.table> [--from <datetime>] [--to <datetime>] [--key <Column>=<Value>] [--undo] [--replay] [--snapshot <datetime>] [--user <name> --password <pw>] [--timeout <seconds>] [--export csv|json|sql [--output <path>]]
```

| Flag | Effect |
|---|---|
| `--from` / `--to` | Filter which events are *printed* to that incident window. Reconstruction still uses the table's full observed history, so before/after values stay accurate even when the window is narrow. |
| `--key <Column>=<Value>` | Show only events for one row's full history. Matches against either the before or after image, so it finds the row whether or not that column changed in a given event. |
| `--undo` | Print a suggested SQL statement reversing each shown event. The `WHERE` clause matches every observed column, not just a primary key, so it becomes a safe no-op if the row has changed again since LogCarver saw it. |
| `--replay` | Print a suggested SQL statement reproducing each shown event forward. Same safe-`WHERE` behavior as `--undo`. |
| `--snapshot <datetime>` | Reconstruct what every row looked like at that exact moment, instead of listing events. Ignores `--from`/`--to`/`--key`/`--undo`/`--replay`. |
| `--user <name>` / `--password <pw>` | Connect with SQL authentication instead of the current Windows account. Both or neither must be given. The password is visible in your shell history and this process's command line while it runs — prefer Windows authentication where you can. |
| `--timeout <seconds>` | Raise the time limit for each log-scanning query (default 120 seconds, `0` = no limit). Needed when the active log is large, because each run reads the whole log more than once; run it off-peak. |
| `--export csv\|json\|sql` / `--output <path>` | Write the result as a file (or to stdout) instead of the console listing. `csv` and `json`: one column per observed table column (`Before_<Col>` / `After_<Col>`), not a single flattened cell. `sql`: a runnable `.sql` script of the `--undo` statements (newest event first) or the `--replay` statements (oldest event first); give exactly one of the two. A `.sql` or `.csv` file written with `--output` is UTF-8 with a BOM so SSMS and Excel read non-ASCII text correctly (`json` has no BOM). Does not apply to `--snapshot`. `--output` writes to that file instead of stdout and requires `--export`. |

Examples:

```
LogCarver.exe localhost MyDatabase dbo.Orders
LogCarver.exe localhost MyDatabase dbo.Orders --from "2026-09-23T09:00" --to "2026-09-23T10:00"
LogCarver.exe localhost MyDatabase dbo.Orders --key Id=5 --undo
LogCarver.exe localhost MyDatabase dbo.Orders --snapshot "2026-09-23T09:30"
LogCarver.exe localhost MyDatabase dbo.Orders --user sa --password "..."
LogCarver.exe localhost MyDatabase dbo.Orders --export csv --output orders.csv
LogCarver.exe localhost MyDatabase dbo.Orders --from "2026-09-23T09:00" --to "2026-09-23T10:00" --undo --export sql --output undo.sql
```

**Large logs.** Each run reads the whole active log (`fn_dblog`) more than once. A database in FULL recovery that never takes log backups never truncates its log, so the log can hold months of history and a single scan can take minutes. If a run stops with a timeout message, raise the limit with `--timeout` and run it off-peak.

## Why do I see "0 row event(s)"?

**TL;DR: under SIMPLE recovery, VLF reuse can finish within seconds, so the data can vanish from `fn_dblog` almost immediately — that's exactly the situation LogCarverOffline exists to solve.**

If you just changed data and LogCarver reports zero events for that table, this is not a bug — it means SQL Server itself has already stopped reporting that history, even to `SELECT * FROM fn_dblog(NULL, NULL)` run by hand.

`fn_dblog` can only ever show what's in the transaction log's **currently active VLFs** (virtual log files). As soon as SQL Server checkpoints and nothing else needs an old VLF — no open transaction, no pending log backup, no replication — it marks that VLF "reusable" and `fn_dblog` immediately stops reporting *everything* in it, not just the operation you're investigating. Under the **SIMPLE** recovery model this can happen within seconds of a checkpoint, since there's nothing (like a log backup) to delay it. A small, low-traffic database in SIMPLE recovery can cycle its entire log — including the original `INSERT`s that first created a row, not just a later `DELETE` — before you've even finished investigating.

Concretely: a table in a SIMPLE-recovery database gets a row deleted, then LogCarver is run against it moments later and reports `0 row event(s)`. That's expected — by the time the tool ran, the checkpoint had already marked the relevant VLF reusable, and `fn_dblog` had already forgotten the delete *and* the insert that came before it.

The important nuance: "marked reusable" is not the same as "physically overwritten." The bytes may still be sitting untouched in the `.ldf` file — SQL Server just isn't telling you about them anymore. That gap between what `fn_dblog` reports and what's still physically recoverable is exactly what **LogCarverOffline** (below) is built to close.

**What LogCarver prints when it finds nothing.** The CLI reads a few facts from the server and prints them: the database's recovery model and log reuse wait, the time span the online log covers, whether the login is sysadmin, and which tables still have deleted rows in the log. It then names the most likely reason, for example that the log starts after the `--from` time you gave, or that the database is in SIMPLE recovery. Treat this as a hint, not proof: some causes can look the same from `fn_dblog`'s side, such as a wrong table name and a log that has already been reused.

## Offline recovery

If `fn_dblog` reports nothing for a table, that usually means the relevant VLF has already been marked reusable and rotated past — but the data may still be physically present in the `.ldf` file. **[LogCarverOffline](https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1)**, a paid tool built on the same decode engine, reads raw `.ldf` bytes directly and can often recover exactly this case, even after the database has gone offline or been detached. [Free trial](https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW) available (no license, first 10 recovered events) — confirm it can find your data before buying. Questions: `logcarveroffline@gmail.com`.

## Use it from an AI assistant (MCP)

[LogCarver.Mcp](https://github.com/caiderek/LogCarver.Mcp) wraps this decode engine in a read-only [MCP](https://modelcontextprotocol.io) server, so an AI assistant such as Claude Code or Cursor can ask "what was deleted from this table?" directly. It has one tool, `find_deleted_rows`, runs only `SELECT`s, and stays on your machine. Install with `dotnet tool install -g LogCarver.Mcp`; it is also listed in the official MCP registry as `io.github.caiderek/logcarver-mcp`.

## Building from source

```
dotnet publish src/LogCarver.Cli -c Release
```

Produces a single ~81MB self-contained `LogCarver.exe` in `src/LogCarver.Cli/bin/Release/net10.0/win-x64/publish/`, with no .NET installation required on the target machine (verified against Windows Server 2012 through 2025 and Windows 10/11 per the [.NET 10 supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)).

## Testing

```
dotnet test
```

Runs both `LogCarver.Core.Tests` (pure decode logic, no database needed) and `LogCarver.Core.IntegrationTests` (exercises real `fn_dblog` behavior against `localhost`; needs a local SQL Server and creates/drops its own sandbox databases, all prefixed `LogCarver_`).

## License

MIT — see [LICENSE](LICENSE).

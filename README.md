<p align="center"><img src="docs/assets/logo.png" width="96" alt="LogCarver logo"></p>

# LogCarver

**English | [繁體中文](README.zh-TW.md)**

SQL Server transaction log parser — reads insert/update/delete history straight from the transaction log, without requiring Audit/CDC/Change Tracking to have been enabled beforehand.

## Download

Grab the latest self-contained `LogCarver.exe` from the [Releases page](https://github.com/caiderek/LogCarver/releases/latest) — no .NET installation required on the target machine.

## Status

**Working MVP.** Connects to a live SQL Server instance, decodes a table's full INSERT/UPDATE (before/after)/DELETE history, guards against the schema-drift trap (decoding old rows with a newer table structure), and supports filtering to an incident time window, single-row history lookup, Undo/Replay SQL generation, and point-in-time snapshots. See [`docs/原理說明.md`](docs/原理說明.md) (Traditional Chinese) for a plain-language walkthrough of how it works.

## Prerequisites

- SQL Server, with an account that has `sysadmin` or `db_owner`-level rights on the target database — `fn_dblog` requires elevated permissions.
- **Only SQL Server 2025, 2022, 2019, and 2016 (SP2) have been validated so far.** The tool warns rather than silently misdecoding when it detects an unvalidated version, but treat output from other versions with extra caution until they're confirmed.
- Windows integrated authentication by default; SQL authentication is available via `--user`/`--password` (see [Usage](#usage) below) for servers that aren't domain-joined or don't support integrated security.

## Scope (current)

- SQL Server only, connects to a live instance via `fn_dblog`
- Runs entirely locally — no network calls, no data ever leaves the machine it runs on
- **Column types currently decoded: `int`, `datetime2(3)`/`datetime2(4)`, `char`, `nchar`, `varchar`, `nvarchar`.** Any other type (`decimal`/`numeric`/`money`, `bigint`/`smallint`/`tinyint`, `bit`, `float`/`real`, `date`/`time`, `datetime2` at other scales, `uniqueidentifier`, etc.) is explicitly refused, not guessed — you'll see `not shown - ... is not implemented yet` for those rows. More types are on the roadmap; check before relying on this for a table with financial (`decimal`/`money`) columns today.
- Also explicitly detected and refused rather than guessed at: compressed tables (ROW/PAGE), off-row LOB values, records predating a schema-changing DDL
- **Known over-refusal, not a wrong-data risk:** `TRUNCATE TABLE` produces the same kind of log record (`LOP_HOBT_DDL`) as a real schema-changing `ALTER TABLE`, and the two aren't currently distinguished — so a `TRUNCATE` gets treated as a schema boundary, and every event before it gets refused too, even though `TRUNCATE` never actually changes column layout. The refused history is real and decodable; this tool just doesn't show it yet.
- Offline `.ldf` file analysis — recovering data `fn_dblog` can no longer see but that hasn't been physically overwritten yet — isn't part of this free tool. That's the paid **LogCarverOffline**'s differentiator; see [Offline recovery](#offline-recovery) below.

## Disclaimer

LogCarver only ever **prints suggested SQL** (for `--undo`/`--replay`) — it never connects with write intent or executes anything itself. Always review generated SQL and test it against a non-production copy first. This tool is provided with no warranty of correctness; verify recovered data independently before relying on it, especially for anything compliance- or finance-sensitive. See [LICENSE](LICENSE) for the full MIT disclaimer.

## Usage

```
LogCarver.exe <server> <database> <schema.table> [--from <datetime>] [--to <datetime>] [--key <Column>=<Value>] [--undo] [--replay] [--snapshot <datetime>] [--user <name> --password <pw>]
```

| Flag | Effect |
|---|---|
| `--from` / `--to` | Filter which events are *printed* to that incident window. Reconstruction still uses the table's full observed history, so before/after values stay accurate even when the window is narrow. |
| `--key <Column>=<Value>` | Show only events for one row's full history. Matches against either the before or after image, so it finds the row whether or not that column changed in a given event. |
| `--undo` | Print a suggested SQL statement reversing each shown event. The `WHERE` clause matches every observed column, not just a primary key, so it becomes a safe no-op if the row has changed again since LogCarver saw it. |
| `--replay` | Print a suggested SQL statement reproducing each shown event forward. Same safe-`WHERE` behavior as `--undo`. |
| `--snapshot <datetime>` | Reconstruct what every row looked like at that exact moment, instead of listing events. Ignores `--from`/`--to`/`--key`/`--undo`/`--replay`. |
| `--user <name>` / `--password <pw>` | Connect with SQL authentication instead of the current Windows account. Both or neither must be given. The password is visible in your shell history and this process's command line while it runs — prefer Windows authentication where you can. |

Examples:

```
LogCarver.exe localhost MyDatabase dbo.Orders
LogCarver.exe localhost MyDatabase dbo.Orders --from "2026-09-23T09:00" --to "2026-09-23T10:00"
LogCarver.exe localhost MyDatabase dbo.Orders --key Id=5 --undo
LogCarver.exe localhost MyDatabase dbo.Orders --snapshot "2026-09-23T09:30"
LogCarver.exe localhost MyDatabase dbo.Orders --user sa --password "..."
```

## Why do I see "0 row event(s)"?

**TL;DR: under SIMPLE recovery, VLF reuse can finish within seconds, so the data can vanish from `fn_dblog` almost immediately — that's exactly the situation LogCarverOffline exists to solve.**

If you just changed data and LogCarver reports zero events for that table, this is not a bug — it means SQL Server itself has already stopped reporting that history, even to `SELECT * FROM fn_dblog(NULL, NULL)` run by hand.

`fn_dblog` can only ever show what's in the transaction log's **currently active VLFs** (virtual log files). As soon as SQL Server checkpoints and nothing else needs an old VLF — no open transaction, no pending log backup, no replication — it marks that VLF "reusable" and `fn_dblog` immediately stops reporting *everything* in it, not just the operation you're investigating. Under the **SIMPLE** recovery model this can happen within seconds of a checkpoint, since there's nothing (like a log backup) to delay it. A small, low-traffic database in SIMPLE recovery can cycle its entire log — including the original `INSERT`s that first created a row, not just a later `DELETE` — before you've even finished investigating.

Concretely: a table in a SIMPLE-recovery database gets a row deleted, then LogCarver is run against it moments later and reports `0 row event(s)`. That's expected — by the time the tool ran, the checkpoint had already marked the relevant VLF reusable, and `fn_dblog` had already forgotten the delete *and* the insert that came before it.

The important nuance: "marked reusable" is not the same as "physically overwritten." The bytes may still be sitting untouched in the `.ldf` file — SQL Server just isn't telling you about them anymore. That gap between what `fn_dblog` reports and what's still physically recoverable is exactly what **LogCarverOffline** (below) is built to close.

## Offline recovery

If `fn_dblog` reports nothing for a table, that usually means the relevant VLF has already been marked reusable and rotated past — but the data may still be physically present in the `.ldf` file. **[LogCarverOffline](https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1)**, a paid tool built on the same decode engine, reads raw `.ldf` bytes directly and can often recover exactly this case, even after the database has gone offline or been detached. [Free trial](https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW) available (no license, first 10 recovered events) — confirm it can find your data before buying. Questions: `logcarveroffline@gmail.com`.

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

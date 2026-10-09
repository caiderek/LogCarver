# LogCarver FAQ

**English | [繁體中文](faq.zh-TW.md)**

Answers to the questions people actually ask when SQL Server data goes missing. Each answer stands on its own — if you only read one, read the one with your exact problem.

## SQL Server has no backup and no CDC/Audit/Change Tracking enabled — can deleted rows still be recovered?

Yes, for DELETE and UPDATE, as long as the transaction log still holds the relevant record. SQL Server's transaction log cannot be turned off — every INSERT/UPDATE/DELETE is written to it before the data file itself changes, regardless of what auditing features you have or haven't enabled. [LogCarver](../README.md) reads that log directly via `fn_dblog`, so you don't need to have set anything up in advance.

The catch is time: the log only holds what's currently in its active VLFs (virtual log files). Under SIMPLE recovery, that window can close within seconds of a checkpoint. See the next question and [the README's "0 row event(s)" section](../README.md#why-do-i-see-0-row-events) for what happens when it already has.

## `fn_dblog` reports nothing for the rows I just deleted — why?

This is not a bug — it means SQL Server itself has already stopped tracking that history, even for a manual `SELECT * FROM fn_dblog(NULL, NULL)`. Full explanation: [README → Why do I see "0 row event(s)"?](../README.md#why-do-i-see-0-row-events)

Short version: once a VLF is marked reusable, `fn_dblog` immediately stops reporting everything in it — not just the delete you're investigating, but potentially the original INSERT too. "Marked reusable" is not the same as "physically overwritten": the bytes may still sit untouched in the `.ldf` file. That gap is what the paid **LogCarverOffline** reads directly — see below.

## Can a `TRUNCATE TABLE`'d table be recovered?

**No.** `TRUNCATE` deallocates whole pages/extents rather than logging each row's deletion individually — there is no row-level log record for the truncated data itself, in either the free or paid tool. This is a structural property of how SQL Server logs `TRUNCATE`, not a gap either tool plans to close.

Separately, the current free version has an unrelated over-refusal bug worth knowing about: `TRUNCATE` produces the same kind of log record (`LOP_HOBT_DDL`) as a real schema-changing `ALTER TABLE`, and the two aren't currently distinguished. So history from *before* the `TRUNCATE` also gets refused, as if it predated a real structure change — even though `TRUNCATE` itself never altered the column layout. That earlier history is real and decodable; the tool just doesn't surface it yet.

## Can a `DROP TABLE`'d table be recovered?

**No**, for the same underlying reason as `TRUNCATE`: dropping a table deallocates its pages without logging individual row deletions. There's no row-level record in the transaction log to decode.

## Can I see what a row's value was *before* an UPDATE, not just after?

Yes. SQL Server's UPDATE log record only stores the changed byte range (a diff, not a full row), so LogCarver reconstructs each row's full history from its original INSERT forward, applying every UPDATE in order — giving you both the before and after image of any change. Run with `--key <Column>=<Value>` to see one row's full history, or `--snapshot <datetime>` to see every row as of a specific moment.

## What permissions does this tool need? Does it touch my database?

It needs an account with `sysadmin` or `db_owner`-level rights on the target database — `fn_dblog` itself requires elevated permissions to query, independent of anything LogCarver does. Beyond that:

- **Read-only.** LogCarver never connects with write intent. `--undo`/`--replay` only *print* suggested SQL — nothing is ever executed automatically. Review and test any generated SQL against a non-production copy first.
- **No network calls.** Runs entirely locally; no data ever leaves the machine it runs on.

## Which SQL Server versions have actually been validated, not just assumed to work?

See [README → Prerequisites](../README.md#prerequisites) for the current, explicitly-tested list. The tool warns rather than silently misdecoding when it detects an unvalidated version — but treat output from other versions with extra caution until they're confirmed.

## Which column types can be decoded?

See [README → Scope](../README.md#scope-current) for the current list. Anything not listed there is explicitly refused, not guessed — you'll see a clear `not shown - ... is not implemented yet` marker on that column rather than a silently wrong value.

## What's the difference between the free LogCarver and the paid LogCarverOffline?

Both share the same decode engine. The free CLI reads a **live** SQL Server instance through `fn_dblog`, which only reports what's still in the log's active VLFs. **[LogCarverOffline](https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1)** instead reads the raw `.ldf` file's bytes directly, offline, which can often still recover data `fn_dblog` has already stopped reporting — as long as the bytes haven't been physically overwritten. A [free trial](https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW) (no license, first 10 recovered events) lets you confirm it can find your data before buying.

Neither version recovers `TRUNCATE` or `DROP` — see above, that's a logging limitation, not a feature gap.

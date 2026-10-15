<p align="center"><img src="docs/assets/logo.png" width="96" alt="LogCarver logo"></p>

# LogCarver

**[English](README.md) | 繁體中文**

SQL Server 交易記錄檔解析工具 —— 直接從交易記錄檔讀出 insert/update/delete 歷程,不需要事先啟用 Audit / CDC / Change Tracking。

資料被刪了、又沒有備份?先看 [處理步驟](docs/recover-deleted-rows.zh-TW.md)。

## 下載

到 [Releases 頁面](https://github.com/caiderek/LogCarver/releases/latest) 下載最新的 `LogCarver.exe`(單一檔案、內含執行環境),目標機器不需要另外安裝 .NET。

## 狀態

**可用的 MVP。** 連上執行中的 SQL Server,解碼一張表的 INSERT / UPDATE(含前後值)/ DELETE 歷程。會擋掉 schema-drift 這個陷阱(拿新的表結構去解舊的資料列)。也支援篩選事故時間窗、查單筆資料的完整歷史、產生 Undo / Replay SQL,還有重建某個時間點的快照。

## 使用前提

- 需要一台 SQL Server,連線帳號要有 `sysadmin` 或 `db_owner` 等級的權限 —— `fn_dblog` 本身就要求較高權限才能查。
- **目前只驗證過 SQL Server 2025、2022、2019、2016(SP2)。** 遇到沒驗證過的版本,工具會警告,不會默默解錯 —— 但那些版本的輸出結果還是先謹慎對待,等實際確認過再依賴它。
- 預設用 Windows 整合式驗證(Integrated Security),也可以用 `--user`/`--password` 走 SQL 帳號密碼登入(見下方[使用方式](#使用方式)),給不在網域裡、或不支援整合式驗證的伺服器用。

## 目前範圍

- 只支援 SQL Server,透過 `fn_dblog` 連到執行中的實例
- 完全在本機執行 —— 不對外連線,資料不會離開執行這個工具的機器
- **目前能解碼的欄位型別:**`int`、`bigint`、`smallint`、`tinyint`、`bit`、`uniqueidentifier`、`date`、`datetime2`(任意精度 0–7)、`datetime`、`smalldatetime`、`decimal`/`numeric`(任意精度 1–38)、`money`、`smallmoney`、`float`、`real`、`char`、`nchar`、`varchar`、`nvarchar`、`varbinary`、`binary`——也包含 `geography`/`geometry`/`hierarchyid`,因為 SQL Server 內部把它們跟 `varbinary` 用同一種方式儲存。其他型別(例如單獨的 `time`、`xml`、`sql_variant`)會明確拒絕解碼,不會用猜的 —— 這些欄位所在的列會顯示 `not shown - ... is not implemented yet`。
- 一樣是「測不出來就拒絕,不要亂猜」的原則:壓縮表(ROW/PAGE)、off-row LOB 欄位、早於改表結構(schema-changing DDL)的舊紀錄,都不解。
- **已知的「過度拒絕」限制,不是解錯資料的風險**:`TRUNCATE TABLE` 產生的 log 紀錄類型(`LOP_HOBT_DDL`)跟真正改變欄位結構的 `ALTER TABLE` 是同一種,目前工具分不出兩者的差別 —— 所以 `TRUNCATE` 會被當成改表結構的邊界,連帶讓它之前所有事件都被拒絕顯示,即使 `TRUNCATE` 本身沒改欄位配置。那些被拒絕的歷史紀錄其實是可以正確解碼的,只是這個工具現在還沒把它們秀出來。
- 離線解析 `.ldf` 檔案(救回 `fn_dblog` 已經看不到、但實體上還沒被覆寫的資料)不包含在這個免費工具裡 —— 這是付費版 **LogCarverOffline** 的差異化能力,詳見下方[離線救援](#離線救援)。

## 免責聲明

LogCarver 永遠只會**印出建議的 SQL**(`--undo`/`--replay`)—— 它不會用寫入意圖連線,也不會自己執行任何東西。請務必先審閱產生的 SQL,並在非正式環境測試過再用。本工具不保證解碼結果一定正確,尤其牽涉合規或財務用途時,請自行獨立驗證救回的資料。完整免責聲明見 [LICENSE](LICENSE)(MIT 授權)。

## 使用方式

```
LogCarver.exe <server> <database> <schema.table> [--from <datetime>] [--to <datetime>] [--key <Column>=<Value>] [--undo] [--replay] [--snapshot <datetime>] [--user <name> --password <pw>] [--timeout <seconds>]
```

| 旗標 | 效果 |
|---|---|
| `--from` / `--to` | 只篩選*印出來*的事件落在這個事故時間窗內。重建過程還是會用該表完整的觀察歷程,所以就算窗口很窄,前後值一樣準確。 |
| `--key <Column>=<Value>` | 只顯示某一列的完整歷史。會比對每個事件的前值或後值,所以不管該欄位在那次事件裡有沒有變,都找得到這一列。 |
| `--undo` | 針對每個顯示的事件,印出一句「還原」用的建議 SQL。`WHERE` 子句會比對所有觀察到的欄位,不只是主鍵,所以如果這列在 LogCarver 看到之後又被改過,這句 SQL 執行起來會是安全的空操作。 |
| `--replay` | 印出「正向重現」該事件的建議 SQL。跟 `--undo` 一樣有安全的 `WHERE` 子句。 |
| `--snapshot <datetime>` | 重建那個確切時間點每一列的樣子,而不是列出事件清單。會忽略 `--from`/`--to`/`--key`/`--undo`/`--replay`。 |
| `--user <name>` / `--password <pw>` | 改用 SQL 帳號密碼登入,不用目前的 Windows 帳號。兩個要嘛一起給、要嘛都不給。密碼在執行期間會出現在你的 shell 紀錄跟這個行程的命令列裡 —— 能用 Windows 驗證就盡量用。 |
| `--timeout <seconds>` | 調高每次掃描記錄檔查詢的時間上限(預設 120 秒,`0` 表示不限制)。使用中的記錄檔很大時需要,因為每次執行會把整份記錄檔讀超過一次;建議在離峰時段跑。 |

範例:

```
LogCarver.exe localhost MyDatabase dbo.Orders
LogCarver.exe localhost MyDatabase dbo.Orders --from "2026-09-23T09:00" --to "2026-09-23T10:00"
LogCarver.exe localhost MyDatabase dbo.Orders --key Id=5 --undo
LogCarver.exe localhost MyDatabase dbo.Orders --snapshot "2026-09-23T09:30"
LogCarver.exe localhost MyDatabase dbo.Orders --user sa --password "..."
```

**記錄檔很大的情況。** 每次執行都會把整份使用中的記錄檔(`fn_dblog`)讀超過一次。FULL 復原模式的資料庫如果一直沒有做記錄檔備份,記錄檔就不會被截斷,可能留著好幾個月的歷史,單次掃描就要好幾分鐘。如果執行到一半出現逾時訊息,用 `--timeout` 調高上限,並在離峰時段跑。

## 為什麼會看到「0 row event(s)」?

**簡單說:SIMPLE 復原模式下,VLF 輪替可能幾秒內就完成,資料幾乎馬上就從 `fn_dblog` 消失 —— 這種狀況正是 LogCarverOffline 要解決的。**

剛改完資料,LogCarver 卻查不到任何事件?這不是 bug,是 SQL Server 自己已經不記得那段歷史了 —— 就算你手動下 `SELECT * FROM fn_dblog(NULL, NULL)` 去查,一樣也是空的。

`fn_dblog` 能看到的,永遠只有交易記錄檔裡**目前還在用的 VLF**(virtual log file)。SQL Server 做完 checkpoint、確認沒有任何交易、記錄檔備份或複寫還卡著某個舊的 VLF,就會把它標記成「可重用」。這個 VLF 裡**所有**的東西都會跟著從 `fn_dblog` 消失,不只是你正在查的那筆操作。**SIMPLE** 復原模式沒有記錄檔備份幫忙延後這件事,checkpoint 一做完可能幾秒內就發生。一個活動量不高、規模不大的 SIMPLE 資料庫,常常在你還沒查完之前,就已經把整段記錄檔(包括當初新增這筆資料的 `INSERT`,不只是後來那筆 `DELETE`)輪替覆蓋掉了。

實際測試就遇過這種狀況:SIMPLE 復原模式的資料庫刪掉一筆資料,沒過多久拿 LogCarver 去查,結果是 `0 row event(s)`。原因很單純 —— checkpoint 早一步把相關 VLF 標成可重用,`fn_dblog` 連那筆刪除、跟更早的那筆新增,一起忘光了。

重點是:「標記成可重用」跟「已經被實體覆寫」是兩件事。位元組很可能還原封不動躺在 `.ldf` 檔案裡,只是 SQL Server 不讓你看到而已。`fn_dblog` 看得到的範圍,跟實際上還救得回來的範圍,中間的落差就是下面 **LogCarverOffline** 要補的地方。

**查不到事件時,LogCarver 會印什麼。** CLI 會從伺服器讀幾項資訊一併印出:資料庫的復原模式與記錄檔重用等待原因、線上記錄檔涵蓋的時間範圍、登入帳號是不是 sysadmin、以及記錄檔裡還有哪些資料表留著刪除的資料列。接著指出最可能的原因,例如記錄檔的起點晚於你給的 `--from` 時間,或資料庫是 SIMPLE 復原模式。這只是提示,不是證明:有些原因從 `fn_dblog` 這一端看起來是一樣的,例如資料表名稱打錯,和記錄檔已經被重用。

## 離線救援

如果 `fn_dblog` 對某張表什麼都查不到,通常代表相關的 VLF 已經被標記為可重用、視窗已經滾過去了 —— 但資料很可能實體上還留在 `.ldf` 檔案裡。**[LogCarverOffline](https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1)** 是建構在同一套解碼引擎上的付費工具,直接讀取 `.ldf` 原始位元組,即使資料庫已經離線或 detach,通常仍能救回這種情況。提供[免費試用版](https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW)(不用序號,可看前 10 筆救回的事件)—— 先確認救得回你要的資料再購買。有問題可以寄信到 `logcarveroffline@gmail.com`。

## 讓 AI 助理直接使用(MCP)

[LogCarver.Mcp](https://github.com/caiderek/LogCarver.Mcp) 把這套解碼引擎包成唯讀的 [MCP](https://modelcontextprotocol.io) server,讓 Claude Code、Cursor 這類 AI 助理可以直接問「這張表剛剛被刪了什麼?」。它只有一個 tool `find_deleted_rows`,只執行 `SELECT`,資料不會離開你的機器。用 `dotnet tool install -g LogCarver.Mcp` 安裝;官方 MCP registry 裡也有,名稱是 `io.github.caiderek/logcarver-mcp`。

## 從原始碼建置

```
dotnet publish src/LogCarver.Cli -c Release
```

會在 `src/LogCarver.Cli/bin/Release/net10.0/win-x64/publish/` 產生一個約 81MB、內含執行環境的單一檔案 `LogCarver.exe`,目標機器不需要安裝 .NET(依照 [.NET 10 支援的作業系統清單](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md),已驗證可在 Windows Server 2012 到 2025、以及 Windows 10/11 上執行)。

## 測試

```
dotnet test
```

會同時跑 `LogCarver.Core.Tests`(純解碼邏輯,不需要資料庫)與 `LogCarver.Core.IntegrationTests`(對 `localhost` 的真實 `fn_dblog` 行為做測試;需要本機有 SQL Server,測試會自行建立/刪除自己的沙盒資料庫,全部以 `LogCarver_` 為前綴)。

## 授權

MIT —— 詳見 [LICENSE](LICENSE)。

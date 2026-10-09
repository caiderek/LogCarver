<p align="center"><img src="docs/assets/logo.png" width="96" alt="LogCarver logo"></p>

# LogCarver

**[English](README.md) | 繁體中文**

SQL Server 交易記錄檔解析工具 —— 直接從交易記錄檔讀出 insert/update/delete 歷程,不需要事先啟用 Audit / CDC / Change Tracking。

## 下載

到 [Releases 頁面](https://github.com/caiderek/LogCarver/releases/latest) 下載最新的 `LogCarver.exe`(單一檔案、內含執行環境),目標機器不需要另外安裝 .NET。

## 狀態

**可用的 MVP。** 連上執行中的 SQL Server 實例,解碼一張表完整的 INSERT / UPDATE(前後值)/ DELETE 歷程,會防範 schema-drift 陷阱(用新的表結構去解舊資料列),支援篩選事故時間窗、單筆資料歷史查詢、Undo / Replay SQL 產生,以及時間點快照。想了解原理可以看 [`docs/原理說明.md`](docs/原理說明.md)。

## 使用前提

- 需要一台 SQL Server,且連線帳號要有 `sysadmin` 或 `db_owner` 等級的權限 —— `fn_dblog` 需要較高權限才能查詢。
- **目前只驗證過 SQL Server 2025、2022、2019、2016(SP2)。** 偵測到未驗證的版本時工具會警告而不是默默解錯,但其他版本的輸出結果請先謹慎對待,等確認過再依賴它。
- 預設用 Windows 整合式驗證(Integrated Security),也可以用 `--user`/`--password` 走 SQL 帳號密碼登入(見下方[使用方式](#使用方式)),適合非網域主機或不支援整合式驗證的伺服器。

## 目前範圍

- 只支援 SQL Server,透過 `fn_dblog` 連線到執行中的實例
- 完全在本機執行 —— 不會對外連線,資料不會離開執行這個工具的機器
- **目前能解碼的欄位型別:**`int`、`bigint`、`smallint`、`tinyint`、`bit`、`uniqueidentifier`、`date`、`datetime2`(任意精度 0–7)、`datetime`、`smalldatetime`、`decimal`/`numeric`(任意精度 1–38)、`money`、`smallmoney`、`float`、`real`、`char`、`nchar`、`varchar`、`nvarchar`、`varbinary`、`binary`——也包含 `geography`/`geometry`/`hierarchyid`,因為 SQL Server 內部把它們跟 `varbinary`用同一種方式儲存。其他型別(例如單獨的 `time`、`xml`)會明確拒絕解碼,不會用猜的 —— 這些欄位所在的列會顯示 `not shown - ... is not implemented yet`。
- 同樣採取「明確偵測並拒絕、而非用猜的」原則:壓縮表(ROW/PAGE)、off-row LOB 欄位、早於某次改表結構(schema-changing DDL)的舊紀錄
- **已知的「過度拒絕」限制,不是解錯資料的風險**:`TRUNCATE TABLE` 產生的 log 紀錄類型(`LOP_HOBT_DDL`)跟真正改變欄位結構的 `ALTER TABLE` 是同一種,目前工具分不出兩者的差別——所以 `TRUNCATE` 會被當成改表結構的邊界,連帶讓它之前所有事件都被拒絕顯示,即使 `TRUNCATE` 根本沒有改變欄位配置。那些被拒絕的歷史紀錄其實是可以正確解碼的,只是這個工具現在還沒把它們秀出來。
- 離線解析 `.ldf` 檔案(救回 `fn_dblog` 已經看不到、但實體上還沒被覆寫的資料)不包含在這個免費工具裡——這是付費版 **LogCarverOffline** 的差異化能力,詳見下方[離線救援](#離線救援)。

## 免責聲明

LogCarver 永遠只會**印出建議的 SQL**(`--undo`/`--replay`)—— 它不會用寫入意圖連線,也不會自己執行任何東西。請務必先審閱產生的 SQL,並在非正式環境測試過再用。本工具不保證解碼結果一定正確,尤其牽涉合規或財務用途時,請自行獨立驗證救回的資料。完整免責聲明見 [LICENSE](LICENSE)(MIT 授權)。

## 使用方式

```
LogCarver.exe <server> <database> <schema.table> [--from <datetime>] [--to <datetime>] [--key <Column>=<Value>] [--undo] [--replay] [--snapshot <datetime>] [--user <name> --password <pw>]
```

| 旗標 | 效果 |
|---|---|
| `--from` / `--to` | 只篩選*印出來*的事件落在這個事故時間窗內。重建過程仍然會用該表完整的觀察歷程,所以就算窗口很窄,前後值還是準確的。 |
| `--key <Column>=<Value>` | 只顯示某一列的完整歷史。會比對每個事件的前值或後值,所以不管該欄位在那次事件裡有沒有變,都找得到這一列。 |
| `--undo` | 針對每個顯示的事件,印出一句「還原」用的建議 SQL。`WHERE` 子句會比對所有觀察到的欄位,不只是主鍵,所以如果這列在 LogCarver 看到之後又被改過,這句 SQL 執行起來會是安全的空操作。 |
| `--replay` | 印出「正向重現」該事件的建議 SQL。跟 `--undo` 一樣有安全的 `WHERE` 子句。 |
| `--snapshot <datetime>` | 重建那個確切時間點每一列的樣子,而不是列出事件清單。會忽略 `--from`/`--to`/`--key`/`--undo`/`--replay`。 |
| `--user <name>` / `--password <pw>` | 改用 SQL 帳號密碼登入,不用目前的 Windows 帳號。兩個要嘛一起給、要嘛都不給。密碼在執行期間會出現在你的 shell 紀錄跟這個行程的命令列裡——能用 Windows 驗證就盡量用。 |

範例:

```
LogCarver.exe localhost MyDatabase dbo.Orders
LogCarver.exe localhost MyDatabase dbo.Orders --from "2026-09-23T09:00" --to "2026-09-23T10:00"
LogCarver.exe localhost MyDatabase dbo.Orders --key Id=5 --undo
LogCarver.exe localhost MyDatabase dbo.Orders --snapshot "2026-09-23T09:30"
LogCarver.exe localhost MyDatabase dbo.Orders --user sa --password "..."
```

## 為什麼會看到「0 row event(s)」?

**簡單說:SIMPLE 復原模式下,VLF 輪替可能幾秒內就完成,資料幾乎馬上就從 `fn_dblog` 消失——這種狀況正是 LogCarverOffline 要解決的。**

剛改完資料,LogCarver 卻查不到任何事件?這不是 bug,是 SQL Server 自己已經不記得那段歷史了——就算你手動下 `SELECT * FROM fn_dblog(NULL, NULL)` 去查,一樣也是空的。

`fn_dblog` 能看到的,永遠只有交易記錄檔裡**目前還在用的 VLF**(virtual log file)。SQL Server 做完 checkpoint、確認沒有任何交易、記錄檔備份或複寫還卡著某個舊的 VLF,就會把它標記成「可重用」——而這個 VLF 裡**所有**的東西都會跟著從 `fn_dblog` 消失,不只是你正在查的那筆操作。**SIMPLE** 復原模式下沒有記錄檔備份這道防線幫忙延後,checkpoint 一做完可能幾秒內就發生。一個活動量不高、規模不大的 SIMPLE 資料庫,常常在你都還沒查完之前,就已經把整段記錄檔(包括當初新增這筆資料的 `INSERT`,不只是後來那筆 `DELETE`)輪替覆蓋掉了。

這正是實測遇到的狀況:SIMPLE 復原模式的資料庫刪掉一筆資料,沒過多久就拿 LogCarver 去查,結果是 `0 row event(s)`。原因很單純——checkpoint 早一步把相關 VLF 標成可重用,`fn_dblog` 連那筆刪除、跟更早的那筆新增,一起忘光了。

關鍵在於:「標記為可重用」跟「已經被實體覆寫」是兩回事。位元組很可能原封不動地還躺在 `.ldf` 檔案裡,只是 SQL Server 不再讓你看到而已。`fn_dblog` 看得到的範圍,跟實際上還救得回來的範圍,兩者之間的落差就是下面 **LogCarverOffline** 要補上的地方。

## 離線救援

如果 `fn_dblog` 對某張表什麼都查不到,通常代表相關的 VLF 已經被標記為可重用、視窗已經滾過去了 —— 但資料很可能實體上還留在 `.ldf` 檔案裡。**[LogCarverOffline](https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1)** 是建構在同一套解碼引擎上的付費工具,直接讀取 `.ldf` 原始位元組,即使資料庫已經離線或 detach,通常仍能救回這種情況。提供[免費試用版](https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW)(不用序號,可看前 10 筆救回的事件)——先確認救得回你要的資料再購買。有問題可以寄信到 `logcarveroffline@gmail.com`。

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

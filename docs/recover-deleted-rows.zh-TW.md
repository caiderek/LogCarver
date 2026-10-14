---
title: SQL Server 誤刪資料、沒有備份,怎麼救回來
description: "SQL Server 資料被 DELETE 或 UPDATE 改掉,沒有備份、也沒開 CDC 或稽核,還救得回來嗎?從交易記錄檔讀出被刪的資料列,以及要在記錄檔被回收之前做的事。"
---

# SQL Server 誤刪資料、沒有備份,怎麼救回來

**[English](recover-deleted-rows.md) | 繁體中文**

這頁講的是沒有備份的情況。如果你有完整備份加上連續的交易記錄備份,直接還原到出事之前的時間點(`RESTORE LOG ... WITH STOPAT`)是最穩的做法,不用往下看。

## 結論

DELETE 和 UPDATE 有機會救回來。SQL Server 的交易記錄是關不掉的,每一次 INSERT、UPDATE、DELETE 都會先寫進去才改資料檔,所以被刪的那一列原本的內容通常還在記錄檔裡。

但是隨著使用時間，記錄檔會被回收再利用,一旦回收,SQL Server 自己就查不到了。所以第一件事是別讓它被回收。

## 現在先做的事

下面這幾點我在測試資料庫上實測過:

1. **不要手動下 `CHECKPOINT`。** SIMPLE (簡單)復原模式下,checkpoint 一做完,被刪資料的紀錄就從 `fn_dblog` 消失了。
2. **不要備份交易記錄。** FULL (完整) 復原模式下,備份交易記錄之後再有一些寫入,被刪資料的紀錄一樣查不到了。我測的時候,刪除之後第一次備份,紀錄還在,但再多寫一些資料、再備份一次之後就沒了。沒辦法保證哪一次會清掉,保守一點就是都不要做，這邊通常都會很違反DBA的做法，但保險一點就是這樣做。
3. **盡量少寫入這個資料庫。** 寫得越多,記錄檔越快被回收。

## 確認記錄檔裡還有沒有

```sql
USE YourDatabase;

SELECT COUNT(*) AS delete_records
FROM fn_dblog(NULL, NULL)
WHERE Operation = 'LOP_DELETE_ROWS'
  AND AllocUnitName LIKE 'dbo.YourTable%';
```

`fn_dblog` 需要高權限,通常要 sysadmin。回傳大於 0 就代表那張表的刪除紀錄還在。回傳 0 的話,先看下面「限制」。

## 把被刪的資料讀出來

`fn_dblog` 給的是原始的位元組,不是一欄一欄的值,要自己解碼才看得懂。[LogCarver](https://github.com/caiderek/LogCarver) 是我做的免費開源工具,就是做這件事:

```
LogCarver.exe localhost YourDatabase dbo.YourTable
```

輸出會像這樣(這是我在測試資料庫刪掉 3 筆之後跑出來的):

```
[0000002F:000001B0:0002 2026-10-13 15:37:42] DELETE: Id=3, Amount=100, Customer=cust-3
[0000002F:000001B0:0004 2026-10-13 15:37:42] DELETE: Id=4, Amount=100, Customer=cust-4
[0000002F:000001B0:0005 2026-10-13 15:37:42] DELETE: Id=5, Amount=100, Customer=cust-5
```

加上 `--undo` 會印出建議的還原 SQL。工具只會印出來,不會自己執行。拿到之後請先在非正式環境測過再用。

## 限制

- **TRUNCATE TABLE 和 DROP TABLE 救不回來。** 這兩個動作是整頁檔案釋放,記錄檔裡不會有逐筆的刪除紀錄,不管用什麼工具都一樣。
- **記錄檔被回收之後,免費版看不到。** 付費版 [LogCarverOffline](faq.zh-TW.md#免費版-logcarver-跟付費版-logcarveroffline-差在哪) 直接讀 `.ldf` 檔案的位元組,只要那段位元組還沒被實際覆寫,有機會救回 `fn_dblog` 已經不回報的資料。
- **壓縮的資料表、存在列外面的 LOB 欄位會直接拒絕不做動作**,這個是已知的問題。
- **INSERT 已經不在記錄檔裡的資料列,UPDATE 會從該列目前的狀態倒推回去;做不到的會標示為需要人工檢查。** 工具會從資料頁(或之後的 DELETE 記錄)取得該列,再逐一還原記錄檔裡的變更,每一步都和記錄比對。找不到該列、或對不上時寧可拒絕也不猜。這部分在 SQL Server 2025 上驗證過,其他版本請先檢查輸出再採用。
- **只能在 Windows 上跑,連線帳號要有 sysadmin 或 db_owner 的權限。**

完整的限制和支援的欄位型別在 [README](https://github.com/caiderek/LogCarver) 和 [常見問題](faq.zh-TW.md)。

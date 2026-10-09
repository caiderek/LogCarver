---
title: LogCarver 常見問題
description: "SQL Server 沒有備份,誤刪的資料還能救回來嗎?fn_dblog 為什麼查不到剛刪除的資料?TRUNCATE、DROP TABLE 救得回來嗎?這個工具需要什麼權限?直接回答,包含誠實的限制。"
---

# LogCarver 常見問題

**[English](faq.md) | 繁體中文**

整理資料不見時大家真正會問的問題。每一題都可以獨立看——只想看跟自己狀況相符的那一題也沒問題。

## SQL Server 沒有備份、也沒開 CDC / Audit / Change Tracking,誤刪的資料還能救回來嗎？

可以,DELETE 跟 UPDATE 都有機會,只要交易記錄檔還留著相關紀錄就行。SQL Server 的交易記錄檔是關不掉的——不管你有沒有事先開任何稽核功能,每一次 INSERT/UPDATE/DELETE 都會先寫進去,才真的去改資料檔本身。[LogCarver](../README.md) 直接透過 `fn_dblog` 讀這份記錄檔,所以不需要事先設定什麼。

問題在時間:記錄檔只保留目前還在用的 VLF(virtual log file)裡的內容。在 SIMPLE 復原模式下,這個窗口可能在 checkpoint 之後幾秒內就關閉。詳見下一題,以及 [README 的「0 row event(s)」那一段](../README.zh-TW.md#為什麼會看到0-row-events)。

## `fn_dblog` 查不到我剛刪除的資料,為什麼？

這不是 bug——代表 SQL Server 自己已經不追蹤那段歷史了,就算你手動下 `SELECT * FROM fn_dblog(NULL, NULL)` 也一樣查不到。完整說明:[README → 為什麼會看到「0 row event(s)」](../README.zh-TW.md#為什麼會看到0-row-events)

簡單說:VLF 一旦被標記為可重用,`fn_dblog` 就會立刻停止回報裡面的所有東西——不只是你正在查的那筆刪除,連當初的 INSERT 都可能一起消失。但「標記為可重用」不等於「已經被實體覆寫」:位元組可能原封不動還留在 `.ldf` 檔案裡。這個落差正是付費版 **LogCarverOffline** 直接讀取的對象,見下方說明。

## `TRUNCATE TABLE` 的資料救得回來嗎？

**不行。** `TRUNCATE` 是整頁/整個 extent 的釋放動作,不會逐列記錄刪除,所以被清空的資料本身在交易記錄檔裡根本沒有列層級的紀錄可以解碼——免費版跟付費版都一樣。這是 SQL Server 記錄 `TRUNCATE` 這個動作本身的結構性限制,不是哪個版本規劃要補的功能。

另外有一個跟上面無關、值得知道的現況限制:`TRUNCATE` 產生的 log 紀錄類型(`LOP_HOBT_DDL`)跟真正改變欄位結構的 `ALTER TABLE` 是同一種,目前工具分不出兩者的差別。結果是 `TRUNCATE` *之前* 的歷史紀錄也會被當成「可能是舊版面」而一併拒絕顯示,即使 `TRUNCATE` 本身根本沒有改變欄位配置。那些被拒絕顯示的舊歷史其實是可以正確解碼的,只是工具現在還沒把它們秀出來。

## `DROP TABLE` 之後,資料救得回來嗎？

**不行**,原因跟 `TRUNCATE` 一樣:砍掉整張表會釋放它的頁面,不會逐列記錄刪除動作。交易記錄檔裡沒有列層級的紀錄可以解碼。

## UPDATE 之前的原始值查得到嗎,不只是改之後的值？

可以。SQL Server 的 UPDATE log 紀錄只存改動的那一小段位元組(差異片段,不是整列重存),所以 LogCarver 會從這一列當初的 INSERT 開始,依序套用每一次 UPDATE,重建出完整歷程——同時給你改之前跟改之後的值。用 `--key <Column>=<Value>` 可以看單一列的完整歷史,用 `--snapshot <datetime>` 可以看某個時間點所有列的樣子。

## 這個工具需要什麼權限？會不會動到我的資料庫？

連線帳號要有 `sysadmin` 或 `db_owner` 等級的權限——這是 `fn_dblog` 本身查詢就需要的高權限,跟 LogCarver 做了什麼無關。除此之外:

- **唯讀。** LogCarver 永遠不會用寫入意圖連線。`--undo`/`--replay` 只會**印出**建議的 SQL,不會自動執行任何東西。產生的 SQL 請先審閱,並在非正式環境測試過再用。
- **不連網路。** 完全在本機執行,資料不會離開執行這個工具的機器。

## 目前實際驗證過(不是憑經驗推測)的 SQL Server 版本是哪些？

見 [README → 使用前提](../README.zh-TW.md#使用前提) 目前的清單。偵測到未驗證的版本時,工具會警告而不是默默解錯——但其他版本的輸出結果請先謹慎對待,等確認過再依賴它。

## 目前能解碼哪些欄位型別？

見 [README → 目前範圍](../README.zh-TW.md#目前範圍) 目前的清單。沒列在裡面的型別會明確拒絕解碼,不會用猜的——那一欄會顯示清楚的 `not shown - ... is not implemented yet` 標記,而不是悄悄給你一個錯的值。

## 免費版 LogCarver 跟付費版 LogCarverOffline 差在哪？

兩者用同一套解碼引擎。免費版 CLI 透過 `fn_dblog` 讀**執行中**的 SQL Server 實例,只能看到記錄檔目前還在用的 VLF 裡的內容。**[LogCarverOffline](https://buy.polar.sh/polar_cl_tEgu9FbaX6cGO3dorFdFUPp8kK9F32RVHG5op1Gavh1)** 則直接離線讀取 `.ldf` 檔案的原始位元組,只要底層位元組還沒被實際覆寫,通常還能救回 `fn_dblog` 已經不再回報的資料。提供[免費試用版](https://buy.polar.sh/polar_cl_OOcUPxj6yjJYwHRYqBrMixDGa3r3HVapldYVu4PtSeW)(不用序號,可看前 10 筆救回的事件),先確認救得回你要的資料再購買。

兩個版本都救不回 `TRUNCATE` 或 `DROP` 清空的資料——見上方說明,這是記錄機制本身的限制,不是功能缺口。

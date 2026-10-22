# Changelog

## [2.0.0] — 2026-10-22

**行為的 major,不是 API 的 major。** 公開型別與成員、資料庫 schema、相依樓地板(EF Core 8.0.11、
Roslyn 4.8)都沒有變:以反射比對 v1.1.0 的 net8.0 與 netstandard2.0 組件,公開表面逐行相同,升級不必改任何簽章,
也不需要 migration。升 major 是因為兩項語意改動會讓 1.x 的部分呼叫改變結果——以名稱查詢的四支 API 改依資料庫
collation 比對名稱、`UpdateAsync` 的 lambda 只能改目標標籤本身——加上 registry 封存後整個不可變
(`LabelStoreFactory.Create` 也跟 `AddLabeling` 一樣封存),以及幾項對外可見的例外型別與訊息改變。
其餘是 bug 修正:寫入失敗時把自己動過的追蹤器還原、`UpdateAsync` 的循環檢查看得到導覽屬性、
EF Core 10 上幾萬筆的批次與大子樹不再撞參數上限等。

### 破壞性變更與遷移

1. **`FindByLabelAsync`／`FindByLabelsAsync`／`QueryByLabelAsync`／`QueryByLabelsAsync` 依資料庫 collation 比對名稱。**
   只影響不分大小寫的 collation(SQL Server 預設、`NOCASE` 的 SQLite):大小寫不同的名稱以前查不到(回傳 0 筆、
   `LabelMatch.All` 整個變空),現在跟 `FindAsync`、貼標、撕標一樣查得到。區分大小寫的 collation(SQLite 與 PostgreSQL
   的預設)上結果不變。
   - 舊:為了讓查詢對得上,先 `FindAsync(name)` 取回儲存的寫法再傳給 `FindByLabelAsync(label.Name)`。
     新:直接 `FindByLabelAsync(name)`,舊寫法照樣可用。
   - 舊:依賴「大小寫要完全一樣才查得到」。新:把 `Label.Name` 設成區分大小寫的 collation
     (`b.Entity<Label>().Property(l => l.Name).UseCollation("…")`,在 `ApplyLabelModel` 之後),
     名稱的唯一性、`FindAsync`、貼標與這四支查詢會一起改成區分大小寫——套件不再有只對其中幾支成立的比對規則。
2. **`UpdateAsync` 的 lambda 只能改目標標籤本身;別的標籤只能經 `Parent`／`Children` 搬移。** 改了別的標籤
   `ParentId` 以外的欄位、帶進新標籤、刪掉別的標籤,1.x 照樣存檔(或在資料庫才拋 `DbUpdateException`),
   現在拋 `InvalidOperationException`(`update may only change the label being updated (and move other labels through Parent or Children), but it …`),
   什麼都不存。
   - 舊:`UpdateAsync(child.Id, l => { l.Name = "tmp"; l.Parent!.Name = "a"; })`。
     新:`await store.RenameAsync(child.Id, "tmp"); await store.RenameAsync(parentId, "a");`
   - 舊:`UpdateAsync(child.Id, l => l.Parent!.Color = "#000000")`。新:`UpdateAsync(parentId, l => l.Color = "#000000")`。
   - 舊:`UpdateAsync(p.Id, l => l.Children.Add(new Label { … }))`。新:`CreateAsync(name, parentId: p.Id)`。
   - 舊:lambda 裡 `db.Remove(other)`。新:`DeleteAsync(other.Id)`。
   - 照常可用:`l.Parent = x`、`l.Children.Add(既有的標籤)`、經導覽屬性讓別的標籤換父標籤(照樣檢查循環),
     以及你在呼叫前自己對別的標籤做、還沒存的修改。
3. **對外可見的例外型別與訊息**(只影響接住或斷言這些例外的程式碼):
   - `AddLabeling(configure: null)`、`ApplyLabelModel(null, …)`／`ApplyLabelModel(b, null)`:
     `NullReferenceException` → `ArgumentNullException`(`ParamName` 為 `configure`、`b`、`registry`)。
     舊 `catch (NullReferenceException)` → 新 `catch (ArgumentNullException)`。
   - `UpdateAsync` 的 lambda 把同 Id 的另一個 `Label` 實例放進導覽屬性:型別仍是 `InvalidOperationException`,
     訊息從 EF 的身分衝突換成 `the update put another Label instance with Id … into a navigation; use the instance this context already tracks.`
   - `UpdateAsync` 的 lambda 改了別的標籤的 `Id`:訊息從 EF 的「key 不可修改」換成與改目標 `Id` 相同的
     `a label's Id must not be changed inside UpdateAsync.`
   - `CHSU001` 對巢狀與泛型型別的訊息改成照抄得進註冊呼叫的寫法(`Type 'Outer.Inner' …`、`Type 'Box<T>' …`);
     頂層非泛型型別的訊息一字不變。
4. **`GetLabelsOfManyAsync` 的排序改跟資料庫 collation 走**(與 `GetLabelsOfAsync`／`GetAllAsync` 一致)。
   SQL Server 預設、PostgreSQL 語言學 collation 上,`SortOrder` 相同、大小寫混雜的名稱順序會改變。
   要 1.x 的順序:`labels.OrderBy(l => l.SortOrder).ThenBy(l => l.Name, StringComparer.Ordinal)`。
5. **registry 封存後整個不可變,而且 `LabelStoreFactory.Create` 也會封存。** 兩條路徑一致:`AddLabeling` 跑完 `configure`、
   或第一次 `LabelStoreFactory.Create` 之後,再呼叫 `registry.Labelable<T>()` 拋 `InvalidOperationException`
   (`LabelRegistry is sealed; no further types can be registered.`),再設定 `LabelTableName`、`LinkTablePrefix`、
   `AutoCreateLabels` 也拋(`LabelRegistry is sealed; {屬性名} can no longer be changed.`)。1.x 的 `Create` 不封存,
   兩條路徑的三個屬性也都隨時可改。
   - 舊:`var store = LabelStoreFactory.Create(db, registry); registry.Labelable<Extra>();`
     新:先註冊再建 store——`registry.Labelable<Extra>(); var store = LabelStoreFactory.Create(db, registry);`
   - 舊:`var store = LabelStoreFactory.Create(db, registry); registry.AutoCreateLabels = false;`
     新:`var registry = new LabelRegistry { AutoCreateLabels = false };`,或至少在第一次 `Create` 之前設定。
   - 舊:`services.AddLabeling<AppDbContext>(r => r.Labelable<Note>());` 之後再從容器取出
     `LabelRegistry` 改 `LabelTableName`／`LinkTablePrefix`／`AutoCreateLabels`。
     新:全部寫在 `configure` 裡——`services.AddLabeling<AppDbContext>(r => { r.Labelable<Note>(); r.AutoCreateLabels = false; });`
   - 舊:執行中切換 `AutoCreateLabels`(例如匯入時暫時開啟自動建立)。新:不再支援;需要的流程先用
     `CreateAsync` 建好標籤再貼,或在 `AutoCreateLabels = false` 時接住列出缺漏名稱的例外。
   - 型別或設定分散在各模組的話,在第一次 `Create`(也是第一個 DbContext 建 model)之前全部收齊;
     registry 照舊是全 App 一份,之後每個 DbContext 各自 `Create` 不受影響。

### 公開 API 與相依

- 公開型別與成員:與 v1.1.0 相同(反射逐行比對 `Cornhsu.Labeling` net8.0／netstandard2.0 與
  `Cornhsu.Labeling.EntityFrameworkCore` net8.0 的型別、方法、屬性、欄位、參數預設值,無差異)。
  XML 註解有更新(見下)。
- 相依樓地板、TFM、資料庫 schema、analyzer 規則 ID 與嚴重度:不變。
- `Cornhsu.Labeling.EntityFrameworkCore` 多了 `InternalsVisibleTo("Cornhsu.Labeling.Tests")`,只給本 repo 的測試
  直接量內部的效能防線,不是公開介面。

### Fixed
- **`UpdateAsync` 被擋下時會還原 lambda 做的修改**。lambda 改的是 DbContext 追蹤中的實體,
  以前驗證失敗只拋例外,那份修改留在追蹤器裡,下一次 `SaveChanges`(store 的任何寫入,或你自己存檔)
  就把它寫進資料庫:被擋下的父子循環、空白名稱、超長名稱都能這樣落地;名稱已被占用時下一次存檔撞唯一索引;
  改 `Id` 被擋下之後,那個 context 每次存檔都拋「key 不可修改」。現在驗證失敗、lambda 自己拋例外、
  或存檔失敗時,追蹤器裡每個標籤都還原成呼叫前的樣子(你在呼叫前自己做、還沒存的修改照樣保留),
  包括 lambda 經導覽屬性做、還沒寫回外鍵的修改,以及被改掉的主鍵;lambda 經導覽屬性帶進的新標籤
  會 detach,並從其他標籤的 `Children` 裡拿掉。lambda 自己拋的例外原樣傳出。
  名稱要不要驗證改成跟資料庫裡的名稱比,不再被追蹤器裡的髒值跳過。例外型別與訊息不變。
- **`UpdateAsync` 的循環檢查看得到導覽屬性**。以前只看 `ParentId`,而 EF Core 要到存檔時
  才把 `l.Parent = x`、`x.Children.Add(l)` 寫回 `ParentId`,檢查當下看到的是舊值:自我父節點與父子循環
  都會直接寫進資料庫,成環之後連刪除都做不到。現在先偵測標籤的變更再檢查,而且祖先鏈會疊上同一個
  context 裡其他標籤還沒存的 `ParentId`(它們會在同一次存檔一起送出);lambda 經導覽屬性換了別的標籤的
  父標籤,那些標籤也一起檢查。沿用既有的循環例外與訊息,只多擋下本來就會成環的寫入。
  README 的「`UpdateAsync` 只依 `ParentId` 檢查循環」限制已移除。
- **`UpdateAsync` 的 lambda 把「同 Id 的另一個 Label 實例」放進導覽屬性時,store 先擋下、context 不會壞掉**。
  例如 `l.Parent = new Label { Id = 已追蹤的那個 Id, … }`。以前由 EF 偵測時拋身分衝突;
  在 EF Core 10 上衝突的那一筆會以 Detached 留在追蹤器,之後每一次存檔都拋 `ArgumentOutOfRangeException`,
  context 只能丟掉(v1.1.0 + EF 10 一樣)。現在 store 在自己偵測之前就找出來,拋 `InvalidOperationException`
  (`the update put another Label instance with Id … into a navigation; use the instance this context already tracks.`),
  把那條連結剪掉再還原,context 照常可用。例外型別不變,訊息從 EF 的換成 store 的(見上方破壞性變更 3)。
- **`UpdateAsync` 的 lambda 經 `Parent`／`Children` 搬移別的標籤時,那些標籤的並發戳記也輪換**。
  以前只輪換目標標籤的戳記:另一邊拿舊戳記搬同一個標籤會默默蓋掉這次的搬移。現在被搬的標籤也輪換,
  後存檔的一方拿到 `DbUpdateConcurrencyException`;衝突時被搬的標籤若呼叫前沒有你自己的未存修改,store 會把它重讀,
  重試就能成功。
- **`QueryByLabelAsync`／`QueryByLabelsAsync` 對不上任何標籤時,回傳的也是 EF Core 查詢**。
  以前名稱都不存在、全是空白,或 `LabelMatch.All` 下缺任一名稱時,回傳的是記憶體裡的
  `Enumerable.Empty<T>().AsQueryable()`,接著呼叫 README 教的 `ToListAsync`／`CountAsync`／`AnyAsync`
  就拋 `InvalidOperationException`——還沒有任何標籤的新資料庫第一次查就會遇到。現在回傳 EF 支撐的空查詢,
  非同步運算子與續接的 Where/OrderBy/Skip 都照常可用。回傳型別仍是 `IQueryable<T>`;
  README 以前建議的 `query is IAsyncEnumerable<T>` 判斷現在永遠為 true,留著也不會壞,可以拿掉。
  README 的對應限制段已移除。
- **寫入方法存檔失敗時,撤掉自己放進 DbContext 的變更**。store 直接呼叫你的 context 的
  `SaveChangesAsync`,以前失敗就把例外往外丟,自己加進追蹤器的東西原封不動留著:`CreateAsync` 輸掉同名競態
  (或 SQL Server／PostgreSQL 上 `Color`／`Icon` 超長)之後,那筆新增中的標籤讓之後每一次存檔都撞同一個錯;
  `AttachAsync` 的連結違反外鍵之後也一樣;`UpdateAsync`／`RenameAsync` 拋並發例外之後,同一個 store 重讀
  拿到的是沒存進去的值,重試也永遠失敗——README 教的「接住、重讀、重試」在同一個 scope 裡做不到。
  現在 `CreateAsync`、`RenameAsync`、`UpdateAsync`、`DeleteAsync`、`AttachAsync`、`AttachManyAsync`、
  `DetachAsync` 與自動建立標籤那一步失敗時,都會先還原自己動過的項目(`DeleteAsync` 連 EF 當下 cascade 掉的
  連結、被設成 null 的外鍵一起還原),遇到並發衝突再把衝突的標籤或連結從資料庫重讀,然後原例外照拋。
  你自己在同一個 context 裡還沒存的變更不會被動到——衝突若出在你自己改過、還沒存的別的項目上,store 不會重讀它。
  唯一的例外是這次要寫的那個標籤:並發衝突時它一律重讀,連你自己對它還沒存的修改也一起丟掉,
  否則舊戳記留在追蹤器裡,重試永遠撞同一個衝突。存檔已經提交、例外是之後才拋的(例如你的 `SavedChanges`
  攔截器出錯)時不還原:寫進去的就是寫進去了,追蹤器維持提交後的樣子——但你自己開了交易(顯式交易或
  `TransactionScope`)時一律還原,因為那時還沒真的提交,你可能接著 rollback:連 EF 已經接受的原值與並發戳記
  一起放回呼叫前的樣子,rollback 之後透過同一個 store 重試就能成功(1.1.0 會撞並發例外)。同一次存檔裡
  被 EF 接受的、你自己的修改不在此列;context 帶著這種修改的話,rollback 之後仍照 EF Core 的建議丟掉它。
  例外型別與訊息不變。
- **context 關掉自動偵測變更(`ChangeTracker.AutoDetectChangesEnabled = false`)時,`RenameAsync`／`UpdateAsync`
  也會存檔**。這兩支直接改追蹤中的 `Label` 屬性,以前靠 `SaveChanges` 自己偵測變更;關掉之後改名、
  改欄位都靜默地沒有寫進資料庫,並發戳記也沒輪換。現在存檔前會偵測標籤的變更:`RenameAsync` 只偵測它改的
  那個標籤;`UpdateAsync` 會偵測追蹤中的每一個標籤(lambda 可能經導覽屬性動到別的標籤,循環檢查要看得到),
  所以你自己對其他標籤做、還沒偵測的修改,也會被這次存檔一起送出。其他型別的實體不會替你偵測。
- **`AttachAsync`／`AttachManyAsync` 自動建立標籤前,先驗證完所有要建立的名稱**。以前逐一「查詢、驗證、
  建立、存檔」,`AttachAsync(note, "ok", <65 字的名稱>)` 拋 `ArgumentException` 時,`ok` 已經建立並存檔,
  那次存檔還一起送出了 context 裡其他未存的變更。現在先查完全部名稱、驗證完要建立的名稱,才開始寫入;
  參數不合法就什麼都不建立、不存檔。`AutoCreateLabels = false` 的行為不變(缺的名稱照舊一次列出,不做長度檢查)。
  例外型別與訊息不變。
- **`AttachManyAsync`／`GetLabelsOfManyAsync` 一次給幾萬個實體時分批查詢**。EF Core 10 把 `Contains`
  展開成一個值一個參數,超過資料庫的參數上限就拋例外:EF 10 + SQLite 上 4 萬個實體拋 `too many SQL variables`
  (SQLite 上限 32,766;SQL Server 是 2,100)。現在依實體每 1,000 個查一批,對所有 provider 都在上限之內;
  結果、排序與冪等都不變,4 萬條連結的耗時跟一次查差不多(本機 EF 8 約 2.0 s、EF 10 約 2.4 s)。這是 1.1.0 + EF 10 就有的問題。
- **以名稱查詢時,子樹裡有幾萬個標籤也照常完成**。`FindByLabel(s)Async`／`QueryByLabel(s)Async` 把名稱連同子孫
  展開成標籤 Id 集合再查連結,EF Core 10 上同樣一個 Id 一個參數:單一子樹超過約 32,000 個標籤(SQLite)或 2,100 個(SQL Server)
  就拋例外。`QueryByLabelsAsync` 回傳的是讓你接著組合的 `IQueryable`,只能是一條 SQL、不能分批往返,
  所以 Id 超過 1,000 個時改用 `EF.Parameter`(EF Core 9 起才有)把整個集合當成一個參數送出——這正是 EF Core 8/9 的預設做法;
  EF Core 8 本來就是一個參數,不受影響。數量少時照舊用 EF 的預設。這也是 1.1.0 + EF 10 就有的問題。
- **`GetLabelsOfManyAsync` 的排序改在資料庫做,與 `GetLabelsOfAsync`／`GetAllAsync` 一致**。
  以前名稱在記憶體用 Ordinal 排,不分大小寫的 collation 上 `SortOrder` 相同的標籤,同一個實體用兩種讀法
  會排得不一樣(`B-upper, a-lower` 對 `a-lower, B-upper`)。現在批次讀的順序一律跟資料庫的 collation 走:
  SQL Server 預設、PostgreSQL 的語言學 collation 上,大小寫混雜的名稱順序會改變;SQLite 預設的 BINARY
  幾乎不受影響,只有 BMP 以外的字元與 U+E000–U+FFFF 之間的相對順序會改成跟資料庫一致。
  README 的對應例外已移除。
- **`CHSU001` 不再對帶型別引數的註冊誤報;巢狀與泛型型別的訊息改成照抄得進註冊呼叫的寫法**
  (訊息文字見上方破壞性變更 3)。
  - 誤報:`r.Labelable<Box<int>>()`(`class Box<T> : ILabelable<int>`)與 `r.Labelable<Outer<int>.Note>()`
    (泛型類別裡的巢狀實體)以前照樣報 `CHSU001`,因為註冊收到的是建構型別、比對的是宣告,兩者永遠不相等。
    現在比的是宣告:任何一個建構型別有註冊,宣告就算有註冊。為了這兩種情況加的 `#pragma warning disable CHSU001`
    或 `.editorconfig` 抑制可以拿掉。跨組件註冊、經泛型 helper 註冊仍然會誤報(本質限制,README 有寫)。
  - 訊息:以前 `{0}` 是 `type.Name`,對巢狀型別建議 `r.Labelable<Inner>()`、對泛型型別建議 `r.Labelable<Box>()`,
    都編譯不過。現在帶外層型別與型別參數:`Type 'Outer.Inner' …`、`Type 'Box<T>' …`、`Type 'Outer<T>.Note' …`。
    頂層非泛型型別(絕大多數情況)的訊息一字不變。規則 ID、嚴重度、分類、`CHSU002` 的訊息都不變。
    有人 grep 或斷言巢狀型別的 `CHSU001` 訊息的話要跟著改。
- **`AddLabeling` 與 `ApplyLabelModel` 的 null 參數改拋 `ArgumentNullException`**(對外可見的例外型別改變,見上方破壞性變更 3)。
  以前 `AddLabeling(configure: null)`、`ApplyLabelModel(b 或 registry: null)` 拋的是 `NullReferenceException`;
  `AddLabeling(services: null)` 雖然拋 `ArgumentNullException`,但 `configure` 已經先執行完、registry 也已封存。
  現在進入方法就檢查,`ParamName` 分別是 `services`、`configure`、`b`、`registry`,與 store 的其他公開入口一致。
  只影響本來就是程式錯誤的呼叫;有人接 `NullReferenceException` 的話要改接 `ArgumentNullException`。

### Changed
- **`FindByLabelAsync`／`FindByLabelsAsync`／`QueryByLabelAsync`／`QueryByLabelsAsync` 改在資料庫端比對名稱,
  跟其他吃名稱的方法一樣依 collation 走**(破壞性,遷移見上方 1)。以前這四支把所有標籤撈回記憶體、
  以 Ordinal(區分大小寫)比對名稱:在不分大小寫的 collation 上(SQL Server 預設、`NOCASE` 的 SQLite),
  `AttachAsync(note, "Paper")` 沿用了既有的 `paper` 標籤、`FindAsync("Paper")` 也找得到它,
  `FindByLabelAsync("Paper")` 卻靜默地回傳 0 筆,`LabelMatch.All` 下只要一個名稱大小寫不同整個結果就是空的。
  現在每個名稱交給資料庫比對,`FindAsync` 找得到的標籤,這四支就查得到;子孫照舊沿階層展開。
  README Limitations 的例外段與 XML 的 `<remarks>` 已移除。查詢改成每個名稱一次查詢(含子孫時再讀一次階層),
  不再一次撈出全部標籤的名稱;`samples/Benchmark` 前後量測的差異在雜訊範圍內。
- **`UpdateAsync` 的 lambda 只能改目標標籤本身;別的標籤只能經 `Parent`／`Children` 搬移**(破壞性,遷移見上方 2)。
  lambda 拿到的是追蹤中的實體,經導覽屬性碰得到別的標籤。以前改了什麼都照樣存檔:`l.Parent!.Name = "   "`
  把空白名稱寫進父標籤、別的標籤被改了欄位卻不輪換並發戳記(兩邊同時改,後存的一方不會拿到並發例外)、
  經導覽屬性帶進的新標籤沒有建立時間與戳記(主鍵已設的還會被當成既有的標籤下 UPDATE、影響 0 列而拋並發例外)。
  現在 lambda 執行完先比對它前後的差異:改了別的標籤 `ParentId` 以外的欄位、帶進新標籤、刪掉別的標籤,
  都拋 `InvalidOperationException` 並照既有機制還原,什麼都不存。改了別的標籤的 `Id` 也一樣擋下,
  訊息與改目標 `Id` 相同。你在呼叫前自己做、還沒存的修改不受限制,照舊隨這次存檔送出;
  lambda 只是載入進來讀的標籤也不算修改。1.x 為了讓「目標讓出名字、別的標籤接手」這類寫法繼續成功而加的
  其他標籤名稱驗證與讓出判斷,隨之整段拿掉。
- **registry 封存後整個不可變;`LabelStoreFactory.Create` 建完 store 之後也封存**(破壞性,遷移見上方 5)。
  以前只有 `AddLabeling` 封存,而且封存只擋註冊型別:不經 DI 的路徑在 `Create` 之後還能註冊型別,
  兩條路徑都還能改 `LabelTableName`／`LinkTablePrefix`,都不會報錯。但 EF Core 的 model 以 DbContext 型別快取,
  model 建好之後才註冊的型別不在 model 裡、改過的表名或前綴跟快取的 model 對不上,要到貼標或查詢時才以
  缺表之類的資料庫錯誤冒出來,或者完全沒有錯誤訊息。現在封存後註冊型別與設定三個屬性都拋例外,錯誤出現在寫錯的那一行;
  `AutoCreateLabels` 不影響 model,一併鎖住是為了讓「封存後整個不可變」沒有例外。
  參數檢查失敗(`context` 或 `registry` 為 null)時不封存。
- **`UpdateAsync` 每次呼叫多了掃過追蹤中標籤的成本**(行為不變)。還原與循環檢查要求在 lambda 前後各看一遍
  追蹤中的標籤;追蹤 1,000 個標籤、另加 10,000 個其他實體時,每次呼叫約 4–6 ms(1.1.0 約 2.5 ms)。
  內部列舉時暫時關掉自動偵測、只偵測標籤,不再每次都掃整個 context(修正前同樣的量測是 17–20 ms);
  README Performance 寫了這件事。
- **`ILabelStore.AttachManyAsync` 的 XML 註解更正**(只改文件)。以前寫 "everything is saved in a single
  SaveChanges",需要自動建立標籤時不成立:每個要建立的標籤各自先存一次,連結再一起存一次,整個操作不是原子的。
  IntelliSense 現在照實寫;README 早已改正。
- **`ILabelableDescriptor.TypeKey` 的 XML 註解更正**(只改文件)。以前寫它 "does not change when the class
  is renamed",只有註冊時用 `typeKey:` 釘住才成立;預設取類別名,改類別名就換表名,下一次 migration 會 drop 掉舊連結表。
  推導規則不變:改它會替每個沒釘 `typeKey` 的型別換表名,等於資料不見,2.0 也刻意不動。
- **README 與 `LabelRegistry.Labelable` 的 XML 寫明泛型實體一定要釘 `typeKey`**(只改文件)。
  沒釘的話預設取 CLR 名稱,`Box<int>` 得到 ``Box`1``:連結表名帶反引號,而且所有 `Box<…>` 共用這個鍵,
  第二個建構型別註冊時拋 ``TypeKey 'Box`1' is already registered.``。推導規則與例外訊息都不變。

## [1.1.0] — 2026-08-07

**對外語言統一為英文。** 公開 API 的簽名、行為、資料庫 schema 全部不變 —— 這一版只換
「使用者讀到的字」。升 minor 而非 patch:例外訊息與 analyzer 診斷文字是有人在斷言、
在 grep 的東西,換語言對他們是破壞性的。

### Changed
- **公開 API 的 XML 文件註解全面英文化。** 本套件開了 `GenerateDocumentationFile`,
  那些 `///` 註解會打包成 .xml 隨 nupkg 出貨 —— 也就是說,非中文使用者在 Visual Studio
  裡把游標移到 `AttachAsync` 上,跳出來的 IntelliSense 提示是中文。範圍涵蓋
  `ILabelStore`、`ILabelable`、`Label`、`LabelHit`、`LabelMatch`、`LabelRegistry`、
  `LabelLink`、`LabelStoreFactory`、`ILabelableDescriptor` 與兩個擴充方法類別。
  `Cornhsu.Labeling.xml` 已無任何中文。
  (`EfLabelStore` / `LabelableDescriptor` / `ILabelableOperations` 是 internal,註解維持中文 ——
  它們仍會出現在 `Cornhsu.Labeling.EntityFrameworkCore.xml` 裡,但消費端存取不到那些成員,
  IntelliSense 不會顯示。)
- **例外訊息英文化**(18 處):標籤名稱驗證、重複名稱、找不到標籤、階層循環、
  未註冊型別、`AutoCreateLabels` 停用時的提示等。這些會進消費端的錯誤日誌。
- **Analyzer 診斷文字英文化**:`CHSU001` / `CHSU002` 的 title 與 message ——
  它們顯示在 Visual Studio 的 Error List 裡。規則 ID、分類與嚴重度不變。
- **NuGet 套件 description 英文化**(兩個套件),它是 nuget.org 搜尋結果上的那行摘要。
- 程式碼內部註解維持中文,不在本次範圍。

- **README 改為英文為主、中文並存**(`README.md` / `README.zh-Hant.md`,互相連結)。
  `README.md` 會被打包進兩個 nupkg —— 也就是說 nuget.org 上這兩個套件的說明頁原本
  只有中文。與 PolyMigrate / XamlContrast 的雙語形狀對齊。

### Added
- **`CHSU001` / `CHSU002` 補上 `helpLinkUri`**,Visual Studio 的「深入了解」連結不再是死的;
  對應的說明文件 [`docs/analyzer-rules.md`](docs/analyzer-rules.md) 一併補上
  (每條規則附觸發範例、修法、以及該不該壓掉)。
- `.github/dependabot.yml`(nuget + github-actions)。對這個 repo 特別有意義:相依刻意釘在
  「8.0 系列中已修補已知弱點的版本」(EF Core Relational 8.0.11、DI.Abstractions 8.0.2)
  而非最低的 8.0.0,那個判斷有保存期限。

### Fixed
- CI 的 actions 版本升級(`checkout@v4` → `v7`、`setup-dotnet@v4` → `v6`),脫離 Node 20 淘汰線 ——
  另外三個姊妹套件早已升過,只有這裡留在舊版。

## [1.0.1] — 2026-07-21

### Changed
- README 加上作品集回連,QuillNest 連結改指向作品頁(原本指向 GitHub 個人頁)。
  純文件版,程式碼未變。

## [1.0.0] — 2026-07-15

> 正式版:API 從此穩定。規畫書 M1–M5 全數完成——
> 真實驗證(QuillNest 以 0.2.0/0.3.0 在正式資料上運行)、
> 三資料庫 × EF 8/9/10 測試矩陣、並發保護、編譯期防呆。
> NuGet `Cornhsu.*` 前綴保留已核准。

### Added
- **Roslyn Analyzer 隨 `Cornhsu.Labeling` 套件出貨**(安裝即生效):
  `CHSU001` 實作了 `ILabelable<TKey>` 但編譯單元內沒有 `r.Labelable<T>()` 註冊 → 編譯警告;
  `CHSU002` 只實作非泛型 marker(註冊必拋例外)→ 編譯警告。
- `AttachManyAsync(entities, labelNames)`:批次貼標——標籤解析一次、既有連結一次查詢、
  單次 SaveChanges;冪等,已貼過的組合自動略過。
- 可選 `ILogger`:DI 路徑自動注入(有註冊 logging 就會用),`LabelStoreFactory.Create`
  加可選 logger 參數。auto-create 標籤記 Information(提醒策展式 App 關掉
  AutoCreateLabels),建立/刪除記 Debug。
- CI 新增 **EF Core 9 / 10 matrix**:真的引用 EF 9(net8)與 EF 10(net10)跑整套測試,
  不是只換 SDK。測試專案以 `-p:CornhsuEfVersion` / `-p:CornhsuTestTfm` 參數化。

## [0.3.0] — 2026-07-15

> v1.0 前置清單全數完成的版本;讓消費端先跑一陣子,穩定後以同內容升 1.0.0。

### Added
- **`Label.ConcurrencyStamp` 並發戳記**(v1.0 前最後的 schema 決定):每次透過 store 修改
  標籤就輪換;兩邊同時修改同一個標籤時,後存檔的一方得到 `DbUpdateConcurrencyException`,
  不再默默後蓋前。不依賴資料庫功能(如 rowversion),所有 provider 行為一致。
  **Breaking(schema)**:Label 表多一欄,消費端需跑一次 migration。
- **多 provider 驗證**:同一套測試(66 條)在 SQLite、SQL Server(本機 LocalDB 與 CI 容器)、
  PostgreSQL(CI 容器)全綠;測試基礎建設以 `CORNHSU_TEST_PROVIDER` 環境變數切換。

## [0.2.0] — 2026-07-15

> M4 里程碑:QuillNest 已刪除本地 AppLabel 實作、改用本套件,
> 正式資料完成遷移(方案 A:單選分類留消費端 FK,多對多與 Label 本體走套件)。

### Added
- `GetLabelsOfManyAsync(entities)`:批次讀取多個實體的標籤(一次查詢),解清單畫面 N+1。
  回傳字典以傳入實體實例為鍵(參考相等),每個實體保證有項目。
  benchmark:50 筆 SQLite 本機快 5 倍(省 49 次 roundtrip)。
- 多標籤查詢 `FindByLabelsAsync` / `QueryByLabelsAsync<T>` + `LabelMatch`(Any=OR / All=AND):
  - `All` 模式任一名稱不存在 → 結果必為空;`Any` 模式不存在的名稱被忽略。
  - `includeDescendants` 下每個名稱代表「該標籤或其任一子孫」(群組語意)。
- `samples/Benchmark`:效能量測 harness(5 型別 × 10k 筆),數據入 README「效能」節。
- `LabelStoreFactory.Create(context, registry)`:無 DI 容器應用程式(WPF/WinForms 的
  singleton 服務架構)直接建立 `ILabelStore` 的正門——M4 真實遷移 QuillNest 時發現的缺口:
  `EfLabelStore` 是 internal,先前唯一入口 `AddLabeling` 強制要求 DI。
- 參數驗證:`AttachAsync`/`DetachAsync`/`GetLabelsOfAsync` 的 null 實體改拋 `ArgumentNullException`;
  `CreateAsync` 驗證 `parentId` 存在;建立/改名主動驗證名稱長度上限
  `Label.MaxNameLength`(= 64,SQLite 不強制 `HasMaxLength`,不能只靠資料庫)。

## [0.1.0-preview.2] — 2026-07-15

### Added
- `Cornhsu.Labeling`(抽象層):`Label`、`ILabelable<TKey>`、`ILabelStore`、`LabelHit`。
- **泛型主鍵支援**:實體主鍵可為 `int`/`long`/`Guid`/`string` 等,同一 App 可混用;
  主鍵型別在 `r.Labelable<TEntity>()` 註冊時自動推斷,公開 API 維持單一型別參數。
  `LabelHit.EntityId` 為 `object`,以 `EntityIdAs<TKey>()` 取回強型別。
- `Cornhsu.Labeling.EntityFrameworkCore`(EF Core 實作):
  - `LabelLink<TEntity>` 泛型連結實體——每個註冊型別自動產生一張具備真外鍵的 join table。
  - `LabelRegistry` / `AddLabeling<TContext>()` / `ApplyLabelModel()`。
  - `EfLabelStore`:標籤 CRUD、貼標/撕標(冪等)、跨型別查詢、強型別查詢、階層解析、使用次數統計。
- `AttachAsync`/`DetachAsync` 新增 `IEnumerable<string> + CancellationToken` 多載。
- 標籤名稱在所有入口自動 Trim,避免產生視覺上相同的重複標籤。
- 函式庫內所有 await 使用 `ConfigureAwait(false)`(避免 WPF/WinForms 同步等待死鎖)。
- 測試:SQLite in-memory,涵蓋外鍵完整性、多型查詢、階層、冪等、註冊驗證(規劃書 §9.2 全 14 條)。

- `UpdateAsync(labelId, l => ...)`:更新顏色/排序/父標籤(含改名唯一性檢查與父子循環防護)。
- `Label.Icon`:視覺識別欄位(emoji / 圖示名稱 / 短碼),與 `Color` 同層級;
  `CreateAsync` 新增 `icon` 參數。業務語意欄位請用伴生表擴充(見 README「擴充 Label」)。
- `LabelRegistry.AutoCreateLabels`(預設 true):設為 false 時,`AttachAsync` 遇到
  不存在的標籤會拋出列出全部缺漏名稱的例外,而不是自動建立裸標籤——
  給「標籤由管理介面策展」的 App 用。
- 明文化「名稱全域唯一(含跨階層)」為刻意取捨,理由與替代方案寫入 README Limitations。

### Changed
- `ILabelableDescriptor` 公開介面縮減為純描述資訊(ClrType/KeyType/TypeKey);
  連結表操作管線改為 internal,不再是公開 API 承諾。
- get-or-create 的競態處理改為「重讀驗證」:重讀不到同名標籤表示不是競態,原例外照拋。
- 相依樓地板 8.0.0 → 8.0.11(EF Core)/ 8.0.2(DI.Abstractions):
  預設不再拖進帶已知弱點通報的傳遞相依,消費端仍可 unify 至 EF Core 9/10。
- `samples/MinimalConsole`:獨立的第二個消費者。

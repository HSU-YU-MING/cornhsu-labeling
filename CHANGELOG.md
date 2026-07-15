# Changelog

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

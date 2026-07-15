# Changelog

## [Unreleased] — 0.1.0-preview.1

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

### Changed
- `ILabelableDescriptor` 公開介面縮減為純描述資訊(ClrType/KeyType/TypeKey);
  連結表操作管線改為 internal,不再是公開 API 承諾。
- get-or-create 的競態處理改為「重讀驗證」:重讀不到同名標籤表示不是競態,原例外照拋。
- 相依樓地板 8.0.0 → 8.0.11(EF Core)/ 8.0.2(DI.Abstractions):
  預設不再拖進帶已知弱點通報的傳遞相依,消費端仍可 unify 至 EF Core 9/10。
- `samples/MinimalConsole`:獨立的第二個消費者。

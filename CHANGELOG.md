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
- 測試:SQLite in-memory,涵蓋外鍵完整性、多型查詢、階層、冪等、註冊驗證(規劃書 §9.2 全 14 條)。
- `samples/MinimalConsole`:獨立的第二個消費者。

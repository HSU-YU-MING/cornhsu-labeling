# Changelog

## [Unreleased] — 0.1.0-preview.1

### Added
- `Cornhsu.Labeling`(抽象層):`Label`、`ILabelable`、`ILabelStore`、`LabelHit`。
- `Cornhsu.Labeling.EntityFrameworkCore`(EF Core 實作):
  - `LabelLink<TEntity>` 泛型連結實體——每個註冊型別自動產生一張具備真外鍵的 join table。
  - `LabelRegistry` / `AddLabeling<TContext>()` / `ApplyLabelModel()`。
  - `EfLabelStore`:標籤 CRUD、貼標/撕標(冪等)、跨型別查詢、強型別查詢、階層解析、使用次數統計。
- 測試:SQLite in-memory,涵蓋外鍵完整性、多型查詢、階層、冪等、註冊驗證(規劃書 §9.2 全 14 條)。
- `samples/MinimalConsole`:獨立的第二個消費者。

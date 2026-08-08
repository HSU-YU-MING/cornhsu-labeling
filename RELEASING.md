# Releasing（照 Parity 的形狀）

## 流程

1. 更新 `CHANGELOG.md`：新增版本段落。**Release notes 直接取自這一節**，
   所以這裡寫得夠不夠好，就等於 GitHub Releases 頁上的品質。
2. 推 tag：

```bash
git tag v1.2.0
git push origin v1.2.0
```

3. `.github/workflows/release.yml` 接手：從 tag 導出版本 → restore → build → test →
   `dotnet pack` → 以 **NuGet Trusted Publishing**（OIDC，無長期 API key）發佈
   `Cornhsu.Labeling` 與 `Cornhsu.Labeling.EntityFrameworkCore` → **自動建 GitHub Release**

> 版號**只從 tag 來**。csproj 裡沒有寫死的 `<Version>`，不需要（也不該）事先改任何檔案的版號。

## 版本規則

自 1.0.0 起 API 穩定：

- **major**：公開 API 的破壞性變更、或抬高相依樓地板（見下）
- **minor**：新增 API、或對外可見文字的改變（例外訊息、analyzer 診斷 —— 有人在斷言這些）
- **patch**：修正，對外行為不變

## 抬高相依樓地板 = major

README 承諾「支援 EF Core 8+，消費端用 9/10 會自動 unify」。把
`src/Cornhsu.Labeling.EntityFrameworkCore` 的 `Microsoft.EntityFrameworkCore.Relational`
往上抬，會讓還在 EF 8 的使用者直接裝不起來 —— 那是破壞性變更。

同理，`Cornhsu.Labeling.Analyzers` 的 `Microsoft.CodeAnalysis.CSharp` 版本
決定了消費端需要的最低 VS/SDK 世代（目前 4.8 = VS2022 17.8 / SDK 8，相容範圍最大）。

⚠ 真的要抬樓地板時，記得 `tests/Cornhsu.Labeling.Tests.csproj` 的 `CornhsuEfVersion`
預設值必須跟著改成同一個版本 —— 它的意義就是「測我們對外承諾的最低版本」。
但那個值目前**卡在 8.0.11**：它同時餵三個 provider，而 Npgsql 的 EF 8 線只到 8.0.11
（Microsoft 走到 8.0.29）。詳見該 csproj 的註解。

dependabot 已對這幾項設 `ignore`，不會自動提出 —— 抬樓地板要有意識地改 csproj +
README + CHANGELOG，不是按 merge。

## CI 矩陣

每次 push 都跑：三資料庫（SQLite / SQL Server / PostgreSQL）× EF Core 8 / 9 / 10。
EF 9/10 是**真的引用新版 EF 跑同一套測試**，不是只換 SDK：

```bash
dotnet test tests/Cornhsu.Labeling.Tests -p:CornhsuTestTfm=net10.0 -p:CornhsuEfVersion=10.0.*
```

`samples/MinimalConsole` 也在 CI 跑 —— 它是「抽象是否成立」的哨兵，不只是示範。

## 首次發佈前的一次性設定（已完成）

- NuGet.org：兩個套件都設定 Trusted Publishing
  （repo `HSU-YU-MING/cornhsu-labeling`、workflow `release.yml`）
- `Cornhsu.*` 前綴已獲 NuGet 官方保留
- workflow 已宣告 `id-token: write`（OIDC）與 `contents: write`（建 Release）

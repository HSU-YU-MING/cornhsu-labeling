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

> 版號**只從 tag 來**。不需要（也不該）事先改任何檔案的版號。

### 為什麼 `Directory.Build.props` 裡是 `0.0.0-dev`

那格**不能留空**。留空會退回 MSBuild 預設的 `1.0.0` —— 那是個看起來完全合理的真版本號，
所以從原始碼建置出來的組件會自稱 1.0.0，而不會有任何人懷疑它。`0.0.0-dev` 是刻意選的：
**錯得明顯勝過錯得像真的**。發版時 `release.yml` 用 `-p:Version=` 覆寫它，命令列的
global property 一定贏過專案檔，所以這格永遠不會影響發出去的套件。

⚠ 這個套件是**函式庫**，不是全域安裝的 CLI 工具，所以多一個 CLI 那邊不會有的陷阱：
本機 `dotnet pack` 出來的 `0.0.0-dev` 若裝進一個同時引用正式版的專案，NuGet 會 unify
到正式版，**你的本機改動被靜默忽略、而且沒有任何錯誤訊息**。要測本機修改就用
`ProjectReference`，不要繞 pack + install。

`SECURITY.md` 的 supported versions 一節也刻意不寫任何版本號 —— 寫了就是第二個版本來源，
遲早跟 tag 說法不一致。

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

`samples/ReadmeSnippets` 是 **README 的守門員**：README 教的每一個呼叫都在那裡編譯並執行。
改公開 API 的簽章時，`tests/` 會被迫跟著改（不然紅燈），但 README 是散文、不會被迫，
於是文件會安靜地過期 —— 而讀者是照文件寫的。那支專案就是那個「被迫」。

**在 README 加一段新的 API 用法時，同時在 ReadmeSnippets 補一行。** 這個守門員驗過會擋：
把 `LabelHit.EntityIdAs<TKey>()` 多加一個必填參數，紅燈直接指到
`samples/ReadmeSnippets/Program.cs`。

> 這裡沒有「跑一次工具、跟 README 貼的輸出比對」那種檢查（Parity / XamlContrast 有），
> 因為這是函式庫、沒有 console 輸出，README 裡一段都沒貼 —— 會走鐘的是 C# 片段，
> 而讓片段編譯比比對字串更根治，也不需要 `-Update`。

## 首次發佈前的一次性設定（已完成）

- NuGet.org：兩個套件都設定 Trusted Publishing
  （repo `HSU-YU-MING/cornhsu-labeling`、workflow `release.yml`）
- `Cornhsu.*` 前綴已獲 NuGet 官方保留
- workflow 已宣告 `id-token: write`（OIDC）與 `contents: write`（建 Release）

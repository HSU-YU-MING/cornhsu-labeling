# Cornhsu.Labeling 開發指南

EF Core 的多型標籤函式庫（一個標籤可貼到任何型別，且每一條連結背後都有真外鍵）。
**這是函式庫，不是 CLI 工具**——沒有可執行檔、沒有 console 輸出，消費端是別人的專案。

使用者文件在 [README.md](README.md) / [README.zh-Hant.md](README.zh-Hant.md)，
發版流程在 [RELEASING.md](RELEASING.md)，未做事項與觸發條件在 [ROADMAP.md](ROADMAP.md)，
威脅面與設計聲明在 [SECURITY.md](SECURITY.md)。這份是開發慣例，不重複那些內容。

## ⚠ 路徑陷阱：git repo 在子目錄

```
D:\擴充功能\nuget\Labeling\              ← 只是資料夾，不是 repo（規劃文件、報告、截圖）
D:\擴充功能\nuget\Labeling\cornhsu-labeling\   ← 真正的 git repo 在這裡
```

在外層 `Labeling\` 下 `git` 指令會得到「不是 repo」，很容易誤判成這個專案不在版控裡、
或不屬於 `Cornhsu.*` 那套體系裡——它在。四個姊妹套件裡**只有這個多一層**
（Parity / PolyMigrate / XamlContrast 的 repo 就是資料夾本身）。
所有指令、所有 git 操作都要先進到 `cornhsu-labeling\`。

## 指令

- 建置：`dotnet build Cornhsu.Labeling.slnx`
- 測試：`dotnet test`——78 條（主測試 72 + analyzer 6）。動了 `src/` 的任何東西**必跑**。
  預設走 **SQLite in-memory**，秒級。
- 換 provider 跑同一套測試（環境變數，見 `tests/.../TestInfrastructure.cs`）：

  ```bash
  CORNHSU_TEST_PROVIDER=sqlserver dotnet test tests/Cornhsu.Labeling.Tests
  CORNHSU_TEST_PROVIDER=postgres  dotnet test tests/Cornhsu.Labeling.Tests
  ```

  SQL Server 預設連 `(localdb)\MSSQLLocalDB`，PostgreSQL 預設連本機 5432；
  連線字串可用 `CORNHSU_TEST_SQLSERVER` / `CORNHSU_TEST_POSTGRES` 覆寫。
  每個 `TestDb` 開一個獨立資料庫、結束時 `EnsureDeleted`。
- 本機跑 EF 9 / 10 那兩條線（CI 的矩陣就是這個）：

  ```bash
  dotnet test tests/Cornhsu.Labeling.Tests -p:CornhsuTestTfm=net10.0 -p:CornhsuEfVersion=10.0.* -p:CornhsuDiVersion=10.0.*
  ```

- 兩支 sample **是 CI 的一部分，不是裝飾**，動了公開 API 之後自己先跑一次：

  ```bash
  dotnet run --project samples/MinimalConsole   -c Release   # 抽象是否成立的哨兵
  dotnet run --project samples/ReadmeSnippets   -c Release   # README 的守門員
  ```

  `ReadmeSnippets` 開頭會印出實際跑到的 runtime 與 EF 組件版本——**這是刻意的**：
  一個被靜默忽略的 `-p:` 旗標會讓矩陣看起來跑了三個版本、其實跑了三次同一個。
- 效能量測：`dotnet run --project samples/Benchmark -c Release`（5 型別 × 10k 筆，
  數據會進 README「Performance」節）。

## 發版

**共通流程全部在全域 skill `nuget-packages`**（`C:\Users\ASUS\.claude\skills\nuget-packages\SKILL.md`），
細節在 [RELEASING.md](RELEASING.md)。這裡只記本 repo 特有的：

- **實際發佈的 NuGet 套件只有兩個**：`Cornhsu.Labeling` 與 `Cornhsu.Labeling.EntityFrameworkCore`。
  `Cornhsu.Labeling.Analyzers` 是 `IsPackable=false`，它的 dll 以
  `analyzers/dotnet/cs` 路徑**夾帶在 `Cornhsu.Labeling` 裡出貨**（見 `Cornhsu.Labeling.csproj` 的
  `None Include=...\bin\$(Configuration)\netstandard2.0\...dll`）。
  那是一條**硬寫的 bin 路徑**：改打包設定或改 analyzer 的 TFM 之後，
  要打開產出的 nupkg 確認 `analyzers/dotnet/cs` 裡真的有那顆 dll，
  否則使用者裝了套件卻沒有編譯期防呆，而且沒有任何訊息告訴他。
- **抬高相依樓地板 = major**（EF Core 8.0.11、Roslyn 4.8）。
  README 承諾「支援 EF Core 8+」，往上抬會讓 EF 8 的使用者直接裝不起來。
  analyzer 的 `Microsoft.CodeAnalysis.CSharp` 版本則決定消費端需要的最低 VS/SDK 世代。
- **沒有「跑一次工具、跟 README 貼的輸出比對」的腳本**（XamlContrast 與 Parity 有）。
  這是函式庫、沒有 console 輸出，README 一段輸出都沒貼——會走鐘的是 C# 片段，
  而那個由 `samples/ReadmeSnippets` 用「編譯不過就紅燈」守著，比比對字串更根治。
- 消費端 **QuillNest** (`D:\應用程式\QuillNest\QuillNest\QuillNest.csproj`) 目前釘在
  `Cornhsu.Labeling.EntityFrameworkCore` **1.0.0**（走公開 nuget.org，不是本機 feed）。
  改公開 API 或 schema 時，它是唯一的真實使用者，值得順手看一眼。

## 專案界線：src 乾淨、tests/samples 才碰 provider

這條界線是這個 repo 最重要的結構性約束，**不要為了方便而破壞它**：

| | 引用什麼 | 為什麼 |
|---|---|---|
| `src/Cornhsu.Labeling` | 什麼都不引用（netstandard2.0 + net8.0） | 純抽象層，可被非 EF 的實作採用 |
| `src/Cornhsu.Labeling.EntityFrameworkCore` | 只有 `Microsoft.EntityFrameworkCore.Relational` + `DI.Abstractions` | **provider-neutral**：刻意不綁 Sqlite / SqlServer / Npgsql |
| `tests/` `samples/` | 三個 provider 全上（Sqlite / SqlServer / Npgsql） | 只在開發期存在，不會進使用者的相依圖 |

**發佈用的 src 專案一旦引用任何具體 provider，就等於替所有使用者選了資料庫**，
而且會把該 provider 的整條傳遞相依（含它的原生二進位與弱點通報）塞進每個消費端。
下面「技術債留帳」的 CVE 判讀完全建立在這條界線上——破壞它，那個判讀立刻失效。

`src/Cornhsu.Labeling` 同時出 **netstandard2.0**，靠 `Nullable` / `IsExternalInit` 兩個
PrivateAssets 墊片撐住 `nullable` 與 `init`。在那個專案裡用到更新的語法時，
netstandard2.0 那一輪會先爆——先確認墊片有沒有涵蓋，不要直接砍掉 netstandard2.0。

## 刻意的取捨（動手前先讀，不要「順手改好」）

README 的 Design trade-offs 已經完整寫了「為什麼是每型別一張連結表」，不重複。
維護者要知道的是**哪些東西不能動**：

- **`LabelRegistry` 必須是全 App 單例。** EF Core 的 model cache 以 DbContext 型別為 key，
  同一個 DbContext 型別配到不同 registry 會拿到**錯誤的快取 model，而且完全沒有錯誤訊息**。
  `AddLabeling` 已經註冊成 Singleton 並在建完之後 `Seal()`。
  測試能繞過這件事，是因為 `TestInfrastructure` 每個 `TestDb` 都加了
  `EnableServiceProviderCaching(false)` 隔離內部 provider——**正式 App 不需要也不該加那行**。
- **`Label` 保持最小**，只留視覺識別欄位（Name / Color / Icon / 階層 / 排序）。
  業務語意欄位（標籤型別、模組或租戶隔離、權限）刻意不收，因為套件無法理解也無法替人把關。
  要擴充走 1:1 伴生表。**「讓 Label 支援泛型 / 讓使用者繼承 Label」這條路已經評估過並否決**，
  理由是它會把泛型汙染整個套件、把侵入性帶回來（README「Extending Label」有寫）。
- **標籤名稱全域唯一（含跨階層）。** 整個 API 以名稱定址，允許同名會讓每一個名稱呼叫變歧義。
  要改成「每父層唯一」必然伴隨路徑定址 API——那是 v2，見 ROADMAP。
- **跨型別查詢就是 N 次查詢**（N = 註冊型別數）。已量測：5 型別 × 10k 筆、12 萬連結
  仍在 ~18 ms。合併成 `UNION ALL` 的觸發條件寫在 ROADMAP，**不要為了漂亮先做**。
- **`LabelHit.EntityId` 是 `object`** 是泛型主鍵的必然代價，不是待辦。ROADMAP 已明文不做。
- **標籤是全域的、跨 tenant 互相看得見**（SECURITY.md 有明文聲明）。
  這是設計，不是漏洞；有人回報「A 租戶查到 B 的資料」時不要當 bug 修。

## 地雷

### `TypeKey` 就是資料表名，改類別名 = 改表名 = 資料不見

`r.Labelable<Note>()` 沒指定 `typeKey` 時，表名預設取 **CLR 類別名**
（`LinkTablePrefix` + `TypeKey` → `LabelLink_Note`）。
消費端重構改類別名，下一次 migration 就會生出一張新表，舊連結留在舊表裡。
`Labelable<T>` 的 XML 註解已建議明確釘 `typeKey`——**改動註冊相關的程式碼時不要把這個建議弄丟**。

### 寫入方法會 `SaveChangesAsync` 你的 DbContext

`ILabelStore` 的寫入路徑（含 `AttachAsync` 自動建立標籤那條）直接呼叫消費端 DbContext 的
`SaveChangesAsync`，**該 context 裡其他還沒存的變更會被一起送出去**。
這是共用 DbContext 的固有代價，已寫進 README Limitations；
要隔離就從另一個 DbContext scope 呼叫 store。動寫入路徑時別把這個語意悄悄改掉。

### get-or-create 的競態靠「重讀驗證」，不是無腦吞例外

`EfLabelStore.GetOrCreateLabelsAsync` 撞到 `DbUpdateException` 時會 detach、重讀同名標籤：
讀得到 → 採用既有那筆；**讀不到 → 不是同名競態，原例外照拋**。
不要簡化成 `catch (DbUpdateException) { }`，那會把真正的寫入失敗吃掉。

### `TreatWarningsAsErrors` + `GenerateDocumentationFile`：漏 XML 註解就編不過

`src/` 底下新增或修改公開成員時，缺 `<summary>` / `<param>` 會直接讓建置紅燈（如 CS1573）。
這其實是這個 repo 的第一道文件守門員——**不要用 `#pragma` 壓掉它**。

XML 註解會被打包成 `.xml` 隨 nupkg 出貨，也就是消費端在 IDE 裡看到的 IntelliSense。
**公開表面的註解一律英文**（1.1.0 已全面英文化）；internal 型別
（`EfLabelStore` / `LabelableDescriptor` / `ILabelableOperations`）的中文註解是刻意留著的，
消費端存取不到那些成員。

### 對外可見的文字改動 = minor，不是 patch

例外訊息與 analyzer 診斷文字（`CHSU001` / `CHSU002`）是**有人在斷言、在 grep 的東西**。
改語言或措辭要升 minor 並寫進 CHANGELOG（1.1.0 就是這樣的一版：API 一個字沒改，只換文字）。

### 測試不能用 EF InMemory Provider

它不執行外鍵約束，而外鍵完整性正是這個套件的賣點——用它等於測不到重點。
一律 SQLite in-memory 起跳。

### `CHSU001` 在跨組件註冊時是誤判

它是 `CompilationEnd` 診斷，只看得到當前編譯單元。註冊寫在另一個組件裡就會誤報，
解法是 `#pragma warning disable CHSU001` 或 `.editorconfig`。
這件事 README 與 `docs/analyzer-rules.md` 都有寫，收到回報不要當 bug。
另外它是 `CompilationEnd`，**IDE 的即時分析不會產生它**，要完整建置才看得到——
「在 VS 裡沒跳」不代表壞掉。

## git log 挖到的教訓

- **`CornhsuEfVersion` 的 8.0.11 是天花板，不是還沒升。**（#10、commit `2c2280d`）
  測試專案用**一個變數餵三個 provider**，而 Microsoft 的 EF 8 線走到 8.0.29、
  Npgsql 只到 8.0.11。dependabot 把它推到 8.0.29 之後 Npgsql 解析不到對應版本 →
  NuGet 浮動到 9.0.0 → 把 EF Relational 9 拖進相依圖 → 撞上釘住的 8.x，
  `NU1603` + `NU1605` 三個 job 全紅。dependabot 現在連 patch 一起 ignore。
  要解開天花板必須把 Npgsql 拆成獨立變數，**那是有意識的改動，不是版本升級**。
  同時它的意義是「測我們對外承諾的最低版本」，所以它得**永遠等於 src 的樓地板**——
  讓它浮動等於「承諾支援 8.0.11，卻從來沒測過 8.0.11」。
- **版本欄位漂移最難發現的形式是「看起來合理的真版本號」。**（commit `3b285d0`）
  `Directory.Build.props` 原本沒有 `<Version>`，退回 MSBuild 預設的 `1.0.0`，
  而實際已發到 1.1.0——沒有人會懷疑一個 1.0.0。現在固定 `0.0.0-dev`：
  **錯得明顯勝過錯得像真的**。發版時 `release.yml` 用 `-p:Version=` 覆寫，
  命令列的 global property 一定贏過專案檔。**不要手動去改那格。**
- **函式庫特有的陷阱**（CLI 工具那邊不會遇到）：本機 `dotnet pack` 出來的 `0.0.0-dev`
  若裝進一個同時引用正式版的專案，NuGet 會 unify 到正式版，
  **你的本機改動被靜默忽略、而且沒有任何錯誤訊息**。測本機修改一律用 `ProjectReference`。
- **README 是散文，不會被編譯器逼著更新。**（commit `8a6c794`）
  改公開 API 簽章時 `tests/` 會被迫跟著改，README 不會，於是文件安靜過期、而讀者照文件寫。
  `samples/ReadmeSnippets` 就是那個「被迫」。實測會擋：給 `EntityIdAs<TKey>()`
  加一個必填參數，紅燈直接指到 `samples/ReadmeSnippets/Program.cs`。
  **在 README 加一段新的 API 用法時，同時在 ReadmeSnippets 補一行。**
  順帶一提，「加參數」這條路本來就有 XML 註解在守（缺 `<param>` → CS1573，src 先炸）；
  真正沒人守的是**改型別、改回傳、改預設值**那類——那才是這支專案補的洞。
- **守門員只掛在樓地板上等於只驗了承諾的下限。**（#18、commit `fd8c97c`）
  ReadmeSnippets 原本只跑在 EF 8，但 README 承諾 8+，而它教的東西
  （IQueryable 繼續組合、DbContext 建構與 model 快取、`IDesignTimeDbContextFactory`）
  剛好都落在 EF 各版之間會動的地方。現在三條線都跑。
- **`PackageProjectUrl` 與 `RepositoryUrl` 不是同一件事**（commit `e5b9c4f`），
  已分別指向作品頁與 GitHub。⚠ **已發佈版本的中繼資料是凍結的**，
  改了要等下次發版才會在 nuget.org 生效。
- **不要照抄姊妹 repo 的文件**（commit `3822106`）。XamlContrast 的 SECURITY.md 骨幹是
  「我解析不信任的檔案，XXE 已擋」，Labeling 完全沒有這件事——照抄會變成一份講錯話的文件。

## 技術債留帳

### CVE-2025-6965（SQLite 記憶體毀損）：判讀 low、刻意不處理

`tests/` 與 `samples/` 透過 `Microsoft.EntityFrameworkCore.Sqlite 8.0.11` 傳遞帶入
`SQLitePCLRaw.lib.e_sqlite3 2.1.6`，該版含 SQLite 記憶體毀損漏洞
（**GHSA-2m69-gcr7-jv3q**，資料庫等級 High / CVSS 7.2）。**2026-08-23 稽核判讀為 low、不處理**：

1. **三個 src 專案全部乾淨**——`Cornhsu.Labeling.EntityFrameworkCore` 只引用
   provider-neutral 的 `Microsoft.EntityFrameworkCore.Relational`，刻意不綁 Sqlite，
   **使用者不會被帶入**（已實查 `src/.../obj/project.assets.json`，零筆 SQLitePCLRaw）。
2. 純開發期依賴，SQL 全部由自家測試碼產生，**沒有不可信輸入**。
3. 查證當時**上游尚無修補版**。

**觸發點**：等 SQLitePCLRaw 釋出帶 SQLite 3.50.2+ 的版本，再升 tests/samples 的
EF Core Sqlite，或加一筆直接引用蓋掉 transitive 版本。
在那之前**不用重新緊張**——掃到這條就回來看這一段。
（注意：升 tests 的 EF Sqlite 會撞上前面那個 8.0.11 天花板，兩件事要一起處理。）

### 這兩個不是機密，不要「修」

- CI 的 `MSSQL_SA_PASSWORD: "Cornhsu-CI-P4ss!"` 是 GitHub Actions 臨時測試容器的**拋棄式密碼**
  （job 結束即銷毀、不對應任何真實系統）。
- 同理 postgres 測試的 `Password=postgres`。

把它們搬進 GitHub Secrets 只會讓 CI 更難維護，換不到任何安全性。

### 不要改回 `@v1`

`release.yml` 的 `NuGet/login` 已**釘 commit SHA**
（`8d196754b4036150537f80ac539e15c2f1028841` = v1.2.0，2026-08-25 安全硬化）。
它是發佈鏈上唯一經手憑證（OIDC 換臨時金鑰）的第三方 action，
而 tag 可被上游挪動、SHA 不行。上游出新版要更新時，**四個姊妹 repo 一起換新 SHA**。

### 缺口：README 示範輸出沒有自動比對腳本

XamlContrast 有 `verify-readme-sample.ps1`、Parity 有 `verify-readme-facts.ps1`，
這裡沒有。C# 片段那一面已由 `samples/ReadmeSnippets` 守住，但 README 裡的
**效能數據表、測試條數、支援矩陣**這類散文事實仍然只能人工檢查。
改到那些數字對得上的東西時，記得回頭看 README。

## 收尾慣例

- 動了 `src/` → `dotnet test` 必跑；動了公開 API → 連 `MinimalConsole` 與 `ReadmeSnippets` 一起跑。
- 動了公開 API 或行為 → 同步 README（英文）**與** README.zh-Hant，兩份都要改。
- 動了對外可見文字（例外訊息、analyzer 診斷）→ CHANGELOG 標成 minor 的理由要寫清楚。
- 新增 analyzer 規則 → `AnalyzerReleases.Unshipped.md` 要跟著寫
  （`EnforceExtendedAnalyzerRules=true` 會擋），並補 `docs/analyzer-rules.md` 與 `helpLinkUri`。
- CHANGELOG 的版本段落**就是 GitHub Release 的內容**（`release.yml` 直接抓那一節），
  寫得夠不夠好等於 Releases 頁的品質。
- 判斷「這是不是待辦」之前先看 ROADMAP.md——那裡把「明文不做」「有觸發條件才做」
  「v2 範疇」分開列了，很多看起來像遺漏的東西是已經決定過的。

# Roadmap

「已完成」看 [README](README.md) 與 [CHANGELOG](CHANGELOG.md)。這裡只記**還沒做的**。

這份文件存在的理由：README 的 Limitations 混了兩種東西 —— 「這是泛型主鍵的必然代價，
你得認了」和「這個以後會有」。讀者分不出來。前者留在 README，後者收在這裡。

## 明文不做（不是遺漏）

- **`LabelHit.EntityId` 改成強型別**。跨型別查詢的命中本來就可能來自不同主鍵型別
  （`Note` 是 Guid、`TodoItem` 是 int），`object` 是泛型主鍵的必然代價，
  不是偷懶。要強型別用 `EntityIdAs<TKey>()`。
- **`Label` 加業務欄位**（標籤型別、模組/租戶隔離、權限…）。套件無法理解、
  也無法替你把關這些概念，加了只是假裝支援。用 1:1 伴生表，見 README「擴充 Label」。

## 有觸發條件才做

- **跨型別查詢合併成 `UNION ALL`**。現在是「每個註冊型別一次查詢」的樸素策略。
  benchmark 顯示 5 型別 × 10,000 筆、12 萬連結時仍在 ~18 ms —— 這個規模下不值得
  換複雜度。**觸發條件**：有人回報實測瓶頸，或註冊型別數成長到兩位數。
- **多租戶：各租戶不同的可標記型別**。EF Core 的 model cache 以 DbContext 型別為 key，
  要支援得自訂 `IModelCacheKeyFactory`。**觸發條件**：出現真實需求 ——
  這會把 registry 從「全 App 單例」變成「每租戶一份」，是核心假設的改動，不能為想像中的
  使用者先做。
- **Analyzer 的 code fix**。`CHSU001` 現在只警告不修。技術上卡在兩點：它是
  `CompilationEnd` 診斷（要完整建置才出現，IDE 的即時分析不產生，燈泡幾乎不會亮），
  而且修法要動到「另一個檔案的 `AddLabeling` lambda」，跨檔案的 code fix 既脆弱又難預測。
  `CHSU002` 倒是可以做（純本地改寫：讀 `Id` 型別、把 `ILabelable` 改成 `ILabelable<TKey>`），
  但它只會在每個型別上發生一次，而診斷訊息已經明說該怎麼改。
  **觸發條件**：有人回報訊息不夠清楚。目前的解法是
  [docs/analyzer-rules.md](docs/analyzer-rules.md) 加上 `helpLinkUri`。

## v2 範疇（會破壞 API）

- **標籤名稱改為「每父層唯一」**。現在是全域唯一，含跨階層 ——「工作/雜項」和
  「生活/雜項」不能各有一個「雜項」。這是刻意取捨：整個 API 以名稱定址
  （`AttachAsync`、`FindByLabelAsync` 都吃名稱字串），允許同名會讓所有名稱定址的呼叫變歧義。
  真要支援，勢必伴隨路徑定址（`"生活/雜項"`）的 API 改版 —— 那是 major。
  在那之前，把限定詞放進名稱本身（如「生活·雜項」）。

## 1.0 之後的維護原則

API 已凍結。相依樓地板（EF Core 8、Roslyn 4.8）也是對外承諾的一部分，
抬高它等同破壞性變更 —— 見 [RELEASING.md](RELEASING.md)。

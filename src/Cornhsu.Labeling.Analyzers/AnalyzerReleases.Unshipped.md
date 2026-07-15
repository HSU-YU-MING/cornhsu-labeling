; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CHSU001 | Cornhsu.Labeling | Warning | ILabelable 型別未在編譯單元內以 r.Labelable\<T\>() 註冊
CHSU002 | Cornhsu.Labeling | Warning | 只實作非泛型 ILabelable marker,註冊時必拋例外

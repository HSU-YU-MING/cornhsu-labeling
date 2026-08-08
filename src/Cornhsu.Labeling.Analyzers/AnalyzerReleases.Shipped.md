; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CHSU001 | Cornhsu.Labeling | Warning | Labelable type is not registered via r.Labelable\<T\>() in this compilation
CHSU002 | Cornhsu.Labeling | Warning | Only the non-generic ILabelable marker is implemented; registration will throw

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Cornhsu.Labeling.Analyzers;

/// <summary>
/// CHSU001:型別實作了 ILabelable&lt;TKey&gt;,但整個編譯單元裡找不到對它的
///          r.Labelable&lt;T&gt;() 註冊——執行期貼標時會拋「型別未註冊」。
/// CHSU002:型別只實作了非泛型的 ILabelable(marker)——註冊時必定拋
///          「無法推斷主鍵型別」,應改實作 ILabelable&lt;TKey&gt;。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LabelableRegistrationAnalyzer : DiagnosticAnalyzer
{
    private const string MarkerTypeName = "Cornhsu.Labeling.ILabelable";
    private const string GenericTypeName = "Cornhsu.Labeling.ILabelable`1";
    private const string RegistryTypeName = "Cornhsu.Labeling.EntityFrameworkCore.LabelRegistry";

    private const string HelpUriBase =
        "https://github.com/HSU-YU-MING/cornhsu-labeling/blob/main/docs/analyzer-rules.md#";

    public static readonly DiagnosticDescriptor NotRegistered = new(
        id: "CHSU001",
        title: "Labelable type is not registered",
        messageFormat: "Type '{0}' implements ILabelable<{1}>, but this compilation contains no " +
                       "r.Labelable<{0}>() registration; attaching or querying it at run time will throw " +
                       "\"type is not registered\". Suppress this warning if the registration lives in another assembly.",
        category: "Cornhsu.Labeling",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUriBase + "chsu001",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor MarkerOnly = new(
        id: "CHSU002",
        title: "Only the non-generic ILabelable marker is implemented",
        messageFormat: "Type '{0}' implements only the non-generic ILabelable; registering it will throw " +
                       "because the key type cannot be inferred. Implement ILabelable<TKey> instead " +
                       "(for example ILabelable<int> or ILabelable<Guid>).",
        category: "Cornhsu.Labeling",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpUriBase + "chsu002",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(NotRegistered, MarkerOnly);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            var marker = start.Compilation.GetTypeByMetadataName(MarkerTypeName);
            var generic = start.Compilation.GetTypeByMetadataName(GenericTypeName);
            if (marker is null || generic is null) return;   // 沒引用本套件 → 不做事

            var registered = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var candidates = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);

            // 收集 r.Labelable<T>() 的 T
            start.RegisterOperationAction(op =>
            {
                var invocation = (IInvocationOperation)op.Operation;
                var method = invocation.TargetMethod;
                if (method.Name != "Labelable" || !method.IsGenericMethod || method.TypeArguments.Length != 1)
                    return;
                if (method.ContainingType?.ToDisplayString() != RegistryTypeName)
                    return;
                if (method.TypeArguments[0] is INamedTypeSymbol entity)
                    registered.TryAdd(entity, 0);
            }, OperationKind.Invocation);

            // 收集實作 ILabelable 的具象類別
            start.RegisterSymbolAction(sym =>
            {
                var type = (INamedTypeSymbol)sym.Symbol;
                if (type.TypeKind != TypeKind.Class || type.IsAbstract) return;
                if (type.AllInterfaces.Any(i =>
                        SymbolEqualityComparer.Default.Equals(i, marker) ||
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, generic)))
                    candidates.TryAdd(type, 0);
            }, SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end =>
            {
                foreach (var type in candidates.Keys)
                {
                    var genericImpl = type.AllInterfaces.FirstOrDefault(i =>
                        SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, generic));

                    var location = type.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location is null) continue;

                    if (genericImpl is null)
                    {
                        // 只有 marker → 註冊必炸
                        end.ReportDiagnostic(Diagnostic.Create(MarkerOnly, location, type.Name));
                    }
                    else if (!registered.ContainsKey(type))
                    {
                        end.ReportDiagnostic(Diagnostic.Create(
                            NotRegistered, location, type.Name, genericImpl.TypeArguments[0].Name));
                    }
                }
            });
        });
    }
}

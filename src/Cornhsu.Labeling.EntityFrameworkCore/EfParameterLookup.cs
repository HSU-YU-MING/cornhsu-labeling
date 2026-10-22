using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 找 EF Core 9 起才有的 <c>EF.Parameter&lt;T&gt;(T)</c>。樓地板是 EF Core 8,只能在執行期找;找不到(EF Core 8)時是 null。
/// </summary>
internal static class EfParameterLookup
{
    /// <summary>EF.Parameter 封閉成 <c>List&lt;Guid&gt;</c> 的版本;EF Core 8 上是 null。</summary>
    public static readonly MethodInfo? ForGuidList = Find(typeof(EF), typeof(List<Guid>));

    /// <summary>
    /// 在 <paramref name="efType"/> 上找 <c>Parameter&lt;T&gt;(T)</c>,封閉成 <paramref name="argumentType"/>。
    /// 逐一篩選而不是 <c>GetMethod("Parameter")</c>:EF 日後多一個同名多載時,後者會拋 AmbiguousMatchException,
    /// 而這個欄位在型別初始化時就求值,一拋整個 store 都不能用。找不到、或不只一個符合的,一律回傳 null(退回 EF 的預設)。
    /// </summary>
    internal static MethodInfo? Find(Type efType, Type argumentType)
    {
        var candidates = efType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Parameter"
                && m.IsGenericMethodDefinition
                && m.GetGenericArguments() is [var t]
                && m.GetParameters() is [var p]
                && p.ParameterType == t
                && m.ReturnType == t)
            .ToList();
        return candidates is [var method] ? method.MakeGenericMethod(argumentType) : null;
    }
}

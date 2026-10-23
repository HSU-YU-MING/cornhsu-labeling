using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 找 EF Core 9 起才有的 <c>EF.Parameter&lt;T&gt;(T)</c>。樓地板是 EF Core 8,只能在執行期找;找不到(EF Core 8)時是 null。
/// </summary>
internal static class EfParameterLookup
{
    /// <summary>EF.Parameter 封閉成 <c>List&lt;Guid&gt;</c> 的版本;EF Core 8 上是 null。</summary>
    public static readonly MethodInfo? ForGuidList = Find(typeof(EF), typeof(List<Guid>));

    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<bool>> Translatable = new();

    /// <summary>
    /// 這個 context 的資料庫能不能把整個集合當成一個參數(<c>EF.Parameter(list).Contains(x)</c>)翻譯出來:
    /// 要資料庫能拆 JSON 陣列,SQL Server 的相容性層級低於 130 時沒有 OPENJSON,EF 會在翻譯時拋例外。
    /// 用一個很小的查詢試翻一次,結果依 EF 的內部服務容器快取——相容性層級屬於那一層,
    /// 同樣設定的 context 共用同一個容器(也跨 DI scope),不必每個型別、每次查詢都把完整的清單翻一遍。
    /// </summary>
    public static bool CanSendAsOneParameter(DbContext db)
    {
        if (ForGuidList is null) return false;
        return Translatable.GetValue(db.GetInfrastructure(), _ => new StrongBox<bool>(Probe(db))).Value;
    }

    private static bool Probe(DbContext db)
    {
        var label = Expression.Parameter(typeof(Label), "l");
        var values = Expression.Call(ForGuidList!, Expression.Field(
            Expression.Constant(new StrongBox<List<Guid>>(new List<Guid> { Guid.Empty })), nameof(StrongBox<List<Guid>>.Value)));
        var body = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), new[] { typeof(Guid) },
            values, Expression.Property(label, nameof(Label.Id)));
        try
        {
            db.Set<Label>().Where(Expression.Lambda<Func<Label, bool>>(body, label)).ToQueryString();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

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

namespace Cornhsu.Labeling;

/// <summary>
/// 可標記實體的非泛型基底(marker)。請不要直接實作這個介面——
/// 實作 <see cref="ILabelable{TKey}"/> 並指定你的主鍵型別,例如
/// <c>class Note : ILabelable&lt;int&gt;</c> 或 <c>class Memo : ILabelable&lt;Guid&gt;</c>。
/// 這個介面存在的目的是讓 API 只需要一個型別參數(主鍵型別在註冊時自動推斷)。
/// </summary>
public interface ILabelable
{
}

/// <summary>任何想被標記的實體都實作這個介面。刻意不要求繼承任何基底類別。</summary>
/// <typeparam name="TKey">
/// 實體主鍵型別。支援 <see cref="int"/>、<see cref="long"/>、<see cref="Guid"/>、
/// <see cref="string"/> 等具備相等比較的型別。
/// </typeparam>
public interface ILabelable<TKey> : ILabelable
    where TKey : notnull
{
    /// <summary>實體主鍵。</summary>
    TKey Id { get; }
}

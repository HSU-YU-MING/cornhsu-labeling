namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>
/// 一個已註冊可標記型別的公開描述資訊。
/// 實際的連結表操作是套件內部實作細節,不屬於公開 API。
/// </summary>
public interface ILabelableDescriptor
{
    /// <summary>可標記實體的 CLR 型別。</summary>
    Type ClrType { get; }

    /// <summary>實體的主鍵型別(自 <see cref="ILabelable{TKey}"/> 推斷)。</summary>
    Type KeyType { get; }

    /// <summary>持久化用的穩定型別鍵;會成為表名的一部分,不隨類別改名而變。</summary>
    string TypeKey { get; }
}

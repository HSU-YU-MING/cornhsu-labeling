namespace Cornhsu.Labeling;

/// <summary>任何想被標記的實體都實作這個介面。刻意不要求繼承任何基底類別。</summary>
public interface ILabelable
{
    /// <summary>實體主鍵。v1 僅支援 <see cref="Guid"/>。</summary>
    Guid Id { get; }
}

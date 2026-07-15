using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>把標籤系統掛進 EF Core model 的擴充方法。</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// 在 <c>OnModelCreating</c> 中呼叫:配置 Label 表,並為每個已註冊型別產生一張 LabelLink_* 表。
    /// </summary>
    /// <param name="b">EF Core 的 ModelBuilder。</param>
    /// <param name="registry">標籤註冊表(必須是全 App 單例)。</param>
    public static ModelBuilder ApplyLabelModel(this ModelBuilder b, LabelRegistry registry)
    {
        b.Entity<Label>(e =>
        {
            e.ToTable(registry.LabelTableName);
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(64);
            e.Property(x => x.Color).HasMaxLength(16);
            e.HasIndex(x => x.Name).IsUnique();

            e.HasOne(x => x.Parent).WithMany(x => x.Children)
             .HasForeignKey(x => x.ParentId)
             .OnDelete(DeleteBehavior.Restrict);   // 有子標籤時不准直接刪父標籤
        });

        foreach (var d in registry.Operations)
            d.ConfigureModel(b, registry);

        return b;
    }
}

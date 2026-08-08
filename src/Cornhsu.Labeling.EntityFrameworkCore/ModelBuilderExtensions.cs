using Microsoft.EntityFrameworkCore;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>Extension methods that wire the labeling system into an EF Core model.</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Call this from <c>OnModelCreating</c>: it configures the Label table and generates one
    /// LabelLink_* table per registered type.
    /// </summary>
    /// <param name="b">EF Core's ModelBuilder.</param>
    /// <param name="registry">The label registry (must be an application-wide singleton).</param>
    public static ModelBuilder ApplyLabelModel(this ModelBuilder b, LabelRegistry registry)
    {
        b.Entity<Label>(e =>
        {
            e.ToTable(registry.LabelTableName);
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(Label.MaxNameLength);
            e.Property(x => x.Color).HasMaxLength(16);
            e.Property(x => x.Icon).HasMaxLength(128);
            e.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();   // 後蓋前 → 拋例外
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

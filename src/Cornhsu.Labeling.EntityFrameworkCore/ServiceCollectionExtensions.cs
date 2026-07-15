using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>把標籤系統掛進 DI 容器的擴充方法。</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 註冊標籤系統:建立並封存 <see cref="LabelRegistry"/>(Singleton),
    /// 並以 <typeparamref name="TContext"/> 為後端註冊 <see cref="ILabelStore"/>(Scoped)。
    /// </summary>
    /// <typeparam name="TContext">應用程式的 DbContext。</typeparam>
    /// <param name="services">DI 服務集合。</param>
    /// <param name="configure">註冊可標記型別的設定動作。</param>
    public static IServiceCollection AddLabeling<TContext>(
        this IServiceCollection services,
        Action<LabelRegistry> configure)
        where TContext : DbContext
    {
        var registry = new LabelRegistry();
        configure(registry);
        registry.Seal();

        // 必須 Singleton:EF Core model cache 以 DbContext 型別為 key,registry 不可有多份
        services.AddSingleton(registry);
        services.AddScoped<ILabelStore, EfLabelStore<TContext>>();
        return services;
    }
}

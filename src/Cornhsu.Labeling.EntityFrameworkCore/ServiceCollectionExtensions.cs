using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>Extension methods that wire the labeling system into a DI container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the labeling system: builds and seals a <see cref="LabelRegistry"/> (Singleton),
    /// and registers <see cref="ILabelStore"/> (Scoped) backed by <typeparamref name="TContext"/>.
    /// Once sealed the registry is immutable, so register the types and set the registry's properties
    /// inside <paramref name="configure"/>.
    /// </summary>
    /// <typeparam name="TContext">Your application's DbContext.</typeparam>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">Callback that registers the labelable types and sets the registry's properties.</param>
    public static IServiceCollection AddLabeling<TContext>(
        this IServiceCollection services,
        Action<LabelRegistry> configure)
        where TContext : DbContext
    {
        // 先檢查參數:services 為 null 時不能等 configure 跑完、registry 封存之後才由 DI 拋
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        var registry = new LabelRegistry();
        configure(registry);
        registry.Seal();

        // 必須 Singleton:EF Core model cache 以 DbContext 型別為 key,registry 不可有多份
        services.AddSingleton(registry);
        services.AddScoped<ILabelStore, EfLabelStore<TContext>>();
        return services;
    }
}

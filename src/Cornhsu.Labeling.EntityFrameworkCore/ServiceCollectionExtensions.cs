using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cornhsu.Labeling.EntityFrameworkCore;

/// <summary>Extension methods that wire the labeling system into a DI container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the labeling system: builds and seals a <see cref="LabelRegistry"/> (Singleton),
    /// and registers <see cref="ILabelStore"/> (Scoped) backed by <typeparamref name="TContext"/>.
    /// </summary>
    /// <typeparam name="TContext">Your application's DbContext.</typeparam>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">Callback that registers the labelable types.</param>
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

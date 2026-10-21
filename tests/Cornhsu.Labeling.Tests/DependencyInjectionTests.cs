using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>AddLabeling 的 DI 註冊:LabelRegistry 必須是全 App 單例(EF model cache 以 DbContext 型別為 key),store 跟著 scope 走。</summary>
public class DependencyInjectionTests
{
    [Fact]
    public void AddLabeling_參數為null_拋出ArgumentNullException_且不執行configure()
    {
        var called = false;

        var nullServices = () => ((IServiceCollection)null!).AddLabeling<TestDbContext>(_ => called = true);
        var nullConfigure = () => new ServiceCollection().AddLabeling<TestDbContext>(null!);

        nullServices.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("services");
        called.Should().BeFalse("參數檢查要在執行 configure 之前,不然 registry 已經建好又封存了才失敗");
        nullConfigure.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("configure");
    }

    [Fact]
    public void ApplyLabelModel_參數為null_拋出ArgumentNullException()
    {
        var nullBuilder = () => ((ModelBuilder)null!).ApplyLabelModel(new LabelRegistry());
        var nullRegistry = () => new ModelBuilder().ApplyLabelModel(null!);

        nullBuilder.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("b");
        nullRegistry.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("registry");
    }

    [Fact]
    public void AddLabeling_Registry為Singleton_Store為Scoped()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestDbContext>(o => o.UseSqlite("DataSource=:memory:"));

        var returned = services.AddLabeling<TestDbContext>(r => r.Labelable<TestNote>());

        returned.Should().BeSameAs(services, "可串接");
        services.Single(d => d.ServiceType == typeof(LabelRegistry)).Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(ILabelStore)).Lifetime.Should().Be(ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var registry = provider.GetRequiredService<LabelRegistry>();
        scope1.ServiceProvider.GetRequiredService<LabelRegistry>().Should().BeSameAs(registry);
        scope2.ServiceProvider.GetRequiredService<LabelRegistry>().Should().BeSameAs(registry);
        registry.Descriptors.Should().ContainSingle().Which.ClrType.Should().Be(typeof(TestNote));

        var store1 = scope1.ServiceProvider.GetRequiredService<ILabelStore>();
        scope1.ServiceProvider.GetRequiredService<ILabelStore>().Should().BeSameAs(store1);
        scope2.ServiceProvider.GetRequiredService<ILabelStore>().Should().NotBeSameAs(store1);
    }
}

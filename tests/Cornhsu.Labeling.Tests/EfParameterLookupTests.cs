using System.Reflection;
using Cornhsu.Labeling.EntityFrameworkCore;
using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Tests;

/// <summary>
/// EF.Parameter 是 EF Core 9 起才有的方法,樓地板是 EF Core 8,只能在執行期以反射找。
/// 日後 EF 若多一個 Parameter 多載,找法不能在型別初始化就拋例外(那會讓整個 store 用不了)。
/// </summary>
public class EfParameterLookupTests
{
    /// <summary>模擬未來的 EF:多了幾個同名的多載。</summary>
    private static class FutureEf
    {
        public static T Parameter<T>(T argument) => argument;
        public static T Parameter<T>(T argument, string name) => argument;
        public static object Parameter(object argument) => argument;
    }

    /// <summary>模擬 EF Core 8:沒有 Parameter。</summary>
    private static class OldEf
    {
        public static T Constant<T>(T argument) => argument;
    }

    [Fact]
    public void 有多個Parameter多載時_找到單一參數的泛型版本()
    {
        var method = EfParameterLookup.Find(typeof(FutureEf), typeof(List<Guid>));

        method.Should().NotBeNull();
        method!.GetParameters().Should().ContainSingle().Which.ParameterType.Should().Be(typeof(List<Guid>));
        method.ReturnType.Should().Be(typeof(List<Guid>));
    }

    [Fact]
    public void 沒有Parameter時_回傳null_不拋例外()
        => EfParameterLookup.Find(typeof(OldEf), typeof(List<Guid>)).Should().BeNull();

    [Fact]
    public void 真正的EF_9以上找得到_8找不到()
    {
        var efMajor = typeof(Microsoft.EntityFrameworkCore.EF).Assembly.GetName().Version!.Major;
        var method = EfParameterLookup.ForGuidList;

        if (efMajor >= 9)
            method.Should().NotBeNull();
        else
            method.Should().BeNull();
    }
}

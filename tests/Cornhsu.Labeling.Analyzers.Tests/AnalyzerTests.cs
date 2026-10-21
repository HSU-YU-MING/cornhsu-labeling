using System.Collections.Immutable;
using System.Globalization;
using Cornhsu.Labeling.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Cornhsu.Labeling.Analyzers.Tests;

public class AnalyzerTests
{
    /// <summary>把測試原始碼編譯起來跑 analyzer,回傳它產生的診斷。</summary>
    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        // 執行期的全部平台組件 + 本套件兩顆 dll
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(Cornhsu.Labeling.ILabelable).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Cornhsu.Labeling.EntityFrameworkCore.LabelRegistry).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestTarget",
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // 測試原始碼本身必須先能編譯,否則診斷結果沒有意義
        compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("測試原始碼要能編譯");

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new LabelableRegistrationAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task 實作ILabelable但沒註冊_報CHSU001()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public class Note : ILabelable<int>
            {
                public int Id { get; set; }
            }
            """);

        diags.Should().ContainSingle(d => d.Id == "CHSU001")
            .Which.GetMessage().Should().Contain("Note");
    }

    [Fact]
    public async Task 有註冊_不報警告()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public class Note : ILabelable<int>
            {
                public int Id { get; set; }
            }

            public static class Setup
            {
                public static void Configure(LabelRegistry r) => r.Labelable<Note>();
            }
            """);

        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task 只實作marker_報CHSU002()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public class Broken : ILabelable
            {
                public int Id { get; set; }
            }
            """);

        diags.Should().ContainSingle(d => d.Id == "CHSU002")
            .Which.GetMessage().Should().Contain("Broken");
    }

    [Fact]
    public async Task 抽象類別實作ILabelable_不報警告()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public abstract class EntityBase : ILabelable<int>
            {
                public int Id { get; set; }
            }

            public class Note : EntityBase { }

            public static class Setup
            {
                public static void Configure(LabelRegistry r) => r.Labelable<Note>();
            }
            """);

        diags.Should().BeEmpty("抽象基底不用註冊;具象的 Note 有註冊");
    }

    [Fact]
    public async Task 多型別部分註冊_只報沒註冊的那個()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public class Note : ILabelable<int> { public int Id { get; set; } }
            public class Todo : ILabelable<int> { public int Id { get; set; } }

            public static class Setup
            {
                public static void Configure(LabelRegistry r) => r.Labelable<Note>(null, null);
            }
            """);

        diags.Should().ContainSingle(d => d.Id == "CHSU001")
            .Which.GetMessage().Should().Contain("Todo");
    }

    [Fact]
    public async Task 沒引用本套件的程式碼_analyzer不做事()
    {
        var diags = await AnalyzeAsync("""
            public class Plain
            {
                public int Id { get; set; }
            }
            """);

        diags.Should().BeEmpty();
    }

    // ---- 對外可見的描述子與訊息文字:改了算 minor,要寫進 CHANGELOG ----

    [Fact]
    public void 診斷描述子_ID嚴重度分類標題與說明連結()
    {
        new LabelableRegistrationAnalyzer().SupportedDiagnostics.Select(d => d.Id)
            .Should().Equal("CHSU001", "CHSU002");

        var notRegistered = LabelableRegistrationAnalyzer.NotRegistered;
        notRegistered.Id.Should().Be("CHSU001");
        notRegistered.Title.ToString(CultureInfo.InvariantCulture).Should().Be("Labelable type is not registered");
        notRegistered.Category.Should().Be("Cornhsu.Labeling");
        notRegistered.DefaultSeverity.Should().Be(DiagnosticSeverity.Warning);
        notRegistered.IsEnabledByDefault.Should().BeTrue();
        notRegistered.HelpLinkUri.Should().Be(
            "https://github.com/HSU-YU-MING/cornhsu-labeling/blob/main/docs/analyzer-rules.md#chsu001");
        notRegistered.CustomTags.Should().Contain(WellKnownDiagnosticTags.CompilationEnd);

        var markerOnly = LabelableRegistrationAnalyzer.MarkerOnly;
        markerOnly.Id.Should().Be("CHSU002");
        markerOnly.Title.ToString(CultureInfo.InvariantCulture).Should().Be("Only the non-generic ILabelable marker is implemented");
        markerOnly.Category.Should().Be("Cornhsu.Labeling");
        markerOnly.DefaultSeverity.Should().Be(DiagnosticSeverity.Warning);
        markerOnly.IsEnabledByDefault.Should().BeTrue();
        markerOnly.HelpLinkUri.Should().Be(
            "https://github.com/HSU-YU-MING/cornhsu-labeling/blob/main/docs/analyzer-rules.md#chsu002");
        markerOnly.CustomTags.Should().Contain(WellKnownDiagnosticTags.CompilationEnd);
    }

    [Fact]
    public async Task CHSU001_完整訊息文字_主鍵型別印metadata名稱_位置在型別名稱上()
    {
        var source = """
            using System;
            using Cornhsu.Labeling;

            public class Note : ILabelable<int> { public int Id { get; set; } }
            public class Memo : ILabelable<Guid> { public Guid Id { get; set; } }
            """;
        var diags = await AnalyzeAsync(source);

        diags.Should().HaveCount(2).And.OnlyContain(d => d.Id == "CHSU001" && d.Severity == DiagnosticSeverity.Warning);
        diags.Select(d => d.GetMessage(CultureInfo.InvariantCulture)).Should().BeEquivalentTo(
            "Type 'Note' implements ILabelable<Int32>, but this compilation contains no r.Labelable<Note>() registration; " +
            "attaching or querying it at run time will throw \"type is not registered\". " +
            "Suppress this warning if the registration lives in another assembly.",
            "Type 'Memo' implements ILabelable<Guid>, but this compilation contains no r.Labelable<Memo>() registration; " +
            "attaching or querying it at run time will throw \"type is not registered\". " +
            "Suppress this warning if the registration lives in another assembly.");
        diags.Select(d => source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length))
            .Should().BeEquivalentTo("Note", "Memo");
    }

    [Fact]
    public async Task CHSU002_完整訊息文字()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public class Broken : ILabelable { }
            """);

        diags.Should().ContainSingle().Which.Should().Match<Diagnostic>(d =>
            d.Id == "CHSU002" && d.Severity == DiagnosticSeverity.Warning);
        diags[0].GetMessage(CultureInfo.InvariantCulture).Should().Be(
            "Type 'Broken' implements only the non-generic ILabelable; registering it will throw " +
            "because the key type cannot be inferred. Implement ILabelable<TKey> instead " +
            "(for example ILabelable<int> or ILabelable<Guid>).");
    }

    // ---- 判定邊界 ----

    [Fact]
    public async Task record_class與巢狀類別_有註冊不報_沒註冊照報()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public record Note(int Id) : ILabelable<int>;
            public record Orphan(int Id) : ILabelable<int>;

            public class Outer
            {
                public class Inner : ILabelable<int> { public int Id { get; set; } }
                public class Lost : ILabelable<int> { public int Id { get; set; } }
            }

            public static class Setup
            {
                public static void Configure(LabelRegistry r) => r.Labelable<Note>().Labelable<Outer.Inner>();
            }
            """);

        diags.Should().OnlyContain(d => d.Id == "CHSU001");
        diags.Select(d => d.GetMessage(CultureInfo.InvariantCulture))
            .Should().HaveCount(2)
            .And.ContainSingle(m => m.StartsWith("Type 'Orphan' ", StringComparison.Ordinal))
            .And.ContainSingle(m => m.StartsWith("Type 'Outer.Lost' ", StringComparison.Ordinal),
                "巢狀型別帶外層型別名,訊息建議的 r.Labelable<Outer.Lost>() 才編譯得過");
    }

    [Fact]
    public async Task 泛型實體以建構型別註冊_不報()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public class Box<T> : ILabelable<int> { public int Id { get; set; } }
            public class Keyed<TKey> : ILabelable<TKey> where TKey : notnull { public TKey Id { get; set; } = default!; }

            public static class Setup
            {
                public static void Configure(LabelRegistry r)
                    => r.Labelable<Box<string>>(typeKey: "StringBox").Labelable<Keyed<int>>(typeKey: "IntKeyed");
            }
            """);

        diags.Should().BeEmpty("註冊了任何一個建構型別,宣告就算有註冊");
    }

    [Fact]
    public async Task 泛型類別裡的巢狀實體以建構型別註冊_不報()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public class Outer<T>
            {
                public class Note : ILabelable<int> { public int Id { get; set; } }
            }

            public static class Setup
            {
                public static void Configure(LabelRegistry r) => r.Labelable<Outer<int>.Note>();
            }
            """);

        diags.Should().BeEmpty();
    }

    [Fact]
    public async Task 泛型與巢狀實體沒註冊_訊息裡的型別寫法照抄得進註冊呼叫()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public class Box<T> : ILabelable<int> { public int Id { get; set; } }

            public class Outer<T>
            {
                public class Note : ILabelable<int> { public int Id { get; set; } }
            }

            public class Plain
            {
                public class Inner : ILabelable<int> { public int Id { get; set; } }
            }
            """);

        diags.Should().HaveCount(3).And.OnlyContain(d => d.Id == "CHSU001");
        diags.Select(d => d.GetMessage(CultureInfo.InvariantCulture)).Should().BeEquivalentTo(
            "Type 'Box<T>' implements ILabelable<Int32>, but this compilation contains no r.Labelable<Box<T>>() registration; " +
            "attaching or querying it at run time will throw \"type is not registered\". " +
            "Suppress this warning if the registration lives in another assembly.",
            "Type 'Outer<T>.Note' implements ILabelable<Int32>, but this compilation contains no r.Labelable<Outer<T>.Note>() registration; " +
            "attaching or querying it at run time will throw \"type is not registered\". " +
            "Suppress this warning if the registration lives in another assembly.",
            "Type 'Plain.Inner' implements ILabelable<Int32>, but this compilation contains no r.Labelable<Plain.Inner>() registration; " +
            "attaching or querying it at run time will throw \"type is not registered\". " +
            "Suppress this warning if the registration lives in another assembly.");
    }

    [Fact]
    public async Task 在lambda裡註冊也算數()
    {
        var diags = await AnalyzeAsync("""
            using System;
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public class Note : ILabelable<int> { public int Id { get; set; } }

            public static class Setup
            {
                public static readonly Action<LabelRegistry> Configure = r => r.Labelable<Note>(n => n.Id.ToString());
            }
            """);

        diags.Should().BeEmpty("AddLabeling(r => ...) 是最常見的寫法");
    }

    [Fact]
    public async Task 已註冊基底類別的衍生類別本身沒註冊_照報()
    {
        // 執行期 Require 以精確型別比對:註冊了 Note 不代表 SpecialNote 可以貼標
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;
            using Cornhsu.Labeling.EntityFrameworkCore;

            public class Note : ILabelable<int> { public int Id { get; set; } }
            public class SpecialNote : Note { }

            public static class Setup
            {
                public static void Configure(LabelRegistry r) => r.Labelable<Note>();
            }
            """);

        diags.Should().ContainSingle(d => d.Id == "CHSU001")
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().StartWith("Type 'SpecialNote' ");
    }

    [Fact]
    public async Task 介面繼承ILabelable_不是類別_不報()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public interface IHasLabels : ILabelable<int> { }
            public interface IMarkerToo : ILabelable { }
            """);

        diags.Should().BeEmpty("只有具象類別才需要註冊");
    }

    [Fact]
    public async Task struct與record_struct實作ILabelable_註冊不了_不報()
    {
        // Labelable<TEntity> 限定 class:struct 根本無法註冊,報 CHSU001 叫人去註冊只是噪音
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public struct Point : ILabelable<int> { public int Id { get; set; } }
            public record struct Pair(int Id) : ILabelable<int>;
            public struct Bare : ILabelable { }
            """);

        diags.Should().BeEmpty("struct 不能交給 Labelable<T>,警告了也沒有辦法照著修");
    }

    [Fact]
    public async Task 別的型別上同名的Labelable方法_不算註冊()
    {
        var diags = await AnalyzeAsync("""
            using Cornhsu.Labeling;

            public class Note : ILabelable<int> { public int Id { get; set; } }

            public class OtherRegistry
            {
                public OtherRegistry Labelable<T>() => this;
                public OtherRegistry Labelable() => this;
            }

            public static class Setup
            {
                public static void Configure(OtherRegistry r) => r.Labelable<Note>().Labelable();
            }
            """);

        diags.Should().ContainSingle(d => d.Id == "CHSU001")
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().StartWith("Type 'Note' ");
    }
}

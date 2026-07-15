using System.Collections.Immutable;
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
}

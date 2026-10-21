using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Cornhsu.Labeling.Analyzers.Tests;

/// <summary>
/// 打包與版本設定的守門。這兩件事出錯時 build、test、pack 全部照樣成功,沒有任何訊息,
/// 只有使用者那一端會發現,所以直接讀專案檔、對照這次建置的實際產物。
/// </summary>
public class PackagingTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Cornhsu.Labeling.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("找不到 repo 根目錄(Cornhsu.Labeling.slnx)。");
    }

    [Fact]
    public void Cornhsu_Labeling打包時_analyzer的dll放進analyzers_dotnet_cs_路徑指向這次建置的產物()
    {
        // CLAUDE.md 地雷:analyzer 不獨立發佈,它的 dll 靠 Cornhsu.Labeling.csproj 裡一條硬寫的 bin 路徑
        // 打進套件。改 analyzer 的 TFM 或輸出位置而路徑沒跟上時,pack 不會報錯,
        // 使用者裝了套件卻沒有編譯期防呆。
        var coreDir = Path.Combine(RepoRoot, "src", "Cornhsu.Labeling");
        var packed = XDocument.Load(Path.Combine(coreDir, "Cornhsu.Labeling.csproj"))
            .Descendants("None")
            .Where(e => (string?)e.Attribute("PackagePath") == "analyzers/dotnet/cs")
            .Should().ContainSingle("analyzer 的 dll 要以 analyzers/dotnet/cs 路徑打進 Cornhsu.Labeling").Subject;
        ((string?)packed.Attribute("Pack")).Should().Be("true");

        var analyzerTfm = XDocument.Load(Path.Combine(RepoRoot, "src", "Cornhsu.Labeling.Analyzers", "Cornhsu.Labeling.Analyzers.csproj"))
            .Descendants("TargetFramework").Should().ContainSingle().Subject.Value;
        analyzerTfm.Should().Be("netstandard2.0", "VS 與各版 SDK 只保證能載入 netstandard2.0 的 analyzer");

        var configuration = typeof(PackagingTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var segments = ((string)packed.Attribute("Include")!).Replace("$(Configuration)", configuration).Split('\\', '/');
        segments.Should().Contain(analyzerTfm, "硬寫路徑裡的 TFM 要跟 analyzer 專案一致,否則會打包到過期的產物或找不到檔案");

        var dll = Path.GetFullPath(Path.Combine(new[] { coreDir }.Concat(segments).ToArray()));
        File.Exists(dll).Should().BeTrue($"打包時會去拿 {dll},那裡要有這次建置出來的 analyzer");
        AssemblyName.GetAssemblyName(dll).Name.Should().Be(typeof(LabelableRegistrationAnalyzer).Assembly.GetName().Name);
    }

    [Fact]
    public void 版本欄位固定是0_0_0_dev_只在Directory_Build_props寫一次()
    {
        // CLAUDE.md 教訓(commit 3b285d0):版本的真相源是 git tag,release.yml 以 -p:Version= 覆寫。
        // 留空會退回 MSBuild 預設的 1.0.0,寫死真版本號又一定會跟 tag 漂開——
        // 兩種都是「看起來合理」的錯,從原始碼建置的組件會自稱舊版而沒有人懷疑。
        XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"))
            .Descendants("Version").Select(e => e.Value)
            .Should().Equal(new[] { "0.0.0-dev" }, "錯得明顯勝過錯得像真的");

        foreach (var project in Directory.GetFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories))
            XDocument.Load(project).Descendants()
                .Where(e => e.Name.LocalName is "Version" or "VersionPrefix" or "PackageVersion")
                .Should().BeEmpty($"{Path.GetFileName(project)} 不該另外覆寫版本");
    }
}

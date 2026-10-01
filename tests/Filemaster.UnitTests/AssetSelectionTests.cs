using System.Reflection;
using System.Runtime.Versioning;

namespace Filemaster.UnitTests;

/// <summary>Verifica quale asset (TFM) delle librerie viene davvero caricato dal progetto di test.</summary>
public sealed class AssetSelectionTests
{
    [Theory]
    [InlineData("Filemaster.Domain")]
    [InlineData("Filemaster.Application")]
    [InlineData("Filemaster.Infrastructure")]
    [InlineData("Filemaster")]
    public void Library_assembly_is_loaded_from_the_expected_asset(string assemblyName)
    {
        var expected = typeof(AssetSelectionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ExpectedLibraryAsset")?.Value;
        Assert.False(string.IsNullOrEmpty(expected), "Metadato ExpectedLibraryAsset assente: il csproj non lo ha generato.");

        var actual = Assembly.Load(new AssemblyName(assemblyName)).GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        TestContext.Current.SendDiagnosticMessage($"{assemblyName}: asset {actual} (atteso {expected}), runtime {Environment.Version}");

        Assert.Equal(expected, actual);
    }
}

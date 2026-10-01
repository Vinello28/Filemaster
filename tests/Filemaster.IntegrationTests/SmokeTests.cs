using System.Reflection;

namespace Filemaster.IntegrationTests;

public sealed class SmokeTests
{
    [Theory]
    [InlineData("Filemaster.Domain")]
    [InlineData("Filemaster.Application")]
    [InlineData("Filemaster.Infrastructure")]
    [InlineData("Filemaster")]
    public void Library_assembly_loads(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        Assert.Equal(assemblyName, assembly.GetName().Name);
    }
}

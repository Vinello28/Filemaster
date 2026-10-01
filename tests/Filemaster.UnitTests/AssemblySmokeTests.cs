using System.Reflection;
using ApplicationGuard = Filemaster.Application.Guard;
using DomainGuard = Filemaster.Domain.Guard;
using InfrastructureGuard = Filemaster.Infrastructure.Guard;

namespace Filemaster.UnitTests;

/// <summary>Prova di fumo: ogni assembly si carica e il suo Guard interno e' raggiungibile via InternalsVisibleTo.</summary>
public sealed class AssemblySmokeTests
{
    [Fact]
    public void Domain_Guard_NotNull_with_null_throws_ArgumentNullException_with_parameter_name()
    {
        object? value = null;

        var exception = Assert.Throws<ArgumentNullException>(() => DomainGuard.NotNull(value));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Application_Guard_NotNull_with_null_throws_ArgumentNullException_with_parameter_name()
    {
        object? value = null;

        var exception = Assert.Throws<ArgumentNullException>(() => ApplicationGuard.NotNull(value));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Infrastructure_Guard_NotNull_with_null_throws_ArgumentNullException_with_parameter_name()
    {
        object? value = null;

        var exception = Assert.Throws<ArgumentNullException>(() => InfrastructureGuard.NotNull(value));

        Assert.Equal("value", exception.ParamName);
    }

    [Fact]
    public void Filemaster_assembly_loads()
    {
        // Il root non espone internals ai test e per ora non ha tipi pubblici: lo si carica per nome.
        var assembly = Assembly.Load(new AssemblyName("Filemaster"));

        Assert.Equal("Filemaster", assembly.GetName().Name);
    }
}

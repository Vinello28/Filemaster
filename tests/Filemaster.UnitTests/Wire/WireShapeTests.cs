using System.Reflection;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Wire;

/// <summary>
/// La forma del livello wire, resa eseguibile: nessun tipo nuovo e' pubblico (la superficie pubblica dell'Infrastructure resta quella delle
/// opzioni, e il test di <c>InfrastructureShapeTests</c> lo controlla a parte), nessun <c>init</c> ne' <c>required</c> in nessun membro (nemmeno
/// internal: chi chiama da C# 7.3 non puo' usarli e il codice interno segue la stessa regola), nessun metodo pubblico di istanza che lanci
/// eccezioni di <c>System.Text.Json</c> (non c'e' codice pubblico: tutto e' internal).
/// </summary>
public sealed class WireShapeTests
{
    private const string IsExternalInitFullName = "System.Runtime.CompilerServices.IsExternalInit";
    private const string RequiredMemberFullName = "System.Runtime.CompilerServices.RequiredMemberAttribute";

    private static readonly Assembly InfrastructureAssembly = typeof(WireJson).Assembly;

    // I tipi del livello wire (cartella Wire/). Se se ne aggiunge uno, va qui: il test sotto controlla che ognuno esista, cosi' l'elenco non resta indietro.
    private static readonly string[] WireTypeNames =
    {
        "WireContext", "WireJson", "WireObject", "WireIdParser`1", "WireDates", "PageWire", "FolderWire", "TenantWire", "DocumentWire", "VerifyWire",
        "ContactWire", "HealthWire", "PercentEncoding", "QueryBuilder", "Routes", "ContentDispositionHeader", "ContentRangeHeader", "DownloadHeaders",
    };

    private static IEnumerable<Type> WireTypes() =>
        WireTypeNames.Select(name => InfrastructureAssembly.GetType("Filemaster.Infrastructure." + name, throwOnError: false)
            ?? throw new InvalidOperationException("Tipo wire non trovato: " + name));

    [Fact]
    public void Every_wire_type_exists_and_none_is_public_or_nested_public()
    {
        var types = WireTypes().ToArray();

        Assert.Equal(WireTypeNames.Length, types.Length);
        Assert.All(types, t => Assert.False(t.IsPublic || t.IsNestedPublic, t.Name + " e' pubblico"));
    }

    [Fact]
    public void The_public_surface_of_the_assembly_is_still_only_the_two_options_types()
    {
        var exported = InfrastructureAssembly.GetExportedTypes().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "FilemasterOptions", "FilemasterRetryOptions" }, exported);
    }

    [Fact]
    public void The_helpers_are_static_classes_and_the_models_are_sealed()
    {
        foreach (var name in new[] { "WireJson", "WireDates", "PageWire", "FolderWire", "TenantWire", "DocumentWire", "VerifyWire", "ContactWire", "HealthWire", "PercentEncoding", "Routes", "ContentDispositionHeader", "ContentRangeHeader" })
        {
            var type = InfrastructureAssembly.GetType("Filemaster.Infrastructure." + name)!;

            Assert.True(type.IsAbstract && type.IsSealed, name + ": classe statica");
        }

        Assert.True(typeof(DownloadHeaders).IsSealed);
        Assert.True(typeof(QueryBuilder).IsSealed);
        Assert.True(typeof(WireContext).IsSealed);
    }

    [Fact]
    public void No_member_of_the_wire_types_has_an_init_accessor_or_is_required()
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in WireTypes())
        {
            foreach (var property in type.GetProperties(All))
            {
                if (property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == IsExternalInitFullName) == true)
                {
                    offenders.Add(type.Name + "." + property.Name + " (init)");
                }

                if (property.CustomAttributes.Any(a => a.AttributeType.FullName == RequiredMemberFullName))
                {
                    offenders.Add(type.Name + "." + property.Name + " (required)");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_wire_types_do_not_call_the_network()
    {
        // Funzioni pure: nessun campo ne' membro dei tipi wire usa HttpClient o HttpMessageHandler (le intestazioni si leggono da
        // HttpContentHeaders, che e' dato, non rete).
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in WireTypes())
        {
            foreach (var field in type.GetFields(All))
            {
                Assert.False(typeof(HttpClient).IsAssignableFrom(field.FieldType) || typeof(HttpMessageHandler).IsAssignableFrom(field.FieldType), type.Name + "." + field.Name);
            }

            foreach (var method in type.GetMethods(All))
            {
                Assert.False(typeof(Task).IsAssignableFrom(method.ReturnType), type.Name + "." + method.Name + " e' asincrono");
            }
        }
    }

    [Fact]
    public void The_fixtures_were_copied_next_to_the_assembly_and_the_scrubbed_README_is_there()
    {
        Assert.True(WireFixtures.CapturedFileCount() > 100);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Wire", "Fixtures", "README.md")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Wire", "Fixtures", "INDEX.tsv")));
    }
}

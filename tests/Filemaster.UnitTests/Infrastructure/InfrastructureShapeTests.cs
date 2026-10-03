using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Filemaster.Infrastructure;

namespace Filemaster.UnitTests.Infrastructure;

/// <summary>
/// La superficie pubblica dell'Infrastructure, resa eseguibile: i soli tipi pubblici sono le opzioni e la composizione
/// (<see cref="FilemasterHttp"/>); nessun <c>init</c> e nessun <c>required</c> sui membri pubblici (chi chiama da C# 7.3 non
/// puo' usarli); ogni tipo, proprieta', campo e metodo pubblico ha un <c>&lt;summary&gt;</c> nel file XML; l'assembly non dipende dal
/// livello sopra (<c>Filemaster</c>) ne' da <c>Microsoft.Extensions.Http</c> o <c>DependencyInjection</c>. Tutto il resto del codice
/// dell'Infrastructure (trasporto, mappatura degli errori, adapter, DTO) e' <c>internal</c>.
/// </summary>
public sealed class InfrastructureShapeTests
{
    // Per nome completo, mai con typeof: sull'asset netstandard2.0 il modreq e' una copia interna di PolySharp, e un confronto con il
    // tipo del framework passerebbe a vuoto.
    private const string IsExternalInitFullName = "System.Runtime.CompilerServices.IsExternalInit";
    private const string RequiredMemberFullName = "System.Runtime.CompilerServices.RequiredMemberAttribute";

    private static readonly Assembly InfrastructureAssembly = typeof(FilemasterOptions).Assembly;

    // I tipi pubblici, ESATTAMENTE questi (ordine ordinale): un tipo in piu' o in meno e' un errore.
    private static readonly string[] PublicTypes =
    {
        nameof(FilemasterHttp),
        nameof(FilemasterOptions),
        nameof(FilemasterRetryOptions),
    };

    [Fact]
    public void The_public_types_of_the_assembly_are_exactly_the_options_and_the_composition()
    {
        var publicTypes = InfrastructureAssembly.GetExportedTypes().Select(t => t.FullName!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(PublicTypes.Select(name => "Filemaster.Infrastructure." + name), publicTypes);
    }

    [Fact]
    public void The_composition_is_a_static_class_with_the_one_CreateClient_method()
    {
        var type = typeof(FilemasterHttp);
        Assert.True(type.IsAbstract && type.IsSealed, "FilemasterHttp: classe static");

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        var create = Assert.Single(methods);
        Assert.Equal(nameof(FilemasterHttp.CreateClient), create.Name);
        Assert.Equal(
            new[] { typeof(HttpClient), typeof(FilemasterOptions), typeof(Microsoft.Extensions.Logging.ILoggerFactory), typeof(TimeProvider) },
            create.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Filemaster.Application.IFilemasterClient), create.ReturnType);
    }

    [Fact]
    public void No_public_member_has_an_init_accessor_or_is_required()
    {
        var offenders = new List<string>();
        foreach (var type in InfrastructureAssembly.GetExportedTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == IsExternalInitFullName) == true)
                {
                    offenders.Add($"{type.Name}.{property.Name} (init)");
                }

                if (property.CustomAttributes.Any(a => a.AttributeType.FullName == RequiredMemberFullName))
                {
                    offenders.Add($"{type.Name}.{property.Name} (required)");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_options_are_sealed_classes_with_public_getters_and_setters_and_a_Validate_method()
    {
        foreach (var type in new[] { typeof(FilemasterOptions), typeof(FilemasterRetryOptions) })
        {
            Assert.True(type.IsClass && type.IsSealed && !type.IsAbstract, $"{type.Name}: classe sealed");
            var validate = type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, binder: null, Type.EmptyTypes, modifiers: null);
            Assert.True(validate is not null && validate.ReturnType == typeof(void), $"{type.Name}: manca un Validate() void senza argomenti");
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true, $"{type.Name}.{property.Name}: get e set pubblici");
            }
        }
    }

    [Fact]
    public void The_assembly_depends_on_neither_the_composition_root_nor_Microsoft_Extensions_Http_nor_DependencyInjection()
    {
        var references = InfrastructureAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.Contains("Filemaster.Domain", references); // il controllo non passa a vuoto (la mappatura degli errori lancia le eccezioni del Domain)
        Assert.DoesNotContain(references, n => n == "Filemaster");
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_public_type_member_and_constant_has_a_summary_in_the_XML_documentation()
    {
        // CS1591 e' gia' un errore di build; questo guardiano rileva chi lo spegne (NoWarn, #pragma). Il file .xml viaggia accanto
        // alla DLL nella cartella dei test.
        var xmlPath = Path.Combine(AppContext.BaseDirectory, "Filemaster.Infrastructure.xml");
        Assert.True(File.Exists(xmlPath), $"manca {xmlPath}: GenerateDocumentationFile e' spento o il file non viene copiato");

        var documented = XDocument.Load(xmlPath)
            .Descendants("member")
            .Where(member => !string.IsNullOrWhiteSpace((string?)member.Element("summary")) || member.Element("inheritdoc") is not null)
            .Select(member => (string?)member.Attribute("name"))
            .ToArray();

        var missing = new List<string>();
        void Require(string id, bool prefix = false)
        {
            if (!documented.Any(name => prefix ? name!.StartsWith(id + "(", StringComparison.Ordinal) || name == id : name == id))
            {
                missing.Add(id);
            }
        }

        foreach (var type in InfrastructureAssembly.GetExportedTypes())
        {
            var typeName = type.FullName!.Replace('+', '.');
            Require("T:" + typeName);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Require("P:" + typeName + "." + property.Name);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!field.IsSpecialName)
                {
                    Require("F:" + typeName + "." + field.Name);
                }
            }

            // I metodi si cercano per nome senza la lista dei parametri (l'identificatore XML dei riferimenti e' macchinoso da
            // ricostruire). Il codice generato non ha documentazione e non ne ha bisogno.
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!method.IsSpecialName && !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    Require("M:" + typeName + "." + method.Name, prefix: true);
                }
            }

            // Il costruttore implicito senza parametri non ha documentazione e CS1591 non la chiede.
            if (type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Any(c => c.GetParameters().Length > 0))
            {
                Require("M:" + typeName + ".#ctor", prefix: true);
            }
        }

        Assert.Empty(missing);
    }
}

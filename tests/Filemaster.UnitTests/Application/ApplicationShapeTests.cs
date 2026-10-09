using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Filemaster.Application;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Application;

/// <summary>
/// Le regole del piano sulla forma pubblica dell'Application, rese eseguibili: nessun <c>init</c> e nessun <c>required</c>
/// (chi chiama da C# 7.3 non puo' usarli), i setter pubblici solo sui tipi di input che si validano, ogni metodo asincrono
/// delle porte (e ogni metodo statico asincrono, cioe' le estensioni) finisce con un <see cref="CancellationToken"/> con default, la facciata espone le porte senza ereditarle,
/// l'assembly non dipende da <c>System.Net.Http</c> ne' da <c>Microsoft.Extensions.*</c>, tutto e' documentato, e ogni record
/// posizionale restituisce da ogni proprieta' l'argomento passato al costruttore.
/// </summary>
public sealed class ApplicationShapeTests
{
    // Per nome completo, mai con typeof: sull'asset netstandard2.0 il modreq e' una copia interna di PolySharp, e un
    // confronto con il tipo del framework passerebbe a vuoto.
    private const string IsExternalInitFullName = "System.Runtime.CompilerServices.IsExternalInit";
    private const string RequiredMemberFullName = "System.Runtime.CompilerServices.RequiredMemberAttribute";

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly ApplicationAssembly = typeof(IFilemasterClient).Assembly;

    // I soli tipi con un setter pubblico: gli input, che si riempiono con "set" e si controllano con Validate().
    private static readonly string[] ExpectedInputTypes =
    {
        nameof(ContactQuery),
        nameof(CreateFolderRequest),
        nameof(DocumentQuery),
        nameof(UpdateFolderRequest),
        nameof(UploadDocumentRequest),
    };

    private static readonly Type[] ExpectedPorts =
    {
        typeof(IContactDirectory),
        typeof(IDocumentStore),
        typeof(IFilemasterHealth),
        typeof(IFolderCatalog),
        typeof(ITenantInfo),
    };

    [Fact]
    public void No_type_has_an_init_accessor_or_a_required_member()
    {
        var offenders = new List<string>();
        foreach (var type in ApplicationAssembly.GetTypes())
        {
            foreach (var property in type.GetProperties(AllDeclared))
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
    public void Only_the_input_types_have_a_public_setter_and_each_of_them_has_a_Validate_method()
    {
        // Un setter pubblico "ereditato" non conta: i tipi che derivano dal framework (VerifiedContentStream : Stream) hanno
        // Position, ReadTimeout e WriteTimeout con il setter di Stream, e una proprieta' sovrascritta (Position) ha come
        // definizione di base quella del framework. Conta solo un setter la cui prima definizione sta in questo assembly.
        var withSetter = ApplicationAssembly.GetExportedTypes()
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(p => p.SetMethod?.IsPublic == true && p.SetMethod.GetBaseDefinition().DeclaringType!.Assembly == ApplicationAssembly))
            .ToArray();

        Assert.Equal(ExpectedInputTypes, withSetter.Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        foreach (var type in withSetter)
        {
            Assert.True(type.IsClass && type.IsSealed && !IsRecordClass(type), $"{type.Name}: un input e' una classe sealed, non un record");

            var validate = type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, binder: null, Type.EmptyTypes, modifiers: null);
            Assert.True(validate is not null && validate.ReturnType == typeof(void), $"{type.Name}: manca un Validate() void senza argomenti");
        }
    }

    [Fact]
    public void Every_asynchronous_port_method_ends_with_a_CancellationToken_that_has_a_default()
    {
        var methods = ExportedInterfaces().SelectMany(i => i.GetMethods()).Where(m => !m.IsSpecialName).ToArray(); // senza i getter delle proprieta'

        Assert.True(methods.Length >= 20, $"troppo pochi metodi trovati ({methods.Length}): il test non guarda le porte");
        var offenders = new List<string>();
        foreach (var method in methods)
        {
            var name = $"{method.DeclaringType!.Name}.{method.Name}";
            var returnsTask = method.ReturnType == typeof(Task)
                || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>));
            if (!returnsTask || !method.Name.EndsWith("Async", StringComparison.Ordinal))
            {
                offenders.Add($"{name}: deve restituire un Task e chiamarsi ...Async");
                continue;
            }

            var last = method.GetParameters().LastOrDefault();
            if (last is null || last.ParameterType != typeof(CancellationToken) || !last.HasDefaultValue)
            {
                offenders.Add($"{name}: l'ultimo parametro deve essere un CancellationToken con default");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_public_static_method_that_returns_a_Task_or_an_async_sequence_ends_with_a_CancellationToken_that_has_a_default()
    {
        // Le estensioni sulle porte (EnumerateAsync, FindByArxivarDocnumberAsync, OpenVerifiedContentAsync): stessa regola dei metodi
        // delle porte. Si guardano tutti i metodi statici pubblici, di estensione o no. IAsyncEnumerable si riconosce per nome: sull'asset
        // netstandard2.0 il tipo viene da Microsoft.Bcl.AsyncInterfaces. [EnumeratorCancellation] non si impone: sta sull'iteratore
        // privato, non su un metodo pubblico che valida e basta.
        var methods = ApplicationAssembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && ReturnsTaskOrAsyncSequence(m.ReturnType))
            .ToArray();

        Assert.True(methods.Length >= 4, $"troppi pochi metodi trovati ({methods.Length}): il test non guarda le estensioni");
        var offenders = new List<string>();
        foreach (var method in methods)
        {
            var last = method.GetParameters().LastOrDefault();
            if (last is null || last.ParameterType != typeof(CancellationToken) || !last.HasDefaultValue)
            {
                offenders.Add($"{method.DeclaringType!.Name}.{method.Name}: l'ultimo parametro deve essere un CancellationToken con default");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_client_facade_exposes_every_port_as_a_read_only_property_and_inherits_none()
    {
        var facade = typeof(IFilemasterClient);

        Assert.Empty(facade.GetInterfaces()); // niente IDisposable, niente porte ereditate
        Assert.DoesNotContain(facade.GetMethods(), m => !m.IsSpecialName); // solo i getter delle proprieta'

        var properties = facade.GetProperties();
        Assert.All(properties, p => Assert.True(p.CanRead && !p.CanWrite, $"{p.Name}: solo lettura"));
        Assert.Equal(
            ExpectedPorts.Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal),
            properties.Select(p => p.PropertyType.Name).OrderBy(name => name, StringComparer.Ordinal));

        // Le porte sono tutte e sole le interfacce dell'assembly (oltre alla facciata): una porta nuova va esposta.
        Assert.Equal(
            ExpectedPorts.Select(t => t.Name).Concat(new[] { nameof(IFilemasterClient) }).OrderBy(name => name, StringComparer.Ordinal),
            ExportedInterfaces().Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void The_assembly_depends_on_neither_the_HTTP_stack_nor_Microsoft_Extensions_nor_the_layers_above()
    {
        var references = ApplicationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.Contains("Filemaster.Domain", references); // il controllo non passa a vuoto
        Assert.DoesNotContain(references, n => n.StartsWith("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n == "Filemaster.Infrastructure" || n == "Filemaster");
    }

    [Fact]
    public void Every_public_type_member_and_constant_has_a_summary_in_the_XML_documentation()
    {
        // CS1591 e' gia' un errore di build; questo guardiano rileva chi lo spegne (NoWarn, #pragma) e controlla anche
        // le proprieta' ridichiarate. Il file .xml viaggia accanto alla DLL nella cartella dei test.
        var xmlPath = Path.Combine(AppContext.BaseDirectory, "Filemaster.Application.xml");
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

        foreach (var type in ApplicationAssembly.GetExportedTypes())
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

            // I metodi si cercano per nome senza la lista dei parametri (l'identificatore XML dei tipi generici e dei
            // riferimenti e' macchinoso da ricostruire). Il codice generato dai record (Equals, Deconstruct, ...) non ha
            // documentazione e non ne ha bisogno.
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!method.IsSpecialName && !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    Require("M:" + typeName + "." + method.Name, prefix: true);
                }
            }

            // Per i record il costruttore e' quello posizionale, che il compilatore documenta con le proprieta'. Il costruttore
            // implicito senza parametri (i tipi di input a soli set) non ha documentazione e CS1591 non la chiede.
            if (!IsRecordClass(type) && type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Any(c => c.GetParameters().Length > 0))
            {
                Require("M:" + typeName + ".#ctor", prefix: true);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void UploadResult_returns_what_it_was_given()
    {
        var document = SampleDocument();

        var result = new UploadResult(document, Deduplicated: true);

        Assert.Same(document, result.Document);
        Assert.True(result.Deduplicated);
        Assert.False(new UploadResult(document, Deduplicated: false).Deduplicated);
    }

    [Fact]
    public void HealthProbeResult_returns_what_it_was_given()
    {
        var result = new HealthProbeResult(IsHealthy: false, Status: "unavailable", Detail: "storage not configured");

        Assert.False(result.IsHealthy);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal("storage not configured", result.Detail);
        Assert.True(new HealthProbeResult(true, "ok", null).IsHealthy);
        Assert.Null(new HealthProbeResult(true, "ok", null).Detail);
    }

    [Fact]
    public void ContentRange_returns_what_it_was_given_in_the_right_order()
    {
        var range = new ContentRange(FirstByte: 10, LastByte: 19, TotalLength: 1000);

        Assert.Equal(10, range.FirstByte);
        Assert.Equal(19, range.LastByte);
        Assert.Equal(1000, range.TotalLength);
    }

    [Fact]
    public void Page_returns_what_it_was_given_and_a_null_list_becomes_an_empty_one()
    {
        var items = new[] { "a", "b" };

        var page = new Page<string>(items, "next-cursor");

        Assert.Same(items, page.Items);
        Assert.Equal("next-cursor", page.NextCursor);

        var empty = new Page<string>(null!, null);
        Assert.NotNull(empty.Items);
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
    }

    private static bool ReturnsTaskOrAsyncSequence(Type returnType) =>
        returnType == typeof(Task)
        || (returnType.IsGenericType
            && (returnType.GetGenericTypeDefinition() == typeof(Task<>)
                || returnType.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IAsyncEnumerable`1"));

    private static IEnumerable<Type> ExportedInterfaces() => ApplicationAssembly.GetExportedTypes().Where(t => t.IsInterface);

    private static bool IsRecordClass(Type type) =>
        type.IsClass && type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static Document SampleDocument() =>
        new(
            new DocumentId("1"),
            FolderId: null,
            OriginalFilename: "fattura.pdf",
            MimeType: "application/pdf",
            Sha256: null,
            SizeBytes: 0,
            Owner: null,
            Tag: null,
            Sender: null,
            Recipient: null,
            Metadata: default,
            CreatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Contacts: Array.Empty<DocumentContact>());
}

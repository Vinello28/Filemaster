using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Filemaster.Domain;

namespace Filemaster.UnitTests.Domain;

/// <summary>
/// Le regole del piano sulla forma pubblica del Domain, rese eseguibili: nessun setter (nemmeno <c>init</c>) sui tipi
/// pubblici, ogni enum con <c>Unknown = 0</c>, ogni tipo e ogni proprieta' pubblici documentati, e ogni record
/// posizionale restituisce da ogni proprieta' l'argomento passato al costruttore (le proprieta' sono ridichiarate a
/// mano per toglierne l'<c>init</c>: un copia e incolla sbagliato su 24 campi non lo vede il compilatore).
/// </summary>
public sealed class PublicShapeTests
{
    // Per nome completo, mai con typeof: sull'asset netstandard2.0 il modreq e' una copia interna di PolySharp, e un
    // confronto con il tipo del framework passerebbe a vuoto.
    private const string IsExternalInitFullName = "System.Runtime.CompilerServices.IsExternalInit";

    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly DomainAssembly = typeof(DocumentId).Assembly;

    // I record posizionali del Domain (le entita', Arxivar e gli eventi); la base astratta WebhookEvent non si istanzia.
    private static readonly string[] ExpectedRecords =
    {
        nameof(ArxivarMetadata),
        nameof(BulkVerifyResult),
        nameof(Contact),
        nameof(ContactCategory),
        nameof(Document),
        nameof(DocumentContact),
        nameof(DocumentDeletedEvent),
        nameof(DocumentIntegrityFailedEvent),
        nameof(DocumentUploadedEvent),
        nameof(Folder),
        nameof(IntegrityCheck),
        nameof(Tenant),
        nameof(UnknownWebhookEvent),
    };

    [Fact]
    public void Public_Domain_types_have_no_setter_and_in_particular_no_init_accessor()
    {
        // I record posizionali generano "{ get; init; }" per ogni parametro: qui le proprieta' sono ridichiarate get-only,
        // cosi' "x with { P = ... }" non aggira il costruttore e il tipo si legge uguale da C# 7.3.
        var offenders = new List<string>();
        foreach (var type in DomainAssembly.GetExportedTypes())
        {
            foreach (var property in type.GetProperties(AllDeclared))
            {
                var setter = property.SetMethod;
                if (setter is null)
                {
                    continue;
                }

                var isInit = setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == IsExternalInitFullName);
                if (isInit || setter.IsPublic)
                {
                    offenders.Add($"{type.Name}.{property.Name} ({(isInit ? "init" : "set")})");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_public_enum_has_Unknown_as_its_only_zero_member_and_it_is_the_default()
    {
        var enums = DomainAssembly.GetExportedTypes().Where(t => t.IsEnum).ToArray();

        Assert.NotEmpty(enums);
        foreach (var type in enums)
        {
            Assert.Contains("Unknown", Enum.GetNames(type));

            var unknown = Enum.Parse(type, "Unknown");
            Assert.Equal(0L, Convert.ToInt64(unknown, CultureInfo.InvariantCulture));
            Assert.Equal(unknown, Activator.CreateInstance(type)); // un campo assente o mai assegnato e' Unknown
            Assert.Equal(
                1,
                Enum.GetValues(type).Cast<object>().Count(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) == 0)); // nessun alias a zero
        }
    }

    [Fact]
    public void Phase_A_enums_have_Unknown_equal_to_zero()
    {
        Assert.Equal(0, (int)TenantStatus.Unknown);
        Assert.Equal(0, (int)ContactKind.Unknown);
        Assert.Equal(0, (int)ContactRole.Unknown);
    }

    [Fact]
    public void Every_public_type_property_and_field_has_a_summary_in_the_XML_documentation()
    {
        // CS1591 e' gia' un errore di build; questo guardiano rileva chi lo spegne (NoWarn, #pragma) e controlla anche
        // le proprieta' ridichiarate. Il file .xml viaggia accanto alla DLL nella cartella dei test.
        var xmlPath = Path.Combine(AppContext.BaseDirectory, "Filemaster.Domain.xml");
        Assert.True(File.Exists(xmlPath), $"manca {xmlPath}: GenerateDocumentationFile e' spento o il file non viene copiato");

        var documented = XDocument.Load(xmlPath)
            .Descendants("member")
            .Where(member => !string.IsNullOrWhiteSpace((string?)member.Element("summary")) || member.Element("inheritdoc") is not null)
            .Select(member => (string?)member.Attribute("name"))
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();
        void Require(string id)
        {
            if (!documented.Contains(id))
            {
                missing.Add(id);
            }
        }

        foreach (var type in DomainAssembly.GetExportedTypes())
        {
            var typeName = type.FullName!.Replace('+', '.');
            Require("T:" + typeName);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Require("P:" + typeName + "." + property.Name);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (!field.IsSpecialName) // esclude value__ degli enum
                {
                    Require("F:" + typeName + "." + field.Name);
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Positional_records_expose_every_constructor_argument_through_the_property_of_the_same_name()
    {
        var records = DomainAssembly.GetExportedTypes().Where(IsRecordClass).Where(t => !t.IsAbstract).ToArray();
        Assert.Equal(ExpectedRecords, records.Select(t => t.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

        foreach (var type in records)
        {
            var constructor = Assert.Single(type.GetConstructors());
            var parameters = constructor.GetParameters();
            var arguments = parameters.Select((p, i) => Sample(p.ParameterType, p.Name!, i + 1)).ToArray();

            var instance = constructor.Invoke(arguments);

            for (var i = 0; i < parameters.Length; i++)
            {
                var property = type.GetProperty(parameters[i].Name!)
                    ?? throw new InvalidOperationException($"{type.Name}: nessuna proprieta' {parameters[i].Name}");
                AssertSameValue(arguments[i], property.GetValue(instance), $"{type.Name}.{property.Name}");
            }
        }
    }

    private static bool IsRecordClass(Type type) =>
        type.IsClass && type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    // Un valore diverso per ogni parametro (per nome e posizione): due campi scambiati danno due valori diversi.
    // Un tipo di parametro nuovo senza campione lancia: l'helper va aggiornato, il test non passa a vuoto.
    private static object Sample(Type parameterType, string name, int index)
    {
        var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        var number = index.ToString("D25", CultureInfo.InvariantCulture); // 25 cifre: con un prefisso di 0 e' un ULID canonico

        if (type == typeof(string))
        {
            return "value-" + name;
        }

        if (type == typeof(int))
        {
            return 1000 + index;
        }

        if (type == typeof(long))
        {
            return 5_000_000_000L + index;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(DateTime))
        {
            return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddDays(index);
        }

        if (type == typeof(DateTimeOffset))
        {
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(index);
        }

        if (type == typeof(JsonElement))
        {
            // Mai smaltito: i JsonElement vivono per la durata del test.
            return JsonDocument.Parse("{\"index\":" + index.ToString(CultureInfo.InvariantCulture) + "}").RootElement;
        }

        if (type == typeof(DocumentId))
        {
            return new DocumentId("doc_0" + number);
        }

        if (type == typeof(ContactId))
        {
            return new ContactId("con_0" + number);
        }

        if (type == typeof(TenantId))
        {
            return new TenantId("ten_0" + number);
        }

        if (type == typeof(FolderCode))
        {
            return new FolderCode("CODE-" + index.ToString(CultureInfo.InvariantCulture));
        }

        if (type.IsEnum)
        {
            var values = Enum.GetValues(type).Cast<object>().Where(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0).ToArray();
            return values[index % values.Length];
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var array = Array.CreateInstance(element, 1);
            array.SetValue(Sample(element, name, index), 0);
            return array;
        }

        if (IsRecordClass(type))
        {
            var constructor = Assert.Single(type.GetConstructors());
            return constructor.Invoke(constructor.GetParameters().Select((p, i) => Sample(p.ParameterType, p.Name!, index * 100 + i)).ToArray());
        }

        throw new NotSupportedException($"nessun campione per {type} (parametro {name}): aggiornare Sample");
    }

    private static void AssertSameValue(object expected, object? actual, string where)
    {
        if (expected is Array expectedList)
        {
            var actualList = Assert.IsAssignableFrom<IEnumerable>(actual);
            Assert.True(
                expectedList.Cast<object>().SequenceEqual(actualList.Cast<object>()),
                $"{where}: la lista restituita non corrisponde a quella passata");
            return;
        }

        Assert.True(Equals(expected, actual), $"{where}: atteso {expected}, trovato {actual}");
    }
}

using System.Xml;
using System.Xml.Schema;

namespace Xsd2CSharp.Core.Xsd;

public sealed class SchemaLoadException(string message, IReadOnlyList<string> validationErrors)
    : Exception(message)
{
    public IReadOnlyList<string> ValidationErrors { get; } = validationErrors;
}

/// <summary>The result of loading a schema: the compiled set plus which target namespace(s) came from the explicitly-passed root file(s) (as opposed to ones pulled in transitively via xsd:import).</summary>
public sealed record LoadedSchema(XmlSchemaSet Set, IReadOnlyCollection<string> RootNamespaces);

/// <summary>
/// Loads and compiles one or more .xsd files (following xsd:import/xsd:include) using the BCL's
/// System.Xml.Schema.XmlSchemaSet, which does the heavy lifting of resolving group refs,
/// attribute groups, and type derivation chains for us.
/// </summary>
public static class SchemaLoader
{
    public static LoadedSchema LoadFromFiles(IEnumerable<string> paths)
    {
        XmlSchemaSet set = new() { XmlResolver = new XmlUrlResolver() };
        List<string> errors = [];
        HashSet<string> rootNamespaces = new(StringComparer.Ordinal);

        set.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
                errors.Add($"{e.Message} ({e.Exception?.LineNumber}:{e.Exception?.LinePosition})");
        };

        foreach (string path in paths)
        {
            using XmlReader reader = XmlReader.Create(path);
            XmlSchema? schema = set.Add(null, reader);
            if (schema is not null)
                rootNamespaces.Add(schema.TargetNamespace ?? "");
        }

        Compile(set, errors);
        return new LoadedSchema(set, rootNamespaces);
    }

    public static LoadedSchema LoadFromReader(XmlReader reader, string? baseUri = null)
    {
        XmlSchemaSet set = new() { XmlResolver = new XmlUrlResolver() };
        List<string> errors = [];

        set.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
                errors.Add(e.Message);
        };

        XmlSchema? schema = set.Add(null, reader);
        string[] rootNamespaces = schema is null
            ? Array.Empty<string>()
            : [schema.TargetNamespace ?? ""];

        Compile(set, errors);
        return new LoadedSchema(set, rootNamespaces);
    }

    private static void Compile(XmlSchemaSet set, List<string> errors)
    {
        try
        {
            set.Compile();
        }
        catch (XmlSchemaException ex)
        {
            errors.Add(ex.Message);
        }

        if (errors.Count > 0)
            throw new SchemaLoadException($"Schema failed to compile with {errors.Count} error(s).", errors);
    }
}

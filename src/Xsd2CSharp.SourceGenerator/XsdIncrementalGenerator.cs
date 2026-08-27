using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xsd2CSharp.Core.Generation;
using Xsd2CSharp.Core.Model;
using Xsd2CSharp.Core.Naming;
using Xsd2CSharp.Core.Xsd;

namespace Xsd2CSharp.SourceGenerator;

/// <summary>
/// Generates C# (enums, C# 15 unions, and IXmlSerializable classes) for every .xsd file added to
/// the consuming project as an &lt;AdditionalFiles&gt; item. The consuming project must set
/// &lt;LangVersion&gt;preview&lt;/LangVersion&gt; (and target a .NET 11+ SDK) since the generated
/// code uses C# 15 union types for xsd:choice content.
///
/// The base namespace for a schema's generated types is, in order: the `Xsd2CSharpNamespace`
/// metadata on its &lt;AdditionalFiles&gt; item, otherwise "{RootNamespace}.{PascalCase(file name)}".
/// If the schema spans more than one XML namespace (e.g. types pulled in via xsd:import, like GML
/// inside NeTEx), each non-root namespace gets its own C# sub-namespace underneath that base -
/// `Xsd2CSharpNamespaceMap` metadata (format: "uri1=Segment1;uri2=Segment2") can override the
/// auto-derived segment name for specific XSD namespaces, same as the CLI's --namespace-map.
/// </summary>
[Generator]
public sealed class XsdIncrementalGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
            ctx.AddSource(RuntimeSource.FileName, SourceText.From(WithNullableDirective(RuntimeSource.Text), System.Text.Encoding.UTF8)));

        IncrementalValuesProvider<AdditionalText> xsdFiles = context.AdditionalTextsProvider
            .Where(static f => f.Path.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase));

        IncrementalValuesProvider<(AdditionalText Left, Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider Right)> withOptions = xsdFiles.Combine(context.AnalyzerConfigOptionsProvider);

        context.RegisterSourceOutput(withOptions, static (spc, pair) =>
        {
            (AdditionalText file, Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider optionsProvider) = pair;
            SourceText? text = file.GetText(spc.CancellationToken);
            if (text is null)
                return;

            Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptions? fileOptions = optionsProvider.GetOptions(file);
            fileOptions.TryGetValue("build_metadata.AdditionalFiles.Xsd2CSharpNamespace", out string? explicitNamespace);
            fileOptions.TryGetValue("build_metadata.AdditionalFiles.Xsd2CSharpNamespaceMap", out string? namespaceMapRaw);
            optionsProvider.GlobalOptions.TryGetValue("build_property.RootNamespace", out string? rootNamespace);

            string? baseName = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            string? clrNamespace = !string.IsNullOrEmpty(explicitNamespace)
                ? explicitNamespace!
                : $"{(string.IsNullOrEmpty(rootNamespace) ? "Xsd2CSharpGenerated" : rootNamespace)}.{CSharpIdentifiers.ToPascalIdentifier(baseName)}";
            Dictionary<string, string> namespaceMap = ParseNamespaceMap(namespaceMapRaw);

            try
            {
                using System.Xml.XmlReader? reader = System.Xml.XmlReader.Create(new System.IO.StringReader(text.ToString()), new System.Xml.XmlReaderSettings(), file.Path);
                LoadedSchema loaded = SchemaLoader.LoadFromReader(reader, file.Path);
                SchemaModel model = SchemaModelBuilder.Build(loaded.Set, clrNamespace, loaded.RootNamespaces, namespaceMap);
                IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);

                // Hint names must be unique across the whole compilation, not just this one schema, so
                // prefix each with the schema's own base name (in case two .xsd files happen to produce
                // a same-named type file), then nest under the type's full namespace (root included, same
                // as the CLI's folder layout) so the IDE's generated-files tree mirrors it too.
                string? prefix = CSharpIdentifiers.ToPascalIdentifier(baseName);
                foreach (GeneratedFile? generatedFile in generatedFiles)
                {
                    string? nsPath = generatedFile.ClrNamespace.Replace('.', '/');
                    spc.AddSource($"{prefix}/{nsPath}/{generatedFile.BaseName}.g.cs", SourceText.From(WithNullableDirective(generatedFile.Content), System.Text.Encoding.UTF8));
                }
            }
            catch (SchemaLoadException ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(SchemaError, Location.None, file.Path, ex.Message));
            }
            catch (NotSupportedException ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(SchemaError, Location.None, file.Path, ex.Message));
            }
        });
    }

    /// <summary>
    /// Source-generator-added files don't inherit the consuming project's own &lt;Nullable&gt;
    /// setting the way ordinary source files do - Roslyn requires an explicit #nullable directive
    /// in the file itself, or every nullable annotation triggers CS8669, regardless of whether the
    /// project has nullable reference types enabled. The CLI-written plain .cs files have no such
    /// requirement (they compile as regular project source), so this directive is only added here,
    /// not baked into RuntimeSource.Text/GeneratedFile.Content themselves.
    /// </summary>
    private static string WithNullableDirective(string content) => "#nullable enable\n" + content;

    private static Dictionary<string, string> ParseNamespaceMap(string? raw)
    {
        Dictionary<string, string> result = [];
        if (string.IsNullOrEmpty(raw))
            return result;

        foreach (string entry in raw!.Split(';'))
        {
            int eq = entry.IndexOf('=');
            if (eq > 0)
                result[entry.Substring(0, eq)] = entry.Substring(eq + 1);
        }
        return result;
    }

    private static readonly DiagnosticDescriptor SchemaError = new(
        id: "XSD2CS001",
        title: "XSD-to-C# generation failed",
        messageFormat: "Failed to generate C# for '{0}': {1}",
        category: "Xsd2CSharp",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

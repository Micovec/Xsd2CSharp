using Xsd2CSharp.Core.Generation;
using Xsd2CSharp.Core.Model;
using Xsd2CSharp.Core.Xsd;

List<string> schemaFiles = [];
string? outputDir = null;
string? ns = null;
Dictionary<string, string> namespaceMap = [];

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" or "--output":
            outputDir = args[++i];
            break;
        case "-n" or "--namespace":
            ns = args[++i];
            break;
        case "--namespace-map":
            string? mapping = args[++i];
            int eq = mapping.IndexOf('=');
            if (eq <= 0)
            {
                Console.Error.WriteLine($"--namespace-map expects '<xsd-namespace-uri>=<csharp-namespace-segment>', got '{mapping}'.");
                return 1;
            }
            namespaceMap[mapping.Substring(0, eq)] = mapping.Substring(eq + 1);
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            schemaFiles.Add(args[i]);
            break;
    }
}

if (schemaFiles.Count == 0)
{
    PrintUsage();
    return 1;
}

outputDir ??= ".";
ns ??= Path.GetFileNameWithoutExtension(schemaFiles[0]);
Directory.CreateDirectory(outputDir);

try
{
    LoadedSchema loaded = SchemaLoader.LoadFromFiles(schemaFiles);
    SchemaModel model = SchemaModelBuilder.Build(loaded.Set, ns, loaded.RootNamespaces, namespaceMap);
    IReadOnlyList<GeneratedFile> files = SchemaCodeGenerator.Generate(model);

    // Plain .cs, not .g.cs: these are meant to be committed as regular source, not treated as
    // build-time-regenerated output the way a source generator's files are. The folder layout
    // mirrors the full C# namespace, root included - e.g. with -n NeTEx.Model, a root-namespace
    // type goes into "NeTEx/Model/Foo.cs" and a GML one into "NeTEx/Model/Gml/Bar.cs".
    foreach (GeneratedFile file in files)
    {
        string folder = Path.Combine(outputDir, file.ClrNamespace.Replace('.', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file.BaseName + ".cs"), file.Content);
    }
    string runtimeFolder = Path.Combine(outputDir, RuntimeSource.ClrNamespace.Replace('.', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(runtimeFolder);
    string runtimeFilePath = Path.Combine(runtimeFolder, RuntimeSource.BaseName + ".cs");
    File.WriteAllText(runtimeFilePath, RuntimeSource.Text);

    int namespaceCount = model.AllClrNamespaces.Count();
    Console.WriteLine($"Generated {model.Classes.Count} class(es), {model.Enums.Count} enum(s), {model.Unions.Count} union(s) " +
                       $"across {files.Count} file(s) in {namespaceCount} namespace(s) -> {outputDir}");
    Console.WriteLine($"Wrote runtime support -> {runtimeFilePath}");
    Console.WriteLine();
    Console.WriteLine("Note: the generated code uses C# 15 union types, which require <LangVersion>preview</LangVersion> (and a .NET 11+ SDK) in the consuming project.");
    return 0;
}
catch (SchemaLoadException ex)
{
    Console.Error.WriteLine(ex.Message);
    foreach (string e in ex.ValidationErrors)
        Console.Error.WriteLine("  " + e);
    return 1;
}
catch (NotSupportedException ex)
{
    Console.Error.WriteLine("Unsupported schema construct: " + ex.Message);
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        xsd2cs - generate C# classes (with C# 15 unions for xsd:choice) from an XSD schema.

        Usage:
          xsd2cs <schema.xsd> [<more.xsd> ...] [-o <output-dir>] [-n <namespace>] [--namespace-map <xsd-ns>=<segment>]...

        Options:
          -o, --output        Output directory (default: current directory)
          -n, --namespace     Base C# namespace for generated types (default: derived from the first schema's file name)
          --namespace-map     Map one XSD targetNamespace URI to a C# sub-namespace segment under -n, e.g.
                               --namespace-map http://www.opengis.net/gml/3.2=Gml
                               Repeatable. Namespaces not covered by this get an auto-derived segment name;
                               pass an empty segment (e.g. "http://...=") to fold that namespace into the base namespace instead.
                               Has no effect on a namespace that's already the root (see below) - that one
                               always maps to -n's value.
          -h, --help           Show this help

        Notes:
          - xsd:import/xsd:include are followed automatically; only pass the root schema file(s).
          - Every type is emitted into a C# namespace matching its own XSD targetNamespace: the root schema
            file(s) you pass on the command line map straight to -n; anything pulled in from elsewhere (e.g.
            GML or SIRI types imported into NeTEx) gets its own sub-namespace underneath -n.
          - The output folder layout always mirrors the full C# namespace, root included - e.g. with
            -n NeTEx.Model, a root-namespace type goes into "NeTEx/Model/Foo.cs" and a GML one into
            "NeTEx/Model/Gml/Bar.cs".
          - Generated code requires <LangVersion>preview</LangVersion> and a .NET 11+ SDK in the consuming project,
            because it uses C# 15 union types for xsd:choice content.
        """);
}

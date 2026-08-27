# Xsd2CSharp

Xsd2CSharp turns an XSD schema into hand-rolled C# classes for reading and writing XML - no
runtime reflection, no `System.Xml.Serialization.XmlSerializer`.

- `xsd:choice` and substitution groups become C# 15 `union` types instead of a pile of nullable
  properties, so exactly one branch can ever be set and the compiler enforces it.
- Every generated type implements `IXmlSerializable` itself, with the read/write logic emitted
  directly into the class - there's no attribute-driven reflection step at load time.
- `xsd:complexContent` extension/restriction becomes real C# inheritance.
- `xsd:import`/`xsd:include` are followed automatically, and each distinct XSD namespace gets its
  own C# sub-namespace.

You can use it two ways:

- **CLI** (`xsd2cs`) - generate `.cs` files once and commit them like any other source.
- **Roslyn incremental source generator** - point it at your `.xsd` files via `<AdditionalFiles>`
  and it regenerates the model on every build.

Generated code requires `<LangVersion>preview</LangVersion>` and a **.NET 11+ SDK** in the
consuming project, because it uses C# 15 union types.

## Project layout

| Project | Target | What it is |
|---|---|---|
| `src/Xsd2CSharp.Core` | netstandard2.0 | Schema loading (`SchemaLoader`), the schema -> model builder (`SchemaModelBuilder`), and the C# code generator (`SchemaCodeGenerator`). Shared by both front ends. |
| `src/Xsd2CSharp.Cli` | net10.0 | The `xsd2cs` command-line tool. |
| `src/Xsd2CSharp.SourceGenerator` | netstandard2.0 | The Roslyn incremental source generator (`IIncrementalGenerator`). |
| `tests/Xsd2CSharp.Tests` | net11.0 | xUnit tests that build a schema in-memory, generate C#, and compile/run the result with Roslyn to verify round-tripping. |

## Building

Requires the **.NET 11 SDK (preview)** - the test project targets `net11.0` and generated code
needs `LangVersion=preview`.

```bash
dotnet build Xsd2CSharp.sln
```

## Running the tests

```bash
dotnet test tests/Xsd2CSharp.Tests/Xsd2CSharp.Tests.csproj
```

## Using the CLI

Run it directly from source:

```bash
dotnet run --project src/Xsd2CSharp.Cli -- <schema.xsd> [<more.xsd> ...] -o <output-dir> -n <namespace>
```

Or install it as a local/global .NET tool:

```bash
dotnet pack src/Xsd2CSharp.Cli -c Release
dotnet tool install --global --add-source src/Xsd2CSharp.Cli/bin/Release xsd2cs
xsd2cs <schema.xsd> -o <output-dir> -n <namespace>
```

### Options

```
xsd2cs <schema.xsd> [<more.xsd> ...] [-o <output-dir>] [-n <namespace>] [--namespace-map <xsd-ns>=<segment>]...

  -o, --output        Output directory (default: current directory)
  -n, --namespace     Base C# namespace for generated types (default: derived from the first schema's file name)
  --namespace-map     Map one XSD targetNamespace URI to a C# sub-namespace segment under -n, e.g.
                       --namespace-map http://www.opengis.net/gml/3.2=Gml
                       Repeatable. Namespaces not covered by this get an auto-derived segment name;
                       pass an empty segment (e.g. "http://...=") to fold that namespace into the base namespace instead.
                       Has no effect on a namespace that's already the root - that one always maps to -n's value.
  -h, --help           Show this help
```

Only pass the root schema file(s) - `xsd:import`/`xsd:include` are resolved automatically. The
output folder layout mirrors the full C# namespace, e.g. with `-n NeTEx.Model` a root-namespace
type goes into `NeTEx/Model/Foo.cs` and a GML-imported one into `NeTEx/Model/Gml/Bar.cs`. A small
runtime support file (`Xsd2CSharpRuntime.cs`) is written alongside the model - it's required at
compile time by every generated class.

## Using the source generator

Reference the generator project as an analyzer and add your schema(s) as `AdditionalFiles`:

```xml
<PropertyGroup>
  <LangVersion>preview</LangVersion>
</PropertyGroup>

<ItemGroup>
  <AdditionalFiles Include="Schemas\**\*.xsd" Xsd2CSharpNamespace="MyApp.Model" />
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="Xsd2CSharpNamespace" />
  <CompilerVisibleItemMetadata Include="AdditionalFiles" MetadataName="Xsd2CSharpNamespaceMap" />
</ItemGroup>

<ItemGroup>
  <ProjectReference Include="path\to\Xsd2CSharp.SourceGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

The base namespace for a schema's generated types comes from, in order: the `Xsd2CSharpNamespace`
metadata on its `<AdditionalFiles>` item, otherwise `{RootNamespace}.{PascalCase(file name)}`.
`Xsd2CSharpNamespaceMap` (format: `uri1=Segment1;uri2=Segment2`) overrides the auto-derived
sub-namespace segment for specific XSD namespaces, same as the CLI's `--namespace-map`. The
generator adds the same runtime support file as a generated source automatically.

## License

AGPL-3.0 - see [LICENSE.txt](LICENSE.txt).

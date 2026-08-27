using System.Linq;
using System.Xml.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xsd2CSharp.Core.Generation;
using Xsd2CSharp.Core.Model;
using Xsd2CSharp.Core.Xsd;

namespace Xsd2CSharp.Tests;

public class GenerationTests
{
    private const string PetStoreXsd = """
        <?xml version="1.0" encoding="utf-8"?>
        <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                    targetNamespace="urn:example:petstore"
                    xmlns:tns="urn:example:petstore"
                    elementFormDefault="qualified">

          <xsd:simpleType name="SizeType">
            <xsd:restriction base="xsd:NMTOKEN">
              <xsd:enumeration value="small"/>
              <xsd:enumeration value="medium"/>
              <xsd:enumeration value="x-large"/>
              <xsd:enumeration value="2nd-chance"/>
            </xsd:restriction>
          </xsd:simpleType>

          <xsd:complexType name="DogType">
            <xsd:sequence>
              <xsd:element name="Breed" type="xsd:string"/>
            </xsd:sequence>
            <xsd:attribute name="size" type="tns:SizeType" use="required"/>
          </xsd:complexType>

          <xsd:complexType name="CatType">
            <xsd:sequence>
              <xsd:element name="Indoor" type="xsd:boolean"/>
            </xsd:sequence>
          </xsd:complexType>

          <xsd:complexType name="TagType">
            <xsd:simpleContent>
              <xsd:extension base="xsd:string">
                <xsd:attribute name="lang" type="xsd:string"/>
              </xsd:extension>
            </xsd:simpleContent>
          </xsd:complexType>

          <xsd:complexType name="PetType">
            <xsd:sequence>
              <xsd:element name="Name" type="xsd:string"/>
              <xsd:choice>
                <xsd:element name="Dog" type="tns:DogType"/>
                <xsd:element name="Cat" type="tns:CatType"/>
              </xsd:choice>
              <xsd:element name="Tag" type="tns:TagType" minOccurs="0" maxOccurs="unbounded"/>
            </xsd:sequence>
            <xsd:attribute name="id" type="xsd:int" use="required"/>
          </xsd:complexType>

          <xsd:element name="PetStore">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="Pet" type="tns:PetType" maxOccurs="unbounded"/>
              </xsd:sequence>
            </xsd:complexType>
          </xsd:element>

        </xsd:schema>
        """;

    private static SchemaModel BuildModel(string xsd, string ns)
    {
        using System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xsd));
        LoadedSchema loaded = SchemaLoader.LoadFromReader(reader);
        return SchemaModelBuilder.Build(loaded.Set, ns, loaded.RootNamespaces);
    }

    [Fact]
    public void NmtokenEnumeration_GetsSanitizedMembersAndPreservesOriginalXmlValues()
    {
        SchemaModel model = BuildModel(PetStoreXsd, "T");

        EnumTypeModel sizeEnum = Assert.Single(model.Enums);
        Assert.Equal(4, sizeEnum.Members.Count);

        // "x-large" and "2nd-chance" aren't valid C# identifiers as-is.
        Assert.Contains(sizeEnum.Members, m => m.ClrName == "XLarge" && m.XmlValue == "x-large");
        Assert.Contains(sizeEnum.Members, m => m.ClrName == "_2ndChance" && m.XmlValue == "2nd-chance");
        Assert.Contains(sizeEnum.Members, m => m.ClrName == "Small" && m.XmlValue == "small");
    }

    [Fact]
    public void Choice_BecomesUnionWithOneCasePerBranch()
    {
        SchemaModel model = BuildModel(PetStoreXsd, "T");

        UnionTypeModel union = Assert.Single(model.Unions);
        Assert.True(union.IsElementChoice);
        Assert.Equal(["Dog", "Cat"], union.Cases.Select(c => c.ElementXmlName));
        Assert.All(union.Cases, c => Assert.Equal(IoKind.Serializable, c.ValueIoKind));

        // Dog and Cat branches are already distinct generated classes (DogType/CatType), so no
        // wrapper record is needed - the case type is the value type itself.
        Assert.All(union.Cases, c => Assert.False(c.IsWrapped));
        Assert.Equal(["DogType", "CatType"], union.Cases.Select(c => c.CaseClrName));
    }

    [Fact]
    public void ChoiceWithCollidingBranchTypes_KeepsWrapperRecordsAndRoundTripsThroughXml()
    {
        const string xsd = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                        targetNamespace="urn:example:journey"
                        xmlns:tns="urn:example:journey"
                        elementFormDefault="qualified">

              <xsd:complexType name="PointRefStructure">
                <xsd:attribute name="ref" type="xsd:string" use="required"/>
              </xsd:complexType>

              <xsd:complexType name="JourneyType">
                <xsd:choice>
                  <xsd:element name="FromPointRef" type="tns:PointRefStructure"/>
                  <xsd:element name="ToPointRef" type="tns:PointRefStructure"/>
                  <xsd:element name="ViaPointRef" type="tns:PointRefStructure"/>
                </xsd:choice>
              </xsd:complexType>

              <xsd:element name="Journey" type="tns:JourneyType"/>

            </xsd:schema>
            """;

        SchemaModel model = BuildModel(xsd, "JourneyModel");

        UnionTypeModel union = Assert.Single(model.Unions);
        // All three branches resolve to the same class (PointRefStructure) - a bare-type case couldn't
        // tell "came from FromPointRef" apart from "came from ToPointRef"/"ViaPointRef", so all three
        // must stay wrapped. Three-plus wrapped cases in one Write switch is also what exercises the
        // "each case needs its own pattern-variable name" fix (see EmitElementChoiceUnionCompanion) -
        // a fixed "__case" name for every case fails to compile (CS0128) once there's more than one.
        Assert.All(union.Cases, c => Assert.True(c.IsWrapped));
        Assert.All(union.Cases, c => Assert.NotEqual(c.ValueClrType, c.CaseClrName));
        Assert.All(union.Cases, c => Assert.Equal("PointRefStructure", c.ValueClrType));
        Assert.Equal(3, union.Cases.Count);

        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        (CSharpCompilation _, System.Reflection.Assembly assembly) = CompileAndLoad(generatedFiles);

        Type journeyType = assembly.GetType("JourneyModel.JourneyType")!;
        System.Reflection.PropertyInfo choiceProperty = journeyType.GetProperties().Single(p => p.PropertyType.Name.Contains("Choice"));

        // Union-typed properties can't be set via reflection (implicit conversion operators aren't
        // invoked by PropertyInfo.SetValue), so build the starting object by actually reading real XML
        // through the generated ReadXml - the same path any real caller would use.
        const string xml = """<Journey xmlns="urn:example:journey"><FromPointRef ref="STOP_001"/></Journey>""";
        object journeyObj = Activator.CreateInstance(journeyType)!;
        using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml)))
        {
            reader.MoveToContent();
            journeyType.GetMethod("ReadXml")!.Invoke(journeyObj, [reader]);
        }

        // The union struct itself boxes the active case behind its own "Value" (object?) property -
        // unwrap that first to get at the actual FromPointRefCase/ToPointRefCase wrapper record.
        object unionValue = choiceProperty.GetValue(journeyObj)!;
        object caseValue = unionValue.GetType().GetProperty("Value")!.GetValue(unionValue)!;
        Assert.Equal("FromPointRefCase", caseValue.GetType().Name);
        object pointRef = caseValue.GetType().GetProperty("Value")!.GetValue(caseValue)!;
        Assert.Equal("STOP_001", pointRef.GetType().GetProperty("Ref")!.GetValue(pointRef));

        System.Text.StringBuilder sb = new();
        using (System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(sb))
        {
            writer.WriteStartElement("Journey", "urn:example:journey");
            journeyType.GetMethod("WriteXml")!.Invoke(journeyObj, [writer]);
            writer.WriteEndElement();
        }
        string outXml = sb.ToString();
        Assert.Contains("<FromPointRef", outXml);
        Assert.DoesNotContain("<ToPointRef", outXml);
    }

    [Fact]
    public void GeneratedSource_CompilesCleanWithPreviewLangVersionAndNoDiagnosticErrors()
    {
        SchemaModel model = BuildModel(PetStoreXsd, "PetStoreModel");
        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        AssertCompilesClean(generatedFiles);
    }

    [Fact]
    public void EmptyElementOrAttributeWithXsdDefault_UsesTheDefaultValueInsteadOfFailingToParse()
    {
        // Reproduces real NeTEx data: <HasMinimumPrice/> with default="false" declared on the element
        // is fully XSD-spec-valid (element present but empty -> use the declared default), not invalid
        // input - a naive parser that tries to parse "" as a bool throws FormatException instead.
        // Covers all three IoKind categories the default-fallback flows through (see
        // EmitReadElementCaseBody): Primitive (bool, string), Enum, and (implicitly, since it shares
        // the same ParseSingleExpr dispatch) Serializable/text-union.
        const string xsd = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:defaults" xmlns:tns="urn:example:defaults" elementFormDefault="qualified">
              <xsd:simpleType name="StatusType">
                <xsd:restriction base="xsd:NMTOKEN">
                  <xsd:enumeration value="pending"/>
                  <xsd:enumeration value="active"/>
                </xsd:restriction>
              </xsd:simpleType>
              <xsd:complexType name="ConditionType">
                <xsd:sequence>
                  <xsd:element name="HasMinimumPrice" type="xsd:boolean" default="false" minOccurs="0"/>
                  <xsd:element name="Status" type="tns:StatusType" default="pending" minOccurs="0"/>
                  <xsd:element name="Note" type="xsd:string" default="none" minOccurs="0"/>
                </xsd:sequence>
                <xsd:attribute name="isActive" type="xsd:boolean" default="true"/>
              </xsd:complexType>
              <xsd:element name="Condition" type="tns:ConditionType"/>
            </xsd:schema>
            """;

        SchemaModel model = BuildModel(xsd, "DefaultsModel");
        ClassTypeModel conditionClass = Assert.Single(model.Classes);
        MemberModel hasMinPrice = Assert.Single(conditionClass.Elements, e => e.XmlName == "HasMinimumPrice");
        Assert.Equal("false", hasMinPrice.XsdDefaultValue);
        MemberModel status = Assert.Single(conditionClass.Elements, e => e.XmlName == "Status");
        Assert.Equal("pending", status.XsdDefaultValue);
        MemberModel note = Assert.Single(conditionClass.Elements, e => e.XmlName == "Note");
        Assert.Equal("none", note.XsdDefaultValue);
        MemberModel isActive = Assert.Single(conditionClass.Attributes, a => a.XmlName == "isActive");
        Assert.Equal("true", isActive.XsdDefaultValue);

        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        (CSharpCompilation _, System.Reflection.Assembly assembly) = CompileAndLoad(generatedFiles);
        Type conditionType = assembly.GetType("DefaultsModel.ConditionType")!;

        const string xml = """<Condition xmlns="urn:example:defaults"><HasMinimumPrice/><Status/><Note/></Condition>""";
        object obj = Activator.CreateInstance(conditionType)!;
        using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml)))
        {
            reader.MoveToContent();
            conditionType.GetMethod("ReadXml")!.Invoke(obj, [reader]);
        }

        Assert.Equal(false, conditionType.GetProperty("HasMinimumPrice")!.GetValue(obj));
        object statusValue = conditionType.GetProperty("Status")!.GetValue(obj)!;
        Assert.Equal("Pending", statusValue.ToString());
        Assert.Equal("none", conditionType.GetProperty("Note")!.GetValue(obj));
        // isActive attribute was entirely absent (not even empty) - real-world default-on-omission is
        // out of scope for this fix (see MemberModel.XsdDefaultValue doc), so it just keeps the
        // ordinary "not present" nullable state rather than being force-populated with the default.
        Assert.Null(conditionType.GetProperty("IsActive")!.GetValue(obj));
    }

    [Fact]
    public void NmtokenEnumeration_ParseTrimsWhitespacePerXsdCollapseFacet()
    {
        // xsd:NMTOKEN has XSD's "collapse" whitespace facet: leading/trailing whitespace is stripped
        // before validating against the enumeration, so " left " / "left " are legitimately the value
        // "left", not invalid input - real NeTEx data relies on this.
        const string xsd = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:trim" xmlns:tns="urn:example:trim" elementFormDefault="qualified">
              <xsd:simpleType name="HeadingType">
                <xsd:restriction base="xsd:NMTOKEN">
                  <xsd:enumeration value="left"/>
                  <xsd:enumeration value="right"/>
                </xsd:restriction>
              </xsd:simpleType>
              <xsd:element name="Heading" type="tns:HeadingType"/>
            </xsd:schema>
            """;

        SchemaModel model = BuildModel(xsd, "TrimModel");
        EnumTypeModel heading = Assert.Single(model.Enums);
        Assert.True(heading.HasCollapseWhitespaceFacet);

        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        (CSharpCompilation _, System.Reflection.Assembly assembly) = CompileAndLoad(generatedFiles);
        Type xmlType = assembly.GetType($"TrimModel.{heading.ClrName}Xml")!;
        object? parsed = xmlType.GetMethod("Parse")!.Invoke(null, ["left "]);
        Assert.Equal("Left", parsed!.ToString());
    }

    [Fact]
    public void ChoiceUnionCaseThatIsAnXsdListOfEnumeration_RoundTripsMultipleSpaceSeparatedValues()
    {
        // Reproduces NeTEx's DaysOfWeek: a choice branch whose type is xsd:list of an enumeration
        // (e.g. "Monday Tuesday") - the union case's real value is List<Enum>, not a bare Enum, and
        // must stay wrapped (a bare-value union case couldn't hold more than one item).
        const string xsd = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:days" xmlns:tns="urn:example:days" elementFormDefault="qualified">
              <xsd:simpleType name="DayEnum">
                <xsd:restriction base="xsd:NMTOKEN">
                  <xsd:enumeration value="Monday"/>
                  <xsd:enumeration value="Tuesday"/>
                </xsd:restriction>
              </xsd:simpleType>
              <xsd:simpleType name="DaysListType">
                <xsd:list itemType="tns:DayEnum"/>
              </xsd:simpleType>
              <xsd:complexType name="RefType">
                <xsd:attribute name="ref" type="xsd:string"/>
              </xsd:complexType>
              <xsd:complexType name="EventType">
                <xsd:choice>
                  <xsd:element name="SomeRef" type="tns:RefType"/>
                  <xsd:element name="Days" type="tns:DaysListType"/>
                </xsd:choice>
              </xsd:complexType>
              <xsd:element name="Event" type="tns:EventType"/>
            </xsd:schema>
            """;

        SchemaModel model = BuildModel(xsd, "DaysModel");
        UnionTypeModel union = Assert.Single(model.Unions);
        UnionCaseModel daysCase = Assert.Single(union.Cases, c => c.ElementXmlName == "Days");
        // RefType (class) and DayEnum don't collide, so without the token-list guard this case would
        // have been eligible for the unwrapped-case optimization - it must stay wrapped regardless.
        Assert.True(daysCase.IsTokenList);
        Assert.True(daysCase.IsWrapped);

        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        GeneratedFile unionFile = Assert.Single(generatedFiles, f => f.BaseName == union.ClrName);
        EnumTypeModel dayEnum = Assert.Single(model.Enums);
        Assert.Contains($"public sealed record {daysCase.CaseClrName}(List<{dayEnum.ClrName}> Value);", unionFile.Content);

        (CSharpCompilation _, System.Reflection.Assembly assembly) = CompileAndLoad(generatedFiles);
        Type eventType = assembly.GetType("DaysModel.EventType")!;

        const string xml = """<Event xmlns="urn:example:days"><Days>Monday Tuesday</Days></Event>""";
        object eventObj = Activator.CreateInstance(eventType)!;
        using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml)))
        {
            reader.MoveToContent();
            eventType.GetMethod("ReadXml")!.Invoke(eventObj, [reader]);
        }

        System.Reflection.PropertyInfo choiceProp = eventType.GetProperties().Single(p => p.PropertyType.Name.Contains("Choice"));
        object unionValue = choiceProp.GetValue(eventObj)!;
        object caseValue = unionValue.GetType().GetProperty("Value")!.GetValue(unionValue)!;
        Assert.Equal(daysCase.CaseClrName, caseValue.GetType().Name);
        System.Collections.IEnumerable days = (System.Collections.IEnumerable)caseValue.GetType().GetProperty("Value")!.GetValue(caseValue)!;
        List<string?> dayNames = days.Cast<object>().Select(d => d.ToString()).ToList();
        Assert.Equal(["Monday", "Tuesday"], dayNames);

        System.Text.StringBuilder sb = new();
        using (System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(sb))
        {
            writer.WriteStartElement("Event", "urn:example:days");
            eventType.GetMethod("WriteXml")!.Invoke(eventObj, [writer]);
            writer.WriteEndElement();
        }
        Assert.Contains("<Days>Monday Tuesday</Days>", sb.ToString());
    }

    [Fact]
    public void RootElementType_GetsXmlRootAttributeSoPlainXmlSerializerWorksWithNoManualOverride()
    {
        SchemaModel model = BuildModel(PetStoreXsd, "PetStoreModel");
        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        GeneratedFile petStoreFile = Assert.Single(generatedFiles, f => f.BaseName == "PetStore");
        Assert.Contains(
            """[System.Xml.Serialization.XmlRoot(ElementName = "PetStore", Namespace = "urn:example:petstore")]""",
            petStoreFile.Content);

        (CSharpCompilation _, System.Reflection.Assembly assembly) = CompileAndLoad(generatedFiles);
        Type petStoreType = assembly.GetType("PetStoreModel.PetStore")!;

        // No XmlRootAttribute override passed here - the [XmlRoot] on the generated class itself must
        // be enough for the default constructor to find the right root element/namespace.
        System.Xml.Serialization.XmlSerializer serializer = new(petStoreType);

        const string xml = """
            <PetStore xmlns="urn:example:petstore">
              <Pet id="1">
                <Name>Rex</Name>
                <Dog size="x-large"><Breed>Labrador</Breed></Dog>
              </Pet>
            </PetStore>
            """;
        using System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml));
        object result = serializer.Deserialize(reader)!;

        System.Collections.IEnumerable petList = (System.Collections.IEnumerable)petStoreType.GetProperty("Pet")!.GetValue(result)!;
        object pet = petList.Cast<object>().Single();
        Assert.Equal("Rex", pet.GetType().GetProperty("Name")!.GetValue(pet));
        System.Reflection.PropertyInfo choiceProp = pet.GetType().GetProperties().Single(p => p.PropertyType.Name.Contains("Choice"));
        object union = choiceProp.GetValue(pet)!;
        object caseValue = union.GetType().GetProperty("Value")!.GetValue(union)!;
        Assert.Equal("DogType", caseValue.GetType().Name);
        Assert.Equal("Labrador", caseValue.GetType().GetProperty("Breed")!.GetValue(caseValue));
    }

    [Fact]
    public void MultipleXsdNamespaces_MapToSeparateClrNamespacesWithWorkingCrossReferences()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xsd2cs-ns-test-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            // "geo.xsd" plays the role of an imported library schema (like GML inside NeTEx): its own
            // namespace, pulled in via xsd:import into the main schema.
            string geoPath = System.IO.Path.Combine(dir, "geo.xsd");
            System.IO.File.WriteAllText(geoPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:geo" elementFormDefault="qualified">
                  <xsd:complexType name="PointType">
                    <xsd:sequence>
                      <xsd:element name="Lat" type="xsd:decimal"/>
                      <xsd:element name="Lon" type="xsd:decimal"/>
                    </xsd:sequence>
                  </xsd:complexType>
                </xsd:schema>
                """);

            string mainPath = System.IO.Path.Combine(dir, "main.xsd");
            System.IO.File.WriteAllText(mainPath, """
                <?xml version="1.0" encoding="utf-8"?>
                <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                            xmlns:geo="urn:geo" targetNamespace="urn:main" elementFormDefault="qualified">
                  <xsd:import namespace="urn:geo" schemaLocation="geo.xsd"/>
                  <xsd:element name="Root">
                    <xsd:complexType>
                      <xsd:sequence>
                        <xsd:element name="Name" type="xsd:string"/>
                        <xsd:element name="Location" type="geo:PointType"/>
                      </xsd:sequence>
                    </xsd:complexType>
                  </xsd:element>
                </xsd:schema>
                """);

            LoadedSchema loaded = SchemaLoader.LoadFromFiles([mainPath]);
            SchemaModel model = SchemaModelBuilder.Build(loaded.Set, "MainModel", loaded.RootNamespaces,
                new Dictionary<string, string> { ["urn:geo"] = "Geo" });

            ClassTypeModel pointType = Assert.Single(model.Classes, c => c.ClrName == "PointType");
            Assert.Equal("MainModel.Geo", pointType.ClrNamespace);

            ClassTypeModel rootType = Assert.Single(model.Classes, c => c.Elements.Any(e => e.XmlName == "Location"));
            Assert.Equal("MainModel", rootType.ClrNamespace);
            // Root's Location property references PointType from a *different* namespace - the compile
            // check below proves the generated `using MainModel.Geo;` actually makes that resolve.
            Assert.Contains(rootType.Elements, e => e.XmlName == "Location" && e.ClrTypeName == "PointType");

            IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
            GeneratedFile pointFile = Assert.Single(generatedFiles, f => f.BaseName == "PointType");
            Assert.Equal("MainModel.Geo", pointFile.ClrNamespace);

            AssertCompilesClean(generatedFiles);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ComplexContentExtension_BecomesRealCSharpInheritanceAndRoundTripsThroughXml()
    {
        const string xsd = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                        targetNamespace="urn:example:zoo"
                        xmlns:tns="urn:example:zoo"
                        elementFormDefault="qualified">

              <xsd:complexType name="AnimalType">
                <xsd:sequence>
                  <xsd:element name="Name" type="xsd:string"/>
                </xsd:sequence>
                <xsd:attribute name="id" type="xsd:int" use="required"/>
              </xsd:complexType>

              <xsd:complexType name="DogType">
                <xsd:complexContent>
                  <xsd:extension base="tns:AnimalType">
                    <xsd:sequence>
                      <xsd:element name="Breed" type="xsd:string"/>
                    </xsd:sequence>
                    <xsd:attribute name="goodBoy" type="xsd:boolean" use="optional"/>
                  </xsd:extension>
                </xsd:complexContent>
              </xsd:complexType>

              <xsd:complexType name="PuppyType">
                <xsd:complexContent>
                  <xsd:extension base="tns:DogType">
                    <xsd:sequence>
                      <xsd:element name="Weeks" type="xsd:int"/>
                    </xsd:sequence>
                  </xsd:extension>
                </xsd:complexContent>
              </xsd:complexType>

              <xsd:element name="Puppy" type="tns:PuppyType"/>

            </xsd:schema>
            """;

        SchemaModel model = BuildModel(xsd, "ZooModel");

        ClassTypeModel animal = Assert.Single(model.Classes, c => c.ClrName == "AnimalType");
        ClassTypeModel dog = Assert.Single(model.Classes, c => c.ClrName == "DogType");
        ClassTypeModel puppy = Assert.Single(model.Classes, c => c.ClrName == "PuppyType");

        Assert.Null(animal.BaseClass);
        Assert.Same(animal, dog.BaseClass);
        Assert.Same(dog, puppy.BaseClass);

        // Each level only carries its own new members, not ones inherited from a base.
        Assert.Equal(["Name"], animal.Elements.Select(e => e.XmlName));
        Assert.Equal(["id"], animal.Attributes.Select(a => a.XmlName));
        Assert.Equal(["Breed"], dog.Elements.Select(e => e.XmlName));
        Assert.Equal(["goodBoy"], dog.Attributes.Select(a => a.XmlName));
        Assert.Equal(["Weeks"], puppy.Elements.Select(e => e.XmlName));
        Assert.Empty(puppy.Attributes);

        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        (CSharpCompilation compilation, System.Reflection.Assembly assembly) = CompileAndLoad(generatedFiles);
        AssertNoErrors(compilation);

        Type animalType = assembly.GetType("ZooModel.AnimalType")!;
        Type dogType = assembly.GetType("ZooModel.DogType")!;
        Type puppyType = assembly.GetType("ZooModel.PuppyType")!;

        // Real C# inheritance, not just three unrelated flattened classes.
        Assert.True(dogType.IsSubclassOf(animalType));
        Assert.True(puppyType.IsSubclassOf(dogType));

        object puppyObj = Activator.CreateInstance(puppyType)!;
        puppyType.GetProperty("Id")!.SetValue(puppyObj, 7);
        puppyType.GetProperty("Name")!.SetValue(puppyObj, "Rex");
        puppyType.GetProperty("Breed")!.SetValue(puppyObj, "Labrador");
        puppyType.GetProperty("GoodBoy")!.SetValue(puppyObj, true);
        puppyType.GetProperty("Weeks")!.SetValue(puppyObj, 6);

        System.Text.StringBuilder sb = new();
        using (System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(sb))
        {
            writer.WriteStartElement("Puppy", "urn:example:zoo");
            puppyType.GetMethod("WriteXml")!.Invoke(puppyObj, [writer]);
            writer.WriteEndElement();
        }
        string xml = sb.ToString();
        Assert.Contains("id=\"7\"", xml);
        Assert.Contains("<Name>Rex</Name>", xml);
        Assert.Contains("<Breed>Labrador</Breed>", xml);
        Assert.Contains("goodBoy=\"true\"", xml);
        Assert.Contains("<Weeks>6</Weeks>", xml);
        // Base-class attribute/elements must come before the derived class's own ones, matching the
        // xsd:extension content model order (base content, then the extension's own new content).
        Assert.True(xml.IndexOf("id=\"7\"", StringComparison.Ordinal) < xml.IndexOf("goodBoy", StringComparison.Ordinal));
        Assert.True(xml.IndexOf("<Name>", StringComparison.Ordinal) < xml.IndexOf("<Breed>", StringComparison.Ordinal));
        Assert.True(xml.IndexOf("<Breed>", StringComparison.Ordinal) < xml.IndexOf("<Weeks>", StringComparison.Ordinal));

        object roundTripped = Activator.CreateInstance(puppyType)!;
        using (System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml)))
        {
            reader.MoveToContent();
            puppyType.GetMethod("ReadXml")!.Invoke(roundTripped, [reader]);
        }
        Assert.Equal(7, puppyType.GetProperty("Id")!.GetValue(roundTripped));
        Assert.Equal("Rex", puppyType.GetProperty("Name")!.GetValue(roundTripped));
        Assert.Equal("Labrador", puppyType.GetProperty("Breed")!.GetValue(roundTripped));
        Assert.Equal(true, puppyType.GetProperty("GoodBoy")!.GetValue(roundTripped));
        Assert.Equal(6, puppyType.GetProperty("Weeks")!.GetValue(roundTripped));
    }

    [Fact]
    public void AbstractComplexType_BecomesAbstractCSharpClassAndIsExcludedFromUnions()
    {
        const string xsd = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
                        targetNamespace="urn:example:zoo2"
                        xmlns:tns="urn:example:zoo2"
                        elementFormDefault="qualified">

              <xsd:complexType name="AnimalType" abstract="true">
                <xsd:sequence>
                  <xsd:element name="Name" type="xsd:string"/>
                </xsd:sequence>
              </xsd:complexType>

              <xsd:complexType name="DogType">
                <xsd:complexContent>
                  <xsd:extension base="tns:AnimalType">
                    <xsd:sequence>
                      <xsd:element name="Breed" type="xsd:string"/>
                    </xsd:sequence>
                  </xsd:extension>
                </xsd:complexContent>
              </xsd:complexType>

              <xsd:element name="Animal_Dummy" type="tns:AnimalType" abstract="true"/>
              <xsd:element name="Dog" type="tns:DogType" substitutionGroup="tns:Animal_Dummy"/>

              <xsd:complexType name="ZooType">
                <xsd:sequence>
                  <xsd:element ref="tns:Animal_Dummy"/>
                </xsd:sequence>
              </xsd:complexType>
              <xsd:element name="Zoo" type="tns:ZooType"/>

              <xsd:complexType name="ParkType">
                <xsd:choice>
                  <xsd:element name="ResidentAnimal" type="tns:AnimalType"/>
                  <xsd:element name="ResidentDog" type="tns:DogType"/>
                </xsd:choice>
              </xsd:complexType>
              <xsd:element name="Park" type="tns:ParkType"/>

            </xsd:schema>
            """;

        SchemaModel model = BuildModel(xsd, "ZooModel2");

        ClassTypeModel animal = Assert.Single(model.Classes, c => c.ClrName == "AnimalType");
        ClassTypeModel dog = Assert.Single(model.Classes, c => c.ClrName == "DogType");
        Assert.True(animal.IsAbstract);
        Assert.False(dog.IsAbstract);

        // Substitution union: only the concrete Dog substitute - never the abstract Animal_Dummy head.
        UnionTypeModel substUnion = Assert.Single(model.Unions, u => u.Cases.Any(c => c.ElementXmlName == "Dog"));
        Assert.DoesNotContain(substUnion.Cases, c => c.ElementXmlName == "Animal_Dummy");
        Assert.Single(substUnion.Cases);

        // Choice union: ResidentAnimal's own type (AnimalType) is abstract even though the *element*
        // itself isn't marked abstract - it must still be excluded, only ResidentDog survives.
        UnionTypeModel choiceUnion = Assert.Single(model.Unions, u => u.Cases.Any(c => c.ElementXmlName == "ResidentDog"));
        Assert.DoesNotContain(choiceUnion.Cases, c => c.ElementXmlName == "ResidentAnimal");
        Assert.Single(choiceUnion.Cases);

        IReadOnlyList<GeneratedFile> generatedFiles = SchemaCodeGenerator.Generate(model);
        GeneratedFile animalFile = Assert.Single(generatedFiles, f => f.BaseName == "AnimalType");
        Assert.Contains("public abstract partial class AnimalType", animalFile.Content);

        AssertCompilesClean(generatedFiles);
    }

    private static CSharpCompilation BuildCompilation(System.Collections.Generic.IReadOnlyList<GeneratedFile> generatedFiles, string assemblyName = "GeneratedTest")
    {
        CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        SyntaxTree[] trees = generatedFiles
            .Select(f => CSharpSyntaxTree.ParseText(f.Content, parseOptions, path: f.BaseName + ".cs"))
            .Append(CSharpSyntaxTree.ParseText(RuntimeSource.Text, parseOptions, path: RuntimeSource.FileName))
            .ToArray();

        string tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        MetadataReference[] references = tpa.Split(System.IO.Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p))
            .ToArray();

        return CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static void AssertNoErrors(CSharpCompilation compilation)
    {
        List<Diagnostic> diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(diagnostics.Count == 0, string.Join("\n", diagnostics.Select(d => d.ToString())));
    }

    private static void AssertCompilesClean(System.Collections.Generic.IReadOnlyList<GeneratedFile> generatedFiles) =>
        AssertNoErrors(BuildCompilation(generatedFiles));

    private static (CSharpCompilation compilation, System.Reflection.Assembly assembly) CompileAndLoad(System.Collections.Generic.IReadOnlyList<GeneratedFile> generatedFiles)
    {
        CSharpCompilation compilation = BuildCompilation(generatedFiles, "GeneratedTest_" + Guid.NewGuid().ToString("N"));
        using System.IO.MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
        stream.Position = 0;
        return (compilation, System.Reflection.Assembly.Load(stream.ToArray()));
    }
}

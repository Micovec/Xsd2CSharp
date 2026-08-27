namespace Xsd2CSharp.Core.Model;

public interface ITypeModel
{
    string ClrName { get; }

    /// <summary>The full C# namespace this type is emitted into (its own XSD targetNamespace, mapped to a C# namespace).</summary>
    string ClrNamespace { get; }
}

public sealed class EnumMemberModel(string clrName, string xmlValue)
{
    public string ClrName { get; } = clrName;
    public string XmlValue { get; } = xmlValue;
}

/// <summary>A generated C# enum for an xsd:enumeration-restricted simple type (any base, incl. NMTOKEN/NMTOKENS).</summary>
public sealed class EnumTypeModel(string clrName, string clrNamespace) : ITypeModel
{
    public string ClrName { get; } = clrName;
    public string ClrNamespace { get; } = clrNamespace;
    public List<EnumMemberModel> Members { get; } = [];

    /// <summary>True when the underlying XSD type is an inherent list (NMTOKENS/IDREFS/ENTITIES) or xsd:list of an enumeration.</summary>
    public bool IsList { get; set; }

    /// <summary>
    /// True when the restriction's ultimate base has the XSD "collapse" whitespace facet (NMTOKEN,
    /// token, and their derivations) rather than "preserve"/"replace" (plain string, normalizedString)
    /// - per XSD's lexical-to-value mapping, collapse means leading/trailing whitespace is stripped
    /// (and internal runs collapsed to one space) *before* validating against the enumeration, so e.g.
    /// "left " or " forward" are legitimately valid NMTOKEN enumeration values, not just "left"/"forward".
    /// </summary>
    public bool HasCollapseWhitespaceFacet { get; set; }
}

public sealed class UnionCaseModel(string caseClrName, string valueClrType, IoKind valueIoKind)
{
    /// <summary>
    /// Name of this case's type. When <see cref="IsWrapped"/>, a small single-field wrapper record
    /// synthesized just to be distinct (e.g. "DogCase" wrapping DogType); when not, this is set equal
    /// to <see cref="ValueClrType"/> and the value type is used directly as the case, with no wrapper
    /// - see <see cref="IsWrapped"/> for when each applies.
    /// </summary>
    public string CaseClrName { get; set; } = caseClrName;

    /// <summary>CLR type of the wrapped value (primitive, generated enum, or generated class/union).</summary>
    public string ValueClrType { get; } = valueClrType;

    /// <summary>How the wrapped value is read/written: Primitive (via XsdConvert), Enum (via its companion Xml class), or Serializable (has its own ReadXml/WriteXml, or - for nested simpleType unions - its own Parse/Format).</summary>
    public IoKind ValueIoKind { get; } = valueIoKind;

    /// <summary>Set when this case represents one branch of an xsd:choice (element name/namespace it corresponds to).</summary>
    public string? ElementXmlName { get; set; }
    public string? ElementXmlNamespace { get; set; }

    /// <summary>
    /// The element-name(s) that should route a read dispatch into this case. Usually just
    /// [ElementXmlName], but a row case whose first member is itself a nested choice has no single
    /// leading name - it's triggered by any of that nested choice's own trigger names instead.
    /// </summary>
    public List<string> TriggerNames { get; set; } = [];

    public bool ValueIsValueType { get; set; }

    /// <summary>The wrapped value's lexical form is itself an xsd:list (space-separated tokens), e.g. a choice branch typed as NMTOKENS or an xsd:list-of-enumeration like NeTEx's DaysOfWeek.</summary>
    public bool IsTokenList { get; set; }

    /// <summary>Parse/Format method names on XsdConvert, set only when ValueIoKind is Primitive.</summary>
    public string? ParseMethod { get; set; }
    public string? FormatMethod { get; set; }

    /// <summary>
    /// False when this case's ValueClrType doesn't collide with any other case's in the same union -
    /// in that situation the value type alone is already a valid, distinct C# union case (union case
    /// types must be pairwise distinct), so no wrapper record is needed and CaseClrName just equals
    /// ValueClrType. True (the default, and the only option when a collision exists) synthesizes a
    /// one-field wrapper record so the case stays distinct - see SchemaModelBuilder's
    /// AssignCaseWrapping, which is what actually decides this per-union once all cases are known.
    /// </summary>
    public bool IsWrapped { get; set; } = true;
}

/// <summary>A generated C# union: for xsd:choice content (case per element branch) or xsd:union simpleType (case per member type).</summary>
public sealed class UnionTypeModel(string clrName, string clrNamespace) : ITypeModel
{
    public string ClrName { get; } = clrName;
    public string ClrNamespace { get; } = clrNamespace;
    public List<UnionCaseModel> Cases { get; } = [];

    /// <summary>True for xsd:choice-derived unions (cases keyed by element name); false for xsd:union simpleType-derived unions.</summary>
    public bool IsElementChoice { get; set; }
}

public enum MemberKind { Attribute, Element }

public enum IoKind
{
    /// <summary>A CLR primitive/BCL value with a corresponding XsdConvert.Parse/Format pair (or plain string).</summary>
    Primitive,

    /// <summary>A generated enum type with its own Parse/Format helpers.</summary>
    Enum,

    /// <summary>A type (generated class or union) implementing IXmlSerializable itself.</summary>
    Serializable,

    /// <summary>An xsd:any wildcard - captured verbatim as raw XmlElement(s), no name to match against.</summary>
    Wildcard,
}

public sealed class MemberModel(MemberKind kind, string clrPropertyName, string xmlName, string clrTypeName, IoKind ioKind)
{
    public MemberKind Kind { get; } = kind;

    /// <summary>Settable so a post-pass can dedup collisions within a class (e.g. an attribute and element sanitizing to the same name).</summary>
    public string ClrPropertyName { get; set; } = clrPropertyName;
    public string XmlName { get; } = xmlName;
    public string? XmlNamespace { get; set; }
    public string ClrTypeName { get; } = clrTypeName;
    public IoKind IoKind { get; } = ioKind;

    /// <summary>
    /// The element-name(s) that route a read dispatch to this member. Usually just [XmlName], but a
    /// plain (non-union-case) row member whose first sub-member is itself a nested choice has no
    /// single leading name - see UnionCaseModel.TriggerNames for the same situation on union cases.
    /// </summary>
    public List<string> TriggerNames { get; set; } = [xmlName];

    public bool IsValueType { get; set; }

    /// <summary>Multiple sibling elements (maxOccurs > 1). Never true for attributes.</summary>
    public bool IsRepeating { get; set; }

    /// <summary>The value's lexical form is itself an xsd:list (space-separated tokens within one element/attribute, e.g. NMTOKENS).</summary>
    public bool IsTokenList { get; set; }

    public bool IsOptional { get; set; }
    public bool IsNillable { get; set; }

    /// <summary>For IoKind.Primitive: parse/format method names on XsdConvert, or null if the type is string.</summary>
    public string? ParseMethod { get; set; }
    public string? FormatMethod { get; set; }

    /// <summary>Binary encoding hint (only meaningful for byte[] members).</summary>
    public bool IsBase64 { get; set; }

    /// <summary>
    /// The XSD-declared default="..." (or fixed="...") value, if any. Per XSD's value-constraint
    /// semantics, this applies when the element/attribute is *present but empty* (e.g.
    /// "&lt;HasMinimumPrice/&gt;" with default="false" means false, not "empty text") - distinct from
    /// the element being omitted entirely, which just leaves an optional member unset as usual. Real
    /// schemas rely on this heavily (a single NeTEx file can have 100+ self-closing boolean elements
    /// deferring to their schema default) - without applying it, parsing an empty string as e.g. a
    /// boolean or enum throws FormatException even though the document is fully spec-valid.
    /// </summary>
    public string? XsdDefaultValue { get; set; }
}

public sealed class ClassTypeModel(string clrName, string clrNamespace) : ITypeModel
{
    public string ClrName { get; } = clrName;
    public string ClrNamespace { get; } = clrNamespace;
    public List<MemberModel> Attributes { get; } = [];
    public List<MemberModel> Elements { get; } = [];

    /// <summary>Text content member for a complexType with simpleContent extension (null otherwise).</summary>
    public MemberModel? SimpleContent { get; set; }

    /// <summary>
    /// Set when this type's xsd:complexType extends another (via xsd:complexContent/xsd:extension)
    /// with element/mixed content - modeled as real C# inheritance, so Attributes/Elements above hold
    /// only this type's *own new* members, not ones already declared on the base class.
    /// </summary>
    public ClassTypeModel? BaseClass { get; set; }

    /// <summary>
    /// From the XSD complexType's own abstract="true" - emitted as a C# `abstract` class, and never
    /// usable as a union case's value type (see SchemaModelBuilder.IsUninstantiable), since "new
    /// AbstractType()" wouldn't compile and this tool doesn't do xsi:type-based dynamic dispatch.
    /// </summary>
    public bool IsAbstract { get; set; }

    /// <summary>
    /// True for the synthetic "row" types generated for a multi-element xsd:choice branch (a branch
    /// that's itself a &lt;sequence&gt; of several elements, with no wrapping element of its own).
    /// Row types get bare ReadFrom/WriteTo methods instead of IXmlSerializable's ReadXml/WriteXml,
    /// since there's no enclosing element for them to consume/write.
    /// </summary>
    public bool IsRow { get; set; }

    /// <summary>
    /// From the XSD complexType's own mixed="true" (element content interspersed with free-floating
    /// character data, e.g. NeTEx's MultilingualString - either plain text OR nested &lt;Text&gt;
    /// elements, per the schema's own doc comment on that type). Distinct from SimpleContent, which is
    /// for a complexType with *no* element children at all; a mixed type can have both, and the
    /// generated ReadXml/WriteXml carry an extra MixedText string alongside the normal Elements.
    /// </summary>
    public bool IsMixed { get; set; }
}

public sealed class RootElementModel(string clrTypeName, string xmlName, string? xmlNamespace, string clrNamespace)
{
    public string ClrTypeName { get; } = clrTypeName;
    public string XmlName { get; } = xmlName;
    public string? XmlNamespace { get; } = xmlNamespace;

    /// <summary>The C# namespace this root element's Load/Save helper is emitted into (its own XSD namespace, mapped).</summary>
    public string ClrNamespace { get; } = clrNamespace;

    /// <summary>Parse/Format method names on XsdConvert, set only when the root element's type is a bare primitive (not string).</summary>
    public string? ParseMethod { get; set; }
    public string? FormatMethod { get; set; }
}

public sealed class SchemaModel(string rootClrNamespace)
{
    /// <summary>The base namespace passed by the caller (e.g. via the CLI's -n). Types whose XSD namespace maps to this get emitted directly into it, with no sub-namespace suffix.</summary>
    public string RootClrNamespace { get; } = rootClrNamespace;
    public List<EnumTypeModel> Enums { get; } = [];
    public List<UnionTypeModel> Unions { get; } = [];
    public List<ClassTypeModel> Classes { get; } = [];
    public List<RootElementModel> RootElements { get; } = [];

    /// <summary>Every distinct C# namespace used by any generated type - each generated file gets a `using` for all of these, so cross-namespace type references always resolve without per-file dependency analysis.</summary>
    public IEnumerable<string> AllClrNamespaces =>
        Enums.Select(e => e.ClrNamespace)
            .Concat(Unions.Select(u => u.ClrNamespace))
            .Concat(Classes.Select(c => c.ClrNamespace))
            .Concat(RootElements.Select(r => r.ClrNamespace))
            .Append(RootClrNamespace)
            .Distinct(StringComparer.Ordinal);
}

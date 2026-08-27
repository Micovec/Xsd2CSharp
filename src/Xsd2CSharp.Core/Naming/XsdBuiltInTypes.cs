using System.Xml.Schema;

namespace Xsd2CSharp.Core.Naming;

/// <summary>
/// Describes how a single XSD built-in datatype maps onto a CLR type, and which
/// <c>Xsd2CSharp.Runtime.XsdConvert</c> methods parse/format its lexical (string) form.
/// </summary>
/// <param name="ClrType">The CLR type name to use for generated properties.</param>
/// <param name="IsValueType">Whether the CLR type is a value type (affects nullable annotation).</param>
/// <param name="ParseMethod">Name of the <c>XsdConvert</c> method converting string -> CLR type, or null if the CLR type is <c>string</c> itself.</param>
/// <param name="FormatMethod">Name of the <c>XsdConvert</c> method converting CLR type -> string, or null if the CLR type is <c>string</c> itself.</param>
public sealed record BuiltInTypeInfo(string ClrType, bool IsValueType, string? ParseMethod, string? FormatMethod)
{
    public static readonly BuiltInTypeInfo String = new("string", false, null, null);
}

/// <summary>
/// Maps XSD built-in simple types (identified by <see cref="XmlTypeCode"/>) to CLR types.
/// </summary>
public static class XsdBuiltInTypes
{
    private static readonly Dictionary<XmlTypeCode, BuiltInTypeInfo> Map = new()
    {
        // Character data - all represented as string.
        [XmlTypeCode.String] = BuiltInTypeInfo.String,
        [XmlTypeCode.NormalizedString] = BuiltInTypeInfo.String,
        [XmlTypeCode.Token] = BuiltInTypeInfo.String,
        [XmlTypeCode.Language] = BuiltInTypeInfo.String,
        [XmlTypeCode.Name] = BuiltInTypeInfo.String,
        [XmlTypeCode.NCName] = BuiltInTypeInfo.String,
        [XmlTypeCode.Id] = BuiltInTypeInfo.String,
        [XmlTypeCode.Idref] = BuiltInTypeInfo.String,
        [XmlTypeCode.Entity] = BuiltInTypeInfo.String,
        [XmlTypeCode.NmToken] = BuiltInTypeInfo.String,
        [XmlTypeCode.AnyAtomicType] = BuiltInTypeInfo.String,
        [XmlTypeCode.UntypedAtomic] = BuiltInTypeInfo.String,

        // Not modeled precisely (no lossless CLR equivalent) - kept as their lexical string form.
        [XmlTypeCode.Duration] = BuiltInTypeInfo.String,
        [XmlTypeCode.GYear] = BuiltInTypeInfo.String,
        [XmlTypeCode.GYearMonth] = BuiltInTypeInfo.String,
        [XmlTypeCode.GMonth] = BuiltInTypeInfo.String,
        [XmlTypeCode.GMonthDay] = BuiltInTypeInfo.String,
        [XmlTypeCode.GDay] = BuiltInTypeInfo.String,
        [XmlTypeCode.QName] = BuiltInTypeInfo.String,
        [XmlTypeCode.Notation] = BuiltInTypeInfo.String,

        [XmlTypeCode.Boolean] = new("bool", true, "ParseBoolean", "Format"),

        [XmlTypeCode.Decimal] = new("decimal", true, "ParseDecimal", "Format"),
        [XmlTypeCode.Float] = new("float", true, "ParseSingle", "Format"),
        [XmlTypeCode.Double] = new("double", true, "ParseDouble", "Format"),

        [XmlTypeCode.Byte] = new("sbyte", true, "ParseSByte", "Format"),
        [XmlTypeCode.UnsignedByte] = new("byte", true, "ParseByte", "Format"),
        [XmlTypeCode.Short] = new("short", true, "ParseInt16", "Format"),
        [XmlTypeCode.UnsignedShort] = new("ushort", true, "ParseUInt16", "Format"),
        [XmlTypeCode.Int] = new("int", true, "ParseInt32", "Format"),
        [XmlTypeCode.UnsignedInt] = new("uint", true, "ParseUInt32", "Format"),
        [XmlTypeCode.Long] = new("long", true, "ParseInt64", "Format"),
        [XmlTypeCode.UnsignedLong] = new("ulong", true, "ParseUInt64", "Format"),

        // Arbitrary-precision integer family -> BigInteger.
        [XmlTypeCode.Integer] = new("System.Numerics.BigInteger", true, "ParseBigInteger", "Format"),
        [XmlTypeCode.PositiveInteger] = new("System.Numerics.BigInteger", true, "ParseBigInteger", "Format"),
        [XmlTypeCode.NonPositiveInteger] = new("System.Numerics.BigInteger", true, "ParseBigInteger", "Format"),
        [XmlTypeCode.NegativeInteger] = new("System.Numerics.BigInteger", true, "ParseBigInteger", "Format"),
        [XmlTypeCode.NonNegativeInteger] = new("System.Numerics.BigInteger", true, "ParseBigInteger", "Format"),

        [XmlTypeCode.DateTime] = new("System.DateTimeOffset", true, "ParseDateTimeOffset", "Format"),
        [XmlTypeCode.Date] = new("System.DateOnly", true, "ParseDateOnly", "Format"),
        [XmlTypeCode.Time] = new("System.TimeOnly", true, "ParseTimeOnly", "Format"),

        [XmlTypeCode.AnyUri] = new("System.Uri", false, "ParseUri", "Format"),

        // Binary - encoding (hex vs base64) is tracked separately since both share this CLR type.
        [XmlTypeCode.HexBinary] = new("byte[]", false, "ParseHexBinary", "FormatHexBinary"),
        [XmlTypeCode.Base64Binary] = new("byte[]", false, "ParseBase64Binary", "FormatBase64Binary"),

        [XmlTypeCode.Boolean] = new("bool", true, "ParseBoolean", "Format"),
    };

    public static bool TryGet(XmlTypeCode typeCode, out BuiltInTypeInfo info) => Map.TryGetValue(typeCode, out info!);
}

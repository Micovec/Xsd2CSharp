using System.Globalization;
using System.Text;

namespace Xsd2CSharp.Core.Naming;

/// <summary>
/// Turns arbitrary XSD names (element/type/attribute/enumeration-value names, which may contain
/// characters that are legal in XML but not in C# identifiers - e.g. NMTOKEN values like "en-US"
/// or "1st-class") into valid, de-duplicated C# identifiers.
/// </summary>
public static class CSharpIdentifiers
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit",
        "extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int",
        "interface","internal","is","lock","long","namespace","new","null","object","operator","out",
        "override","params","private","protected","public","readonly","ref","return","sbyte","sealed",
        "short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try",
        "typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
        "union",
    };

    /// <summary>
    /// Converts a raw name into a PascalCase C# identifier suitable for a type or property name.
    /// Non-identifier characters (-, ., :, spaces, ...) become word boundaries.
    /// </summary>
    public static string ToPascalIdentifier(string? raw)
    {
        List<string> words = SplitWords(raw);
        if (words.Count == 0)
            return "Value";

        StringBuilder sb = new();
        foreach (string word in words)
            sb.Append(CapitalizeWord(word));

        return FinishIdentifier(sb.ToString());
    }

    /// <summary>
    /// Converts a raw name into a camelCase C# identifier suitable for a local variable or parameter name.
    /// </summary>
    public static string ToCamelIdentifier(string? raw)
    {
        string pascal = ToPascalIdentifier(raw);
        if (pascal.Length == 0)
            return pascal;

        string camel = char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
        return EscapeIfKeyword(camel);
    }

    /// <summary>
    /// Sanitizes a name that must survive as an enum member: keeps it recognizable when possible
    /// but guarantees a valid identifier. The original XML value is preserved separately for I/O.
    /// </summary>
    public static string ToEnumMemberIdentifier(string xmlValue) => ToPascalIdentifier(xmlValue);

    /// <summary>
    /// Ensures <paramref name="candidate"/> is unique against <paramref name="used"/> by appending
    /// a numeric suffix if needed, then adds the result to <paramref name="used"/>.
    /// </summary>
    public static string Uniquify(string candidate, HashSet<string> used)
    {
        if (used.Add(candidate))
            return candidate;

        int i = 2;
        string next;
        do
        {
            next = candidate + i.ToString(CultureInfo.InvariantCulture);
            i++;
        } while (!used.Add(next));

        return next;
    }

    private static List<string> SplitWords(string? raw)
    {
        List<string> words = [];
        if (raw is null || raw.Length == 0)
            return words;

        StringBuilder current = new();
        bool previousWasLower = false;

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                // Split "camelCase" / "PascalCase" runs into separate words too, so
                // "fooBar" and "foo-bar" both normalize to "FooBar".
                if (char.IsUpper(ch) && previousWasLower)
                    Flush();

                current.Append(ch);
                previousWasLower = char.IsLower(ch);
            }
            else
            {
                Flush();
                previousWasLower = false;
            }
        }

        Flush();
        return words;
    }

    private static string CapitalizeWord(string word)
    {
        if (word.Length == 0)
            return word;

        // Preserve existing internal casing (so "XMLParser" doesn't become "Xmlparser"),
        // just make sure the first character is upper case.
        if (char.IsUpper(word[0]))
            return word;

        return char.ToUpperInvariant(word[0]) + word.Substring(1);
    }

    private static string FinishIdentifier(string identifier)
    {
        if (identifier.Length == 0)
            identifier = "Value";

        if (char.IsDigit(identifier[0]))
            identifier = "_" + identifier;

        return EscapeIfKeyword(identifier);
    }

    private static string EscapeIfKeyword(string identifier) =>
        Keywords.Contains(identifier) ? "@" + identifier : identifier;
}

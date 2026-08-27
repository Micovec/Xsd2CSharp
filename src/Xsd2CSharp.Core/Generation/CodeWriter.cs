using System.Text;

namespace Xsd2CSharp.Core.Generation;

/// <summary>Minimal indenting text writer for emitting generated C# source.</summary>
public sealed class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _sb.Append('\n');
        }
        else
        {
            _sb.Append(' ', _indent * 4).Append(text).Append('\n');
        }
        return this;
    }

    public IDisposable Block(string header, string closer = "}")
    {
        Line(header);
        Line("{");
        _indent++;
        return new Indent(this, closer);
    }

    public IDisposable Indented()
    {
        _indent++;
        return new Indent(this, closer: null);
    }

    private void Dedent(string? closer)
    {
        _indent--;
        if (closer is not null)
            Line(closer);
    }

    public override string ToString() => _sb.ToString();

    private sealed class Indent(CodeWriter writer, string? closer) : IDisposable
    {
        public void Dispose() => writer.Dedent(closer);
    }
}

using System;
using System.Text;

namespace Pakt.Generators
{
    /// <summary>
    /// Lightweight code-generation helper with indentation tracking.
    /// </summary>
    internal sealed class SourceBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;

        public void Indent() => _indent++;
        public void Dedent() => _indent--;

        public void AppendLine(string text)
        {
            _sb.Append(' ', _indent * 4);
            _sb.AppendLine(text);
        }

        public void AppendLine()
        {
            _sb.AppendLine();
        }

        public void Append(string text)
        {
            _sb.Append(' ', _indent * 4);
            _sb.Append(text);
        }

        public void AppendRaw(string text)
        {
            _sb.Append(text);
        }

        public void OpenBrace()
        {
            AppendLine("{");
            Indent();
        }

        public void CloseBrace(string suffix = "")
        {
            Dedent();
            AppendLine("}" + suffix);
        }

        public override string ToString() => _sb.ToString();
    }
}

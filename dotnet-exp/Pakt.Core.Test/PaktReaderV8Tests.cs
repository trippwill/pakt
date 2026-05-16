using System.Buffers;
using System.Text;

using Pakt;

namespace Pakt.Core.Test;

public class PaktReaderV8Tests
{
    private static List<(PaktTokenType Type, string Value)> DrainV8(string paktText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(paktText);
        var seq = new ReadOnlySequence<byte>(bytes);
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string value = reader.ValueSequence.Length > 0
                ? Encoding.UTF8.GetString(reader.ValueSequence)
                : "";
            tokens.Add((reader.TokenType, value));
        }
        return tokens;
    }

    [Fact]
    public void EmptyInput_EmitsEndOfUnit()
    {
        var tokens = DrainV8("");
        Assert.Single(tokens);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[0].Type);
    }

    [Fact]
    public void WhitespaceOnly_EmitsEndOfUnit()
    {
        var tokens = DrainV8("  \n  \t  \n");
        Assert.Single(tokens);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[0].Type);
    }

    [Fact]
    public void Comment_EmitsEndOfUnit()
    {
        var tokens = DrainV8("# this is a comment\n");
        Assert.Single(tokens);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[0].Type);
    }

    [Fact]
    public void SimpleIntAssign()
    {
        var tokens = DrainV8("x:int = 42");
        Assert.Equal(PaktTokenType.StatementName, tokens[0].Type);
        Assert.Equal("x", tokens[0].Value);
        Assert.Equal(PaktTokenType.TypeAnnotationStart, tokens[1].Type);
        Assert.Equal("int", tokens[1].Value);
        Assert.Equal(PaktTokenType.AssignOperator, tokens[2].Type);
        Assert.Equal(PaktTokenType.Int, tokens[3].Type);
        Assert.Equal("42", tokens[3].Value);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[4].Type);
    }

    [Fact]
    public void SimpleBoolAssign()
    {
        var tokens = DrainV8("flag:bool = true");
        Assert.Equal(PaktTokenType.StatementName, tokens[0].Type);
        Assert.Equal("flag", tokens[0].Value);
        Assert.Equal(PaktTokenType.Bool, tokens[3].Type);
        Assert.Equal("true", tokens[3].Value);
    }

    [Fact]
    public void SimpleStructValue()
    {
        var tokens = DrainV8("pt:{x:int y:int} = { 1 2 }");
        Assert.Equal(PaktTokenType.StatementName, tokens[0].Type);
        Assert.Equal(PaktTokenType.TypeAnnotationStart, tokens[1].Type);
        Assert.Equal(PaktTokenType.AssignOperator, tokens[2].Type);
        Assert.Equal(PaktTokenType.StructStart, tokens[3].Type);
        Assert.Equal(PaktTokenType.Int, tokens[4].Type);
        Assert.Equal("1", tokens[4].Value);
        Assert.Equal(PaktTokenType.Int, tokens[5].Type);
        Assert.Equal("2", tokens[5].Value);
        Assert.Equal(PaktTokenType.StructEnd, tokens[6].Type);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[7].Type);
    }

    [Fact]
    public void StreamingList()
    {
        var tokens = DrainV8("items:[int] = ~[1 2 3]");
        Assert.Equal(PaktTokenType.StatementName, tokens[0].Type);
        Assert.Equal(PaktTokenType.AssignOperator, tokens[2].Type);
        Assert.Equal(PaktTokenType.ListStart, tokens[3].Type);
        Assert.Equal(PaktTokenType.Int, tokens[4].Type);
        Assert.Equal("1", tokens[4].Value);
        Assert.Equal(PaktTokenType.Int, tokens[5].Type);
        Assert.Equal("2", tokens[5].Value);
        Assert.Equal(PaktTokenType.Int, tokens[6].Type);
        Assert.Equal("3", tokens[6].Value);
        Assert.Equal(PaktTokenType.ListEnd, tokens[7].Type);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[8].Type);
    }

    [Fact]
    public void StreamingList_TailEof()
    {
        // ~[ without closing ] — EOF terminates
        var tokens = DrainV8("items:[int] = ~[1 2 3");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.ListStart);
        Assert.Equal(PaktTokenType.Int, tokens[^2].Type);
        Assert.Equal("3", tokens[^2].Value);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void TwoStatements()
    {
        var tokens = DrainV8("a:int = 1\nb:int = 2");
        var stmts = tokens.Where(t => t.Type == PaktTokenType.StatementName).ToList();
        Assert.Equal(2, stmts.Count);
        Assert.Equal("a", stmts[0].Value);
        Assert.Equal("b", stmts[1].Value);
    }

    [Fact]
    public void StreamingList_ThenStatement()
    {
        var tokens = DrainV8("items:[int] = ~[1 2]\nnext:int = 99");
        var stmts = tokens.Where(t => t.Type == PaktTokenType.StatementName).ToList();
        Assert.Equal(2, stmts.Count);
        Assert.Equal("items", stmts[0].Value);
        Assert.Equal("next", stmts[1].Value);
    }

    [Fact]
    public void NulTermination()
    {
        byte[] data = [.. "x:int = 42"u8, 0x00];
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
        var tokens = new List<PaktTokenType>();
        while (reader.Read())
            tokens.Add(reader.TokenType);

        Assert.Contains(PaktTokenType.Int, tokens);
        Assert.Contains(PaktTokenType.EndOfUnit, tokens);
    }

    [Fact]
    public void MapEntryBind()
    {
        var tokens = DrainV8("h:<int = int> = < 1 = 2 >");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEntryBind);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEnd);
    }

    [Fact]
    public void StringValue()
    {
        var tokens = DrainV8("x:str = 'hello world'");
        Assert.Equal(PaktTokenType.String, tokens[3].Type);
        Assert.Equal("'hello world'", tokens[3].Value);
    }

    [Fact]
    public void StringWithEscape()
    {
        var tokens = DrainV8(@"x:str = 'hello\nworld'");
        Assert.Equal(PaktTokenType.String, tokens[3].Type);
        Assert.True(tokens[3].Value.Contains("\\n", StringComparison.Ordinal));
    }

    [Fact]
    public void RawString()
    {
        var tokens = DrainV8(@"x:str = r'hello\nworld'");
        Assert.Equal(PaktTokenType.String, tokens[3].Type);
        Assert.StartsWith("r'", tokens[3].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryHex()
    {
        var tokens = DrainV8("x:bin = x'48656c6c6f'");
        Assert.Equal(PaktTokenType.Binary, tokens[3].Type);
        Assert.Equal("x'48656c6c6f'", tokens[3].Value);
    }

    [Fact]
    public void MapWithStrings()
    {
        var tokens = DrainV8("h:<str = str> = < 'a' = 'b' >");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEntryBind);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.String);
    }

    [Fact]
    public void NilValue()
    {
        var tokens = DrainV8("x:int? = nil");
        Assert.Equal(PaktTokenType.Nil, tokens[3].Type);
    }

    // ── Multi-segment tests ─────────────────────────────────────────

    private static List<(PaktTokenType Type, string Value)> DrainV8Seq(ReadOnlySequence<byte> seq)
    {
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string val = reader.ValueSequence.Length > 0
                ? Encoding.UTF8.GetString(reader.ValueSequence) : "";
            tokens.Add((reader.TokenType, val));
        }
        return tokens;
    }

    [Fact]
    public void MultiSegment_IntSplitAcrossSegments()
    {
        var seq = CreateMultiSegment("x:int = 4"u8, "2"u8);
        var tokens = DrainV8Seq(seq);
        Assert.Equal(PaktTokenType.Int, tokens[3].Type);
        Assert.Equal("42", tokens[3].Value);
    }

    [Fact]
    public void MultiSegment_StringSplitAcrossSegments()
    {
        var seq = CreateMultiSegment("x:str = 'hel"u8, "lo'"u8);
        var tokens = DrainV8Seq(seq);
        Assert.Equal(PaktTokenType.String, tokens[3].Type);
        Assert.Equal("'hello'", tokens[3].Value);
    }

    [Fact]
    public void MultiSegment_IdentSplitAcrossSegments()
    {
        var seq = CreateMultiSegment("my-"u8, "field:int = 1"u8);
        var tokens = DrainV8Seq(seq);
        Assert.Equal(PaktTokenType.StatementName, tokens[0].Type);
        Assert.Equal("my-field", tokens[0].Value);
    }

    [Fact]
    public void MultiSegment_ValueSplitAcrossSegments()
    {
        var seq = CreateMultiSegment("items:[int] = ["u8, "1 2]"u8);
        var tokens = DrainV8Seq(seq);
        Assert.Equal(PaktTokenType.AssignOperator, tokens[2].Type);
        Assert.Equal(PaktTokenType.ListStart, tokens[3].Type);
        Assert.Equal(PaktTokenType.Int, tokens[4].Type);
    }

    private static ReadOnlySequence<byte> CreateMultiSegment(
        ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var seg2 = new MemorySegment(second.ToArray());
        var seg1 = new MemorySegment(first.ToArray(), seg2);
        return new ReadOnlySequence<byte>(seg1, 0, seg2, second.Length);
    }

    private sealed class MemorySegment : ReadOnlySequenceSegment<byte>
    {
        public MemorySegment(byte[] data, MemorySegment? next = null)
        {
            Memory = data;
            if (next is not null)
            {
                Next = next;
                next.RunningIndex = data.Length;
            }
        }
    }
}
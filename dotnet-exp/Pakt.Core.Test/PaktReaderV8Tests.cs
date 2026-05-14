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
        var reader = new PaktReader(seq, isFinalBlock: true);
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
    public void ListPack_WithSemicolon()
    {
        var tokens = DrainV8("items:[int] << 1 2 3;");
        Assert.Equal(PaktTokenType.StatementName, tokens[0].Type);
        Assert.Equal(PaktTokenType.PackOperator, tokens[2].Type);
        Assert.Equal(PaktTokenType.Int, tokens[3].Type);
        Assert.Equal("1", tokens[3].Value);
        Assert.Equal(PaktTokenType.Int, tokens[4].Type);
        Assert.Equal("2", tokens[4].Value);
        Assert.Equal(PaktTokenType.Int, tokens[5].Type);
        Assert.Equal("3", tokens[5].Value);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[6].Type);
    }

    [Fact]
    public void ListPack_TailEof()
    {
        var tokens = DrainV8("items:[int] << 1 2 3");
        Assert.Equal(PaktTokenType.Int, tokens[5].Type);
        Assert.Equal("3", tokens[5].Value);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[6].Type);
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
    public void PackTerminatedBySemicolon_ThenStatement()
    {
        var tokens = DrainV8("items:[int] << 1 2;\nnext:int = 99");
        var stmts = tokens.Where(t => t.Type == PaktTokenType.StatementName).ToList();
        Assert.Equal(2, stmts.Count);
        Assert.Equal("items", stmts[0].Value);
        Assert.Equal("next", stmts[1].Value);
    }

    [Fact]
    public void NulTermination()
    {
        byte[] data = [.."x:int = 42"u8, 0x00];
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new PaktReader(seq, isFinalBlock: true);
        var tokens = new List<PaktTokenType>();
        while (reader.Read())
            tokens.Add(reader.TokenType);

        Assert.Contains(PaktTokenType.Int, tokens);
        Assert.Contains(PaktTokenType.EndOfUnit, tokens);
    }

    [Fact]
    public void MapEntryBind()
    {
        var tokens = DrainV8("h:<int => int> = < 1 => 2 >");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEntryBind);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEnd);
    }

    [Fact]
    public void NilValue()
    {
        var tokens = DrainV8("x:int? = nil");
        Assert.Equal(PaktTokenType.Nil, tokens[3].Type);
    }
}

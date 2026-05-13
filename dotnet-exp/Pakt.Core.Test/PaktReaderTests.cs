using System.Text;

using Pakt;

namespace Pakt.Core.Test;

public class PaktReaderTests
{
    // ── Test infrastructure ─────────────────────────────────────────

    private static List<(PaktTokenType Type, string Value)> DrainTokens(string paktText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(paktText);
        using var reader = new PaktMemoryReader(new ReadOnlyMemory<byte>(bytes));
        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string value = reader.ValueSpan.IsEmpty ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            tokens.Add((reader.TokenType, value));
        }
        return tokens;
    }

    private static List<(PaktTokenType Type, string Value)> DrainTokens(byte[] bytes)
    {
        using var reader = new PaktMemoryReader(new ReadOnlyMemory<byte>(bytes));
        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string value = reader.ValueSpan.IsEmpty ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            tokens.Add((reader.TokenType, value));
        }
        return tokens;
    }

    private static void AssertToken(
        List<(PaktTokenType Type, string Value)> tokens,
        int index,
        PaktTokenType expectedType,
        string? expectedValue = null)
    {
        Assert.True(
            index < tokens.Count,
            $"Expected token at index {index} but only {tokens.Count} tokens");
        Assert.Equal(expectedType, tokens[index].Type);
        if (expectedValue is not null)
            Assert.Equal(expectedValue, tokens[index].Value);
    }

    // ── Empty / minimal units ───────────────────────────────────────

    [Fact]
    public void EmptyUnit_EmitsEndOfUnit()
    {
        var tokens = DrainTokens("");
        Assert.Single(tokens);
        AssertToken(tokens, 0, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void WhitespaceOnly_EmitsEndOfUnit()
    {
        var tokens = DrainTokens("  \n  \t  \n");
        Assert.Single(tokens);
        AssertToken(tokens, 0, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void CommentOnly_EmitsEndOfUnit()
    {
        var tokens = DrainTokens("# this is a comment\n");
        Assert.Single(tokens);
        AssertToken(tokens, 0, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void BomPrefix_EmitsEndOfUnit()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var tokens = DrainTokens(bom);
        Assert.Single(tokens);
        AssertToken(tokens, 0, PaktTokenType.EndOfUnit);
    }

    // ── Scalar assigns ──────────────────────────────────────────────

    [Fact]
    public void ScalarAssign_String()
    {
        var tokens = DrainTokens("name:str = 'hello'");
        AssertToken(tokens, 0, PaktTokenType.StatementName, "name");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "str");
        AssertToken(tokens, 2, PaktTokenType.TypeAnnotationEnd);
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
        AssertToken(tokens, 4, PaktTokenType.String, "'hello'");
        AssertToken(tokens, 5, PaktTokenType.EndOfUnit);
        Assert.Equal(6, tokens.Count);
    }

    [Fact]
    public void ScalarAssign_Int()
    {
        var tokens = DrainTokens("count:int = 42");
        AssertToken(tokens, 0, PaktTokenType.StatementName, "count");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "int");
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
        AssertToken(tokens, 4, PaktTokenType.Int, "42");
    }

    [Fact]
    public void ScalarAssign_IntHex()
    {
        var tokens = DrainTokens("hex:int = 0xFF");
        AssertToken(tokens, 4, PaktTokenType.Int, "0xFF");
    }

    [Fact]
    public void ScalarAssign_Dec()
    {
        var tokens = DrainTokens("price:dec = 19.99");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "dec");
        AssertToken(tokens, 4, PaktTokenType.Decimal, "19.99");
    }

    [Fact]
    public void ScalarAssign_Float()
    {
        var tokens = DrainTokens("x:float = 6.022e23");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "float");
        AssertToken(tokens, 4, PaktTokenType.Float, "6.022e23");
    }

    [Fact]
    public void ScalarAssign_BoolTrue()
    {
        var tokens = DrainTokens("flag:bool = true");
        AssertToken(tokens, 4, PaktTokenType.Bool, "true");
    }

    [Fact]
    public void ScalarAssign_BoolFalse()
    {
        var tokens = DrainTokens("flag:bool = false");
        AssertToken(tokens, 4, PaktTokenType.Bool, "false");
    }

    [Fact]
    public void ScalarAssign_Uuid()
    {
        var tokens = DrainTokens("id:uuid = 550e8400-e29b-41d4-a716-446655440000");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "uuid");
        AssertToken(tokens, 4, PaktTokenType.Uuid, "550e8400-e29b-41d4-a716-446655440000");
    }

    [Fact]
    public void ScalarAssign_Date()
    {
        var tokens = DrainTokens("d:date = 2026-06-01");
        AssertToken(tokens, 4, PaktTokenType.Date, "2026-06-01");
    }

    [Fact]
    public void ScalarAssign_Timestamp()
    {
        var tokens = DrainTokens("t:ts = 2026-06-01T14:30:00Z");
        AssertToken(tokens, 4, PaktTokenType.Timestamp, "2026-06-01T14:30:00Z");
    }

    [Fact]
    public void ScalarAssign_BinHex()
    {
        var tokens = DrainTokens("p:bin = x'48656C6C6F'");
        AssertToken(tokens, 4, PaktTokenType.Binary, "x'48656C6C6F'");
    }

    [Fact]
    public void ScalarAssign_BinBase64()
    {
        var tokens = DrainTokens("p:bin = b'SGVsbG8='");
        AssertToken(tokens, 4, PaktTokenType.Binary, "b'SGVsbG8='");
    }

    // ── Type annotations ────────────────────────────────────────────

    [Fact]
    public void TypeAnnotation_Struct()
    {
        var tokens = DrainTokens("x:{name:str age:int} = { 'hi' 1 }");
        AssertToken(tokens, 0, PaktTokenType.StatementName, "x");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "{name:str age:int}");
        AssertToken(tokens, 2, PaktTokenType.TypeAnnotationEnd);
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
    }

    [Fact]
    public void TypeAnnotation_Tuple()
    {
        var tokens = DrainTokens("x:(int str) = (1 'a')");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "(int str)");
        AssertToken(tokens, 2, PaktTokenType.TypeAnnotationEnd);
    }

    [Fact]
    public void TypeAnnotation_List()
    {
        var tokens = DrainTokens("x:[int] = [1]");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "[int]");
        AssertToken(tokens, 2, PaktTokenType.TypeAnnotationEnd);
    }

    [Fact]
    public void TypeAnnotation_Map()
    {
        var tokens = DrainTokens("x:<str => int> = <'a' => 1>");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "<str => int>");
        AssertToken(tokens, 2, PaktTokenType.TypeAnnotationEnd);
    }

    [Fact]
    public void TypeAnnotation_AtomSet_NotYetSupported()
    {
        // v7 lexer produces AtomPrefix for |ident, so atom set type annotations
        // are not yet parseable by PaktMemoryReader.
        Assert.Throws<PaktParseException>(
            () => DrainTokens("x:|dev staging prod| = |dev"));
    }

    // ── Nullable ────────────────────────────────────────────────────

    [Fact]
    public void Nullable_NilValue()
    {
        var tokens = DrainTokens("x:str? = nil");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "str?");
        AssertToken(tokens, 2, PaktTokenType.TypeAnnotationEnd);
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
        AssertToken(tokens, 4, PaktTokenType.Nil, "nil");
        AssertToken(tokens, 5, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void Nullable_WithValue()
    {
        var tokens = DrainTokens("x:int? = 42");
        AssertToken(tokens, 1, PaktTokenType.TypeAnnotationStart, "int?");
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
        AssertToken(tokens, 4, PaktTokenType.Int, "42");
    }

    [Fact]
    public void NilNonNullable_Throws()
    {
        Assert.Throws<PaktParseException>(
            () => DrainTokens("x:str = nil"));
    }

    // ── Composite values ────────────────────────────────────────────

    [Fact]
    public void StructValue_Positional()
    {
        var tokens = DrainTokens("x:{a:str b:int} = { 'hi' 42 }");
        // StatementName, TypeAnnotationStart, TypeAnnotationEnd, AssignOperator
        AssertToken(tokens, 4, PaktTokenType.StructStart);
        AssertToken(tokens, 5, PaktTokenType.String, "'hi'");
        AssertToken(tokens, 6, PaktTokenType.Int, "42");
        AssertToken(tokens, 7, PaktTokenType.StructEnd);
    }

    [Fact]
    public void TupleValue()
    {
        var tokens = DrainTokens("x:(int int) = (1 2)");
        AssertToken(tokens, 4, PaktTokenType.TupleStart);
        AssertToken(tokens, 5, PaktTokenType.Int, "1");
        AssertToken(tokens, 6, PaktTokenType.Int, "2");
        AssertToken(tokens, 7, PaktTokenType.TupleEnd);
    }

    [Fact]
    public void ListValue()
    {
        var tokens = DrainTokens("x:[int] = [1 2 3]");
        // StatementName, TypeAnnotationStart, TypeAnnotationEnd, AssignOperator
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
        AssertToken(tokens, 4, PaktTokenType.ListStart);
        AssertToken(tokens, 5, PaktTokenType.Int, "1");
        AssertToken(tokens, 6, PaktTokenType.Int, "2");
        AssertToken(tokens, 7, PaktTokenType.Int, "3");
        AssertToken(tokens, 8, PaktTokenType.ListEnd);
    }

    [Fact]
    public void EmptyList()
    {
        var tokens = DrainTokens("x:[int] = []");
        int i = tokens.FindIndex(t => t.Type == PaktTokenType.ListStart);
        Assert.True(i >= 0);
        AssertToken(tokens, i + 1, PaktTokenType.ListEnd);
    }

    [Fact]
    public void MapValue()
    {
        var tokens = DrainTokens("x:<str => int> = <'a' => 1>");
        int i = tokens.FindIndex(t => t.Type == PaktTokenType.MapStart);
        Assert.True(i >= 0);
        AssertToken(tokens, i++, PaktTokenType.MapStart);
        AssertToken(tokens, i++, PaktTokenType.String, "'a'");
        AssertToken(tokens, i++, PaktTokenType.MapEntryBind, "=>");
        AssertToken(tokens, i++, PaktTokenType.Int, "1");
        AssertToken(tokens, i++, PaktTokenType.MapEnd);
    }

    [Fact]
    public void EmptyMap()
    {
        var tokens = DrainTokens("x:<str => int> = <>");
        int i = tokens.FindIndex(t => t.Type == PaktTokenType.MapStart);
        Assert.True(i >= 0);
        AssertToken(tokens, i + 1, PaktTokenType.MapEnd);
    }

    [Fact]
    public void NestedStruct()
    {
        var tokens = DrainTokens("x:{pos:{x:int y:int}} = { { 1 2 } }");
        int i = tokens.FindIndex(t => t.Type == PaktTokenType.StructStart);
        Assert.True(i >= 0);
        AssertToken(tokens, i++, PaktTokenType.StructStart);   // outer
        AssertToken(tokens, i++, PaktTokenType.StructStart);   // inner
        AssertToken(tokens, i++, PaktTokenType.Int, "1");
        AssertToken(tokens, i++, PaktTokenType.Int, "2");
        AssertToken(tokens, i++, PaktTokenType.StructEnd);     // inner
        AssertToken(tokens, i++, PaktTokenType.StructEnd);     // outer
    }

    [Fact]
    public void NestedStruct_StringAfterNested()
    {
        var tokens = DrainTokens(
            "x:{a:{b:int c:int} d:str} = { { 1 2 } 'hello' }");
        int i = tokens.FindIndex(t => t.Type == PaktTokenType.StructStart);
        Assert.True(i >= 0);
        AssertToken(tokens, i++, PaktTokenType.StructStart);   // outer
        AssertToken(tokens, i++, PaktTokenType.StructStart);   // inner
        AssertToken(tokens, i++, PaktTokenType.Int, "1");
        AssertToken(tokens, i++, PaktTokenType.Int, "2");
        AssertToken(tokens, i++, PaktTokenType.StructEnd);     // inner
        AssertToken(tokens, i++, PaktTokenType.String, "'hello'");
        AssertToken(tokens, i++, PaktTokenType.StructEnd);     // outer
    }

    [Fact]
    public void NestedStruct_StringInsideNested()
    {
        var tokens = DrainTokens(
            "x:{a:{b:str c:int} d:int} = { { 'hi' 2 } 3 }");
        int i = tokens.FindIndex(t => t.Type == PaktTokenType.StructStart);
        Assert.True(i >= 0);
        AssertToken(tokens, i++, PaktTokenType.StructStart);   // outer
        AssertToken(tokens, i++, PaktTokenType.StructStart);   // inner
        AssertToken(tokens, i++, PaktTokenType.String, "'hi'");
        AssertToken(tokens, i++, PaktTokenType.Int, "2");
        AssertToken(tokens, i++, PaktTokenType.StructEnd);     // inner
        AssertToken(tokens, i++, PaktTokenType.Int, "3");
        AssertToken(tokens, i++, PaktTokenType.StructEnd);     // outer
    }

    [Fact]
    public void NestedStruct_MultiMemberWithStrings()
    {
        var tokens = DrainTokens(
            "x:{server:{host:str port:int} db:{host:str port:int name:str}} = { { 'api.example.com' 443 } { 'db.internal' 5432 'myapp' } }");
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    // ── Packs ───────────────────────────────────────────────────────

    [Fact]
    public void ListPack_TailEof()
    {
        // Tail pack: terminated by EOF (no semicolon required)
        var tokens = DrainTokens("items:[int] << 1 2 3");
        AssertToken(tokens, 0, PaktTokenType.StatementName, "items");
        AssertToken(tokens, 3, PaktTokenType.PackOperator, "<<");
        AssertToken(tokens, 4, PaktTokenType.Int, "1");
        AssertToken(tokens, 5, PaktTokenType.Int, "2");
        AssertToken(tokens, 6, PaktTokenType.Int, "3");
        AssertToken(tokens, 7, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void ListPack_WithSemicolon()
    {
        var tokens = DrainTokens("items:[int] << 1 2 3;");
        AssertToken(tokens, 0, PaktTokenType.StatementName, "items");
        AssertToken(tokens, 3, PaktTokenType.PackOperator, "<<");
        AssertToken(tokens, 4, PaktTokenType.Int, "1");
        AssertToken(tokens, 5, PaktTokenType.Int, "2");
        AssertToken(tokens, 6, PaktTokenType.Int, "3");
        AssertToken(tokens, 7, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void ListPack_ConsecutiveSemicolons()
    {
        // Run of semicolons is consumed as single terminator
        var tokens = DrainTokens("items:[int] << 1 2;;;");
        AssertToken(tokens, 4, PaktTokenType.Int, "1");
        AssertToken(tokens, 5, PaktTokenType.Int, "2");
        AssertToken(tokens, 6, PaktTokenType.EndOfUnit);
    }

    [Fact]
    public void EmptyPack_Throws()
    {
        Assert.Throws<PaktParseException>(
            () => DrainTokens("items:[int] <<"));
    }

    [Fact]
    public void EmptyPackWithSemicolon_Throws()
    {
        Assert.Throws<PaktParseException>(
            () => DrainTokens("items:[int] <<;"));
    }

    [Fact]
    public void PackTerminatedBySemicolon_ThenNextStatement()
    {
        var tokens = DrainTokens("items:[int] << 1 2;\nnext:str = 'x'");
        int nextIdx = tokens.FindIndex(
            t => t.Type == PaktTokenType.StatementName && string.Equals(t.Value, "next", StringComparison.Ordinal));
        Assert.True(nextIdx >= 0);
        // Pack values precede the next statement
        AssertToken(tokens, nextIdx - 2, PaktTokenType.Int, "1");
        AssertToken(tokens, nextIdx - 1, PaktTokenType.Int, "2");
        AssertToken(tokens, nextIdx, PaktTokenType.StatementName, "next");
    }

    // ── Atom values ─────────────────────────────────────────────────

    [Fact]
    public void AtomValue_NotYetSupported()
    {
        // v7 lexer produces AtomPrefix for |ident, so atom set types
        // are not yet parseable by PaktMemoryReader.
        Assert.Throws<PaktParseException>(
            () => DrainTokens("x:|dev staging| = |dev"));
    }

    // ── Multiple statements ─────────────────────────────────────────

    [Fact]
    public void TwoStatements()
    {
        var tokens = DrainTokens("a:int = 1\nb:str = 'x'");
        var stmts = tokens
            .Select((t, i) => (t, i))
            .Where(x => x.t.Type == PaktTokenType.StatementName)
            .Select(x => x.i)
            .ToList();
        Assert.Equal(2, stmts.Count);
        AssertToken(tokens, stmts[0], PaktTokenType.StatementName, "a");
        AssertToken(tokens, stmts[1], PaktTokenType.StatementName, "b");
    }

    // ── Read pull-model specific ────────────────────────────────────

    [Fact]
    public void Read_StopAfterFirstToken()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("x:int = 42");
        using var reader = new PaktMemoryReader(new ReadOnlyMemory<byte>(bytes));
        bool result = reader.Read();
        Assert.True(result);
        Assert.Equal(PaktTokenType.StatementName, reader.TokenType);
    }

    // ── NUL framing ─────────────────────────────────────────────────

    [Fact]
    public void NulTerminatesUnit()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("x:int = 1"), 0x00, .. Encoding.UTF8.GetBytes("ignored")];
        var tokens = DrainTokens(bytes);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
        Assert.DoesNotContain(
            tokens,
            t => string.Equals(t.Value, "ignored", StringComparison.Ordinal));
    }

    // ── Error cases ─────────────────────────────────────────────────

    [Fact]
    public void NoLayoutAroundOperator_ParsesSuccessfully()
    {
        // v7 reader does not enforce layout around operators.
        var tokens = DrainTokens("x:str='hi'");
        AssertToken(tokens, 0, PaktTokenType.StatementName, "x");
        AssertToken(tokens, 3, PaktTokenType.AssignOperator, "=");
        AssertToken(tokens, 4, PaktTokenType.String, "'hi'");
    }

    [Fact]
    public void Error_TypeMismatch_IntGetsString()
    {
        Assert.Throws<PaktParseException>(
            () => DrainTokens("x:int = 'hello'"));
    }

    [Fact]
    public void Error_ArityMismatch_TooFewFields()
    {
        Assert.Throws<PaktParseException>(
            () => DrainTokens("x:{a:int b:int} = { 1 }"));
    }

    // ── Stream reader (NUL-framed) ──────────────────────────────────

    [Fact]
    public async Task StreamReader_SingleUnit()
    {
        byte[] data = Encoding.UTF8.GetBytes("x:int = 42");
        using var stream = new MemoryStream(data);
        await using var reader = new PaktStreamReader(stream);

        Assert.True(await reader.ReadUnitAsync(TestContext.Current.CancellationToken));

        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string val = reader.ValueSpan.IsEmpty ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            tokens.Add((reader.TokenType, val));
        }

        AssertToken(tokens, 0, PaktTokenType.StatementName, "x");
        AssertToken(tokens, 4, PaktTokenType.Int, "42");

        Assert.False(await reader.ReadUnitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamReader_NulFramedUnits()
    {
        // Two units separated by NUL
        byte[] unit1 = Encoding.UTF8.GetBytes("a:int = 1");
        byte[] unit2 = Encoding.UTF8.GetBytes("b:str = 'hi'");
        byte[] data = [..unit1, 0x00, ..unit2];

        using var stream = new MemoryStream(data);
        await using var reader = new PaktStreamReader(stream);

        // First unit
        Assert.True(await reader.ReadUnitAsync(TestContext.Current.CancellationToken));
        var tokens1 = DrainStreamTokens(reader);
        AssertToken(tokens1, 0, PaktTokenType.StatementName, "a");

        // Second unit
        Assert.True(await reader.ReadUnitAsync(TestContext.Current.CancellationToken));
        var tokens2 = DrainStreamTokens(reader);
        AssertToken(tokens2, 0, PaktTokenType.StatementName, "b");

        Assert.False(await reader.ReadUnitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamReader_EmptyStream()
    {
        using var stream = new MemoryStream([]);
        await using var reader = new PaktStreamReader(stream);
        Assert.False(await reader.ReadUnitAsync(TestContext.Current.CancellationToken));
    }

    private static List<(PaktTokenType Type, string Value)> DrainStreamTokens(PaktStreamReader reader)
    {
        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string val = reader.ValueSpan.IsEmpty ? "" : Encoding.UTF8.GetString(reader.ValueSpan);
            tokens.Add((reader.TokenType, val));
        }
        return tokens;
    }
}
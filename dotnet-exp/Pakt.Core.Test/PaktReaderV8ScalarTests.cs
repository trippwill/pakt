using System.Buffers;
using System.Text;

using Pakt;

namespace Pakt.Core.Test;

/// <summary>
/// Tests exercising all PAKT scalar types through the v8 PaktSequenceReader typed accessors.
/// Highlights types where JSON must use string workarounds.
/// </summary>
public class PaktReaderV8ScalarTests
{
    // ── String ──────────────────────────────────────────────────────

    [Fact]
    public void String_Simple()
    {
        var reader = MakeReader("x:str = 'hello world'");
        AdvanceToValue(ref reader);
        Assert.Equal("hello world", reader.GetString());
    }

    [Fact]
    public void String_WithEscapes()
    {
        var reader = MakeReader("x:str = 'line1\\nline2\\ttab'");
        AdvanceToValue(ref reader);
        Assert.Equal("line1\nline2\ttab", reader.GetString());
    }

    [Fact]
    public void String_Raw()
    {
        var reader = MakeReader("x:str = r'no\\escape\\here'");
        AdvanceToValue(ref reader);
        Assert.Equal("no\\escape\\here", reader.GetString());
    }

    [Fact]
    public void String_UnicodeEscape()
    {
        var reader = MakeReader("x:str = '\\u0041\\u0042'");
        AdvanceToValue(ref reader);
        Assert.Equal("AB", reader.GetString());
    }

    // ── Int ─────────────────────────────────────────────────────────
    // JSON: exact for int32, but int64 > 2^53 loses precision in JavaScript

    [Fact]
    public void Int_Simple()
    {
        var reader = MakeReader("x:int = 42");
        AdvanceToValue(ref reader);
        Assert.Equal(42, reader.GetInt32());
    }

    [Fact]
    public void Int_Negative()
    {
        var reader = MakeReader("x:int = -100");
        AdvanceToValue(ref reader);
        Assert.Equal(-100, reader.GetInt32());
    }

    [Fact]
    public void Int_Hex()
    {
        // JSON has no hex literals — must use decimal or string
        var reader = MakeReader("x:int = 0xFF");
        AdvanceToValue(ref reader);
        Assert.Equal(255, reader.GetInt32());
    }

    [Fact]
    public void Int_Binary()
    {
        // JSON has no binary literals
        var reader = MakeReader("x:int = 0b11001010");
        AdvanceToValue(ref reader);
        Assert.Equal(0b11001010, reader.GetInt32());
    }

    [Fact]
    public void Int_Octal()
    {
        // JSON has no octal literals
        var reader = MakeReader("x:int = 0o755");
        AdvanceToValue(ref reader);
        Assert.Equal(493, reader.GetInt32()); // 0o755 = 493
    }

    [Fact]
    public void Int_WithUnderscores()
    {
        // JSON has no digit separators
        var reader = MakeReader("x:int = 1_000_000");
        AdvanceToValue(ref reader);
        Assert.Equal(1_000_000, reader.GetInt32());
    }

    [Fact]
    public void Int64_Large()
    {
        // JSON: JavaScript loses precision above 2^53
        var reader = MakeReader("x:int = 9007199254740993");
        AdvanceToValue(ref reader);
        Assert.Equal(9007199254740993L, reader.GetInt64());
    }

    // ── Decimal ─────────────────────────────────────────────────────
    // JSON: no decimal type — all numbers are IEEE 754 double

    [Fact]
    public void Decimal_Simple()
    {
        var reader = MakeReader("x:dec = 3.14");
        AdvanceToValue(ref reader);
        Assert.Equal(3.14m, reader.GetDecimal());
    }

    [Fact]
    public void Decimal_FinancialPrecision()
    {
        // JSON double: 0.1 + 0.2 = 0.30000000000000004
        // PAKT decimal: exact
        var reader = MakeReader("x:dec = 0.30");
        AdvanceToValue(ref reader);
        Assert.Equal(0.30m, reader.GetDecimal());
    }

    // ── Float ───────────────────────────────────────────────────────

    [Fact]
    public void Float_Scientific()
    {
        var reader = MakeReader("x:float = 6.022e23");
        AdvanceToValue(ref reader);
        Assert.Equal(6.022e23, reader.GetDouble(), precision: 5);
    }

    [Fact]
    public void Float_NegativeExponent()
    {
        var reader = MakeReader("x:float = 1.602e-19");
        AdvanceToValue(ref reader);
        Assert.Equal(1.602e-19, reader.GetDouble(), precision: 5);
    }

    // ── Bool ────────────────────────────────────────────────────────

    [Fact]
    public void Bool_True()
    {
        var reader = MakeReader("x:bool = true");
        AdvanceToValue(ref reader);
        Assert.True(reader.GetBool());
    }

    [Fact]
    public void Bool_False()
    {
        var reader = MakeReader("x:bool = false");
        AdvanceToValue(ref reader);
        Assert.False(reader.GetBool());
    }

    // ── UUID ────────────────────────────────────────────────────────
    // JSON: must use string "550e8400-e29b-41d4-a716-446655440000"

    [Fact]
    public void Uuid_Standard()
    {
        var reader = MakeReader("x:uuid = 550e8400-e29b-41d4-a716-446655440000");
        AdvanceToValue(ref reader);
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), reader.GetGuid());
    }

    // ── Date ────────────────────────────────────────────────────────
    // JSON: must use string "2026-06-15"

    [Fact]
    public void Date_Simple()
    {
        var reader = MakeReader("x:date = 2026-06-15");
        AdvanceToValue(ref reader);
        Assert.Equal(new DateOnly(2026, 6, 15), reader.GetDate());
    }

    // ── Timestamp ───────────────────────────────────────────────────
    // JSON: must use string "2026-06-15T14:30:00Z"

    [Fact]
    public void Timestamp_Utc()
    {
        var reader = MakeReader("x:ts = 2026-06-15T14:30:00Z");
        AdvanceToValue(ref reader);
        var ts = reader.GetTimestamp();
        Assert.Equal(2026, ts.Year);
        Assert.Equal(6, ts.Month);
        Assert.Equal(15, ts.Day);
        Assert.Equal(14, ts.Hour);
        Assert.Equal(30, ts.Minute);
        Assert.Equal(TimeSpan.Zero, ts.Offset);
    }

    [Fact]
    public void Timestamp_WithOffset()
    {
        var reader = MakeReader("x:ts = 2026-06-15T14:30:00+05:30");
        AdvanceToValue(ref reader);
        var ts = reader.GetTimestamp();
        Assert.Equal(new TimeSpan(5, 30, 0), ts.Offset);
    }

    [Fact]
    public void Timestamp_WithFractionalSeconds()
    {
        var reader = MakeReader("x:ts = 2026-06-15T14:30:00.123456Z");
        AdvanceToValue(ref reader);
        var ts = reader.GetTimestamp();
        Assert.Equal(123, ts.Millisecond);
    }

    // ── Binary ──────────────────────────────────────────────────────
    // JSON: must use base64 string — PAKT has native hex and base64 literals

    [Fact]
    public void Binary_Hex()
    {
        var reader = MakeReader("x:bin = x'48656c6c6f'");
        AdvanceToValue(ref reader);
        byte[] bytes = reader.GetBytes();
        Assert.Equal("Hello"u8.ToArray(), bytes);
    }

    [Fact]
    public void Binary_Base64()
    {
        var reader = MakeReader("x:bin = b'SGVsbG8='");
        AdvanceToValue(ref reader);
        byte[] bytes = reader.GetBytes();
        Assert.Equal("Hello"u8.ToArray(), bytes);
    }

    [Fact]
    public void Binary_EmptyHex()
    {
        var reader = MakeReader("x:bin = x''");
        AdvanceToValue(ref reader);
        Assert.Empty(reader.GetBytes());
    }

    // ── Nil ─────────────────────────────────────────────────────────

    [Fact]
    public void Nil_Value()
    {
        var reader = MakeReader("x:str? = nil");
        AdvanceToValue(ref reader);
        Assert.Equal(PaktTokenType.Nil, reader.TokenType);
    }

    // ── Accessor Guard Tests ────────────────────────────────────────

    [Fact]
    public void Guard_GetStringOnInt_Throws()
    {
        var reader = MakeReader("x:int = 42");
        AdvanceToValue(ref reader);
        Assert.Equal(PaktTokenType.Int, reader.TokenType);
        try { reader.GetString(); Assert.Fail("Expected PaktParseException"); }
        catch (PaktParseException) { }
    }

    [Fact]
    public void Guard_GetInt32OnString_Throws()
    {
        var reader = MakeReader("x:str = 'hello'");
        AdvanceToValue(ref reader);
        Assert.Equal(PaktTokenType.String, reader.TokenType);
        try { reader.GetInt32(); Assert.Fail("Expected PaktParseException"); }
        catch (PaktParseException) { }
    }

    [Fact]
    public void Guard_GetBoolOnInt_Throws()
    {
        var reader = MakeReader("x:int = 42");
        AdvanceToValue(ref reader);
        try { reader.GetBool(); Assert.Fail("Expected PaktParseException"); }
        catch (PaktParseException) { }
    }

    [Fact]
    public void Guard_GetGuidOnString_Throws()
    {
        var reader = MakeReader("x:str = 'not-a-uuid'");
        AdvanceToValue(ref reader);
        try { reader.GetGuid(); Assert.Fail("Expected PaktParseException"); }
        catch (PaktParseException) { }
    }

    [Fact]
    public void Guard_TryGetInt32OnString_ReturnsFalse()
    {
        var reader = MakeReader("x:str = 'hello'");
        AdvanceToValue(ref reader);
        Assert.False(reader.TryGetInt32(out _));
    }

    [Fact]
    public void Guard_GetDoubleAcceptsInt()
    {
        // Pragmatic widening: GetDouble accepts Int tokens
        var reader = MakeReader("x:int = 42");
        AdvanceToValue(ref reader);
        Assert.Equal(42.0, reader.GetDouble());
    }

    [Fact]
    public void Guard_GetDecimalAcceptsInt()
    {
        var reader = MakeReader("x:int = 42");
        AdvanceToValue(ref reader);
        Assert.Equal(42m, reader.GetDecimal());
    }

    // ── All-Scalars Composite ───────────────────────────────────────
    // One struct with every scalar type — the "JSON can't do this natively" test

    [Fact]
    public void AllScalars_InOneStruct()
    {
        string pakt = """
            record:{name:str count:int price:dec ratio:float active:bool id:uuid created:date modified:ts hash:bin} = {
                'widget'
                42
                19.99
                3.14e0
                true
                550e8400-e29b-41d4-a716-446655440000
                2026-06-15
                2026-06-15T14:30:00Z
                x'deadbeef'
            }
            """;

        var reader = MakeReader(pakt);
        AdvanceToValue(ref reader);

        // AdvanceToValue positioned us on StructStart
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        // name:str
        Assert.True(reader.Read());
        Assert.Equal("widget", reader.GetString());

        // count:int
        Assert.True(reader.Read());
        Assert.Equal(42, reader.GetInt32());

        // price:dec
        Assert.True(reader.Read());
        Assert.Equal(19.99m, reader.GetDecimal());

        // ratio:float
        Assert.True(reader.Read());
        Assert.Equal(3.14, reader.GetDouble(), precision: 5);

        // active:bool
        Assert.True(reader.Read());
        Assert.True(reader.GetBool());

        // id:uuid — JSON would need: "id": "550e8400-..."
        Assert.True(reader.Read());
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), reader.GetGuid());

        // created:date — JSON would need: "created": "2026-06-15"
        Assert.True(reader.Read());
        Assert.Equal(new DateOnly(2026, 6, 15), reader.GetDate());

        // modified:ts — JSON would need: "modified": "2026-06-15T14:30:00Z"
        Assert.True(reader.Read());
        var ts = reader.GetTimestamp();
        Assert.Equal(2026, ts.Year);

        // hash:bin — JSON would need: "hash": "3q2+7w==" (base64 string)
        Assert.True(reader.Read());
        Assert.Equal([0xde, 0xad, 0xbe, 0xef], reader.GetBytes());

        // StructEnd
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.StructEnd, reader.TokenType);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static PaktSequenceReader MakeReader(string pakt)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(pakt);
        return new PaktSequenceReader(new ReadOnlySequence<byte>(bytes), isFinalBlock: true);
    }

    /// <summary>Advance past statement header to the first value token.</summary>
    private static void AdvanceToValue(ref PaktSequenceReader reader)
    {
        // StatementName → TypeAnnotationStart → AssignOperator → value
        Assert.True(reader.Read()); // StatementName
        Assert.True(reader.Read()); // TypeAnnotationStart
        Assert.True(reader.Read()); // AssignOperator (v8 doesn't emit TypeAnnotationEnd)
        Assert.True(reader.Read()); // The value token
    }
}
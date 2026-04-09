using System.Text;

namespace Pakt.Tests.ReaderTests;

public class ScalarReaderTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void String_SingleQuoted()
    {
        var reader = new PaktReader(ToUtf8("s:str = 'hello world'"));
        reader.Read(); reader.Read();
        Assert.Equal("hello world", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void String_DoubleQuoted()
    {
        var reader = new PaktReader(ToUtf8("s:str = \"hello world\""));
        reader.Read(); reader.Read();
        Assert.Equal("hello world", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void String_EscapeSequences()
    {
        var reader = new PaktReader(ToUtf8("s:str = 'hello\\nworld\\t!'"));
        reader.Read(); reader.Read();
        Assert.Equal("hello\nworld\t!", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void String_EscapedQuote()
    {
        var reader = new PaktReader(ToUtf8("s:str = 'it\\'s'"));
        reader.Read(); reader.Read();
        Assert.Equal("it's", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void String_Raw()
    {
        var reader = new PaktReader(ToUtf8("s:str = r'hello\\nworld'"));
        reader.Read(); reader.Read();
        Assert.Equal("hello\\nworld", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void String_Multiline()
    {
        var input = "s:str = '''\n  hello\n  world\n  '''";
        var reader = new PaktReader(ToUtf8(input));
        reader.Read(); reader.Read();
        Assert.Equal("hello\nworld", reader.GetString());
        reader.Dispose();
    }

    [Theory]
    [InlineData("n:int = 42", 42L)]
    [InlineData("n:int = -7", -7L)]
    [InlineData("n:int = 0", 0L)]
    [InlineData("n:int = 1_000", 1000L)]
    public void Int_Decimal(string input, long expected)
    {
        var reader = new PaktReader(ToUtf8(input));
        reader.Read(); reader.Read();
        Assert.Equal(expected, reader.GetInt64());
        reader.Dispose();
    }

    [Fact]
    public void Int_Hex()
    {
        var reader = new PaktReader(ToUtf8("n:int = 0xFF"));
        reader.Read(); reader.Read();
        Assert.Equal(255L, reader.GetInt64());
        reader.Dispose();
    }

    [Fact]
    public void Int_Binary()
    {
        var reader = new PaktReader(ToUtf8("n:int = 0b1010"));
        reader.Read(); reader.Read();
        Assert.Equal(10L, reader.GetInt64());
        reader.Dispose();
    }

    [Fact]
    public void Int_Octal()
    {
        var reader = new PaktReader(ToUtf8("n:int = 0o77"));
        reader.Read(); reader.Read();
        Assert.Equal(63L, reader.GetInt64());
        reader.Dispose();
    }

    [Theory]
    [InlineData("n:dec = 3.14", 3.14)]
    [InlineData("n:dec = -0.5", -0.5)]
    [InlineData("n:dec = 1_000.50", 1000.50)]
    public void Dec_Values(string input, double expected)
    {
        var reader = new PaktReader(ToUtf8(input));
        reader.Read(); reader.Read();
        Assert.Equal((decimal)expected, reader.GetDecimal());
        reader.Dispose();
    }

    [Theory]
    [InlineData("n:float = 6.022e23", 6.022e23)]
    [InlineData("n:float = -1.5E-10", -1.5e-10)]
    public void Float_Values(string input, double expected)
    {
        var reader = new PaktReader(ToUtf8(input));
        reader.Read(); reader.Read();
        Assert.Equal(expected, reader.GetDouble());
        reader.Dispose();
    }

    [Fact]
    public void Bool_True()
    {
        var reader = new PaktReader(ToUtf8("b:bool = true"));
        reader.Read(); reader.Read();
        Assert.True(reader.GetBoolean());
        reader.Dispose();
    }

    [Fact]
    public void Bool_False()
    {
        var reader = new PaktReader(ToUtf8("b:bool = false"));
        reader.Read(); reader.Read();
        Assert.False(reader.GetBoolean());
        reader.Dispose();
    }

    [Fact]
    public void Uuid_Value()
    {
        var reader = new PaktReader(ToUtf8("id:uuid = 550e8400-e29b-41d4-a716-446655440000"));
        reader.Read(); reader.Read();
        Assert.Equal(Guid.Parse("550e8400-e29b-41d4-a716-446655440000"), reader.GetGuid());
        reader.Dispose();
    }

    [Fact]
    public void Date_Value()
    {
        var reader = new PaktReader(ToUtf8("d:date = 2026-06-01"));
        reader.Read(); reader.Read();
        Assert.Equal(new DateOnly(2026, 6, 1), reader.GetDate());
        reader.Dispose();
    }

    [Fact]
    public void Timestamp_WithZ()
    {
        var reader = new PaktReader(ToUtf8("t:ts = 2026-06-01T14:30:00Z"));
        reader.Read(); reader.Read();
        var time = reader.GetTimestamp();
        Assert.Equal(2026, time.Year);
        Assert.Equal(6, time.Month);
        Assert.Equal(1, time.Day);
        Assert.Equal(14, time.Hour);
        Assert.Equal(30, time.Minute);
        Assert.Equal(TimeSpan.Zero, time.Offset);
        reader.Dispose();
    }

    [Fact]
    public void Timestamp_WithOffset()
    {
        var reader = new PaktReader(ToUtf8("t:ts = 2026-06-01T14:30:00-04:00"));
        reader.Read(); reader.Read();
        var time = reader.GetTimestamp();
        Assert.Equal(2026, time.Year);
        Assert.Equal(6, time.Month);
        Assert.Equal(1, time.Day);
        Assert.Equal(14, time.Hour);
        Assert.Equal(30, time.Minute);
        Assert.Equal(TimeSpan.FromHours(-4), time.Offset);
        reader.Dispose();
    }

    [Fact]
    public void Timestamp_Value()
    {
        var reader = new PaktReader(ToUtf8("dt:ts = 2026-06-01T14:30:00Z"));
        reader.Read(); reader.Read();
        var dt = reader.GetTimestamp();
        Assert.Equal(2026, dt.Year);
        Assert.Equal(6, dt.Month);
        Assert.Equal(1, dt.Day);
        Assert.Equal(14, dt.Hour);
        Assert.Equal(30, dt.Minute);
        reader.Dispose();
    }

    [Fact]
    public void Bin_Hex()
    {
        var reader = new PaktReader(ToUtf8("data:bin = x'48656C6C6F'"));
        reader.Read(); reader.Read();
        var dest = new byte[5];
        int written = reader.GetBytesFromBin(dest);
        Assert.Equal(5, written);
        Assert.Equal("Hello"u8.ToArray(), dest);
        reader.Dispose();
    }

    [Fact]
    public void Bin_Base64()
    {
        var reader = new PaktReader(ToUtf8("data:bin = b'SGVsbG8='"));
        reader.Read(); reader.Read();
        var dest = new byte[5];
        int written = reader.GetBytesFromBin(dest);
        Assert.Equal(5, written);
        Assert.Equal("Hello"u8.ToArray(), dest);
        reader.Dispose();
    }

    [Fact]
    public void Atom_Value()
    {
        var reader = new PaktReader(ToUtf8("env:|dev, staging, prod| = |prod"));
        reader.Read(); reader.Read();
        Assert.Equal(PaktScalarType.Atom, reader.ScalarType);
        Assert.Equal("prod", reader.GetAtom());
        reader.Dispose();
    }

    [Fact]
    public void Nil_ForNullableType()
    {
        var reader = new PaktReader(ToUtf8("name:str? = nil"));
        reader.Read();
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        reader.Read();
        Assert.Equal(PaktTokenType.Nil, reader.TokenType);
        Assert.True(reader.IsNullValue);

        reader.Read();
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }
}

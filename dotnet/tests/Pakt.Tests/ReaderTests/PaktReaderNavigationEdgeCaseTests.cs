using System.Text;
using Pakt;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests.ReaderTests;

public class PaktReaderNavigationEdgeCaseTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void StructFields_EmptyStruct()
    {
        var reader = new PaktReader(ToUtf8("cfg:{} = {}"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        var count = 0;
        reader.StructFields((ref PaktReader r, PaktFieldEntry f) => { count++; return true; });
        Assert.Equal(0, count);
        reader.Dispose();
    }

    [Fact]
    public void TupleElements_EarlyBreak()
    {
        var reader = new PaktReader(ToUtf8("t:(int, int, int) = (10, 20, 30)\nname:str = 'after'"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.TupleStart, reader.TokenType);

        var first = 0;
        reader.TupleElements((ref PaktReader r, PaktTupleEntry e) =>
        {
            first = (int)r.GetInt64();
            return false;
        });

        Assert.Equal(10, first);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal("after", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void ListElements_EmptyList()
    {
        var reader = new PaktReader(ToUtf8("items:[int] = []"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);

        var count = 0;
        reader.ListElements<int>(TestPaktContext.Default, _ => { count++; return true; });
        Assert.Equal(0, count);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        reader.Dispose();
    }

    [Fact]
    public void MapEntries_EmptyMap()
    {
        var reader = new PaktReader(ToUtf8("m:<str ; int> = <>"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.MapStart, reader.TokenType);

        var count = 0;
        reader.MapEntries<string, int>(TestPaktContext.Default, _ => { count++; return true; });
        Assert.Equal(0, count);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        reader.Dispose();
    }

    [Fact]
    public void SkipValue_CompositeList()
    {
        var reader = new PaktReader(ToUtf8("items:[int] = [1, 2, 3]\nname:str = 'after'"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);
        reader.SkipValue();
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        reader.Dispose();
    }

    [Fact]
    public void SkipValue_CompositeMap()
    {
        var reader = new PaktReader(ToUtf8("m:<str ; int> = <'a' ; 1, 'b' ; 2>\nname:str = 'after'"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.MapStart, reader.TokenType);
        reader.SkipValue();
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        reader.Dispose();
    }

    [Fact]
    public void SkipValue_CompositeTuple()
    {
        var reader = new PaktReader(ToUtf8("t:(int, str) = (1, 'two')\nname:str = 'after'"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.TupleStart, reader.TokenType);
        reader.SkipValue();
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        reader.Dispose();
    }

    [Fact]
    public void StructFields_WrongToken_Throws()
    {
        var reader = new PaktReader(ToUtf8("n:int = 42"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);

        static bool Handler(ref PaktReader r, PaktFieldEntry f) => true;
        try
        {
            reader.StructFields(Handler);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException) { }
        reader.Dispose();
    }

    [Fact]
    public void TupleElements_WrongToken_Throws()
    {
        var reader = new PaktReader(ToUtf8("n:int = 42"));
        Assert.True(reader.Read());
        Assert.True(reader.Read());

        static bool Handler(ref PaktReader r, PaktTupleEntry e) => true;
        try
        {
            reader.TupleElements(Handler);
            Assert.Fail("Expected InvalidOperationException");
        }
        catch (InvalidOperationException) { }
        reader.Dispose();
    }
}

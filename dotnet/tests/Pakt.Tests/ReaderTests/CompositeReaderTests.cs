using System.Text;

namespace Pakt.Tests.ReaderTests;

public class CompositeReaderTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Struct_SimpleFields()
    {
        var data = ToUtf8("config:{host:str, port:int} = { 'localhost', 8080 }");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        Assert.True(reader.Read()); // StructStart
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        Assert.True(reader.Read()); // ScalarValue 'localhost'
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal("host", reader.CurrentName);
        Assert.Equal("localhost", reader.GetString());

        Assert.True(reader.Read()); // ScalarValue 8080
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal("port", reader.CurrentName);
        Assert.Equal(8080L, reader.GetInt64());

        Assert.True(reader.Read()); // StructEnd
        Assert.Equal(PaktTokenType.StructEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignEnd
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Struct_Nested()
    {
        var data = ToUtf8("outer:{inner:{val:str}} = { { 'hello' } }");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // outer StructStart
        Assert.True(reader.Read()); // inner StructStart
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);
        Assert.True(reader.Read()); // ScalarValue
        Assert.Equal("hello", reader.GetString());
        Assert.True(reader.Read()); // inner StructEnd
        Assert.Equal(PaktTokenType.StructEnd, reader.TokenType);
        Assert.True(reader.Read()); // outer StructEnd
        Assert.Equal(PaktTokenType.StructEnd, reader.TokenType);
        Assert.True(reader.Read()); // AssignEnd

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Tuple_Simple()
    {
        var data = ToUtf8("pair:(int, str) = (42, 'hello')");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // TupleStart
        Assert.Equal(PaktTokenType.TupleStart, reader.TokenType);

        Assert.True(reader.Read()); // ScalarValue 42
        Assert.Equal(42L, reader.GetInt64());
        Assert.Equal("[0]", reader.CurrentName);

        Assert.True(reader.Read()); // ScalarValue 'hello'
        Assert.Equal("hello", reader.GetString());
        Assert.Equal("[1]", reader.CurrentName);

        Assert.True(reader.Read()); // TupleEnd
        Assert.Equal(PaktTokenType.TupleEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignEnd

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void List_WithElements()
    {
        var data = ToUtf8("nums:[int] = [1, 2, 3]");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // ListStart
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);

        Assert.True(reader.Read()); Assert.Equal(1L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(2L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());

        Assert.True(reader.Read()); // ListEnd
        Assert.Equal(PaktTokenType.ListEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void List_Empty()
    {
        var data = ToUtf8("nums:[int] = []");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // ListStart
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);
        Assert.True(reader.Read()); // ListEnd
        Assert.Equal(PaktTokenType.ListEnd, reader.TokenType);
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Map_Simple()
    {
        var data = ToUtf8("m:<str ; int> = <'a' ; 1, 'b' ; 2>");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // MapStart
        Assert.Equal(PaktTokenType.MapStart, reader.TokenType);

        Assert.True(reader.Read()); // key 'a'
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal("a", reader.GetString());

        Assert.True(reader.Read()); // value 1
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal(1L, reader.GetInt64());
        Assert.Equal("a", reader.CurrentName);

        Assert.True(reader.Read()); // key 'b'
        Assert.Equal("b", reader.GetString());

        Assert.True(reader.Read()); // value 2
        Assert.Equal(2L, reader.GetInt64());
        Assert.Equal("b", reader.CurrentName);

        Assert.True(reader.Read()); // MapEnd
        Assert.Equal(PaktTokenType.MapEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Map_Empty()
    {
        var data = ToUtf8("m:<str ; int> = <>");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // MapStart
        Assert.True(reader.Read()); // MapEnd
        Assert.Equal(PaktTokenType.MapEnd, reader.TokenType);
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Nested_ListOfStructs()
    {
        var data = ToUtf8("items:[{name:str, count:int}] = [{ 'apple', 5 }, { 'banana', 3 }]");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // ListStart

        Assert.True(reader.Read()); // StructStart
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);
        Assert.True(reader.Read()); Assert.Equal("apple", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal(5L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal("banana", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // ListEnd
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Map_WithStructValues()
    {
        var data = ToUtf8("users:<str ; {age:int}> = <'alice' ; { 30 }, 'bob' ; { 25 }>");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // MapStart

        Assert.True(reader.Read()); Assert.Equal("alice", reader.GetString());
        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal(30L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); Assert.Equal("bob", reader.GetString());
        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal(25L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // MapEnd
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void List_WithNewlineSeparators()
    {
        var data = ToUtf8("nums:[int] = [\n  1\n  2\n  3\n]");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // ListStart
        Assert.True(reader.Read()); Assert.Equal(1L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(2L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());
        Assert.True(reader.Read()); // ListEnd
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void List_TrailingComma()
    {
        var data = ToUtf8("nums:[int] = [1, 2, 3,]");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // ListStart
        Assert.True(reader.Read()); Assert.Equal(1L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(2L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());
        Assert.True(reader.Read()); // ListEnd
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }
}

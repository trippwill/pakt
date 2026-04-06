using System.Text;

namespace Pakt.Tests.ReaderTests;

public class StreamReaderTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ListStream_MultipleElements()
    {
        var data = ToUtf8("events:[str] << 'alpha', 'beta', 'gamma'");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // StreamStart
        Assert.Equal(PaktTokenType.StreamStart, reader.TokenType);
        Assert.Equal("events", reader.StatementName);
        Assert.True(reader.IsStreamStatement);

        Assert.True(reader.Read()); Assert.Equal("alpha", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal("beta", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal("gamma", reader.GetString());

        Assert.True(reader.Read()); // StreamEnd
        Assert.Equal(PaktTokenType.StreamEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void ListStream_TerminatedByEof()
    {
        var data = ToUtf8("nums:[int] << 1, 2, 3");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // StreamStart
        Assert.True(reader.Read()); Assert.Equal(1L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(2L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());
        Assert.True(reader.Read()); // StreamEnd
        Assert.Equal(PaktTokenType.StreamEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void ListStream_TerminatedByNextStatement()
    {
        var data = ToUtf8("items:[int] << 10, 20\nname:str = 'hello'");
        var reader = new PaktReader(data);

        Assert.True(reader.Read()); // StreamStart
        Assert.True(reader.Read()); Assert.Equal(10L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(20L, reader.GetInt64());
        Assert.True(reader.Read()); // StreamEnd
        Assert.Equal(PaktTokenType.StreamEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignStart
        Assert.Equal("name", reader.StatementName);
        Assert.True(reader.Read()); Assert.Equal("hello", reader.GetString());
        Assert.True(reader.Read()); // AssignEnd

        Assert.False(reader.Read());
        reader.Dispose();
    }
}

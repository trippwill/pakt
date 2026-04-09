using System.Text;

namespace Pakt.Tests.ReaderTests;

public class BasicReaderTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Parse_SimpleStringAssignment()
    {
        var data = ToUtf8("name:str = 'hello'");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.Equal("name", reader.CurrentName);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal(PaktScalarType.Str, reader.ScalarType);
        Assert.Equal("hello", reader.GetString());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_SimpleIntAssignment()
    {
        var data = ToUtf8("count:int = 42");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal(PaktScalarType.Int, reader.ScalarType);
        Assert.Equal(42L, reader.GetInt64());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_SimpleBoolAssignment()
    {
        var data = ToUtf8("active:bool = true");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal(PaktScalarType.Bool, reader.ScalarType);
        Assert.True(reader.GetBoolean());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_MultipleAssignments()
    {
        var data = ToUtf8("name:str = 'Alice'\nage:int = 30");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.Equal("name", reader.StatementName);

        Assert.True(reader.Read());
        Assert.Equal("Alice", reader.GetString());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.Equal("age", reader.StatementName);

        Assert.True(reader.Read());
        Assert.Equal(30L, reader.GetInt64());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_CommentsAreSkipped()
    {
        var data = ToUtf8("# This is a comment\nname:str = 'hello'\n# Another comment");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        Assert.True(reader.Read());
        Assert.Equal("hello", reader.GetString());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_BOMIsSkipped()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var content = Encoding.UTF8.GetBytes("name:str = 'hello'");
        var data = new byte[bom.Length + content.Length];
        bom.CopyTo(data, 0);
        content.CopyTo(data, bom.Length);

        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        Assert.True(reader.Read());
        Assert.Equal("hello", reader.GetString());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);

        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsFalse()
    {
        var reader = new PaktReader(ReadOnlySpan<byte>.Empty);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsFalse()
    {
        var data = ToUtf8("   \n  \t  \n  ");
        var reader = new PaktReader(data);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_CommentsOnly_ReturnsFalse()
    {
        var data = ToUtf8("# comment 1\n# comment 2\n");
        var reader = new PaktReader(data);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_DoubleQuotedString()
    {
        var data = ToUtf8("name:str = \"hello\"");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal("hello", reader.GetString());
        Assert.True(reader.Read());
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Parse_StatementName_SetCorrectly()
    {
        var data = ToUtf8("my_config:str = 'value'");
        var reader = new PaktReader(data);

        Assert.True(reader.Read());
        Assert.Equal("my_config", reader.StatementName);
        Assert.False(reader.IsPackStatement);

        reader.Dispose();
    }
}

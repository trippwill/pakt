using System.Text;

namespace Pakt.Tests.ReaderTests;

public class ErrorReaderTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Error_NilOnNonNullable()
    {
        var data = ToUtf8("name:str = nil");
        var reader = new PaktReader(data);

        reader.Read(); // AssignStart
        PaktException? caught = null;
        try { reader.Read(); }
        catch (PaktException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.Equal(PaktErrorCode.NilNonNullable, caught!.ErrorCode);
        reader.Dispose();
    }

    [Fact]
    public void Error_UnexpectedEof_InAssignment()
    {
        var data = ToUtf8("name:str =");
        var reader = new PaktReader(data);

        reader.Read(); // AssignStart
        PaktException? caught = null;
        try { reader.Read(); }
        catch (PaktException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.Equal(PaktErrorCode.UnexpectedEof, caught!.ErrorCode);
        reader.Dispose();
    }

    [Fact]
    public void Error_MissingEquals()
    {
        var data = ToUtf8("name:str 'hello'");
        var reader = new PaktReader(data);

        PaktException? caught = null;
        try { reader.Read(); }
        catch (PaktException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.Equal(PaktErrorCode.Syntax, caught!.ErrorCode);
        reader.Dispose();
    }

    [Fact]
    public void Error_UnterminatedString()
    {
        var data = ToUtf8("name:str = 'hello");
        var reader = new PaktReader(data);

        reader.Read(); // AssignStart
        PaktException? caught = null;
        try { reader.Read(); }
        catch (PaktException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.Equal(PaktErrorCode.UnexpectedEof, caught!.ErrorCode);
        reader.Dispose();
    }

    [Fact]
    public void Error_TooFewStructFields()
    {
        var data = ToUtf8("config:{host:str, port:int} = { 'localhost' }");
        var reader = new PaktReader(data);

        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        reader.Read(); // first field 'localhost'

        PaktException? caught = null;
        try { while (reader.Read()) { } }
        catch (PaktException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.Contains("Too few values", caught!.Message);
        reader.Dispose();
    }

    [Fact]
    public void Error_Position_IsReported()
    {
        var data = ToUtf8("name:str = nil");
        var reader = new PaktReader(data);

        reader.Read(); // AssignStart
        PaktException? caught = null;
        try { reader.Read(); }
        catch (PaktException ex) { caught = ex; }

        Assert.NotNull(caught);
        Assert.NotEqual(PaktPosition.None, caught!.Position);
        reader.Dispose();
    }
}

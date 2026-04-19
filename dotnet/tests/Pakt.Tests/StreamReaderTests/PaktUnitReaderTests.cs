using System.Buffers;
using System.Text;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Tests.StreamReaderTests;

using Pakt.Tests.SerializerTests;

public class PaktMemoryReaderTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ReadStatement_SingleAssignment()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("server:{host:str, port:int} = {'localhost', 8080}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        Assert.Equal("server", reader.StatementName);
        Assert.False(reader.IsPack);
        Assert.False(reader.ReadStatement());
    }

    [Fact]
    public void ReadStatement_MultipleAssignments()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("a:{host:str, port:int} = {'one', 1}\nb:{host:str, port:int} = {'two', 2}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        Assert.Equal("a", reader.StatementName);
        reader.Skip();

        Assert.True(reader.ReadStatement());
        Assert.Equal("b", reader.StatementName);
        Assert.False(reader.ReadStatement());
    }

    [Fact]
    public void ReadStatement_TracksStatementPosition()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("a:{host:str, port:int} = {'one', 1}\nb:{host:str, port:int} = {'two', 2}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        Assert.Equal(new PaktPosition(1, 1), reader.StatementPosition);
        reader.ReadValue<SimpleServer>();

        Assert.True(reader.ReadStatement());
        Assert.Equal(new PaktPosition(2, 1), reader.StatementPosition);
    }

    [Fact]
    public void ReadValue_SimpleServer()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("server:{host:str, port:int} = {'localhost', 8080}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var server = reader.ReadValue<SimpleServer>();

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);
    }

    [Fact]
    public void ReadValue_MultipleStatements()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("a:{host:str, port:int} = {'first', 1}\nb:{host:str, port:int} = {'second', 2}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var first = reader.ReadValue<SimpleServer>();
        Assert.Equal("first", first.Host);
        Assert.Equal(1, first.Port);

        Assert.True(reader.ReadStatement());
        var second = reader.ReadValue<SimpleServer>();
        Assert.Equal("second", second.Host);
        Assert.Equal(2, second.Port);

        Assert.False(reader.ReadStatement());
    }

    [Fact]
    public void ReadValue_WithNestedValues()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("p:{name:str, home:{city:str, zip:int}} = {'Alice', {'NYC', 10001}}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var person = reader.ReadValue<PersonWithAddress>();

        Assert.Equal("Alice", person.Name);
        Assert.Equal("NYC", person.Home.City);
        Assert.Equal(10001, person.Home.Zip);
    }

    [Fact]
    public void ReadPack_ListPack()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("servers:[{host:str, port:int}] << {'a', 1}, {'b', 2}, {'c', 3}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var servers = reader.ReadPack<SimpleServer>().ToList();

        Assert.Equal(3, servers.Count);
        Assert.Equal("a", servers[0].Host);
        Assert.Equal(1, servers[0].Port);
        Assert.Equal("c", servers[2].Host);
        Assert.Equal(3, servers[2].Port);
    }

    [Fact]
    public void ReadMapPack_MapPack()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("ports:<str ; int> << 'http' ; 80, 'https' ; 443\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var entries = reader.ReadMapPack<string, int>().ToList();

        Assert.Equal(
            [new PaktMapEntry<string, int>("http", 80), new PaktMapEntry<string, int>("https", 443)],
            entries);
    }

    [Fact]
    public void MixedAssignmentsAndPacks()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("config:{host:str, port:int} = {'main', 80}\nentries:[{host:str, port:int}] << {'a', 1}, {'b', 2}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var config = reader.ReadValue<SimpleServer>();
        Assert.Equal("main", config.Host);
        Assert.Equal(80, config.Port);

        Assert.True(reader.ReadStatement());
        var entries = reader.ReadPack<SimpleServer>().ToList();
        Assert.Equal(2, entries.Count);
        Assert.False(reader.ReadStatement());
    }

    [Fact]
    public void Skip_SkipsUnknownStatements()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("unknown:{host:str, port:int} = {'skip', 0}\nwanted:{host:str, port:int} = {'found', 42}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        reader.Skip();

        Assert.True(reader.ReadStatement());
        var server = reader.ReadValue<SimpleServer>();
        Assert.Equal("found", server.Host);
        Assert.Equal(42, server.Port);
    }

    [Fact]
    public void ReadStatement_EmptyUnit()
    {
        using var reader = PaktMemoryReader.Create(Array.Empty<byte>(), TestPaktContext.Default);
        Assert.False(reader.ReadStatement());
    }

    [Fact]
    public void ReadValue_ThrowsWhenNoStatement()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("s:{host:str, port:int} = {'a', 1}\n"),
            TestPaktContext.Default);

        Assert.Throws<InvalidOperationException>(() => reader.ReadValue<SimpleServer>());
    }

    [Fact]
    public void ReadValue_OnPack_Throws()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("servers:[{host:str, port:int}] << {'a', 1}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var ex = Assert.Throws<InvalidOperationException>(() => reader.ReadValue<SimpleServer>());
        Assert.Contains("ReadPack", ex.Message);
    }

    [Fact]
    public void ReadPack_ThrowsOnNonListPack()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("ports:<str ; int> << 'http' ; 80\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var ex = Assert.Throws<InvalidOperationException>(() => reader.ReadPack<int>().ToList());
        Assert.Contains("ReadMapPack", ex.Message);
    }

    [Fact]
    public void ReadMapPack_ThrowsOnNonMapPack()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("servers:[{host:str, port:int}] << {'a', 1}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        var ex = Assert.Throws<InvalidOperationException>(() => reader.ReadMapPack<string, int>().ToList());
        Assert.Contains("ReadPack", ex.Message);
    }

    [Fact]
    public void Create_FromOwnedMemory()
    {
        var data = Bytes("s:{host:str, port:int} = {'owned', 7}\n");
        var owner = MemoryPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(owner.Memory.Span);

        using var reader = PaktMemoryReader.Create(owner, data.Length, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var server = reader.ReadValue<SimpleServer>();

        Assert.Equal("owned", server.Host);
        Assert.Equal(7, server.Port);
    }

    [Fact]
    public void ReadStatement_AutoSkipsUnconsumedStatement()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("a:{host:str, port:int} = {'first', 1}\nb:{host:str, port:int} = {'second', 2}\n"),
            TestPaktContext.Default);

        Assert.True(reader.ReadStatement());
        Assert.Equal("a", reader.StatementName);

        Assert.True(reader.ReadStatement());
        var second = reader.ReadValue<SimpleServer>();
        Assert.Equal("second", second.Host);
    }

    [Fact]
    public void Dispose_PreventsSubsequentCalls()
    {
        var reader = PaktMemoryReader.Create(
            Bytes("s:{host:str, port:int} = {'a', 1}\n"),
            TestPaktContext.Default);
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.ReadStatement());
    }
}

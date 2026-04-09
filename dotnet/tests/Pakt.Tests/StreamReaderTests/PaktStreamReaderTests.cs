using System.Text;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Tests.StreamReaderTests;

// Reuse test types from serializer tests
using Pakt.Tests.SerializerTests;

public class PaktStreamReaderTests
{
    [Fact]
    public async Task ReadStatementAsync_SingleAssignment()
    {
        var pakt = "server:{host:str, port:int} = {'localhost', 8080}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("server", reader.StatementName);
        Assert.False(reader.IsPack);

        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task ReadStatementAsync_MultipleAssignments()
    {
        var pakt = "a:{host:str, port:int} = {'one', 1}\nb:{host:str, port:int} = {'two', 2}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);

        // Skip first statement
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);

        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task Deserialize_SimpleServer()
    {
        var pakt = "server:{host:str, port:int} = {'localhost', 8080}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        var server = reader.Deserialize(TestPaktContext.Default.SimpleServer);

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);
    }

    [Fact]
    public async Task Deserialize_MultipleStatements()
    {
        var pakt = "a:{host:str, port:int} = {'first', 1}\nb:{host:str, port:int} = {'second', 2}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);
        var first = reader.Deserialize(TestPaktContext.Default.SimpleServer);
        Assert.Equal("first", first.Host);
        Assert.Equal(1, first.Port);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
        var second = reader.Deserialize(TestPaktContext.Default.SimpleServer);
        Assert.Equal("second", second.Host);
        Assert.Equal(2, second.Port);

        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task Deserialize_WithNullableValues()
    {
        var pakt = "n:{label:str?, count:int?} = {'hello', nil}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        var result = reader.Deserialize(TestPaktContext.Default.WithNullable);

        Assert.Equal("hello", result.Label);
        Assert.Null(result.Count);
    }

    [Fact]
    public async Task Deserialize_NestedStruct()
    {
        var pakt = "p:{name:str, home:{city:str, zip:int}} = {'Alice', {'NYC', 10001}}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        var person = reader.Deserialize(TestPaktContext.Default.PersonWithAddress);

        Assert.Equal("Alice", person.Name);
        Assert.Equal("NYC", person.Home.City);
        Assert.Equal(10001, person.Home.Zip);
    }

    [Fact]
    public async Task ReadPackElements_ListPack()
    {
        var pakt = "servers:[{host:str, port:int}] << {'a', 1}, {'b', 2}, {'c', 3}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("servers", reader.StatementName);
        Assert.True(reader.IsPack);

        var servers = new List<SimpleServer>();
        await foreach (var server in reader.ReadPackElements(TestPaktContext.Default.SimpleServer))
        {
            servers.Add(server);
        }

        Assert.Equal(3, servers.Count);
        Assert.Equal("a", servers[0].Host);
        Assert.Equal(1, servers[0].Port);
        Assert.Equal("b", servers[1].Host);
        Assert.Equal(2, servers[1].Port);
        Assert.Equal("c", servers[2].Host);
        Assert.Equal(3, servers[2].Port);
    }

    [Fact]
    public async Task MixedAssignmentsAndPacks()
    {
        var pakt = "config:{host:str, port:int} = {'main', 80}\nentries:[{host:str, port:int}] << {'a', 1}, {'b', 2}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        // Read config assignment
        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("config", reader.StatementName);
        Assert.False(reader.IsPack);
        var config = reader.Deserialize(TestPaktContext.Default.SimpleServer);
        Assert.Equal("main", config.Host);
        Assert.Equal(80, config.Port);

        // Read pack
        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("entries", reader.StatementName);
        Assert.True(reader.IsPack);
        var entries = new List<SimpleServer>();
        await foreach (var entry in reader.ReadPackElements(TestPaktContext.Default.SimpleServer))
        {
            entries.Add(entry);
        }
        Assert.Equal(2, entries.Count);

        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task SkipAsync_SkipsUnknownStatements()
    {
        var pakt = "unknown:{host:str, port:int} = {'skip', 0}\nwanted:{host:str, port:int} = {'found', 42}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("unknown", reader.StatementName);
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("wanted", reader.StatementName);
        var server = reader.Deserialize(TestPaktContext.Default.SimpleServer);
        Assert.Equal("found", server.Host);
        Assert.Equal(42, server.Port);
    }

    [Fact]
    public async Task ReadStatementAsync_EmptyUnit()
    {
        await using var reader = PaktStreamReader.Create(ReadOnlySpan<byte>.Empty);
        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task ReadStatementAsync_CancellationToken()
    {
        var pakt = "s:{host:str, port:int} = {'a', 1}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadStatementAsync(cts.Token));
    }

    [Fact]
    public async Task Deserialize_ThrowsWhenNoStatement()
    {
        var pakt = "s:{host:str, port:int} = {'a', 1}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.Throws<InvalidOperationException>(
            () => reader.Deserialize(TestPaktContext.Default.SimpleServer));
    }

    [Fact]
    public async Task Deserialize_ThrowsOnPack()
    {
        var pakt = "s:[{host:str, port:int}] << {'a', 1}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);
        await reader.ReadStatementAsync();

        Assert.Throws<InvalidOperationException>(
            () => reader.Deserialize(TestPaktContext.Default.SimpleServer));
    }

    [Fact]
    public async Task ReadPackElements_ThrowsOnNonPack()
    {
        var pakt = "s:{host:str, port:int} = {'a', 1}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);
        await reader.ReadStatementAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await foreach (var _ in reader.ReadPackElements(TestPaktContext.Default.SimpleServer))
                {
                }
            });
    }

    [Fact]
    public async Task CreateAsync_FromStream()
    {
        var pakt = "s:{host:str, port:int} = {'test', 123}\n"u8;
        using var stream = new MemoryStream(pakt.ToArray());
        await using var reader = await PaktStreamReader.CreateAsync(stream);

        Assert.True(await reader.ReadStatementAsync());
        var server = reader.Deserialize(TestPaktContext.Default.SimpleServer);
        Assert.Equal("test", server.Host);
        Assert.Equal(123, server.Port);
    }

    [Fact]
    public async Task SkipAsync_AutoSkipOnNextRead()
    {
        // If we call ReadStatementAsync again without consuming, the previous statement is auto-skipped
        var pakt = "a:{host:str, port:int} = {'first', 1}\nb:{host:str, port:int} = {'second', 2}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);
        // Don't consume or skip — just read next

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
        var second = reader.Deserialize(TestPaktContext.Default.SimpleServer);
        Assert.Equal("second", second.Host);
    }

    [Fact]
    public async Task Deserialize_WithList()
    {
        var pakt = "t:{tags:[str]} = {['a', 'b', 'c']}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        var result = reader.Deserialize(TestPaktContext.Default.WithList);
        Assert.Equal(new[] { "a", "b", "c" }, result.Tags);
    }

    [Fact]
    public async Task Deserialize_WithMap()
    {
        var pakt = "m:{scores:<str ; int>} = {< 'alice' ; 10, 'bob' ; 20 >}\n"u8;
        await using var reader = PaktStreamReader.Create(pakt);

        Assert.True(await reader.ReadStatementAsync());
        var result = reader.Deserialize(TestPaktContext.Default.WithMap);
        Assert.Equal(10, result.Scores["alice"]);
        Assert.Equal(20, result.Scores["bob"]);
    }

    [Fact]
    public async Task DisposeAsync_PreventsSubsequentCalls()
    {
        var pakt = "s:{host:str, port:int} = {'a', 1}\n"u8;
        var reader = PaktStreamReader.Create(pakt);
        await reader.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await reader.ReadStatementAsync());
    }
}

using System.Buffers;
using System.Text;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Tests.StreamReaderTests;

using Pakt.Tests.SerializerTests;

public class PaktStreamReaderTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task ReadStatementAsync_SingleAssignment()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("server:{host:str, port:int} = {'localhost', 8080}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("server", reader.StatementName);
        Assert.False(reader.IsPack);
        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task ReadStatementAsync_MultipleAssignments()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("a:{host:str, port:int} = {'one', 1}\nb:{host:str, port:int} = {'two', 2}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task ReadValueAsync_SimpleServer()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("server:{host:str, port:int} = {'localhost', 8080}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var server = await reader.ReadValueAsync<SimpleServer>();

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);
    }

    [Fact]
    public async Task ReadPackAsync_ListPack()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("servers:[{host:str, port:int}] << {'a', 1}, {'b', 2}, {'c', 3}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.True(reader.IsPack);

        var servers = new List<SimpleServer>();
        await foreach (var server in reader.ReadPackAsync<SimpleServer>())
            servers.Add(server);

        Assert.Equal(3, servers.Count);
        Assert.Equal("a", servers[0].Host);
        Assert.Equal(3, servers[2].Port);
    }

    [Fact]
    public async Task ReadMapPackAsync_MapPack()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("ports:<str ; int> << 'http' ; 80, 'https' ; 443\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.True(reader.IsPack);

        var entries = new List<PaktMapEntry<string, int>>();
        await foreach (var entry in reader.ReadMapPackAsync<string, int>())
            entries.Add(entry);

        Assert.Equal(2, entries.Count);
        Assert.Equal("http", entries[0].Key);
        Assert.Equal(80, entries[0].Value);
        Assert.Equal("https", entries[1].Key);
        Assert.Equal(443, entries[1].Value);
    }

    [Fact]
    public async Task SkipAsync_SkipsUnknownStatements()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("unknown:{host:str, port:int} = {'skip', 0}\nwanted:{host:str, port:int} = {'found', 42}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        var server = await reader.ReadValueAsync<SimpleServer>();
        Assert.Equal("found", server.Host);
        Assert.Equal(42, server.Port);
    }

    [Fact]
    public async Task ReadStatementAsync_AutoSkipsUnconsumedStatement()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("a:{host:str, port:int} = {'first', 1}\nb:{host:str, port:int} = {'second', 2}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);

        Assert.True(await reader.ReadStatementAsync());
        var second = await reader.ReadValueAsync<SimpleServer>();
        Assert.Equal("second", second.Host);
    }

    [Fact]
    public async Task ReadStatementAsync_EmptyInput()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Array.Empty<byte>()),
            TestPaktContext.Default);

        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task ReadValueAsync_ThrowsWhenNoStatement()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("s:{host:str, port:int} = {'a', 1}\n")),
            TestPaktContext.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadValueAsync<SimpleServer>().AsTask());
    }

    [Fact]
    public async Task ReadValueAsync_OnPack_Throws()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("servers:[{host:str, port:int}] << {'a', 1}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadValueAsync<SimpleServer>().AsTask());
        Assert.Contains("ReadPackAsync", ex.Message);
    }

    [Fact]
    public async Task DisposeAsync_PreventsSubsequentCalls()
    {
        var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("s:{host:str, port:int} = {'a', 1}\n")),
            TestPaktContext.Default);
        await reader.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadStatementAsync().AsTask());
    }

    [Fact]
    public async Task MixedAssignmentsAndPacks()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("config:{host:str, port:int} = {'main', 80}\nentries:[{host:str, port:int}] << {'a', 1}, {'b', 2}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var config = await reader.ReadValueAsync<SimpleServer>();
        Assert.Equal("main", config.Host);

        Assert.True(await reader.ReadStatementAsync());
        var entries = new List<SimpleServer>();
        await foreach (var s in reader.ReadPackAsync<SimpleServer>())
            entries.Add(s);
        Assert.Equal(2, entries.Count);
        Assert.False(await reader.ReadStatementAsync());
    }

    [Fact]
    public async Task DeserializeAsync_FromStream()
    {
        var stream = new MemoryStream(Bytes("host:str = 'localhost'\nport:int = 8080\n"));
        var server = await PaktSerializer.DeserializeAsync<SimpleServer>(stream, TestPaktContext.Default);

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);
    }

    /// <summary>
    /// Tests chunked reading by using a stream that delivers one byte at a time.
    /// </summary>
    [Fact]
    public async Task ReadStatementAsync_SingleByteStream()
    {
        var data = Bytes("s:{host:str, port:int} = {'test', 42}\n");
        await using var reader = PaktStreamReader.Create(
            new SingleByteStream(data),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var server = await reader.ReadValueAsync<SimpleServer>();
        Assert.Equal("test", server.Host);
        Assert.Equal(42, server.Port);
    }

    [Fact]
    public async Task ReadPackAsync_SingleByteStream()
    {
        var data = Bytes("items:[int] << 1, 2, 3\n");
        await using var reader = PaktStreamReader.Create(
            new SingleByteStream(data),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var items = new List<int>();
        await foreach (var item in reader.ReadPackAsync<int>())
            items.Add(item);

        Assert.Equal([1, 2, 3], items);
    }

    /// <summary>
    /// Helper stream that delivers exactly one byte per ReadAsync call.
    /// </summary>
    private sealed class SingleByteStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length)
                return ValueTask.FromResult(0);

            buffer.Span[0] = data[_position++];
            return ValueTask.FromResult(1);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

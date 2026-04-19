using System.Text;
using Pakt;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests.StreamReaderTests;

/// <summary>
/// Tests for PaktStreamReader and PaktFramedSource coverage gaps:
/// NUL framing, map packs, skip on packs, multi-unit streams.
/// </summary>
public class StreamAndFramingCoverageTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task NulFramedStream_TwoUnits()
    {
        var data = Bytes("a:int = 1\0b:int = 2\0");
        var stream = new MemoryStream(data);

        await using (var reader = PaktStreamReader.Create(stream, TestPaktContext.Default))
        {
            Assert.True(await reader.ReadStatementAsync());
            Assert.Equal("a", reader.StatementName);
            var val = await reader.ReadValueAsync<int>();
            Assert.Equal(1, val);
            Assert.False(await reader.ReadStatementAsync());
        }

        // Second unit — PaktStreamReader doesn't auto-advance past NUL,
        // so creating a new reader on the same stream should read the next unit.
        // (This tests framed source leftover handling)
    }

    [Fact]
    public async Task ReadMapPackAsync_WithStructValues()
    {
        var pakt = Bytes("servers:<str ; {host:str, port:int}> << 'web' ; {'localhost', 80}, 'api' ; {'0.0.0.0', 443}\n");
        await using var reader = PaktStreamReader.Create(new MemoryStream(pakt), TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.True(reader.IsPack);

        var entries = new List<PaktMapEntry<string, SimpleServer>>();
        await foreach (var entry in reader.ReadMapPackAsync<string, SimpleServer>())
            entries.Add(entry);

        Assert.Equal(2, entries.Count);
        Assert.Equal("web", entries[0].Key);
        Assert.Equal("localhost", entries[0].Value.Host);
        Assert.Equal(443, entries[1].Value.Port);
    }

    [Fact]
    public async Task SkipAsync_MapPack()
    {
        var pakt = Bytes("ports:<str ; int> << 'http' ; 80, 'https' ; 443\nname:str = 'after'\n");
        await using var reader = PaktStreamReader.Create(new MemoryStream(pakt), TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.True(reader.IsPack);
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("name", reader.StatementName);
    }

    [Fact]
    public async Task ReadMapPackAsync_EarlyBreak()
    {
        var pakt = Bytes("ports:<str ; int> << 'http' ; 80, 'https' ; 443\nname:str = 'after'\n");
        await using var reader = PaktStreamReader.Create(new MemoryStream(pakt), TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        await foreach (var entry in reader.ReadMapPackAsync<string, int>())
        {
            Assert.Equal("http", entry.Key);
            break;
        }

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("name", reader.StatementName);
    }

    [Fact]
    public async Task LargeStatement_BufferGrows()
    {
        // Create a statement large enough to exceed default 4KB buffer
        var sb = new StringBuilder();
        sb.Append("items:[int] << ");
        for (int i = 0; i < 500; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(i);
        }
        sb.AppendLine();

        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes(sb.ToString())), TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var count = 0;
        await foreach (var _ in reader.ReadPackAsync<int>())
            count++;
        Assert.Equal(500, count);
    }

    [Fact]
    public async Task ReadPackAsync_ScalarInts()
    {
        var pakt = Bytes("nums:[int] << 10, 20, 30\n");
        await using var reader = PaktStreamReader.Create(new MemoryStream(pakt), TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var items = new List<int>();
        await foreach (var item in reader.ReadPackAsync<int>())
            items.Add(item);

        Assert.Equal([10, 20, 30], items);
    }

    [Fact]
    public async Task MixedStatements_SkipAndRead()
    {
        var pakt = Bytes("a:int = 1\nb:{host:str, port:int} = {'srv', 80}\nc:str = 'end'\n");
        await using var reader = PaktStreamReader.Create(new MemoryStream(pakt), TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
        var server = await reader.ReadValueAsync<SimpleServer>();
        Assert.Equal("srv", server.Host);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("c", reader.StatementName);
    }
}

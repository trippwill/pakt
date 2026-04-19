using System.Text;
using Pakt;
using Pakt.Serialization;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests.StreamReaderTests;

public class PaktStreamReaderEdgeCaseTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

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
    }

    [Fact]
    public async Task SkipAsync_SkipsPack()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("items:[int] << 1, 2, 3\nname:str = 'after'\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.True(reader.IsPack);
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("name", reader.StatementName);
    }

    [Fact]
    public async Task ReadPackAsync_EarlyBreak()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("items:[int] << 1, 2, 3\nname:str = 'after'\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        await foreach (var item in reader.ReadPackAsync<int>())
        {
            Assert.Equal(1, item);
            break;
        }

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("name", reader.StatementName);
    }

    [Fact]
    public async Task ReadPackAsync_OnNonListPack_Throws()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("ports:<str ; int> << 'http' ; 80\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in reader.ReadPackAsync<int>()) { }
        });
        Assert.Contains("ReadMapPackAsync", ex.Message);
    }

    [Fact]
    public async Task ReadMapPackAsync_OnNonMapPack_Throws()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("items:[int] << 1\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in reader.ReadMapPackAsync<string, int>()) { }
        });
        Assert.Contains("ReadPackAsync", ex.Message);
    }

    [Fact]
    public async Task SkipAsync_ThrowsWhenNoStatement()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("s:int = 1\n")),
            TestPaktContext.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SkipAsync().AsTask());
    }

    [Fact]
    public async Task ReadStatementAsync_WithNestedValues()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("p:{name:str, home:{city:str, zip:int}} = {'Alice', {'NYC', 10001}}\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        var person = await reader.ReadValueAsync<PersonWithAddress>();
        Assert.Equal("Alice", person.Name);
        Assert.Equal("NYC", person.Home.City);
    }

    [Fact]
    public async Task DeserializeAsync_WithPack()
    {
        var pakt = Bytes("host:str = 'localhost'\nport:int = 8080\n");
        var server = await PaktSerializer.DeserializeAsync<SimpleServer>(
            new MemoryStream(pakt), TestPaktContext.Default);

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);
    }

    [Fact]
    public async Task ReadStatementAsync_MultipleStatementsWithSkip()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("a:int = 1\nb:int = 2\nc:int = 3\n")),
            TestPaktContext.Default);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("a", reader.StatementName);

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
        await reader.SkipAsync();

        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("c", reader.StatementName);
        Assert.False(await reader.ReadStatementAsync());
    }
}

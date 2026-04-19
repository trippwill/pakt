using System.Text;
using Pakt;
using Pakt.Serialization;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests;

/// <summary>
/// Final coverage push: serialization runtime, stream async internals,
/// framed source, materializer async pack paths.
/// </summary>
public class FinalCoverageTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // -- PaktSerializationRuntime: WriteObject branches --

    [Fact]
    public void Serialize_BoolField()
    {
        var server = new SimpleServer { Host = "h", Port = 0 };
        var bytes = PaktSerializer.Serialize(server, TestPaktContext.Default);
        Assert.NotEmpty(bytes);
        Assert.Contains("host"u8.ToArray(), bytes);
    }

    [Fact]
    public void Serialize_NullableWithValues_RoundTrip()
    {
        var val = new WithNullable { Label = "test", Count = 42 };
        var bytes = PaktSerializer.Serialize(val, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithNullable>(bytes, TestPaktContext.Default);
        Assert.Equal("test", result.Label);
        Assert.Equal(42, result.Count);
    }

    [Fact]
    public void Serialize_EmptyList_RoundTrip()
    {
        var val = new WithList { Tags = [] };
        var bytes = PaktSerializer.Serialize(val, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithList>(bytes, TestPaktContext.Default);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void Serialize_EmptyMap_RoundTrip()
    {
        var val = new WithMap { Scores = [] };
        var bytes = PaktSerializer.Serialize(val, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithMap>(bytes, TestPaktContext.Default);
        Assert.Empty(result.Scores);
    }

    // -- PaktStreamReader: ReadMapPackEntriesAsync (0% coverage) --

    [Fact]
    public async Task StreamReader_ReadMapPackEntriesAsync_StringInt()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("m:<str ; int> << 'x' ; 10, 'y' ; 20\n")),
            TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());

        var entryType = typeof(PaktMapEntry<string, int>);
        var entries = new List<object?>();
        await foreach (var entry in reader.ReadMapPackEntriesAsync(entryType))
            entries.Add(entry);

        Assert.Equal(2, entries.Count);
        var first = (PaktMapEntry<string, int>)entries[0]!;
        Assert.Equal("x", first.Key);
        Assert.Equal(10, first.Value);
    }

    [Fact]
    public async Task StreamReader_ReadMapPackEntriesAsync_WithStructValues()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("s:<str ; {host:str, port:int}> << 'web' ; {'srv', 80}\n")),
            TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());

        var entryType = typeof(PaktMapEntry<string, SimpleServer>);
        var entries = new List<object?>();
        await foreach (var entry in reader.ReadMapPackEntriesAsync(entryType))
            entries.Add(entry);

        Assert.Single(entries);
    }

    // -- PaktStreamReader: SkipValueCoreAsync more paths --

    [Fact]
    public async Task StreamReader_SkipAsync_NestedStruct()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("a:{name:str, home:{city:str, zip:int}} = {'skip', {'city', 0}}\nb:int = 42\n")),
            TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());
        await reader.SkipAsync();
        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
    }

    [Fact]
    public async Task StreamReader_SkipAsync_ListPack()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("items:[{host:str, port:int}] << {'a', 1}, {'b', 2}\nend:int = 0\n")),
            TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());
        await reader.SkipAsync();
        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("end", reader.StatementName);
    }

    [Fact]
    public async Task StreamReader_SkipAsync_MapPack()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("m:<str ; int> << 'a' ; 1, 'b' ; 2\nend:int = 0\n")),
            TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());
        await reader.SkipAsync();
        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("end", reader.StatementName);
    }

    // -- PaktFramedSource: buffer compact + growth --

    [Fact]
    public async Task StreamReader_LargeValue_TriggersBufferGrowth()
    {
        var sb = new StringBuilder();
        sb.Append("v:str = '");
        sb.Append(new string('x', 8000)); // exceed default 4KB buffer
        sb.Append("'\n");

        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes(sb.ToString())), TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());
        var val = await reader.ReadValueAsync<string>();
        Assert.Equal(8000, val.Length);
    }

    [Fact]
    public async Task StreamReader_MultipleStatements_CompactsBuffer()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++)
            sb.AppendLine($"s{i}:int = {i}");

        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes(sb.ToString())), TestPaktContext.Default);

        for (int i = 0; i < 20; i++)
        {
            Assert.True(await reader.ReadStatementAsync());
            Assert.Equal($"s{i}", reader.StatementName);
            var val = await reader.ReadValueAsync<int>();
            Assert.Equal(i, val);
        }
        Assert.False(await reader.ReadStatementAsync());
    }

    // -- MaterializeAsync: pack paths via DeserializeAsync --

    [Fact]
    public async Task DeserializeAsync_WithListPack()
    {
        var pakt = Bytes("tags:[str] << 'a', 'b', 'c'\n");
        var result = await PaktSerializer.DeserializeAsync<WithList>(
            new MemoryStream(pakt), TestPaktContext.Default);
        Assert.Equal(["a", "b", "c"], result.Tags);
    }

    [Fact]
    public async Task DeserializeAsync_WithMapPack()
    {
        var pakt = Bytes("scores:<str ; int> << 'x' ; 1, 'y' ; 2\n");
        var result = await PaktSerializer.DeserializeAsync<WithMap>(
            new MemoryStream(pakt), TestPaktContext.Default);
        Assert.Equal(1, result.Scores["x"]);
        Assert.Equal(2, result.Scores["y"]);
    }

    [Fact]
    public async Task DeserializeAsync_MixedStatementsAndPacks()
    {
        var pakt = Bytes("host:str = 'srv'\nport:int = 80\n");
        var result = await PaktSerializer.DeserializeAsync<SimpleServer>(
            new MemoryStream(pakt), TestPaktContext.Default);
        Assert.Equal("srv", result.Host);
        Assert.Equal(80, result.Port);
    }

    [Fact]
    public async Task DeserializeAsync_DuplicateLastWins()
    {
        var pakt = Bytes("host:str = 'a'\nport:int = 1\nhost:str = 'b'\nport:int = 2\n");
        var opts = new DeserializeOptions { Duplicates = DuplicatePolicy.LastWins };
        var result = await PaktSerializer.DeserializeAsync<SimpleServer>(
            new MemoryStream(pakt), TestPaktContext.Default, opts);
        Assert.Equal("b", result.Host);
        Assert.Equal(2, result.Port);
    }

    [Fact]
    public async Task DeserializeAsync_DuplicateError()
    {
        var pakt = Bytes("host:str = 'a'\nport:int = 1\nhost:str = 'b'\n");
        var opts = new DeserializeOptions { Duplicates = DuplicatePolicy.Error };
        await Assert.ThrowsAsync<PaktDeserializeException>(() =>
            PaktSerializer.DeserializeAsync<SimpleServer>(
                new MemoryStream(pakt), TestPaktContext.Default, opts).AsTask());
    }

    [Fact]
    public async Task DeserializeAsync_MissingFieldError()
    {
        var pakt = Bytes("host:str = 'only'\n");
        var opts = new DeserializeOptions { MissingFields = MissingFieldPolicy.Error };
        await Assert.ThrowsAsync<PaktDeserializeException>(() =>
            PaktSerializer.DeserializeAsync<SimpleServer>(
                new MemoryStream(pakt), TestPaktContext.Default, opts).AsTask());
    }

    // -- PaktUnitSyntax: more boundary probing --

    [Fact]
    public void ProbePackItemStart_WithComment()
    {
        var input = "  # comment\n  {'val'}"u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackItemStart(input, ref cursor, unitComplete: true);
        Assert.Equal(PaktUnitSyntax.PackItemStartKind.HasValue, result);
    }

    [Fact]
    public void ProbePackBoundary_NeedMoreData()
    {
        var input = "  "u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackBoundary(input, ref cursor, unitComplete: false);
        Assert.Equal(PaktUnitSyntax.PackBoundaryKind.NeedMoreData, result);
    }

    [Fact]
    public void SkipInsignificant_CarriageReturn()
    {
        var input = "  \r\n  val"u8.ToArray();
        var result = PaktUnitSyntax.SkipInsignificant(input, 0, skipNewlines: true);
        Assert.Equal(6, result);
    }

    // -- PaktConvertContext: ForSegment/CreateError --

    [Fact]
    public void DeserializeError_HasStatementAndFieldPath()
    {
        var pakt = Bytes("host:str = 'x'\nunknown:int = 1\n");
        var opts = new DeserializeOptions { UnknownFields = UnknownFieldPolicy.Error };
        var ex = Assert.Throws<PaktDeserializeException>(() =>
            PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, opts));
        Assert.Contains("unknown", ex.Message);
    }

    [Fact]
    public void DeserializeException_ToString_IncludesPosition()
    {
        var ex = new PaktDeserializeException("test", new PaktPosition(3, 7), "stmt", "field", PaktErrorCode.TypeMismatch);
        var str = ex.ToString();
        Assert.Contains("3:7", str);
    }

    [Fact]
    public void DeserializeException_WithInner()
    {
        var inner = new Exception("inner");
        var ex = new PaktDeserializeException("test", new PaktPosition(1, 1), "s", "f", PaktErrorCode.Syntax, inner);
        Assert.Same(inner, ex.InnerException);
    }
}

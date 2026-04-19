using System.Text;
using Pakt;
using Pakt.Serialization;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests;

/// <summary>
/// Tests targeting PaktDeserializationRuntime and PaktSerializationRuntime coverage gaps.
/// </summary>
public class RuntimeCoverageTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // -----------------------------------------------------------------------
    // Deserialization: scalars via generated + runtime paths
    // -----------------------------------------------------------------------

    [Fact]
    public void Deserialize_Decimal()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:{val:dec} = {123.45}\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
    }

    [Fact]
    public void Deserialize_Guid()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("v:{id:uuid} = {550e8400-e29b-41d4-a716-446655440000}\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
    }

    [Fact]
    public void Deserialize_Timestamp()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("v:{ts:ts} = {2026-01-01T00:00:00Z}\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
    }

    [Fact]
    public void Deserialize_NilNullable_ReturnsNull()
    {
        var pakt = Bytes("n:{label:str?, count:int?} = {nil, nil}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithNullable>();
        Assert.Null(result.Label);
        Assert.Null(result.Count);
    }

    [Fact]
    public void Deserialize_List_Strings()
    {
        var pakt = Bytes("t:{tags:[str]} = {['x', 'y']}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithList>();
        Assert.Equal(["x", "y"], result.Tags);
    }

    [Fact]
    public void Deserialize_Map_StringInt()
    {
        var pakt = Bytes("m:{scores:<str ; int>} = {<'a' ; 10, 'b' ; 20>}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithMap>();
        Assert.Equal(10, result.Scores["a"]);
        Assert.Equal(20, result.Scores["b"]);
    }

    [Fact]
    public void Deserialize_EmptyList()
    {
        var pakt = Bytes("t:{tags:[str]} = {[]}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithList>();
        Assert.Empty(result.Tags);
    }

    [Fact]
    public void Deserialize_EmptyMap()
    {
        var pakt = Bytes("m:{scores:<str ; int>} = {<>}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithMap>();
        Assert.Empty(result.Scores);
    }

    [Fact]
    public void Deserialize_NestedStruct()
    {
        var pakt = Bytes("p:{name:str, home:{city:str, zip:int}} = {'Bob', {'SF', 94105}}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<PersonWithAddress>();
        Assert.Equal("Bob", result.Name);
        Assert.Equal("SF", result.Home.City);
        Assert.Equal(94105, result.Home.Zip);
    }

    // -----------------------------------------------------------------------
    // Serialization: round-trip coverage
    // -----------------------------------------------------------------------

    [Fact]
    public void Serialize_SimpleServer_RoundTrip()
    {
        var original = new SimpleServer { Host = "test.com", Port = 443 };
        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<SimpleServer>(bytes, TestPaktContext.Default);
        Assert.Equal("test.com", result.Host);
        Assert.Equal(443, result.Port);
    }

    [Fact]
    public void Serialize_WithNullable_NilValues()
    {
        var original = new WithNullable { Label = null, Count = null };
        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithNullable>(bytes, TestPaktContext.Default);
        Assert.Null(result.Label);
        Assert.Null(result.Count);
    }

    [Fact]
    public void Serialize_WithNullable_HasValues()
    {
        var original = new WithNullable { Label = "hi", Count = 7 };
        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithNullable>(bytes, TestPaktContext.Default);
        Assert.Equal("hi", result.Label);
        Assert.Equal(7, result.Count);
    }

    [Fact]
    public void Serialize_WithList_RoundTrip()
    {
        var original = new WithList { Tags = ["a", "b", "c"] };
        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithList>(bytes, TestPaktContext.Default);
        Assert.Equal(["a", "b", "c"], result.Tags);
    }

    [Fact]
    public void Serialize_WithMap_RoundTrip()
    {
        var original = new WithMap { Scores = new() { ["x"] = 1, ["y"] = 2 } };
        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithMap>(bytes, TestPaktContext.Default);
        Assert.Equal(1, result.Scores["x"]);
        Assert.Equal(2, result.Scores["y"]);
    }

    [Fact]
    public void Serialize_Nested_RoundTrip()
    {
        var original = new PersonWithAddress
        {
            Name = "Eve",
            Home = new Address { City = "LA", Zip = 90001 }
        };
        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<PersonWithAddress>(bytes, TestPaktContext.Default);
        Assert.Equal("Eve", result.Name);
        Assert.Equal("LA", result.Home.City);
        Assert.Equal(90001, result.Home.Zip);
    }

    // -----------------------------------------------------------------------
    // Materializer policies
    // -----------------------------------------------------------------------

    [Fact]
    public void Materialize_DuplicateLastWins()
    {
        var pakt = Bytes("host:str = 'first'\nport:int = 1\nhost:str = 'last'\nport:int = 9\n");
        var options = new DeserializeOptions { Duplicates = DuplicatePolicy.LastWins };
        var result = PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options);
        Assert.Equal("last", result.Host);
        Assert.Equal(9, result.Port);
    }

    [Fact]
    public void Materialize_Pack_ListPack()
    {
        var pakt = Bytes("host:str = 'main'\nport:int = 80\n");
        var result = PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default);
        Assert.Equal("main", result.Host);
        Assert.Equal(80, result.Port);
    }

    // -----------------------------------------------------------------------
    // Async materializer
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DeserializeAsync_SimpleServer()
    {
        var pakt = Bytes("host:str = 'async'\nport:int = 99\n");
        var result = await PaktSerializer.DeserializeAsync<SimpleServer>(
            new MemoryStream(pakt), TestPaktContext.Default);
        Assert.Equal("async", result.Host);
        Assert.Equal(99, result.Port);
    }

    [Fact]
    public async Task DeserializeAsync_WithNested()
    {
        var pakt = Bytes("name:str = 'Async'\nhome:{city:str, zip:int} = {'NY', 10001}\n");
        var result = await PaktSerializer.DeserializeAsync<PersonWithAddress>(
            new MemoryStream(pakt), TestPaktContext.Default);
        Assert.Equal("Async", result.Name);
        Assert.Equal("NY", result.Home.City);
    }

    [Fact]
    public async Task DeserializeAsync_UnknownFieldSkip()
    {
        var pakt = Bytes("host:str = 'srv'\nextra:int = 0\nport:int = 80\n");
        var result = await PaktSerializer.DeserializeAsync<SimpleServer>(
            new MemoryStream(pakt), TestPaktContext.Default);
        Assert.Equal("srv", result.Host);
        Assert.Equal(80, result.Port);
    }

    [Fact]
    public async Task DeserializeAsync_UnknownFieldError()
    {
        var pakt = Bytes("host:str = 'srv'\nextra:int = 0\n");
        var options = new DeserializeOptions { UnknownFields = UnknownFieldPolicy.Error };
        await Assert.ThrowsAsync<PaktDeserializeException>(() =>
            PaktSerializer.DeserializeAsync<SimpleServer>(
                new MemoryStream(pakt), TestPaktContext.Default, options).AsTask());
    }

    [Fact]
    public async Task DeserializeAsync_DuplicateFirstWins()
    {
        var pakt = Bytes("host:str = 'first'\nport:int = 1\nhost:str = 'second'\nport:int = 2\n");
        var options = new DeserializeOptions { Duplicates = DuplicatePolicy.FirstWins };
        var result = await PaktSerializer.DeserializeAsync<SimpleServer>(
            new MemoryStream(pakt), TestPaktContext.Default, options);
        Assert.Equal("first", result.Host);
        Assert.Equal(1, result.Port);
    }
}

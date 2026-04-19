using System.Text;
using Pakt;
using Pakt.Serialization;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests;

/// <summary>
/// Tests targeting PaktDeserializationRuntime and PaktSerializationRuntime
/// uncovered branches — scalars, composites, nil, converters, errors.
/// </summary>
public class DeserializationRuntimeCoverageTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // -- Scalar type branches in ReadObject/TryReadScalar --

    [Fact]
    public void ReadValue_Long() { AssertScalar<long>("v:int = 42\n", 42L); }

    [Fact]
    public void ReadValue_Int() { AssertScalar<int>("v:int = 7\n", 7); }

    [Fact]
    public void ReadValue_Short() { AssertScalar<short>("v:int = 3\n", (short)3); }

    [Fact]
    public void ReadValue_Byte() { AssertScalar<byte>("v:int = 1\n", (byte)1); }

    [Fact]
    public void ReadValue_ULong() { AssertScalar<ulong>("v:int = 100\n", 100UL); }

    [Fact]
    public void ReadValue_UInt() { AssertScalar<uint>("v:int = 50\n", 50U); }

    [Fact]
    public void ReadValue_UShort() { AssertScalar<ushort>("v:int = 5\n", (ushort)5); }

    [Fact]
    public void ReadValue_SByte() { AssertScalar<sbyte>("v:int = 2\n", (sbyte)2); }

    [Fact]
    public void ReadValue_Decimal() { AssertScalar<decimal>("v:dec = 3.14\n", 3.14m); }

    [Fact]
    public void ReadValue_Double() { AssertScalar<double>("v:float = 2.5e1\n", 25.0); }

    [Fact]
    public void ReadValue_Float() { AssertScalar<float>("v:float = 1.0e0\n", 1.0f); }

    [Fact]
    public void ReadValue_Bool_True() { AssertScalar<bool>("v:bool = true\n", true); }

    [Fact]
    public void ReadValue_Bool_False() { AssertScalar<bool>("v:bool = false\n", false); }

    [Fact]
    public void ReadValue_String() { AssertScalar<string>("v:str = 'hello'\n", "hello"); }

    [Fact]
    public void ReadValue_Guid()
    {
        var guid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        AssertScalar<Guid>("v:uuid = 550e8400-e29b-41d4-a716-446655440000\n", guid);
    }

    [Fact]
    public void ReadValue_DateOnly()
    {
        AssertScalar<DateOnly>("v:date = 2026-01-15\n", new DateOnly(2026, 1, 15));
    }

    [Fact]
    public void ReadValue_DateTimeOffset()
    {
        var expected = DateTimeOffset.Parse("2026-01-15T10:30:00Z");
        AssertScalar<DateTimeOffset>("v:ts = 2026-01-15T10:30:00Z\n", expected);
    }

    [Fact]
    public void ReadValue_ByteArray()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:bin = x'48656c6c6f'\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<byte[]>();
        Assert.Equal("Hello"u8.ToArray(), result);
    }

    // -- Nil handling --

    [Fact]
    public void ReadValue_NilNullableString()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:str? = nil\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<string?>();
        Assert.Null(result);
    }

    [Fact]
    public void ReadValue_NilNullableInt()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:int? = nil\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<int?>();
        Assert.Null(result);
    }

    // -- Composite types via runtime --

    [Fact]
    public void ReadValue_ListOfInts()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:[int] = [1, 2, 3]\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<List<int>>();
        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public void ReadValue_DictionaryStringInt()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:<str ; int> = <'a' ; 1, 'b' ; 2>\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<Dictionary<string, int>>();
        Assert.Equal(1, result["a"]);
        Assert.Equal(2, result["b"]);
    }

    [Fact]
    public void ReadValue_EmptyDictionary()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:<str ; int> = <>\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<Dictionary<string, int>>();
        Assert.Empty(result);
    }

    [Fact]
    public void ReadValue_EmptyList()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:[int] = []\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<List<int>>();
        Assert.Empty(result);
    }

    [Fact]
    public void ReadValue_IntArray()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:[int] = [10, 20]\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<int[]>();
        Assert.Equal([10, 20], result);
    }

    // -- Pack iteration with various types --

    [Fact]
    public void ReadPack_Strings()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:[str] << 'a', 'b', 'c'\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var items = reader.ReadPack<string>().ToList();
        Assert.Equal(["a", "b", "c"], items);
    }

    [Fact]
    public void ReadPack_Ints()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:[int] << 1, 2, 3\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var items = reader.ReadPack<int>().ToList();
        Assert.Equal([1, 2, 3], items);
    }

    [Fact]
    public void ReadPack_EmptyPack()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:[int] <<\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var items = reader.ReadPack<int>().ToList();
        Assert.Empty(items);
    }

    [Fact]
    public void ReadMapPack_StringToInt()
    {
        using var reader = PaktMemoryReader.Create(Bytes("v:<str ; int> << 'x' ; 1, 'y' ; 2\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var entries = reader.ReadMapPack<string, int>().ToList();
        Assert.Equal("x", entries[0].Key);
        Assert.Equal(2, entries[1].Value);
    }

    // -- Serialization runtime branches --

    [Fact]
    public void Serialize_SimpleServer()
    {
        var server = new SimpleServer { Host = "h", Port = 1 };
        var bytes = PaktSerializer.Serialize(server, TestPaktContext.Default);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Serialize_PersonWithAddress()
    {
        var person = new PersonWithAddress
        {
            Name = "N",
            Home = new Address { City = "C", Zip = 1 }
        };
        var bytes = PaktSerializer.Serialize(person, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<PersonWithAddress>(bytes, TestPaktContext.Default);
        Assert.Equal("N", result.Name);
    }

    [Fact]
    public void Serialize_WithNullable_BothNull()
    {
        var val = new WithNullable();
        var bytes = PaktSerializer.Serialize(val, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithNullable>(bytes, TestPaktContext.Default);
        Assert.Null(result.Label);
        Assert.Null(result.Count);
    }

    [Fact]
    public void Serialize_WithList()
    {
        var val = new WithList { Tags = ["x", "y"] };
        var bytes = PaktSerializer.Serialize(val, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithList>(bytes, TestPaktContext.Default);
        Assert.Equal(["x", "y"], result.Tags);
    }

    [Fact]
    public void Serialize_WithMap()
    {
        var val = new WithMap { Scores = new() { ["k"] = 9 } };
        var bytes = PaktSerializer.Serialize(val, TestPaktContext.Default);
        var result = PaktSerializer.Deserialize<WithMap>(bytes, TestPaktContext.Default);
        Assert.Equal(9, result.Scores["k"]);
    }

    // -- Materializer: various pack binding paths --

    [Fact]
    public void Materialize_ListPack()
    {
        var pakt = Bytes("tags:[str] << 'a', 'b'\n");
        var result = PaktSerializer.Deserialize<WithList>(pakt, TestPaktContext.Default);
        Assert.Equal(["a", "b"], result.Tags);
    }

    [Fact]
    public void Materialize_MapPack()
    {
        var pakt = Bytes("scores:<str ; int> << 'a' ; 1, 'b' ; 2\n");
        var result = PaktSerializer.Deserialize<WithMap>(pakt, TestPaktContext.Default);
        Assert.Equal(1, result.Scores["a"]);
        Assert.Equal(2, result.Scores["b"]);
    }

    // -- Stream reader: internal pack APIs --

    [Fact]
    public async Task StreamReader_ReadPackValuesAsync_Ints()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("nums:[int] << 10, 20\n")), TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());

        var items = new List<object?>();
        await foreach (var item in reader.ReadPackValuesAsync(typeof(int)))
            items.Add(item);
        Assert.Equal(2, items.Count);
        Assert.Equal(10, (int)items[0]!);
    }

    [Fact]
    public async Task StreamReader_SkipValueCoreAsync()
    {
        await using var reader = PaktStreamReader.Create(
            new MemoryStream(Bytes("a:{host:str, port:int} = {'skip', 0}\nb:int = 42\n")),
            TestPaktContext.Default);
        Assert.True(await reader.ReadStatementAsync());
        await reader.SkipAsync();
        Assert.True(await reader.ReadStatementAsync());
        Assert.Equal("b", reader.StatementName);
        var val = await reader.ReadValueAsync<int>();
        Assert.Equal(42, val);
    }

    // -- Helper --
    private static void AssertScalar<T>(string pakt, T expected)
    {
        using var reader = PaktMemoryReader.Create(Bytes(pakt), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<T>();
        Assert.Equal(expected, result);
    }
}

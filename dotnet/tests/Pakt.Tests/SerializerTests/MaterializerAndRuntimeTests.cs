using System.Buffers;
using System.Text;
using Pakt;
using Pakt.Serialization;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests.SerializerTests;

public class MaterializerAndRuntimeTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Deserialize_WithNullableNilValue()
    {
        var pakt = Bytes("n:{label:str?, count:int?} = {'hello', nil}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithNullable>();
        Assert.Equal("hello", result.Label);
        Assert.Null(result.Count);
    }

    [Fact]
    public void Deserialize_WithListProperty()
    {
        var pakt = Bytes("t:{tags:[str]} = {['a', 'b', 'c']}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithList>();
        Assert.Equal(["a", "b", "c"], result.Tags);
    }

    [Fact]
    public void Deserialize_WithMapProperty()
    {
        var pakt = Bytes("m:{scores:<str ; int>} = {<'alice' ; 100, 'bob' ; 200>}\n");
        using var reader = PaktMemoryReader.Create(pakt, TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var result = reader.ReadValue<WithMap>();
        Assert.Equal(100, result.Scores["alice"]);
        Assert.Equal(200, result.Scores["bob"]);
    }

    [Fact]
    public void Materialize_UnknownFieldPolicy_Skip()
    {
        var pakt = Bytes("host:str = 'srv'\nunknown:int = 99\nport:int = 80\n");
        var result = PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default);
        Assert.Equal("srv", result.Host);
        Assert.Equal(80, result.Port);
    }

    [Fact]
    public void Materialize_UnknownFieldPolicy_Error()
    {
        var pakt = Bytes("host:str = 'srv'\nunknown:int = 99\nport:int = 80\n");
        var options = new DeserializeOptions { UnknownFields = UnknownFieldPolicy.Error };
        Assert.Throws<PaktDeserializeException>(() =>
            PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options));
    }

    [Fact]
    public void Materialize_DuplicatePolicy_LastWins()
    {
        var pakt = Bytes("host:str = 'first'\nport:int = 1\nhost:str = 'second'\nport:int = 2\n");
        var options = new DeserializeOptions { Duplicates = DuplicatePolicy.LastWins };
        var result = PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options);
        Assert.Equal("second", result.Host);
        Assert.Equal(2, result.Port);
    }

    [Fact]
    public void Materialize_DuplicatePolicy_FirstWins()
    {
        var pakt = Bytes("host:str = 'first'\nport:int = 1\nhost:str = 'second'\nport:int = 2\n");
        var options = new DeserializeOptions { Duplicates = DuplicatePolicy.FirstWins };
        var result = PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options);
        Assert.Equal("first", result.Host);
        Assert.Equal(1, result.Port);
    }

    [Fact]
    public void Materialize_DuplicatePolicy_Error()
    {
        var pakt = Bytes("host:str = 'first'\nport:int = 1\nhost:str = 'second'\n");
        var options = new DeserializeOptions { Duplicates = DuplicatePolicy.Error };
        Assert.Throws<PaktDeserializeException>(() =>
            PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options));
    }

    [Fact]
    public void Materialize_MissingFieldPolicy_Error()
    {
        var pakt = Bytes("host:str = 'srv'\n");
        var options = new DeserializeOptions { MissingFields = MissingFieldPolicy.Error };
        Assert.Throws<PaktDeserializeException>(() =>
            PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options));
    }

    [Fact]
    public void ReadValue_ScalarInt()
    {
        using var reader = PaktMemoryReader.Create(Bytes("n:int = 42\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var val = reader.ReadValue<int>();
        Assert.Equal(42, val);
    }

    [Fact]
    public void ReadValue_ScalarString()
    {
        using var reader = PaktMemoryReader.Create(Bytes("s:str = 'hello'\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var val = reader.ReadValue<string>();
        Assert.Equal("hello", val);
    }

    [Fact]
    public void ReadValue_ScalarBool()
    {
        using var reader = PaktMemoryReader.Create(Bytes("b:bool = true\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var val = reader.ReadValue<bool>();
        Assert.True(val);
    }

    [Fact]
    public void ReadValue_OnPack_ThrowsWithCorrectMessage()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("items:[int] << 1\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var ex = Assert.Throws<InvalidOperationException>(() => reader.ReadValue<int>());
        Assert.Contains("ReadPack", ex.Message);
    }

    [Fact]
    public void ReadValue_OnMapPack_ThrowsWithCorrectMessage()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("m:<str ; int> << 'a' ; 1\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var ex = Assert.Throws<InvalidOperationException>(() => reader.ReadValue<int>());
        Assert.Contains("ReadMapPack", ex.Message);
    }

    [Fact]
    public void ReadPack_FallsBackToRuntimeForUnregisteredType()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("nums:[int] << 1, 2, 3\n"), TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var items = reader.ReadPack<long>().ToList();
        Assert.Equal([1L, 2L, 3L], items);
    }

    [Fact]
    public void ReadMapPack_WithStructValues()
    {
        using var reader = PaktMemoryReader.Create(
            Bytes("servers:<str ; {host:str, port:int}> << 'web' ; {'localhost', 80}, 'api' ; {'0.0.0.0', 443}\n"),
            TestPaktContext.Default);
        Assert.True(reader.ReadStatement());
        var entries = reader.ReadMapPack<string, SimpleServer>().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("web", entries[0].Key);
        Assert.Equal("localhost", entries[0].Value.Host);
        Assert.Equal(443, entries[1].Value.Port);
    }

    [Fact]
    public void Dispose_OwnedMemory_DisposesOwner()
    {
        var data = Bytes("s:int = 1\n");
        var owner = MemoryPool<byte>.Shared.Rent(data.Length);
        data.CopyTo(owner.Memory.Span);

        var reader = PaktMemoryReader.Create(owner, data.Length, TestPaktContext.Default);
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.ReadStatement());
    }

    [Fact]
    public void Create_OwnedMemory_InvalidLength_Throws()
    {
        var owner = MemoryPool<byte>.Shared.Rent(10);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PaktMemoryReader.Create(owner, -1, TestPaktContext.Default));
        owner.Dispose();
    }

    [Fact]
    public void ReadStatement_ThrowsWhenDisposed()
    {
        var reader = PaktMemoryReader.Create(Bytes("s:int = 1\n"), TestPaktContext.Default);
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadStatement());
    }

    [Fact]
    public void Skip_ThrowsWhenNoStatement()
    {
        using var reader = PaktMemoryReader.Create(Bytes("s:int = 1\n"), TestPaktContext.Default);
        Assert.Throws<InvalidOperationException>(() => reader.Skip());
    }
}

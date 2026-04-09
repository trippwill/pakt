using System.Buffers;
using System.Text;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Tests.SerializerTests;

// --- Test types ---

public class SimpleServer
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

public class WithNullable
{
    public string? Label { get; set; }
    public int? Count { get; set; }
}

public class WithList
{
    public List<string> Tags { get; set; } = new();
}

public class WithMap
{
    public Dictionary<string, int> Scores { get; set; } = new();
}

public class Address
{
    public string City { get; set; } = "";
    public int Zip { get; set; }
}

public class PersonWithAddress
{
    public string Name { get; set; } = "";
    public Address Home { get; set; } = new();
}

[PaktSerializable(typeof(SimpleServer))]
[PaktSerializable(typeof(WithNullable))]
[PaktSerializable(typeof(WithList))]
[PaktSerializable(typeof(WithMap))]
[PaktSerializable(typeof(Address))]
[PaktSerializable(typeof(PersonWithAddress))]
public partial class TestPaktContext : PaktSerializerContext { }

// --- Tests ---

public class GeneratedSerializerTests
{
    [Fact]
    public void Default_Singleton_NotNull()
    {
        Assert.NotNull(TestPaktContext.Default);
        Assert.Same(TestPaktContext.Default, TestPaktContext.Default);
    }

    [Fact]
    public void TypeInfo_SimpleServer_HasProperties()
    {
        var info = TestPaktContext.Default.SimpleServer;
        Assert.NotNull(info);
        Assert.Equal(2, info.Properties.Count);
        Assert.Equal("host", info.Properties[0].PaktName);
        Assert.Equal("port", info.Properties[1].PaktName);
    }

    [Fact]
    public void Deserialize_SimpleServer()
    {
        var pakt = "server:{host:str, port:int} = {'localhost', 8080}\n"u8;
        var reader = new PaktReader(pakt);

        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var server = TestPaktContext.DeserializeSimpleServer(ref reader);

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);

        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_SimpleServer()
    {
        var original = new SimpleServer { Host = "example.com", Port = 443 };
        var typeInfo = TestPaktContext.Default.SimpleServer;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("s", typeInfo.PaktType);
        TestPaktContext.SerializeSimpleServer(writer, original);
        writer.WriteAssignmentEnd();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var deserialized = TestPaktContext.DeserializeSimpleServer(ref reader);

        Assert.Equal(original.Host, deserialized.Host);
        Assert.Equal(original.Port, deserialized.Port);

        reader.Dispose();
        writer.Dispose();
    }

    [Fact]
    public void Deserialize_WithNullableValues()
    {
        var pakt = "n:{label:str?, count:int?} = {'hello', nil}\n"u8;
        var reader = new PaktReader(pakt);

        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializeWithNullable(ref reader);

        Assert.Equal("hello", result.Label);
        Assert.Null(result.Count);

        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_WithNullable_Null()
    {
        var original = new WithNullable { Label = null, Count = null };
        var typeInfo = TestPaktContext.Default.WithNullable;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("n", typeInfo.PaktType);
        TestPaktContext.SerializeWithNullable(writer, original);
        writer.WriteAssignmentEnd();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializeWithNullable(ref reader);

        Assert.Null(result.Label);
        Assert.Null(result.Count);

        reader.Dispose();
        writer.Dispose();
    }

    [Fact]
    public void Deserialize_WithList()
    {
        var pakt = "t:{tags:[str]} = {['a', 'b', 'c']}\n"u8;
        var reader = new PaktReader(pakt);

        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializeWithList(ref reader);

        Assert.Equal(new[] { "a", "b", "c" }, result.Tags);

        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_WithList()
    {
        var original = new WithList { Tags = new List<string> { "x", "y" } };
        var typeInfo = TestPaktContext.Default.WithList;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("t", typeInfo.PaktType);
        TestPaktContext.SerializeWithList(writer, original);
        writer.WriteAssignmentEnd();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializeWithList(ref reader);

        Assert.Equal(original.Tags, result.Tags);

        reader.Dispose();
        writer.Dispose();
    }

    [Fact]
    public void Deserialize_WithMap()
    {
        var pakt = "m:{scores:<str ; int>} = {< 'alice' ; 10, 'bob' ; 20 >}\n"u8;
        var reader = new PaktReader(pakt);

        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializeWithMap(ref reader);

        Assert.Equal(10, result.Scores["alice"]);
        Assert.Equal(20, result.Scores["bob"]);

        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_WithMap()
    {
        var original = new WithMap
        {
            Scores = new Dictionary<string, int> { { "x", 1 }, { "y", 2 } }
        };
        var typeInfo = TestPaktContext.Default.WithMap;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("m", typeInfo.PaktType);
        TestPaktContext.SerializeWithMap(writer, original);
        writer.WriteAssignmentEnd();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializeWithMap(ref reader);

        Assert.Equal(original.Scores, result.Scores);

        reader.Dispose();
        writer.Dispose();
    }

    [Fact]
    public void Deserialize_Nested()
    {
        var pakt = "p:{name:str, home:{city:str, zip:int}} = {'Alice', {'NYC', 10001}}\n"u8;
        var reader = new PaktReader(pakt);

        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializePersonWithAddress(ref reader);

        Assert.Equal("Alice", result.Name);
        Assert.Equal("NYC", result.Home.City);
        Assert.Equal(10001, result.Home.Zip);

        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_Nested()
    {
        var original = new PersonWithAddress
        {
            Name = "Bob",
            Home = new Address { City = "LA", Zip = 90001 }
        };

        var typeInfo = TestPaktContext.Default.PersonWithAddress;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("p", typeInfo.PaktType);
        TestPaktContext.SerializePersonWithAddress(writer, original);
        writer.WriteAssignmentEnd();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        reader.Read(); // StructStart
        var result = TestPaktContext.DeserializePersonWithAddress(ref reader);

        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.Home.City, result.Home.City);
        Assert.Equal(original.Home.Zip, result.Home.Zip);

        reader.Dispose();
        writer.Dispose();
    }
}

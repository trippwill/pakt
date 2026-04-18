using System.Buffers;
using System.Text;
using Pakt;
using Pakt.Serialization;

namespace Pakt.Tests.SerializerTests;

public class PaktSerializerConvenienceTests
{
    [Fact]
    public void Deserialize_SingleAssignment()
    {
        var pakt = "host:str = 'localhost'\nport:int = 8080\n"u8.ToArray();
        var server = PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default);

        Assert.Equal("localhost", server.Host);
        Assert.Equal(8080, server.Port);
    }

    [Fact]
    public void Deserialize_WithNullable()
    {
        var pakt = "label:str? = 'hello'\ncount:int? = nil\n"u8.ToArray();
        var result = PaktSerializer.Deserialize<WithNullable>(pakt, TestPaktContext.Default);

        Assert.Equal("hello", result.Label);
        Assert.Null(result.Count);
    }

    [Fact]
    public void Deserialize_Nested()
    {
        var pakt = "name:str = 'Alice'\nhome:{city:str, zip:int} = {'NYC', 10001}\n"u8.ToArray();
        var person = PaktSerializer.Deserialize<PersonWithAddress>(pakt, TestPaktContext.Default);

        Assert.Equal("Alice", person.Name);
        Assert.Equal("NYC", person.Home.City);
        Assert.Equal(10001, person.Home.Zip);
    }

    [Fact]
    public void Deserialize_WithList()
    {
        var pakt = "tags:[str] << 'a', 'b', 'c'\n"u8.ToArray();
        var result = PaktSerializer.Deserialize<WithList>(pakt, TestPaktContext.Default);

        Assert.Equal(new[] { "a", "b", "c" }, result.Tags);
    }

    [Fact]
    public void Deserialize_WithMap()
    {
        var pakt = "scores:<str ; int> << 'alice' ; 10, 'bob' ; 20\n"u8.ToArray();
        var result = PaktSerializer.Deserialize<WithMap>(pakt, TestPaktContext.Default);

        Assert.Equal(10, result.Scores["alice"]);
        Assert.Equal(20, result.Scores["bob"]);
    }

    [Fact]
    public void Serialize_SimpleServer()
    {
        var server = new SimpleServer { Host = "example.com", Port = 443 };
        var bytes = PaktSerializer.Serialize(server, TestPaktContext.Default, "s");

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("host:str = 'example.com'", text);
        Assert.Contains("port:int = 443", text);
    }

    [Fact]
    public void Serialize_WithNullable_AllNull()
    {
        var obj = new WithNullable { Label = null, Count = null };
        var bytes = PaktSerializer.Serialize(obj, TestPaktContext.Default, "n");

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("label:str? = nil", text);
        Assert.Contains("count:int? = nil", text);
    }

    [Fact]
    public void Serialize_WithList()
    {
        var obj = new WithList { Tags = new List<string> { "x", "y" } };
        var bytes = PaktSerializer.Serialize(obj, TestPaktContext.Default, "t");

        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("tags:[str] = [", text);
        Assert.Contains("'x'", text);
        Assert.Contains("'y'", text);
    }

    [Fact]
    public void RoundTrip_SimpleServer()
    {
        var original = new SimpleServer { Host = "roundtrip.com", Port = 9090 };

        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default, "s");
        var result = PaktSerializer.Deserialize<SimpleServer>(bytes, TestPaktContext.Default);

        Assert.Equal(original.Host, result.Host);
        Assert.Equal(original.Port, result.Port);
    }

    [Fact]
    public void RoundTrip_Nested()
    {
        var original = new PersonWithAddress
        {
            Name = "Bob",
            Home = new Address { City = "LA", Zip = 90001 }
        };

        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default, "p");
        var result = PaktSerializer.Deserialize<PersonWithAddress>(bytes, TestPaktContext.Default);

        Assert.Equal(original.Name, result.Name);
        Assert.Equal(original.Home.City, result.Home.City);
        Assert.Equal(original.Home.Zip, result.Home.Zip);
    }

    [Fact]
    public void RoundTrip_WithNullable()
    {
        var original = new WithNullable { Label = "test", Count = 42 };

        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default, "n");
        var result = PaktSerializer.Deserialize<WithNullable>(bytes, TestPaktContext.Default);

        Assert.Equal(original.Label, result.Label);
        Assert.Equal(original.Count, result.Count);
    }

    [Fact]
    public void RoundTrip_WithList()
    {
        var original = new WithList { Tags = new List<string> { "alpha", "beta", "gamma" } };

        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default, "t");
        var result = PaktSerializer.Deserialize<WithList>(bytes, TestPaktContext.Default);

        Assert.Equal(original.Tags, result.Tags);
    }

    [Fact]
    public void RoundTrip_WithMap()
    {
        var original = new WithMap
        {
            Scores = new Dictionary<string, int> { { "x", 100 }, { "y", 200 } }
        };

        var bytes = PaktSerializer.Serialize(original, TestPaktContext.Default, "m");
        var result = PaktSerializer.Deserialize<WithMap>(bytes, TestPaktContext.Default);

        Assert.Equal(original.Scores, result.Scores);
    }

    [Fact]
    public void Deserialize_EmptyUnit_ReturnsDefaultObject()
    {
        var result = PaktSerializer.Deserialize<SimpleServer>(Array.Empty<byte>(), TestPaktContext.Default);

        Assert.Equal(string.Empty, result.Host);
        Assert.Equal(0, result.Port);
    }

    [Fact]
    public void Deserialize_UnknownStatementError_PreservesStatementPosition()
    {
        var pakt = "unknown:int = 1\n"u8.ToArray();
        var options = new DeserializeOptions { UnknownFields = UnknownFieldPolicy.Error };

        var ex = Assert.Throws<PaktDeserializeException>(
            () => PaktSerializer.Deserialize<SimpleServer>(pakt, TestPaktContext.Default, options));

        Assert.Equal(new PaktPosition(1, 1), ex!.Position);
        Assert.Equal("unknown", ex.StatementName);
    }

    [Fact]
    public void Serialize_UsesPropertyNames()
    {
        var server = new SimpleServer { Host = "test", Port = 1 };
        var bytes = PaktSerializer.Serialize(server, TestPaktContext.Default);

        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("host:str = ", text);
    }

    [Fact]
    public void Context_Deserialize_RoundTrip()
    {
        var server = new SimpleServer { Host = "ctx", Port = 42 };
        var bytes = PaktSerializer.Serialize(server, TestPaktContext.Default, "server");
        var result = PaktSerializer.Deserialize<SimpleServer>(bytes, TestPaktContext.Default);

        Assert.Equal("ctx", result.Host);
        Assert.Equal(42, result.Port);
    }

    [Fact]
    public void Context_UnregisteredType_Throws()
    {
        var bytes = Encoding.UTF8.GetBytes("x:int = 1");
        Assert.Throws<InvalidOperationException>(
            () => PaktSerializer.Deserialize<int>(bytes, TestPaktContext.Default));
    }
}

using System.Collections.Generic;
using System.Text;
using Pakt;
using Pakt.Tests.SerializerTests;

namespace Pakt.Tests.ReaderTests;

public class PaktReaderExtensionsTests
{
    private static ReadOnlySpan<byte> ToUtf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void StructFields_EnumeratesFieldsAndLeavesReaderOnAssignEnd()
    {
        var reader = new PaktReader(ToUtf8("cfg:{host:str, port:int} = {'localhost', 8080}"));

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        var names = new List<string>();

        bool HandleField(ref PaktReader current, PaktFieldEntry field)
        {
            names.Add(field.Name);
            if (field.Name == "host")
            {
                Assert.Equal(PaktScalarType.Str, field.Type.ScalarKind);
                Assert.Equal("localhost", current.GetString());
                return true;
            }

            Assert.Equal(PaktScalarType.Int, field.Type.ScalarKind);
            Assert.Equal(8080L, current.GetInt64());
            return true;
        }

        reader.StructFields(HandleField);

        Assert.Equal(["host", "port"], names);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void StructFields_SupportsNestedStructNavigation()
    {
        var reader = new PaktReader(ToUtf8("person:{name:str, home:{city:str, zip:int}} = {'Alice', {'NYC', 10001}}"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        string? name = null;
        string? city = null;
        long zip = 0;

        bool HandleNested(ref PaktReader current, PaktFieldEntry field)
        {
            switch (field.Name)
            {
                case "city":
                    city = current.GetString();
                    break;
                case "zip":
                    zip = current.GetInt64();
                    break;
            }

            return true;
        }

        bool HandleField(ref PaktReader current, PaktFieldEntry field)
        {
            switch (field.Name)
            {
                case "name":
                    name = current.GetString();
                    break;

                case "home":
                    Assert.Equal(PaktTokenType.StructStart, current.TokenType);
                    current.StructFields(HandleNested);
                    break;
            }

            return true;
        }

        reader.StructFields(HandleField);

        Assert.Equal("Alice", name);
        Assert.Equal("NYC", city);
        Assert.Equal(10001L, zip);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void StructFields_EarlyBreak_DrainsUntouchedCompositeField()
    {
        var reader = new PaktReader(ToUtf8("cfg:{a:{x:int}, b:str} = {{1}, 'two'}\nname:str = 'after'"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        bool HandleField(ref PaktReader current, PaktFieldEntry field)
        {
            Assert.Equal("a", field.Name);
            Assert.Equal(PaktTokenType.StructStart, current.TokenType);
            return false;
        }

        reader.StructFields(HandleField);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal("after", reader.GetString());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void TupleElements_EnumeratesIndicesAndSupportsNestedStructs()
    {
        var reader = new PaktReader(ToUtf8("pair:(int, {host:str, port:int}) = (42, {'srv', 8080})"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.TupleStart, reader.TokenType);

        var indices = new List<int>();
        long port = 0;
        string? host = null;

        bool HandleStruct(ref PaktReader current, PaktFieldEntry field)
        {
            if (field.Name == "host")
                host = current.GetString();
            else if (field.Name == "port")
                port = current.GetInt64();

            return true;
        }

        bool HandleElement(ref PaktReader current, PaktTupleEntry element)
        {
            indices.Add(element.Index);
            if (element.Index == 0)
            {
                Assert.Equal(42L, current.GetInt64());
                return true;
            }

            Assert.Equal(PaktTokenType.StructStart, current.TokenType);
            current.StructFields(HandleStruct);
            return true;
        }

        reader.TupleElements(HandleElement);

        Assert.Equal([0, 1], indices);
        Assert.Equal("srv", host);
        Assert.Equal(8080L, port);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void ListElements_DeserializesTypedElements()
    {
        var reader = new PaktReader(ToUtf8("items:[{host:str, port:int}] = [{'a', 1}, {'b', 2}]"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);

        var items = new List<SimpleServer>();
        reader.ListElements<SimpleServer>(
            TestPaktContext.Default,
            item =>
            {
                items.Add(item);
                return true;
            });

        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].Host);
        Assert.Equal(2, items[1].Port);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void ListElements_EarlyBreak_DrainsRemainingElements()
    {
        var reader = new PaktReader(ToUtf8("nums:[int] = [1, 2, 3]\nname:str = 'after'"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);

        var first = 0;
        reader.ListElements<int>(
            TestPaktContext.Default,
            value =>
            {
                first = value;
                return false;
            });

        Assert.Equal(1, first);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal("after", reader.GetString());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void MapEntries_DeserializesTypedEntries()
    {
        var reader = new PaktReader(ToUtf8("users:<str ; {host:str, port:int}> = <'a' ; {'one', 1}, 'b' ; {'two', 2}>"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.MapStart, reader.TokenType);

        var entries = new List<PaktMapEntry<string, SimpleServer>>();
        reader.MapEntries<string, SimpleServer>(
            TestPaktContext.Default,
            entry =>
            {
                entries.Add(entry);
                return true;
            });

        Assert.Equal(2, entries.Count);
        Assert.Equal("a", entries[0].Key);
        Assert.Equal("one", entries[0].Value.Host);
        Assert.Equal(2, entries[1].Value.Port);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void MapEntries_EarlyBreak_DrainsRemainingEntries()
    {
        var reader = new PaktReader(ToUtf8("ports:<str ; int> = <'http' ; 80, 'https' ; 443>\nname:str = 'after'"));

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.MapStart, reader.TokenType);

        string? firstKey = null;
        int firstValue = 0;
        reader.MapEntries<string, int>(
            TestPaktContext.Default,
            entry =>
            {
                firstKey = entry.Key;
                firstValue = entry.Value;
                return false;
            });

        Assert.Equal("http", firstKey);
        Assert.Equal(80, firstValue);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.True(reader.Read());
        Assert.Equal("after", reader.GetString());
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }
}

using System.Text;

namespace Pakt.Core.Test;

// ── Test model types ──

public class SimpleConfig
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public bool Debug { get; set; }
}

public class ConfigWithList
{
    public string Name { get; set; } = "";
    public IList<int> Scores { get; set; } = new List<int>();
}

public class ConfigWithMap
{
    public string Name { get; set; } = "";
    public IDictionary<string, int> Ages { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
}

// ── Source generator context ──

[PaktSerializable(typeof(SimpleConfig))]
[PaktSerializable(typeof(ConfigWithList))]
[PaktSerializable(typeof(ConfigWithMap))]
public partial class TestSerializerContext : PaktSerializerContext;

// ── Tests ──

public class PaktUnitDeserializerTests
{
    // ═══════════════════ Basic unit deserialization ═══════════════════

    [Fact]
    public void SimpleConfig_Deserializes()
    {
        var pakt = "name:str = 'my-app'\nversion:int = 42\ndebug:bool = true"u8;
        var result = PaktUnitDeserializer.Deserialize<SimpleConfig>(
            pakt.ToArray(), TestSerializerContext.Default);

        Assert.Equal("my-app", result.Name);
        Assert.Equal(42, result.Version);
        Assert.True(result.Debug);
    }

    [Fact]
    public void SimpleConfig_OutOfOrder_Deserializes()
    {
        var pakt = "debug:bool = false\nname:str = 'test'\nversion:int = 7"u8;
        var result = PaktUnitDeserializer.Deserialize<SimpleConfig>(
            pakt.ToArray(), TestSerializerContext.Default);

        Assert.Equal("test", result.Name);
        Assert.Equal(7, result.Version);
        Assert.False(result.Debug);
    }

    // ═══════════════════ List deserialization ═══════════════════

    [Fact]
    public void ConfigWithList_AssignList_Deserializes()
    {
        var pakt = "name:str = 'test'\nscores:[int] = [90 85 92]"u8;
        var result = PaktUnitDeserializer.Deserialize<ConfigWithList>(
            pakt.ToArray(), TestSerializerContext.Default);

        Assert.Equal("test", result.Name);
        Assert.Equal([90, 85, 92], result.Scores);
    }

    [Fact]
    public void ConfigWithList_PackList_Deserializes()
    {
        var pakt = "name:str = 'test'\nscores:[int] = ~[90 85 92]"u8;
        var result = PaktUnitDeserializer.Deserialize<ConfigWithList>(
            pakt.ToArray(), TestSerializerContext.Default);

        Assert.Equal("test", result.Name);
        Assert.Equal([90, 85, 92], result.Scores);
    }

    // ═══════════════════ Map deserialization ═══════════════════

    [Fact]
    public void ConfigWithMap_AssignMap_Deserializes()
    {
        var pakt = "name:str = 'test'\nages:<str = int> = <'Alice' = 30 'Bob' = 25>"u8;
        var result = PaktUnitDeserializer.Deserialize<ConfigWithMap>(
            pakt.ToArray(), TestSerializerContext.Default);

        Assert.Equal("test", result.Name);
        Assert.Equal(30, result.Ages["Alice"]);
        Assert.Equal(25, result.Ages["Bob"]);
    }

    [Fact]
    public void ConfigWithMap_PackMap_Deserializes()
    {
        var pakt = "name:str = 'test'\nages:<str = int> = ~<'Alice' = 30 'Bob' = 25>"u8;
        var result = PaktUnitDeserializer.Deserialize<ConfigWithMap>(
            pakt.ToArray(), TestSerializerContext.Default);

        Assert.Equal("test", result.Name);
        Assert.Equal(30, result.Ages["Alice"]);
        Assert.Equal(25, result.Ages["Bob"]);
    }

    // ═══════════════════ Policy: Unknown statements ═══════════════════

    [Fact]
    public void UnknownStatement_Skip_Succeeds()
    {
        var pakt = "name:str = 'test'\nextra:int = 99\nversion:int = 1\ndebug:bool = true"u8;
        var result = PaktUnitDeserializer.Deserialize<SimpleConfig>(
            pakt.ToArray(), TestSerializerContext.Default,
            new PaktSerializationOptions { UnknownStatements = UnknownMemberPolicy.Skip });

        Assert.Equal("test", result.Name);
        Assert.Equal(1, result.Version);
    }

    [Fact]
    public void UnknownStatement_Error_Throws()
    {
        byte[] pakt = "name:str = 'test'\nextra:int = 99\nversion:int = 1\ndebug:bool = true"u8.ToArray();
        Assert.Throws<PaktParseException>(() =>
            PaktUnitDeserializer.Deserialize<SimpleConfig>(
                pakt, TestSerializerContext.Default,
                new PaktSerializationOptions { UnknownStatements = UnknownMemberPolicy.Error }));
    }

    // ═══════════════════ Policy: Missing statements ═══════════════════

    [Fact]
    public void MissingStatement_UseDefault_Succeeds()
    {
        var pakt = "name:str = 'partial'"u8;
        var result = PaktUnitDeserializer.Deserialize<SimpleConfig>(
            pakt.ToArray(), TestSerializerContext.Default,
            new PaktSerializationOptions { MissingStatements = MissingMemberPolicy.UseDefault });

        Assert.Equal("partial", result.Name);
        Assert.Equal(0, result.Version);
        Assert.False(result.Debug);
    }

    [Fact]
    public void MissingStatement_Error_Throws()
    {
        byte[] pakt = "name:str = 'partial'"u8.ToArray();
        Assert.Throws<PaktParseException>(() =>
            PaktUnitDeserializer.Deserialize<SimpleConfig>(
                pakt, TestSerializerContext.Default,
                new PaktSerializationOptions { MissingStatements = MissingMemberPolicy.Error }));
    }

    // ═══════════════════ Policy: Duplicate statements ═══════════════════

    [Fact]
    public void DuplicateStatement_LastWins()
    {
        var pakt = "name:str = 'first'\nversion:int = 1\nname:str = 'last'\ndebug:bool = true"u8;
        var result = PaktUnitDeserializer.Deserialize<SimpleConfig>(
            pakt.ToArray(), TestSerializerContext.Default,
            new PaktSerializationOptions { DuplicateStatements = DuplicatePolicy.LastWins });

        Assert.Equal("last", result.Name);
    }

    [Fact]
    public void DuplicateStatement_FirstWins()
    {
        var pakt = "name:str = 'first'\nversion:int = 1\nname:str = 'last'\ndebug:bool = true"u8;
        var result = PaktUnitDeserializer.Deserialize<SimpleConfig>(
            pakt.ToArray(), TestSerializerContext.Default,
            new PaktSerializationOptions { DuplicateStatements = DuplicatePolicy.FirstWins });

        Assert.Equal("first", result.Name);
    }

    [Fact]
    public void DuplicateStatement_Error_Throws()
    {
        byte[] pakt = "name:str = 'first'\nversion:int = 1\nname:str = 'last'\ndebug:bool = true"u8.ToArray();
        Assert.Throws<PaktParseException>(() =>
            PaktUnitDeserializer.Deserialize<SimpleConfig>(
                pakt, TestSerializerContext.Default,
                new PaktSerializationOptions { DuplicateStatements = DuplicatePolicy.Error }));
    }

    // ═══════════════════ TypeInfo metadata ═══════════════════

    [Fact]
    public void TypeInfo_HasProperties()
    {
        var typeInfo = TestSerializerContext.Default.SimpleConfig;
        Assert.NotNull(typeInfo);
        Assert.Equal(3, typeInfo.Properties.Length);
        Assert.Equal("name", typeInfo.Properties[0].PaktName);
        Assert.Equal("version", typeInfo.Properties[1].PaktName);
        Assert.Equal("debug", typeInfo.Properties[2].PaktName);
    }

    [Fact]
    public void TypeInfo_HasSignature()
    {
        var typeInfo = TestSerializerContext.Default.SimpleConfig;
        Assert.Contains("name:str", typeInfo.PaktTypeSignature, StringComparison.Ordinal);
        Assert.Contains("version:int", typeInfo.PaktTypeSignature, StringComparison.Ordinal);
        Assert.Contains("debug:bool", typeInfo.PaktTypeSignature, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeInfo_ListProperty_IsCollection()
    {
        var typeInfo = TestSerializerContext.Default.ConfigWithList;
        var scoresProp = typeInfo.Properties.ToArray().First(p => string.Equals(p.PaktName, "scores", StringComparison.Ordinal));
        Assert.True(scoresProp.IsCollection);
    }
}
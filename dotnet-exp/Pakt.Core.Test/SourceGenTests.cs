using System.Text;
using Pakt;

namespace Pakt.Core.Test;

// ── Test types ───────────────────────────────────────────────────

public class SmallDoc
{
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public bool Debug { get; set; }
}

// ── Generated context (source generator should produce the partial) ──

[PaktSerializable(typeof(SmallDoc))]
public partial class TestPaktContext : PaktSerializerContext;

// ── Tests ────────────────────────────────────────────────────────

public class SourceGenTests
{
    [Fact]
    public void GeneratedContext_HasDefault()
    {
        var ctx = TestPaktContext.Default;
        Assert.NotNull(ctx);
    }

    [Fact]
    public void GeneratedContext_GetTypeInfo_ReturnsForRegisteredType()
    {
        var ctx = TestPaktContext.Default;
        var info = ctx.GetTypeInfo<SmallDoc>();
        Assert.NotNull(info);
        Assert.Contains("name", info!.PaktTypeName, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedContext_GetTypeInfo_ReturnsNullForUnregistered()
    {
        var ctx = TestPaktContext.Default;
        var info = ctx.GetTypeInfo<string>();
        Assert.Null(info);
    }

    [Fact]
    public void GeneratedDeserialize_SmallDoc()
    {
        var pakt = "doc:{name:str version:int debug:bool} = { 'my-app' 42 true }"u8;
        using var reader = new PaktMemoryReader(new ReadOnlyMemory<byte>(pakt.ToArray()));

        // Advance through statement header to the value
        reader.ExpectToken(PaktTokenType.StatementName);
        reader.ExpectToken(PaktTokenType.TypeAnnotationStart);
        reader.ExpectToken(PaktTokenType.TypeAnnotationEnd);
        reader.ExpectToken(PaktTokenType.AssignOperator);

        var info = TestPaktContext.Default.GetTypeInfo<SmallDoc>()!;
        var doc = info.Deserialize(reader);

        Assert.Equal("my-app", doc.Name);
        Assert.Equal(42, doc.Version);
        Assert.True(doc.Debug);
    }
}

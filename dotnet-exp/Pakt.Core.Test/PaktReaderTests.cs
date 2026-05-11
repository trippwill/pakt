using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Pakt.Core.Test;

public class PaktReaderTests
{
    // ── Test infrastructure ─────────────────────────────────────────

    private record struct EventRecord(PaktEvent.Kind Kind, PaktTypeKind TypeKind, string Payload);

    private static async Task<List<EventRecord>> DrainEvents(string paktText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(paktText);
        PipeReader pipe = PipeReader.Create(new ReadOnlySequence<byte>(bytes));
        PaktReader reader = PaktReader.Create(pipe);
        List<EventRecord> events = [];

        await reader.DrainAsync((scoped in PaktEvent evt) =>
        {
            string payload = evt.Payload.IsEmpty ? "" : Encoding.UTF8.GetString(evt.Payload);
            events.Add(new EventRecord(evt.EventKind, evt.TypeKind, payload));
            return PaktReader.HandlerResult.Continue;
        }).ConfigureAwait(false);

        return events;
    }

    private static void AssertEvent(
        List<EventRecord> events, int index,
        PaktEvent.Kind expectedKind,
        PaktTypeKind expectedTypeKind = PaktTypeKind.None,
        string? expectedPayload = null)
    {
        Assert.True(index < events.Count, $"Expected event at index {index} but only {events.Count} events");
        EventRecord e = events[index];
        Assert.Equal(expectedKind, e.Kind);
        if (expectedTypeKind != PaktTypeKind.None)
            Assert.Equal(expectedTypeKind, e.TypeKind);
        if (expectedPayload is not null)
            Assert.Equal(expectedPayload, e.Payload);
    }

    // ── Empty / minimal units ───────────────────────────────────────

    [Fact]
    public async Task EmptyUnit_EmitsUnitStartAndEnd()
    {
        List<EventRecord> events = await DrainEvents("");
        Assert.Equal(2, events.Count);
        AssertEvent(events, 0, PaktEvent.Kind.UnitStart);
        AssertEvent(events, 1, PaktEvent.Kind.UnitEnd);
    }

    [Fact]
    public async Task WhitespaceOnly_EmitsUnitStartAndEnd()
    {
        List<EventRecord> events = await DrainEvents("  \n  \t  \n");
        Assert.Equal(2, events.Count);
        AssertEvent(events, 0, PaktEvent.Kind.UnitStart);
        AssertEvent(events, 1, PaktEvent.Kind.UnitEnd);
    }

    [Fact]
    public async Task CommentOnly_EmitsUnitStartAndEnd()
    {
        List<EventRecord> events = await DrainEvents("# this is a comment\n");
        Assert.Equal(2, events.Count);
        AssertEvent(events, 0, PaktEvent.Kind.UnitStart);
        AssertEvent(events, 1, PaktEvent.Kind.UnitEnd);
    }

    [Fact]
    public async Task BomPrefix_EmitsUnitStartAndEnd()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        List<EventRecord> events = await DrainEvents(Encoding.UTF8.GetString(bom));
        Assert.Equal(2, events.Count);
        AssertEvent(events, 0, PaktEvent.Kind.UnitStart);
        AssertEvent(events, 1, PaktEvent.Kind.UnitEnd);
    }

    // ── Scalar assigns ──────────────────────────────────────────────

    [Fact]
    public async Task ScalarAssign_String()
    {
        List<EventRecord> events = await DrainEvents("name:str = 'hello'");
        AssertEvent(events, 0, PaktEvent.Kind.UnitStart);
        AssertEvent(events, 1, PaktEvent.Kind.StatementStart, expectedPayload: "name");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.String);
        AssertEvent(events, 3, PaktEvent.Kind.AssignStart);
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.String, "'hello'");
        AssertEvent(events, 5, PaktEvent.Kind.AssignEnd);
        AssertEvent(events, 6, PaktEvent.Kind.UnitEnd);
        Assert.Equal(7, events.Count);
    }

    [Fact]
    public async Task ScalarAssign_Int()
    {
        List<EventRecord> events = await DrainEvents("count:int = 42");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.Int);
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "42");
    }

    [Fact]
    public async Task ScalarAssign_IntHex()
    {
        List<EventRecord> events = await DrainEvents("hex:int = 0xFF");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "0xFF");
    }

    [Fact]
    public async Task ScalarAssign_Dec()
    {
        List<EventRecord> events = await DrainEvents("price:dec = 19.99");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.Decimal);
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Decimal, "19.99");
    }

    [Fact]
    public async Task ScalarAssign_Float()
    {
        List<EventRecord> events = await DrainEvents("x:float = 6.022e23");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.Float);
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Float, "6.022e23");
    }

    [Fact]
    public async Task ScalarAssign_BoolTrue()
    {
        List<EventRecord> events = await DrainEvents("flag:bool = true");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Bool, "true");
    }

    [Fact]
    public async Task ScalarAssign_BoolFalse()
    {
        List<EventRecord> events = await DrainEvents("flag:bool = false");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Bool, "false");
    }

    [Fact]
    public async Task ScalarAssign_Uuid()
    {
        List<EventRecord> events = await DrainEvents("id:uuid = 550e8400-e29b-41d4-a716-446655440000");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.Uuid);
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Uuid, "550e8400-e29b-41d4-a716-446655440000");
    }

    [Fact]
    public async Task ScalarAssign_Date()
    {
        List<EventRecord> events = await DrainEvents("d:date = 2026-06-01");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Date, "2026-06-01");
    }

    [Fact]
    public async Task ScalarAssign_Timestamp()
    {
        List<EventRecord> events = await DrainEvents("t:ts = 2026-06-01T14:30:00Z");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Timestamp, "2026-06-01T14:30:00Z");
    }

    [Fact]
    public async Task ScalarAssign_BinHex()
    {
        List<EventRecord> events = await DrainEvents("p:bin = x'48656C6C6F'");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Binary, "x'48656C6C6F'");
    }

    [Fact]
    public async Task ScalarAssign_BinBase64()
    {
        List<EventRecord> events = await DrainEvents("p:bin = b'SGVsbG8='");
        AssertEvent(events, 4, PaktEvent.Kind.ScalarValue, PaktTypeKind.Binary, "b'SGVsbG8='");
    }

    // ── Type annotations ────────────────────────────────────────────

    [Fact]
    public async Task TypeAnnotation_Struct()
    {
        List<EventRecord> events = await DrainEvents("x:{name:str age:int} = { 'hi' 1 }");
        AssertEvent(events, 2, PaktEvent.Kind.StructTypeStart, PaktTypeKind.Struct);
        AssertEvent(events, 3, PaktEvent.Kind.FieldDecl, PaktTypeKind.String, "name");
        AssertEvent(events, 4, PaktEvent.Kind.FieldDecl, PaktTypeKind.Int, "age");
        AssertEvent(events, 5, PaktEvent.Kind.StructTypeEnd, PaktTypeKind.Struct);
    }

    [Fact]
    public async Task TypeAnnotation_Tuple()
    {
        List<EventRecord> events = await DrainEvents("x:(int str) = (1 'a')");
        AssertEvent(events, 2, PaktEvent.Kind.TupleTypeStart, PaktTypeKind.Tuple);
        AssertEvent(events, 3, PaktEvent.Kind.ElementDecl, PaktTypeKind.Int);
        AssertEvent(events, 4, PaktEvent.Kind.ElementDecl, PaktTypeKind.String);
        AssertEvent(events, 5, PaktEvent.Kind.TupleTypeEnd, PaktTypeKind.Tuple);
    }

    [Fact]
    public async Task TypeAnnotation_List()
    {
        List<EventRecord> events = await DrainEvents("x:[int] = [1]");
        AssertEvent(events, 2, PaktEvent.Kind.ListTypeStart, PaktTypeKind.List);
        AssertEvent(events, 3, PaktEvent.Kind.ScalarType, PaktTypeKind.Int);
        AssertEvent(events, 4, PaktEvent.Kind.ListTypeEnd, PaktTypeKind.List);
    }

    [Fact]
    public async Task TypeAnnotation_Map()
    {
        List<EventRecord> events = await DrainEvents("x:<str => int> = <'a' => 1>");
        AssertEvent(events, 2, PaktEvent.Kind.MapTypeStart, PaktTypeKind.Map);
        AssertEvent(events, 3, PaktEvent.Kind.ScalarType, PaktTypeKind.String);
        AssertEvent(events, 4, PaktEvent.Kind.ScalarType, PaktTypeKind.Int);
        AssertEvent(events, 5, PaktEvent.Kind.MapTypeEnd, PaktTypeKind.Map);
    }

    [Fact]
    public async Task TypeAnnotation_AtomSet()
    {
        List<EventRecord> events = await DrainEvents("x:|dev staging prod| = |dev");
        AssertEvent(events, 2, PaktEvent.Kind.AtomSetStart, PaktTypeKind.AtomSet);
        AssertEvent(events, 3, PaktEvent.Kind.AtomDecl, PaktTypeKind.AtomSet, "dev");
        AssertEvent(events, 4, PaktEvent.Kind.AtomDecl, PaktTypeKind.AtomSet, "staging");
        AssertEvent(events, 5, PaktEvent.Kind.AtomDecl, PaktTypeKind.AtomSet, "prod");
        AssertEvent(events, 6, PaktEvent.Kind.AtomSetEnd, PaktTypeKind.AtomSet);
    }

    // ── Nullable ────────────────────────────────────────────────────

    [Fact]
    public async Task Nullable_NilValue()
    {
        List<EventRecord> events = await DrainEvents("x:str? = nil");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.String);
        AssertEvent(events, 3, PaktEvent.Kind.NullableModifier);
        AssertEvent(events, 5, PaktEvent.Kind.NilValue);
    }

    [Fact]
    public async Task Nullable_WithValue()
    {
        List<EventRecord> events = await DrainEvents("x:int? = 42");
        AssertEvent(events, 2, PaktEvent.Kind.ScalarType, PaktTypeKind.Int);
        AssertEvent(events, 3, PaktEvent.Kind.NullableModifier);
        AssertEvent(events, 5, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "42");
    }

    [Fact]
    public async Task NilNonNullable_Throws()
    {
        await Assert.ThrowsAsync<PaktParseException>(
            () => DrainEvents("x:str = nil"));
    }

    // ── Composite values ────────────────────────────────────────────

    [Fact]
    public async Task StructValue_Positional()
    {
        List<EventRecord> events = await DrainEvents("x:{a:str b:int} = { 'hi' 42 }");
        int i = 7; // after type events + AssignStart
        AssertEvent(events, i++, PaktEvent.Kind.StructValueStart);
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.String, "'hi'");
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "42");
        AssertEvent(events, i++, PaktEvent.Kind.StructValueEnd);
    }

    [Fact]
    public async Task TupleValue()
    {
        List<EventRecord> events = await DrainEvents("x:(int int) = (1 2)");
        int i = 7; // after type events + AssignStart
        AssertEvent(events, i++, PaktEvent.Kind.TupleValueStart);
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "1");
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "2");
        AssertEvent(events, i++, PaktEvent.Kind.TupleValueEnd);
    }

    [Fact]
    public async Task ListValue()
    {
        List<EventRecord> events = await DrainEvents("x:[int] = [1 2 3]");
        int i = 5; // UnitStart, StatementStart, ListTypeStart, ScalarType(int), ListTypeEnd, AssignStart
        AssertEvent(events, i++, PaktEvent.Kind.AssignStart);
        AssertEvent(events, i++, PaktEvent.Kind.ListValueStart);
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "1");
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "2");
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "3");
        AssertEvent(events, i++, PaktEvent.Kind.ListValueEnd);
    }

    [Fact]
    public async Task EmptyList()
    {
        List<EventRecord> events = await DrainEvents("x:[int] = []");
        // Find ListValueStart
        int i = events.FindIndex(e => e.Kind == PaktEvent.Kind.ListValueStart);
        Assert.True(i >= 0);
        AssertEvent(events, i + 1, PaktEvent.Kind.ListValueEnd);
    }

    [Fact]
    public async Task MapValue()
    {
        List<EventRecord> events = await DrainEvents("x:<str => int> = <'a' => 1>");
        int i = events.FindIndex(e => e.Kind == PaktEvent.Kind.MapValueStart);
        Assert.True(i >= 0);
        AssertEvent(events, i++, PaktEvent.Kind.MapValueStart);
        AssertEvent(events, i++, PaktEvent.Kind.MapEntryStart);
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.String, "'a'");
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "1");
        AssertEvent(events, i++, PaktEvent.Kind.MapEntryEnd);
        AssertEvent(events, i++, PaktEvent.Kind.MapValueEnd);
    }

    [Fact]
    public async Task EmptyMap()
    {
        List<EventRecord> events = await DrainEvents("x:<str => int> = <>");
        int i = events.FindIndex(e => e.Kind == PaktEvent.Kind.MapValueStart);
        Assert.True(i >= 0);
        AssertEvent(events, i + 1, PaktEvent.Kind.MapValueEnd);
    }

    [Fact]
    public async Task NestedStruct()
    {
        List<EventRecord> events = await DrainEvents("x:{pos:{x:int y:int}} = { { 1 2 } }");
        int i = events.FindIndex(e => e.Kind == PaktEvent.Kind.StructValueStart);
        Assert.True(i >= 0);
        AssertEvent(events, i++, PaktEvent.Kind.StructValueStart); // outer
        AssertEvent(events, i++, PaktEvent.Kind.StructValueStart); // inner
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "1");
        AssertEvent(events, i++, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "2");
        AssertEvent(events, i++, PaktEvent.Kind.StructValueEnd); // inner
        AssertEvent(events, i++, PaktEvent.Kind.StructValueEnd); // outer
    }

    // ── Packs ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListPack()
    {
        List<EventRecord> events = await DrainEvents("items:[int] << 1 2 3");
        AssertEvent(events, 0, PaktEvent.Kind.UnitStart);
        int packIdx = events.FindIndex(e => e.Kind == PaktEvent.Kind.PackStart);
        Assert.True(packIdx >= 0);
        // After PackStart: 3 scalar values, then PackEnd
        AssertEvent(events, packIdx + 1, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "1");
        AssertEvent(events, packIdx + 2, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "2");
        AssertEvent(events, packIdx + 3, PaktEvent.Kind.ScalarValue, PaktTypeKind.Int, "3");
        AssertEvent(events, packIdx + 4, PaktEvent.Kind.PackEnd);
    }

    [Fact]
    public async Task EmptyPack()
    {
        List<EventRecord> events = await DrainEvents("items:[int] <<");
        int packIdx = events.FindIndex(e => e.Kind == PaktEvent.Kind.PackStart);
        Assert.True(packIdx >= 0);
        AssertEvent(events, packIdx + 1, PaktEvent.Kind.PackEnd);
    }

    [Fact]
    public async Task PackTerminatedByNextStatement()
    {
        List<EventRecord> events = await DrainEvents("items:[int] << 1 2\nnext:str = 'x'");
        int packEnd = events.FindIndex(e => e.Kind == PaktEvent.Kind.PackEnd);
        Assert.True(packEnd >= 0);
        // Next statement follows
        AssertEvent(events, packEnd + 1, PaktEvent.Kind.StatementStart, expectedPayload: "next");
    }

    // ── Atom values ─────────────────────────────────────────────────

    [Fact]
    public async Task AtomValue()
    {
        List<EventRecord> events = await DrainEvents("x:|dev staging| = |dev");
        int i = events.FindIndex(e => e.Kind == PaktEvent.Kind.AtomValue);
        Assert.True(i >= 0);
        AssertEvent(events, i, PaktEvent.Kind.AtomValue, PaktTypeKind.AtomSet, "|dev");
    }

    // ── Multiple statements ─────────────────────────────────────────

    [Fact]
    public async Task TwoStatements()
    {
        List<EventRecord> events = await DrainEvents("a:int = 1\nb:str = 'x'");
        List<int> stmts = events
            .Select((e, i) => (e, i))
            .Where(x => x.e.Kind == PaktEvent.Kind.StatementStart)
            .Select(x => x.i)
            .ToList();
        Assert.Equal(2, stmts.Count);
        AssertEvent(events, stmts[0], PaktEvent.Kind.StatementStart, expectedPayload: "a");
        AssertEvent(events, stmts[1], PaktEvent.Kind.StatementStart, expectedPayload: "b");
    }

    // ── ReadAsync specific ──────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_StopAfterFirstEvent()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("x:int = 42");
        PipeReader pipe = PipeReader.Create(new ReadOnlySequence<byte>(bytes));
        PaktReader reader = PaktReader.Create(pipe);

        PaktEvent.Kind? firstKind = null;
        bool result = await reader.ReadAsync((scoped in PaktEvent evt) =>
        {
            firstKind = evt.EventKind;
            return PaktReader.HandlerResult.Stop;
        }, TestContext.Current.CancellationToken);

        Assert.False(result); // Stop means no more
        Assert.Equal(PaktEvent.Kind.UnitStart, firstKind);
    }

    // ── NUL framing ─────────────────────────────────────────────────

    [Fact]
    public async Task NulTerminatesUnit()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("x:int = 1"), 0x00, .. Encoding.UTF8.GetBytes("ignored")];
        PipeReader pipe = PipeReader.Create(new ReadOnlySequence<byte>(bytes));
        PaktReader reader = PaktReader.Create(pipe);
        List<EventRecord> events = [];

        await reader.DrainAsync((scoped in PaktEvent evt) =>
        {
            string payload = evt.Payload.IsEmpty ? "" : Encoding.UTF8.GetString(evt.Payload);
            events.Add(new EventRecord(evt.EventKind, evt.TypeKind, payload));
            return PaktReader.HandlerResult.Continue;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PaktEvent.Kind.UnitEnd, events[^1].Kind);
        Assert.DoesNotContain(events, e => string.Equals(e.Payload, "ignored", StringComparison.Ordinal));
    }

    // ── Error cases ─────────────────────────────────────────────────

    [Fact]
    public async Task Error_MissingLayoutAroundOperator()
    {
        await Assert.ThrowsAsync<PaktParseException>(
            () => DrainEvents("x:str='hi'"));
    }

    [Fact]
    public async Task Error_TypeMismatch_IntGetsString()
    {
        await Assert.ThrowsAsync<PaktParseException>(
            () => DrainEvents("x:int = 'hello'"));
    }

    [Fact]
    public async Task Error_ArityMismatch_TooFewFields()
    {
        await Assert.ThrowsAsync<PaktParseException>(
            () => DrainEvents("x:{a:int b:int} = { 1 }"));
    }
}

using System.Buffers;
using System.Text;

namespace Pakt.Core.Test;

public class PaktValidatingReaderTests
{
    // ── Helpers ──

    private static List<(PaktTokenType Type, string Value)> Drain(string paktText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(paktText);
        var reader = new PaktValidatingReader(bytes);
        var tokens = new List<(PaktTokenType, string)>();
        while (reader.Read())
        {
            string value = reader.ValueSequence.Length > 0
                ? Encoding.UTF8.GetString(reader.ValueSequence)
                : "";
            tokens.Add((reader.TokenType, value));
        }
        return tokens;
    }

    private static PaktParseException AssertThrows(string paktText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(paktText);
        try
        {
            var reader = new PaktValidatingReader(bytes);
            while (reader.Read()) { }
        }
        catch (PaktParseException ex)
        {
            return ex;
        }
        throw new Xunit.Sdk.XunitException("Expected PaktParseException was not thrown");
    }

    // ═══════════════════ Phase 1: Type Parser Tests ═══════════════════

    [Fact]
    public void TypeParser_ScalarStr()
    {
        int root = ValidationTypeParser.Parse("str"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.Scalar, nodes[root].Kind);
        Assert.Equal(PaktTokenType.String, nodes[root].ExpectedToken);
        Assert.False(nodes[root].IsNullable);
    }

    [Fact]
    public void TypeParser_ScalarInt()
    {
        int root = ValidationTypeParser.Parse("int"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.Scalar, nodes[root].Kind);
        Assert.Equal(PaktTokenType.Int, nodes[root].ExpectedToken);
    }

    [Fact]
    public void TypeParser_NullableStr()
    {
        int root = ValidationTypeParser.Parse("str?"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.Scalar, nodes[root].Kind);
        Assert.True(nodes[root].IsNullable);
    }

    [Fact]
    public void TypeParser_AtomSet()
    {
        int root = ValidationTypeParser.Parse("|dev staging prod|"u8, 32, out var nodes, out var members);
        Assert.Equal(ValidationNodeKind.AtomSet, nodes[root].Kind);
        Assert.Equal(3, nodes[root].MemberCount);

        ReadOnlySpan<byte> anno = "|dev staging prod|"u8;
        Assert.True(members[nodes[root].MemberStart].Slice(anno).SequenceEqual("dev"u8));
        Assert.True(members[nodes[root].MemberStart + 1].Slice(anno).SequenceEqual("staging"u8));
        Assert.True(members[nodes[root].MemberStart + 2].Slice(anno).SequenceEqual("prod"u8));
    }

    [Fact]
    public void TypeParser_AtomSet_RejectsReservedKeyword()
    {
        Assert.Throws<PaktParseException>(() =>
            ValidationTypeParser.Parse("|true false|"u8, 32, out _, out _));
    }

    [Fact]
    public void TypeParser_AtomSet_RejectsEmpty()
    {
        Assert.Throws<PaktParseException>(() =>
            ValidationTypeParser.Parse("||"u8, 32, out _, out _));
    }

    [Fact]
    public void TypeParser_StructType()
    {
        int root = ValidationTypeParser.Parse("{name:str age:int}"u8, 32, out var nodes, out var members);
        Assert.Equal(ValidationNodeKind.Struct, nodes[root].Kind);
        Assert.Equal(2, nodes[root].ChildCount);

        ReadOnlySpan<byte> anno = "{name:str age:int}"u8;
        Assert.True(members[nodes[root].MemberStart].Slice(anno).SequenceEqual("name"u8));
        Assert.True(members[nodes[root].MemberStart + 1].Slice(anno).SequenceEqual("age"u8));
    }

    [Fact]
    public void TypeParser_StructType_RejectsDuplicateFields()
    {
        Assert.Throws<PaktParseException>(() =>
            ValidationTypeParser.Parse("{name:str name:int}"u8, 32, out _, out _));
    }

    [Fact]
    public void TypeParser_TupleType()
    {
        int root = ValidationTypeParser.Parse("(str int bool)"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.Tuple, nodes[root].Kind);
        Assert.Equal(3, nodes[root].ChildCount);
    }

    [Fact]
    public void TypeParser_ListType()
    {
        int root = ValidationTypeParser.Parse("[int]"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.List, nodes[root].Kind);
        Assert.Equal(1, nodes[root].ChildCount);
    }

    [Fact]
    public void TypeParser_MapType()
    {
        int root = ValidationTypeParser.Parse("<str = int>"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.Map, nodes[root].Kind);
        Assert.Equal(2, nodes[root].ChildCount);
    }

    [Fact]
    public void TypeParser_NullableList()
    {
        int root = ValidationTypeParser.Parse("[int]?"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.List, nodes[root].Kind);
        Assert.True(nodes[root].IsNullable);
    }

    [Fact]
    public void TypeParser_NestedStruct()
    {
        int root = ValidationTypeParser.Parse("{name:str items:[{id:int label:str}]}"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.Struct, nodes[root].Kind);
        Assert.Equal(2, nodes[root].ChildCount);
    }

    [Fact]
    public void TypeParser_NullableAtomSet()
    {
        int root = ValidationTypeParser.Parse("|dev prod|?"u8, 32, out var nodes, out _);
        Assert.Equal(ValidationNodeKind.AtomSet, nodes[root].Kind);
        Assert.True(nodes[root].IsNullable);
    }

    [Fact]
    public void TypeParser_UnknownScalarType_Throws()
    {
        Assert.Throws<PaktParseException>(() =>
            ValidationTypeParser.Parse("foobar"u8, 32, out _, out _));
    }

    // ═══════════════════ Phase 2: Scalar Validation ═══════════════════

    [Theory]
    [InlineData("name:str = 'hello'", PaktTokenType.String)]
    [InlineData("count:int = 42", PaktTokenType.Int)]
    [InlineData("pi:dec = 3.14", PaktTokenType.Decimal)]
    [InlineData("e:float = 2.718e0", PaktTokenType.Float)]
    [InlineData("flag:bool = true", PaktTokenType.Bool)]
    [InlineData("day:date = 2026-06-01", PaktTokenType.Date)]
    [InlineData("data:bin = x'48656C6C6F'", PaktTokenType.Binary)]
    public void ScalarAssign_PassesValidation(string input, PaktTokenType expectedValueType)
    {
        var tokens = Drain(input);
        // Should contain: StatementName, TypeAnnotation, AssignOp, value, EndOfUnit
        Assert.Contains(tokens, t => t.Type == expectedValueType);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void ScalarAssign_WrongType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("name:str = 42");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void ScalarAssign_IntExpected_GotString_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("count:int = 'hello'");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void ScalarAssign_BoolExpected_GotInt_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("flag:bool = 42");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    // ═══════════════════ Phase 3: Nullable Validation ═══════════════════

    [Fact]
    public void Nullable_AcceptsNil()
    {
        var tokens = Drain("name:str? = nil");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Nil);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void Nullable_AcceptsActualValue()
    {
        var tokens = Drain("name:str? = 'hello'");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.String);
    }

    [Fact]
    public void NonNullable_RejectsNil()
    {
        var ex = AssertThrows("name:str = nil");
        Assert.Equal((int)PaktErrorCode.NilNonNullable, ex.Code);
    }

    [Fact]
    public void NullableComposite_AcceptsNil()
    {
        var tokens = Drain("items:[int]? = nil");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Nil);
    }

    [Fact]
    public void NullableAtomSet_AcceptsNil()
    {
        var tokens = Drain("status:|dev prod|? = nil");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Nil);
    }

    // ═══════════════════ Phase 4: Atom Validation ═══════════════════

    [Fact]
    public void AtomSet_ValidMember_Passes()
    {
        var tokens = Drain("status:|dev staging prod| = |staging");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Atom);
    }

    [Fact]
    public void AtomSet_InvalidMember_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("status:|dev staging prod| = |unknown");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void AtomSet_WrongTokenType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("status:|dev staging prod| = 42");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    // ═══════════════════ Phase 5: Struct Validation ═══════════════════

    [Fact]
    public void Struct_CorrectArity_Passes()
    {
        var tokens = Drain("person:{name:str age:int} = {'Alice' 30}");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.StructStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.StructEnd);
    }

    [Fact]
    public void Struct_WrongFieldType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("person:{name:str age:int} = {42 30}");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void Struct_TooFewValues_ThrowsArityMismatch()
    {
        var ex = AssertThrows("person:{name:str age:int} = {'Alice'}");
        Assert.Equal((int)PaktErrorCode.ArityMismatch, ex.Code);
    }

    [Fact]
    public void Struct_TooManyValues_ThrowsArityMismatch()
    {
        var ex = AssertThrows("person:{name:str age:int} = {'Alice' 30 true}");
        Assert.Equal((int)PaktErrorCode.ArityMismatch, ex.Code);
    }

    [Fact]
    public void Struct_Empty_Passes()
    {
        var tokens = Drain("empty:{} = {}");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.StructStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.StructEnd);
    }

    // ═══════════════════ Phase 6: Tuple Validation ═══════════════════

    [Fact]
    public void Tuple_CorrectArity_Passes()
    {
        var tokens = Drain("pair:(str int) = ('hello' 42)");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.TupleStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.TupleEnd);
    }

    [Fact]
    public void Tuple_WrongType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("pair:(str int) = (42 'hello')");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void Tuple_TooFew_ThrowsArityMismatch()
    {
        var ex = AssertThrows("pair:(str int) = ('hello')");
        Assert.Equal((int)PaktErrorCode.ArityMismatch, ex.Code);
    }

    [Fact]
    public void Tuple_TooMany_ThrowsArityMismatch()
    {
        var ex = AssertThrows("pair:(str int) = ('hello' 42 true)");
        Assert.Equal((int)PaktErrorCode.ArityMismatch, ex.Code);
    }

    // ═══════════════════ Phase 7: List Validation ═══════════════════

    [Fact]
    public void List_HomogeneousValues_Passes()
    {
        var tokens = Drain("nums:[int] = [1 2 3]");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.ListStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.ListEnd);
    }

    [Fact]
    public void List_WrongElementType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("nums:[int] = [1 'hello' 3]");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void List_Empty_Passes()
    {
        var tokens = Drain("nums:[int] = []");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.ListStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.ListEnd);
    }

    // ═══════════════════ Phase 8: Map Validation ═══════════════════

    [Fact]
    public void Map_ValidKeyValue_Passes()
    {
        var tokens = Drain("ages:<str = int> = <'Alice' = 30 'Bob' = 25>");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEnd);
    }

    [Fact]
    public void Map_WrongKeyType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("ages:<str = int> = <42 = 30>");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void Map_WrongValueType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("ages:<str = int> = <'Alice' = 'thirty'>");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void Map_Empty_Passes()
    {
        var tokens = Drain("m:<str = int> = <>");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapStart);
        Assert.Contains(tokens, t => t.Type == PaktTokenType.MapEnd);
    }

    // ═══════════════════ Phase 9: Nested Composites ═══════════════════

    [Fact]
    public void NestedStructInList_Passes()
    {
        var tokens = Drain("items:[{id:int label:str}] = [{1 'one'} {2 'two'}]");
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void NestedListInStruct_Passes()
    {
        var tokens = Drain("data:{name:str scores:[int]} = {'Alice' [90 85 92]}");
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void MapWithTupleKeys_Passes()
    {
        var tokens = Drain("grid:<(int int) = str> = <(1 2) = 'a' (3 4) = 'b'>");
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    // ═══════════════════ Phase 10: Pack Validation ═══════════════════

    [Fact]
    public void ListPack_ValidElements_Passes()
    {
        var tokens = Drain("nums:[int] << 1 2 3");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.PackOperator);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void ListPack_WrongElementType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("nums:[int] << 1 'hello' 3");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void MapPack_ValidEntries_Passes()
    {
        var tokens = Drain("ages:<str = int> << 'Alice' = 30 'Bob' = 25");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.PackOperator);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void MapPack_WrongKeyType_ThrowsTypeMismatch()
    {
        var ex = AssertThrows("ages:<str = int> << 42 = 30");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    // ═══════════════════ Phase 11: Multiple Statements ═══════════════════

    [Fact]
    public void MultipleStatements_AllValid()
    {
        var tokens = Drain("name:str = 'hello'\ncount:int = 42");
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
        Assert.Equal(2, tokens.Count(t => t.Type == PaktTokenType.StatementName));
    }

    [Fact]
    public void MultipleStatements_SecondInvalid_Throws()
    {
        var ex = AssertThrows("name:str = 'hello'\ncount:int = 'wrong'");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    // ═══════════════════ Phase 12: Edge Cases ═══════════════════

    [Fact]
    public void Uuid_Passes()
    {
        var tokens = Drain("id:uuid = 550e8400-e29b-41d4-a716-446655440000");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Uuid);
    }

    [Fact]
    public void Timestamp_Passes()
    {
        var tokens = Drain("ts:ts = 2026-06-01T14:30:00Z");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Timestamp);
    }

    [Fact]
    public void NullableListElement_AcceptsNil()
    {
        var tokens = Drain("names:[str?] = ['Alice' nil 'Bob']");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Nil);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void NullableListElement_RejectsWrongType()
    {
        var ex = AssertThrows("names:[str?] = ['Alice' 42 'Bob']");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void StructWithNullableField_AcceptsNil()
    {
        var tokens = Drain("person:{name:str age:int?} = {'Alice' nil}");
        Assert.Contains(tokens, t => t.Type == PaktTokenType.Nil);
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }

    [Fact]
    public void WrongCompositeDelimiter_ThrowsTypeMismatch()
    {
        // Expect struct, got list
        var ex = AssertThrows("person:{name:str} = [1 2]");
        Assert.Equal((int)PaktErrorCode.TypeMismatch, ex.Code);
    }

    [Fact]
    public void ListPack_WithStructElements_Passes()
    {
        var tokens = Drain("items:[{id:int name:str}] << {1 'one'} {2 'two'}");
        Assert.Equal(PaktTokenType.EndOfUnit, tokens[^1].Type);
    }
}
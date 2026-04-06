using System.Collections.Immutable;

namespace Pakt.Tests;

public class PaktTokenTypeTests
{
    [Fact]
    public void None_IsDefaultValue()
    {
        Assert.Equal(PaktTokenType.None, default(PaktTokenType));
    }

    [Theory]
    [InlineData(PaktTokenType.AssignStart)]
    [InlineData(PaktTokenType.AssignEnd)]
    [InlineData(PaktTokenType.StreamStart)]
    [InlineData(PaktTokenType.StreamEnd)]
    [InlineData(PaktTokenType.ScalarValue)]
    [InlineData(PaktTokenType.Nil)]
    [InlineData(PaktTokenType.StructStart)]
    [InlineData(PaktTokenType.StructEnd)]
    [InlineData(PaktTokenType.TupleStart)]
    [InlineData(PaktTokenType.TupleEnd)]
    [InlineData(PaktTokenType.ListStart)]
    [InlineData(PaktTokenType.ListEnd)]
    [InlineData(PaktTokenType.MapStart)]
    [InlineData(PaktTokenType.MapEnd)]
    [InlineData(PaktTokenType.Comment)]
    public void AllValues_AreDefined(PaktTokenType value)
    {
        Assert.True(Enum.IsDefined(value));
    }
}

public class PaktScalarTypeTests
{
    [Fact]
    public void None_IsDefaultValue()
    {
        Assert.Equal(PaktScalarType.None, default(PaktScalarType));
    }

    [Theory]
    [InlineData(PaktScalarType.Str)]
    [InlineData(PaktScalarType.Int)]
    [InlineData(PaktScalarType.Dec)]
    [InlineData(PaktScalarType.Float)]
    [InlineData(PaktScalarType.Bool)]
    [InlineData(PaktScalarType.Uuid)]
    [InlineData(PaktScalarType.Date)]
    [InlineData(PaktScalarType.Time)]
    [InlineData(PaktScalarType.DateTime)]
    [InlineData(PaktScalarType.Bin)]
    [InlineData(PaktScalarType.Atom)]
    public void AllScalarTypes_AreDefined(PaktScalarType value)
    {
        Assert.True(Enum.IsDefined(value));
    }
}

public class PaktPositionTests
{
    [Fact]
    public void None_IsZero()
    {
        Assert.Equal(0, PaktPosition.None.Line);
        Assert.Equal(0, PaktPosition.None.Column);
    }

    [Fact]
    public void Constructor_SetsValues()
    {
        var pos = new PaktPosition(10, 25);
        Assert.Equal(10, pos.Line);
        Assert.Equal(25, pos.Column);
    }

    [Fact]
    public void ToString_FormatsAsLineColumn()
    {
        var pos = new PaktPosition(3, 14);
        Assert.Equal("3:14", pos.ToString());
    }

    [Fact]
    public void Equality_WorksCorrectly()
    {
        var a = new PaktPosition(1, 2);
        var b = new PaktPosition(1, 2);
        var c = new PaktPosition(1, 3);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}

public class PaktExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var pos = new PaktPosition(5, 10);
        var ex = new PaktException("bad syntax", pos, PaktErrorCode.Syntax);

        Assert.Equal(pos, ex.Position);
        Assert.Equal(PaktErrorCode.Syntax, ex.ErrorCode);
        Assert.Contains("5:10", ex.Message);
        Assert.Contains("bad syntax", ex.Message);
    }

    [Fact]
    public void Constructor_WithNoPosition_OmitsPrefix()
    {
        var ex = new PaktException("general error", PaktPosition.None);

        Assert.Equal("general error", ex.Message);
        Assert.Equal(PaktErrorCode.None, ex.ErrorCode);
    }

    [Fact]
    public void Constructor_WithInnerException_Preserves()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new PaktException("outer", new PaktPosition(1, 1), PaktErrorCode.TypeMismatch, inner);

        Assert.Same(inner, ex.InnerException);
        Assert.Equal(PaktErrorCode.TypeMismatch, ex.ErrorCode);
    }

    [Theory]
    [InlineData(PaktErrorCode.UnexpectedEof, 1)]
    [InlineData(PaktErrorCode.DuplicateName, 2)]
    [InlineData(PaktErrorCode.TypeMismatch, 3)]
    [InlineData(PaktErrorCode.NilNonNullable, 4)]
    [InlineData(PaktErrorCode.Syntax, 5)]
    public void ErrorCodes_MatchSpecValues(PaktErrorCode code, int expected)
    {
        Assert.Equal(expected, (int)code);
    }
}

public class PaktTypeTests
{
    [Fact]
    public void Scalar_CreatesCorrectType()
    {
        var t = PaktType.Scalar(PaktScalarType.Str);

        Assert.True(t.IsScalar);
        Assert.False(t.IsNullable);
        Assert.Equal(PaktScalarType.Str, t.ScalarKind);
        Assert.Equal("str", t.ToString());
    }

    [Fact]
    public void Scalar_Nullable()
    {
        var t = PaktType.Scalar(PaktScalarType.Int, nullable: true);

        Assert.True(t.IsScalar);
        Assert.True(t.IsNullable);
        Assert.Equal("int?", t.ToString());
    }

    [Fact]
    public void AtomSet_CreatesCorrectType()
    {
        var t = PaktType.AtomSet(["dev", "staging", "prod"]);

        Assert.True(t.IsAtomSet);
        Assert.True(t.IsScalar); // Atom is also a scalar kind
        Assert.Equal("|dev, staging, prod|", t.ToString());
    }

    [Fact]
    public void Struct_CreatesCorrectType()
    {
        var t = PaktType.Struct([
            new PaktField("host", PaktType.Scalar(PaktScalarType.Str)),
            new PaktField("port", PaktType.Scalar(PaktScalarType.Int)),
        ]);

        Assert.True(t.IsStruct);
        Assert.Equal("{host:str, port:int}", t.ToString());
    }

    [Fact]
    public void Tuple_CreatesCorrectType()
    {
        var t = PaktType.Tuple([
            PaktType.Scalar(PaktScalarType.Int),
            PaktType.Scalar(PaktScalarType.Str),
        ]);

        Assert.True(t.IsTuple);
        Assert.Equal("(int, str)", t.ToString());
    }

    [Fact]
    public void List_CreatesCorrectType()
    {
        var t = PaktType.List(PaktType.Scalar(PaktScalarType.Int));

        Assert.True(t.IsList);
        Assert.Equal("[int]", t.ToString());
    }

    [Fact]
    public void Map_CreatesCorrectType()
    {
        var t = PaktType.Map(
            PaktType.Scalar(PaktScalarType.Str),
            PaktType.Scalar(PaktScalarType.Int));

        Assert.True(t.IsMap);
        Assert.Equal("<str ; int>", t.ToString());
    }

    [Fact]
    public void Nested_CompositeType()
    {
        var inner = PaktType.Struct([
            new PaktField("name", PaktType.Scalar(PaktScalarType.Str)),
        ]);
        var t = PaktType.List(inner);

        Assert.Equal("[{name:str}]", t.ToString());
    }

    [Fact]
    public void Equality_SameTypes()
    {
        var a = PaktType.Scalar(PaktScalarType.Bool);
        var b = PaktType.Scalar(PaktScalarType.Bool);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentNullability()
    {
        var a = PaktType.Scalar(PaktScalarType.Str);
        var b = PaktType.Scalar(PaktScalarType.Str, nullable: true);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DifferentKinds()
    {
        var a = PaktType.Scalar(PaktScalarType.Str);
        var b = PaktType.Scalar(PaktScalarType.Int);

        Assert.NotEqual(a, b);
    }
}

using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace Pakt.Tests.WriterTests;

public class PaktWriterTests
{
    private static string Written(ArrayBufferWriter<byte> buffer) =>
        Encoding.UTF8.GetString(buffer.WrittenSpan);

    #region Scalar Assignments — Round-trip

    [Fact]
    public void RoundTrip_StringAssignment()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("name", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("hello");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);
        Assert.Equal("name", reader.StatementName);

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.ScalarValue, reader.TokenType);
        Assert.Equal(PaktScalarType.Str, reader.ScalarType);
        Assert.Equal("hello", reader.GetString());

        Assert.True(reader.Read());
        Assert.Equal(PaktTokenType.AssignEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_IntAssignment()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("count", PaktType.Scalar(PaktScalarType.Int));
        writer.WriteIntValue(42);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read());
        Assert.Equal(42L, reader.GetInt64());
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_IntNegative()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("n", PaktType.Scalar(PaktScalarType.Int));
        writer.WriteIntValue(-9999);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(-9999L, reader.GetInt64());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_DecimalAssignment()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("pi", PaktType.Scalar(PaktScalarType.Dec));
        writer.WriteDecimalValue(3.14m);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(3.14m, reader.GetDecimal());
        reader.Read();
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_DecimalWholeNumber()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("val", PaktType.Scalar(PaktScalarType.Dec));
        writer.WriteDecimalValue(42m);
        writer.WriteAssignmentEnd();
        writer.Flush();

        // Verify output includes decimal point
        var output = Written(buffer);
        Assert.Contains("42.0", output);

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(42.0m, reader.GetDecimal());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_FloatAssignment()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("val", PaktType.Scalar(PaktScalarType.Float));
        writer.WriteFloatValue(6.022e23);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(6.022e23, reader.GetDouble(), precision: 5);
        reader.Read();
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void Float_NaN_Throws()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("val", PaktType.Scalar(PaktScalarType.Float));
        Assert.Throws<ArgumentException>(() => writer.WriteFloatValue(double.NaN));
    }

    [Fact]
    public void Float_Infinity_Throws()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("val", PaktType.Scalar(PaktScalarType.Float));
        Assert.Throws<ArgumentException>(() => writer.WriteFloatValue(double.PositiveInfinity));
    }

    [Fact]
    public void RoundTrip_BoolTrue()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("active", PaktType.Scalar(PaktScalarType.Bool));
        writer.WriteBoolValue(true);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_BoolFalse()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("active", PaktType.Scalar(PaktScalarType.Bool));
        writer.WriteBoolValue(false);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.False(reader.GetBoolean());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_UuidAssignment()
    {
        var guid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("id", PaktType.Scalar(PaktScalarType.Uuid));
        writer.WriteUuidValue(guid);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(guid, reader.GetGuid());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_DateAssignment()
    {
        var date = new DateOnly(2026, 6, 1);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("d", PaktType.Scalar(PaktScalarType.Date));
        writer.WriteDateValue(date);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(date, reader.GetDate());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_TimestampWithZ()
    {
        var time = new DateTimeOffset(2026, 6, 1, 14, 30, 0, TimeSpan.Zero);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("t", PaktType.Scalar(PaktScalarType.Ts));
        writer.WriteTimestampValue(time);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        var result = reader.GetTimestamp();
        Assert.Equal(2026, result.Year);
        Assert.Equal(6, result.Month);
        Assert.Equal(1, result.Day);
        Assert.Equal(14, result.Hour);
        Assert.Equal(30, result.Minute);
        Assert.Equal(0, result.Second);
        Assert.Equal(TimeSpan.Zero, result.Offset);
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_TimestampWithOffset()
    {
        var time = new DateTimeOffset(2026, 6, 1, 14, 30, 0, TimeSpan.FromHours(-4));
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("t", PaktType.Scalar(PaktScalarType.Ts));
        writer.WriteTimestampValue(time);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        var result = reader.GetTimestamp();
        Assert.Equal(2026, result.Year);
        Assert.Equal(6, result.Month);
        Assert.Equal(1, result.Day);
        Assert.Equal(14, result.Hour);
        Assert.Equal(30, result.Minute);
        Assert.Equal(TimeSpan.FromHours(-4), result.Offset);
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_TimestampAssignment()
    {
        var dt = new DateTimeOffset(2026, 6, 1, 14, 30, 0, TimeSpan.Zero);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("dt", PaktType.Scalar(PaktScalarType.Ts));
        writer.WriteTimestampValue(dt);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        var result = reader.GetTimestamp();
        Assert.Equal(2026, result.Year);
        Assert.Equal(6, result.Month);
        Assert.Equal(1, result.Day);
        Assert.Equal(14, result.Hour);
        Assert.Equal(30, result.Minute);
        Assert.Equal(TimeSpan.Zero, result.Offset);
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_BinAssignment()
    {
        var data = "Hello"u8.ToArray();
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("data", PaktType.Scalar(PaktScalarType.Bin));
        writer.WriteBinValue(data);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        var dest = new byte[5];
        int written = reader.GetBytesFromBin(dest);
        Assert.Equal(5, written);
        Assert.Equal(data, dest);
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_AtomAssignment()
    {
        var atomType = PaktType.AtomSet(["dev", "staging", "prod"]);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("env", atomType);
        writer.WriteAtomValue("prod");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read();
        Assert.True(reader.Read());
        Assert.Equal(PaktScalarType.Atom, reader.ScalarType);
        Assert.Equal("prod", reader.GetAtom());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_NilAssignment()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("name", PaktType.Scalar(PaktScalarType.Str, nullable: true));
        writer.WriteNilValue();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // Nil
        Assert.Equal(PaktTokenType.Nil, reader.TokenType);
        Assert.True(reader.IsNullValue);
        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    #endregion

    #region String Escaping

    [Fact]
    public void RoundTrip_StringWithQuotes()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("s", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("it's");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); reader.Read();
        Assert.Equal("it's", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_StringWithNewline()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("s", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("line1\nline2");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); reader.Read();
        Assert.Equal("line1\nline2", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_StringWithTab()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("s", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("col1\tcol2");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); reader.Read();
        Assert.Equal("col1\tcol2", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_StringWithBackslash()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("s", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("path\\to\\file");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); reader.Read();
        Assert.Equal("path\\to\\file", reader.GetString());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_StringWithCarriageReturn()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("s", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("line1\r\nline2");
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); reader.Read();
        Assert.Equal("line1\r\nline2", reader.GetString());
        reader.Dispose();
    }

    #endregion

    #region Composites — Round-trip

    [Fact]
    public void RoundTrip_StructWithFields()
    {
        var structType = PaktType.Struct([
            new PaktField("host", PaktType.Scalar(PaktScalarType.Str)),
            new PaktField("port", PaktType.Scalar(PaktScalarType.Int)),
        ]);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("config", structType);
        writer.WriteStructStart();
        writer.WriteStringValue("localhost");
        writer.WriteIntValue(8080);
        writer.WriteStructEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);

        Assert.True(reader.Read()); // AssignStart
        Assert.Equal(PaktTokenType.AssignStart, reader.TokenType);

        Assert.True(reader.Read()); // StructStart
        Assert.Equal(PaktTokenType.StructStart, reader.TokenType);

        Assert.True(reader.Read()); // 'localhost'
        Assert.Equal("host", reader.CurrentName);
        Assert.Equal("localhost", reader.GetString());

        Assert.True(reader.Read()); // 8080
        Assert.Equal("port", reader.CurrentName);
        Assert.Equal(8080L, reader.GetInt64());

        Assert.True(reader.Read()); // StructEnd
        Assert.Equal(PaktTokenType.StructEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_Tuple()
    {
        var tupleType = PaktType.Tuple([
            PaktType.Scalar(PaktScalarType.Int),
            PaktType.Scalar(PaktScalarType.Str),
        ]);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("pair", tupleType);
        writer.WriteTupleStart();
        writer.WriteIntValue(42);
        writer.WriteStringValue("hello");
        writer.WriteTupleEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);

        Assert.True(reader.Read()); // AssignStart
        Assert.True(reader.Read()); // TupleStart
        Assert.Equal(PaktTokenType.TupleStart, reader.TokenType);

        Assert.True(reader.Read());
        Assert.Equal(42L, reader.GetInt64());

        Assert.True(reader.Read());
        Assert.Equal("hello", reader.GetString());

        Assert.True(reader.Read()); // TupleEnd
        Assert.Equal(PaktTokenType.TupleEnd, reader.TokenType);

        Assert.True(reader.Read()); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_ListWithElements()
    {
        var listType = PaktType.List(PaktType.Scalar(PaktScalarType.Int));
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("ids", listType);
        writer.WriteListStart();
        writer.WriteIntValue(1);
        writer.WriteIntValue(2);
        writer.WriteIntValue(3);
        writer.WriteListEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read()); // ListStart
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);

        Assert.True(reader.Read()); Assert.Equal(1L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(2L, reader.GetInt64());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());

        Assert.True(reader.Read()); // ListEnd
        Assert.Equal(PaktTokenType.ListEnd, reader.TokenType);
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_EmptyList()
    {
        var listType = PaktType.List(PaktType.Scalar(PaktScalarType.Int));
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("ids", listType);
        writer.WriteListStart();
        writer.WriteListEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read()); // ListStart
        Assert.Equal(PaktTokenType.ListStart, reader.TokenType);
        Assert.True(reader.Read()); // ListEnd
        Assert.Equal(PaktTokenType.ListEnd, reader.TokenType);
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_MapWithEntries()
    {
        var mapType = PaktType.Map(
            PaktType.Scalar(PaktScalarType.Str),
            PaktType.Scalar(PaktScalarType.Int));

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("m", mapType);
        writer.WriteMapStart();
        writer.WriteStringValue("a");
        writer.WriteMapKeySeparator();
        writer.WriteIntValue(1);
        writer.WriteStringValue("b");
        writer.WriteMapKeySeparator();
        writer.WriteIntValue(2);
        writer.WriteMapEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read()); // MapStart
        Assert.Equal(PaktTokenType.MapStart, reader.TokenType);

        Assert.True(reader.Read()); // key 'a'
        Assert.Equal("a", reader.GetString());
        Assert.True(reader.Read()); // value 1
        Assert.Equal(1L, reader.GetInt64());

        Assert.True(reader.Read()); // key 'b'
        Assert.Equal("b", reader.GetString());
        Assert.True(reader.Read()); // value 2
        Assert.Equal(2L, reader.GetInt64());

        Assert.True(reader.Read()); // MapEnd
        Assert.Equal(PaktTokenType.MapEnd, reader.TokenType);
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_EmptyMap()
    {
        var mapType = PaktType.Map(
            PaktType.Scalar(PaktScalarType.Str),
            PaktType.Scalar(PaktScalarType.Int));

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("m", mapType);
        writer.WriteMapStart();
        writer.WriteMapEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read()); // MapStart
        Assert.True(reader.Read()); // MapEnd
        Assert.Equal(PaktTokenType.MapEnd, reader.TokenType);
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    #endregion

    #region Nested Composites

    [Fact]
    public void RoundTrip_ListOfStructs()
    {
        var structType = PaktType.Struct([
            new PaktField("name", PaktType.Scalar(PaktScalarType.Str)),
            new PaktField("count", PaktType.Scalar(PaktScalarType.Int)),
        ]);
        var listType = PaktType.List(structType);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("items", listType);
        writer.WriteListStart();

        writer.WriteStructStart();
        writer.WriteStringValue("apple");
        writer.WriteIntValue(5);
        writer.WriteStructEnd();

        writer.WriteStructStart();
        writer.WriteStringValue("banana");
        writer.WriteIntValue(3);
        writer.WriteStructEnd();

        writer.WriteListEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read()); // ListStart

        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal("apple", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal(5L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal("banana", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal(3L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // ListEnd
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_MapWithStructValues()
    {
        var structType = PaktType.Struct([
            new PaktField("age", PaktType.Scalar(PaktScalarType.Int)),
        ]);
        var mapType = PaktType.Map(PaktType.Scalar(PaktScalarType.Str), structType);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("users", mapType);
        writer.WriteMapStart();

        writer.WriteStringValue("alice");
        writer.WriteMapKeySeparator();
        writer.WriteStructStart();
        writer.WriteIntValue(30);
        writer.WriteStructEnd();

        writer.WriteStringValue("bob");
        writer.WriteMapKeySeparator();
        writer.WriteStructStart();
        writer.WriteIntValue(25);
        writer.WriteStructEnd();

        writer.WriteMapEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        reader.Read(); // AssignStart
        Assert.True(reader.Read()); // MapStart

        Assert.True(reader.Read()); Assert.Equal("alice", reader.GetString());
        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal(30L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); Assert.Equal("bob", reader.GetString());
        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); Assert.Equal(25L, reader.GetInt64());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // MapEnd
        reader.Read(); // AssignEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    #endregion

    #region Streams

    [Fact]
    public void RoundTrip_PackWithElements()
    {
        var listType = PaktType.List(PaktType.Scalar(PaktScalarType.Str));
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WritePackStart("events", listType);
        writer.WriteStringValue("alpha");
        writer.WriteStringValue("beta");
        writer.WriteStringValue("gamma");
        writer.WritePackEnd();
        writer.Flush();

        // Verify the pack format includes <<
        var output = Written(buffer);
        Assert.Contains("<<", output);

        var reader = new PaktReader(buffer.WrittenSpan);
        Assert.True(reader.Read()); // PackStart
        Assert.Equal(PaktTokenType.PackStart, reader.TokenType);
        Assert.True(reader.IsPackStatement);

        Assert.True(reader.Read()); Assert.Equal("alpha", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal("beta", reader.GetString());
        Assert.True(reader.Read()); Assert.Equal("gamma", reader.GetString());

        Assert.True(reader.Read()); // PackEnd
        Assert.Equal(PaktTokenType.PackEnd, reader.TokenType);
        Assert.False(reader.Read());
        reader.Dispose();
    }

    [Fact]
    public void RoundTrip_PackWithStructs()
    {
        var structType = PaktType.Struct([
            new PaktField("ts", PaktType.Scalar(PaktScalarType.Ts)),
            new PaktField("msg", PaktType.Scalar(PaktScalarType.Str)),
        ]);
        var listType = PaktType.List(structType);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WritePackStart("events", listType);

        writer.WriteStructStart();
        writer.WriteTimestampValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        writer.WriteStringValue("start");
        writer.WriteStructEnd();

        writer.WriteStructStart();
        writer.WriteTimestampValue(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        writer.WriteStringValue("stop");
        writer.WriteStructEnd();

        writer.WritePackEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);
        Assert.True(reader.Read()); // PackStart

        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); // timestamp
        var dt1 = reader.GetTimestamp();
        Assert.Equal(2026, dt1.Year);
        Assert.Equal(1, dt1.Month);
        Assert.Equal(1, dt1.Day);
        Assert.True(reader.Read()); // 'start'
        Assert.Equal("start", reader.GetString());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // StructStart
        Assert.True(reader.Read()); // timestamp
        var dt2 = reader.GetTimestamp();
        Assert.Equal(2026, dt2.Year);
        Assert.Equal(1, dt2.Month);
        Assert.Equal(2, dt2.Day);
        Assert.True(reader.Read()); // 'stop'
        Assert.Equal("stop", reader.GetString());
        Assert.True(reader.Read()); // StructEnd

        Assert.True(reader.Read()); // PackEnd
        Assert.False(reader.Read());
        reader.Dispose();
    }

    #endregion

    #region Multiple Assignments

    [Fact]
    public void RoundTrip_MultipleAssignments()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);

        writer.WriteAssignmentStart("name", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("Alice");
        writer.WriteAssignmentEnd();

        writer.WriteAssignmentStart("age", PaktType.Scalar(PaktScalarType.Int));
        writer.WriteIntValue(30);
        writer.WriteAssignmentEnd();
        writer.Flush();

        var reader = new PaktReader(buffer.WrittenSpan);

        Assert.True(reader.Read()); // AssignStart name
        Assert.Equal("name", reader.StatementName);
        Assert.True(reader.Read()); Assert.Equal("Alice", reader.GetString());
        Assert.True(reader.Read()); // AssignEnd

        Assert.True(reader.Read()); // AssignStart age
        Assert.Equal("age", reader.StatementName);
        Assert.True(reader.Read()); Assert.Equal(30L, reader.GetInt64());
        Assert.True(reader.Read()); // AssignEnd

        Assert.False(reader.Read());
        reader.Dispose();
    }

    #endregion

    #region Output Format Verification

    [Fact]
    public void Output_ScalarAssignment_Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("name", PaktType.Scalar(PaktScalarType.Str));
        writer.WriteStringValue("hello");
        writer.WriteAssignmentEnd();
        writer.Flush();

        Assert.Equal("name:str = 'hello'\n", Written(buffer));
    }

    [Fact]
    public void Output_EmptyList_Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("ids", PaktType.List(PaktType.Scalar(PaktScalarType.Int)));
        writer.WriteListStart();
        writer.WriteListEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        Assert.Equal("ids:[int] = []\n", Written(buffer));
    }

    [Fact]
    public void Output_EmptyMap_Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        var mapType = PaktType.Map(PaktType.Scalar(PaktScalarType.Str), PaktType.Scalar(PaktScalarType.Int));
        writer.WriteAssignmentStart("m", mapType);
        writer.WriteMapStart();
        writer.WriteMapEnd();
        writer.WriteAssignmentEnd();
        writer.Flush();

        Assert.Equal("m:<str ; int> = <>\n", Written(buffer));
    }

    [Fact]
    public void Output_NilValue_Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("x", PaktType.Scalar(PaktScalarType.Str, nullable: true));
        writer.WriteNilValue();
        writer.WriteAssignmentEnd();
        writer.Flush();

        Assert.Equal("x:str? = nil\n", Written(buffer));
    }

    [Fact]
    public void Output_BoolValues_Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("a", PaktType.Scalar(PaktScalarType.Bool));
        writer.WriteBoolValue(true);
        writer.WriteAssignmentEnd();
        writer.WriteAssignmentStart("b", PaktType.Scalar(PaktScalarType.Bool));
        writer.WriteBoolValue(false);
        writer.WriteAssignmentEnd();
        writer.Flush();

        Assert.Equal("a:bool = true\nb:bool = false\n", Written(buffer));
    }

    [Fact]
    public void Output_BinValue_Format()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new PaktWriter(buffer);
        writer.WriteAssignmentStart("data", PaktType.Scalar(PaktScalarType.Bin));
        writer.WriteBinValue([0xCA, 0xFE]);
        writer.WriteAssignmentEnd();
        writer.Flush();

        Assert.Equal("data:bin = x'CAFE'\n", Written(buffer));
    }

    #endregion

    #region Disposed

    [Fact]
    public void WriterThrowsAfterDispose()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PaktWriter(buffer);
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            writer.WriteAssignmentStart("x", PaktType.Scalar(PaktScalarType.Str)));
    }

    #endregion
}

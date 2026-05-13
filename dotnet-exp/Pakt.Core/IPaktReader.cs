namespace Pakt;

/// <summary>
/// Structural PAKT token reader. Ref struct types may implement this interface
/// and be consumed through generic methods constrained with <c>allows ref struct</c>.
/// </summary>
public interface IPaktReader
{
    bool Read();
    PaktTokenType TokenType { get; }
    ReadOnlySpan<byte> ValueSpan { get; }
    int Depth { get; }

    string ReadString();
    string? ReadStringOrNil();
    int ReadInt32();
    long ReadInt64();
    double ReadDouble();
    decimal ReadDecimal();
    bool ReadBool();
    bool TryReadNil();
    ReadOnlySpan<byte> ReadRawValue();

    void ExpectToken(PaktTokenType expected);
    bool TryExpectToken(PaktTokenType expected);
    bool VerifyTypeAnnotation(ReadOnlySpan<byte> expectedSignature);

    long ByteOffset { get; }
    int Line { get; }
    int Column { get; }

    /// <summary>
    /// Consume a reader through a generic method that accepts ref struct implementors.
    /// Source-generated deserializers use this pattern for zero-alloc dispatch.
    /// </summary>
    static T Consume<TReader, T>(ref TReader reader, Func<TReader, T> consumer)
        where TReader : IPaktReader, allows ref struct
        => consumer(reader);
}
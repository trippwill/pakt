namespace Pakt;

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
}
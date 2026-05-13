using System.Buffers;
using System.Runtime.InteropServices;

namespace Pakt;

/// <summary>
/// Snapshot of a <see cref="PaktEvent"/> that can live on the heap.
/// Payload is stored as an offset+length into the arena name buffer.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal struct BufferedTypeEvent
{
    public PaktEvent.Kind Kind;
    public long Offset;
    public PaktTypeKind TypeKind;
    public int PayloadStart;  // offset into arena name buffer, or -1
    public int PayloadLength;
}

internal sealed partial class Parser
{
    private enum ParserPhase
    {
        UnitStart,
        BetweenStatements,
        StatementName,
        TypeAnnotation,
        TypeParsing,
        TypeDraining,
        StatementOperator,
        StatementValue,
        Complete,
        Error,
    }

    private const int TypeEventBufferSize = 64;

    private readonly PaktReaderOptions _options;
    private readonly PaktTypeArena _types;
    private readonly ValueStack _valueStack;
    private readonly TypeParser _typeParser;

    private ParserPhase _phase;
    private SourceCursor _cursor;

    // Statement-level state (does not nest)
    private ReadOnlySequence<byte> _statementName;
    private PaktTypeRef _statementType;
    private bool _isPack;

    // Buffered type events for batch parsing
    private readonly BufferedTypeEvent[] _typeEventBuffer = new BufferedTypeEvent[TypeEventBufferSize];
    private int _typeEventCount;
    private int _typeEventDrainIndex;

    public Parser(PaktReaderOptions options)
    {
        _options = options;
        _types = new PaktTypeArena();
        _valueStack = new ValueStack(options.MaxNestingDepth);
        _typeParser = new TypeParser(_types, options);

        _phase = ParserPhase.UnitStart;
        _cursor = SourceCursor.Start;
    }

    public SourcePosition CurrentPosition => _cursor.ToPosition();

    public StepResult Step(ref SequenceReader<byte> reader, bool isFinal)
    {
        return _phase switch
        {
            ParserPhase.UnitStart => StepUnitStart(ref reader, isFinal),
            ParserPhase.BetweenStatements => StepBetweenStatements(ref reader, isFinal),
            ParserPhase.StatementName => StepStatementName(ref reader, isFinal),
            ParserPhase.TypeAnnotation => StepTypeAnnotation(ref reader, isFinal),
            ParserPhase.TypeParsing => StepTypeParsing(ref reader, isFinal),
            ParserPhase.TypeDraining => StepTypeDraining(),
            ParserPhase.StatementOperator => StepStatementOperator(ref reader, isFinal),
            ParserPhase.StatementValue => StepValue(ref reader, isFinal),
            ParserPhase.Complete => StepResult.Complete(),
            _ => StepResult.Error(PaktParseError.Syntax(CurrentPosition)),
        };
    }
}

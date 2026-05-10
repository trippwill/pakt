using System.Buffers;

namespace Pakt;

sealed partial class Parser
{
    private enum ParserPhase
    {
        UnitStart,
        BetweenStatements,
        StatementName,
        TypeAnnotation,
        TypeEventDrain,
        StatementOperator,
        StatementValue,
        Complete,
        Error,
    }

    private readonly PaktReaderOptions _options;
    private readonly PaktTypeArena _types;
    private readonly ValueStack _valueStack;
    private readonly List<PendingTypeEvent> _pendingTypeEvents = [];

    private ParserPhase _phase;
    private SourceCursor _cursor;
    private int _pendingDrainIndex;

    // Statement-level state (does not nest)
    private ReadOnlySequence<byte> _statementName;
    private PaktTypeRef _statementType;
    private bool _isPack;

    public Parser(PaktReaderOptions options)
    {
        _options = options;
        _types = new PaktTypeArena();
        _valueStack = new ValueStack(options.MaxNestingDepth);

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
            ParserPhase.TypeEventDrain => StepTypeEventDrain(),
            ParserPhase.StatementOperator => StepStatementOperator(ref reader, isFinal),
            ParserPhase.StatementValue => StepValue(ref reader, isFinal),
            ParserPhase.Complete => StepResult.Complete(),
            _ => StepResult.Error(PaktParseError.Syntax(CurrentPosition)),
        };
    }
}

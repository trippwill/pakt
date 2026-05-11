using System.Buffers;

namespace Pakt;

sealed partial class Parser
{
    private StepResult StepUnitStart(ref SequenceReader<byte> reader, bool isFinal)
    {
        SkipBom(ref reader);
        SkipLayout(ref reader);
        _phase = ParserPhase.BetweenStatements;
        return StepResult.Event(PaktEvent.UnitStart(_cursor.Offset));
    }

    private StepResult StepBetweenStatements(ref SequenceReader<byte> reader, bool isFinal)
    {
        SkipLayout(ref reader);

        if (reader.End)
        {
            if (isFinal)
            {
                _phase = ParserPhase.Complete;
                return StepResult.Event(PaktEvent.UnitEnd(_cursor.Offset));
            }

            return StepResult.MoreData();
        }

        // §10.1: NUL at top level = end-of-unit
        if (reader.TryPeek(out byte b) && b == Lexical.Nul)
        {
            reader.Advance(1);
            _cursor.Offset++;
            _phase = ParserPhase.Complete;
            return StepResult.Event(PaktEvent.UnitEnd(_cursor.Offset));
        }

        _phase = ParserPhase.StatementName;
        return StepResult.Continue();
    }

    private StepResult StepStatementName(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!TryReadIdentifier(ref reader, isFinal, out ReadOnlySequence<byte> name, out StepResult nameResult))
            return nameResult;

        _statementName = name;
        _phase = ParserPhase.TypeAnnotation;
        return StepResult.Event(PaktEvent.StatementStart(_cursor.Offset, name));
    }

    private StepResult StepTypeAnnotation(ref SequenceReader<byte> reader, bool isFinal)
    {
        if (!TryReadExpected(ref reader, Syntax.TypeAscription, isFinal, out StepResult separatorResult))
            return separatorResult;

        _typeParser.Begin(_cursor);
        _phase = ParserPhase.TypeParsing;
        return StepResult.Continue();
    }

    private StepResult StepTypeParsing(ref SequenceReader<byte> reader, bool isFinal)
    {
        TypeStepResult result = _typeParser.Step(
            reader.UnreadSequence, isFinal, out long bytesConsumed);

        reader.Advance(bytesConsumed);
        _cursor = _typeParser.CurrentCursor;

        return result.Status switch
        {
            TypeStepStatus.Continue => StepResult.Continue(),
            TypeStepStatus.Event => StepResult.Event(result.TypeEvent),
            TypeStepStatus.MoreData => StepResult.MoreData(),
            TypeStepStatus.Complete => CompleteTypeParsing(),
            TypeStepStatus.Error => StepResult.Error(result.ParseError!.Value),
            _ => StepResult.Error(PaktParseError.Syntax(CurrentPosition)),
        };
    }

    private StepResult CompleteTypeParsing()
    {
        _statementType = _typeParser.RootTypeRef;
        _phase = ParserPhase.StatementOperator;
        return StepResult.Continue();
    }

    private StepResult StepStatementOperator(ref SequenceReader<byte> reader, bool isFinal)
    {
        // §5.2: No newline permitted inside a statement header
        if (!TryRequireHeaderLayout(ref reader, isFinal, out StepResult layoutResult))
            return layoutResult;

        if (!TryReadStatementOperator(ref reader, isFinal, out StepResult opResult))
            return opResult;

        // Pack body may be empty or start on the next line; assign requires inline layout
        if (_isPack)
        {
            SkipLayout(ref reader);
        }
        else
        {
            if (!TryRequireHeaderLayout(ref reader, isFinal, out StepResult postResult))
                return postResult;
        }

        if (!_valueStack.TryPush(new ValueFrame
        {
            TypeRef = _statementType,
            Index = 0,
            Flags = _isPack ? FrameFlags.Pack : FrameFlags.None,
        }))
            return StepResult.Error(PaktParseError.NestingDepthExceeded(CurrentPosition));

        _phase = ParserPhase.StatementValue;
        return StepResult.Event(
            _isPack ? PaktEvent.PackStart(_cursor.Offset) : PaktEvent.AssignStart(_cursor.Offset));
    }

    private bool TryReadStatementOperator(ref SequenceReader<byte> reader, bool isFinal, out StepResult result)
    {
        if (!reader.TryPeek(out byte op))
        {
            result = isFinal
                ? StepResult.Error(PaktParseError.UnexpectedEndOfInput(CurrentPosition))
                : StepResult.MoreData();
            return false;
        }

        if (op == Syntax.AssignOp)
        {
            reader.Advance(1);
            _cursor.Offset++;
            _cursor.Column++;
            _isPack = false;
            result = default;
            return true;
        }

        if (TryReadDigraph(ref reader, Syntax.PackOp, isFinal, out result))
        {
            _isPack = true;
            return true;
        }

        if (result.Status == Parser.StepStatus.MoreData)
            return false;

        result = StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition));
        return false;
    }
}
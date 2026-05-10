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

        _pendingTypeEvents.Clear();
        _pendingDrainIndex = 0;
        _types.ClearNames();

        if (!TryParseTypeReference(ref reader, isFinal, depth: 0, out PaktTypeRef typeRef, out StepResult typeResult))
            return typeResult;

        _statementType = typeRef;
        _phase = ParserPhase.TypeEventDrain;
        return StepResult.Continue();
    }

    private StepResult StepTypeEventDrain()
    {
        if (_pendingDrainIndex >= _pendingTypeEvents.Count)
        {
            _pendingTypeEvents.Clear();
            _pendingDrainIndex = 0;
            _phase = ParserPhase.StatementOperator;
            return StepResult.Continue();
        }

        var pe = _pendingTypeEvents[_pendingDrainIndex++];
        ReadOnlySequence<byte> payload = pe.NameStart >= 0
            ? new ReadOnlySequence<byte>(_types.NameBuffer, pe.NameStart, pe.NameLength)
            : default;
        return StepResult.Event(new PaktEvent(pe.Kind, pe.Offset, pe.TypeKind, payload));
    }

    private StepResult StepStatementOperator(ref SequenceReader<byte> reader, bool isFinal)
    {
        // §7: Layout is required around '=' and '<<'
        if (!TryRequireLayout(ref reader, isFinal, out StepResult layoutResult))
            return layoutResult;

        if (!TryReadStatementOperator(ref reader, isFinal, out StepResult opResult))
            return opResult;

        if (!TryRequireLayout(ref reader, isFinal, out StepResult postResult))
            return postResult;

        if (!_valueStack.TryPush(new ValueFrame { TypeRef = _statementType, Index = 0, Flags = FrameFlags.None }))
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
                ? StepResult.Error(PaktParseError.InvalidHeader(CurrentPosition))
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
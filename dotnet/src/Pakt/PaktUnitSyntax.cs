using System;

namespace Pakt;

internal static class PaktUnitSyntax
{
    internal enum PackItemStartKind
    {
        HasValue,
        Terminated,
        NeedMoreData,
    }

    internal enum PackBoundaryKind
    {
        Separator,
        Terminated,
        NeedMoreData,
        Invalid,
    }

    private enum SeparatorProbeKind
    {
        None,
        Separator,
        NeedMoreData,
    }

    private enum PackValueStartKind
    {
        CanStartValue,
        CannotStartValue,
        NeedMoreData,
    }

    public static int SkipHorizontalWhitespace(ReadOnlySpan<byte> buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var b = buffer[offset];
            if (b == ' ' || b == '\t')
            {
                offset++;
                continue;
            }

            break;
        }

        return offset;
    }

    public static int SkipInsignificant(ReadOnlySpan<byte> buffer, int offset, bool skipNewlines)
    {
        while (offset < buffer.Length)
        {
            var b = buffer[offset];
            if (b == ' ' || b == '\t')
            {
                offset++;
                continue;
            }

            if (b == '#')
            {
                offset = SkipComment(buffer, offset);
                continue;
            }

            if (skipNewlines && (b == '\n' || b == '\r'))
            {
                offset++;
                if (b == '\r' && offset < buffer.Length && buffer[offset] == '\n')
                    offset++;
                continue;
            }

            break;
        }

        return offset;
    }

    public static PackItemStartKind ProbePackItemStart(ReadOnlySpan<byte> buffer, ref int offset, bool unitComplete)
    {
        offset = SkipInsignificant(buffer, offset, skipNewlines: true);
        if (offset >= buffer.Length)
            return unitComplete ? PackItemStartKind.Terminated : PackItemStartKind.NeedMoreData;

        if (buffer[offset] == 0x00)
            return PackItemStartKind.Terminated;

        return GetPackValueStart(buffer, offset) switch
        {
            PackValueStartKind.CanStartValue => PackItemStartKind.HasValue,
            PackValueStartKind.CannotStartValue => PackItemStartKind.Terminated,
            _ => unitComplete ? PackItemStartKind.Terminated : PackItemStartKind.NeedMoreData,
        };
    }

    public static PackBoundaryKind ProbePackBoundary(ReadOnlySpan<byte> buffer, ref int offset, bool unitComplete)
    {
        var separatorOffset = offset;
        switch (ProbeSeparator(buffer, ref separatorOffset, unitComplete))
        {
            case SeparatorProbeKind.Separator:
                offset = separatorOffset;
                return PackBoundaryKind.Separator;

            case SeparatorProbeKind.NeedMoreData:
                offset = separatorOffset;
                return PackBoundaryKind.NeedMoreData;
        }

        return ProbePackTermination(buffer, ref offset, unitComplete);
    }

    private static int SkipComment(ReadOnlySpan<byte> buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var b = buffer[offset++];
            if (b == '\n')
                return offset;

            if (b == '\r')
            {
                if (offset < buffer.Length && buffer[offset] == '\n')
                    offset++;
                return offset;
            }
        }

        return offset;
    }

    private static SeparatorProbeKind ProbeSeparator(ReadOnlySpan<byte> buffer, ref int offset, bool unitComplete)
    {
        offset = SkipInsignificant(buffer, offset, skipNewlines: false);
        if (offset >= buffer.Length)
            return unitComplete ? SeparatorProbeKind.None : SeparatorProbeKind.NeedMoreData;

        var b = buffer[offset];
        if (b == ',')
        {
            offset++;
            offset = SkipInsignificant(buffer, offset, skipNewlines: true);
            return SeparatorProbeKind.Separator;
        }

        if (b == '\n' || b == '\r')
        {
            offset++;
            if (b == '\r' && offset < buffer.Length && buffer[offset] == '\n')
                offset++;
            offset = SkipInsignificant(buffer, offset, skipNewlines: true);
            return SeparatorProbeKind.Separator;
        }

        return SeparatorProbeKind.None;
    }

    private static PackBoundaryKind ProbePackTermination(ReadOnlySpan<byte> buffer, ref int offset, bool unitComplete)
    {
        offset = SkipInsignificant(buffer, offset, skipNewlines: true);
        if (offset >= buffer.Length)
            return unitComplete ? PackBoundaryKind.Terminated : PackBoundaryKind.NeedMoreData;

        if (buffer[offset] == 0x00)
            return PackBoundaryKind.Terminated;

        return GetPackValueStart(buffer, offset) switch
        {
            PackValueStartKind.CanStartValue => PackBoundaryKind.Invalid,
            PackValueStartKind.CannotStartValue => PackBoundaryKind.Terminated,
            _ => unitComplete ? PackBoundaryKind.Terminated : PackBoundaryKind.NeedMoreData,
        };
    }

    private static PackValueStartKind GetPackValueStart(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset >= buffer.Length)
            return PackValueStartKind.NeedMoreData;

        var b = buffer[offset];
        if (CanStartValue(b) == PackValueStartKind.CanStartValue)
            return PackValueStartKind.CanStartValue;

        return b switch
        {
            (byte)'t' => ProbeKeyword(buffer, offset, "true"u8),
            (byte)'f' => ProbeKeyword(buffer, offset, "false"u8),
            (byte)'n' => ProbeKeyword(buffer, offset, "nil"u8),
            (byte)'r' => ProbeSecondByte(buffer, offset, (byte)'\'', (byte)'"'),
            (byte)'b' or (byte)'x' => ProbeSecondByte(buffer, offset, (byte)'\''),
            _ => PackValueStartKind.CannotStartValue,
        };
    }

    private static PackValueStartKind CanStartValue(byte b)
        => b switch
        {
            (byte)'\'' or (byte)'"' => PackValueStartKind.CanStartValue,
            (byte)'{' => PackValueStartKind.CanStartValue,
            (byte)'(' => PackValueStartKind.CanStartValue,
            (byte)'[' => PackValueStartKind.CanStartValue,
            (byte)'<' => PackValueStartKind.CanStartValue,
            (byte)'|' => PackValueStartKind.CanStartValue,
            (byte)'.' => PackValueStartKind.CanStartValue,
            (byte)'-' => PackValueStartKind.CanStartValue,
            _ => b >= (byte)'0' && b <= (byte)'9'
                ? PackValueStartKind.CanStartValue
                : PackValueStartKind.CannotStartValue,
        };

    private static PackValueStartKind ProbeKeyword(ReadOnlySpan<byte> buffer, int offset, ReadOnlySpan<byte> keyword)
    {
        if (buffer.Length - offset < keyword.Length)
            return MatchesKeywordPrefix(buffer.Slice(offset), keyword)
                ? PackValueStartKind.NeedMoreData
                : PackValueStartKind.CannotStartValue;

        for (var i = 0; i < keyword.Length; i++)
        {
            if (buffer[offset + i] != keyword[i])
                return PackValueStartKind.CannotStartValue;
        }

        if (offset + keyword.Length < buffer.Length)
        {
            var next = buffer[offset + keyword.Length];
            if (IsAlpha(next) || IsDigit(next) || next == '_' || next == '-')
                return PackValueStartKind.CannotStartValue;
        }

        return PackValueStartKind.CanStartValue;
    }

    private static PackValueStartKind ProbeSecondByte(ReadOnlySpan<byte> buffer, int offset, params byte[] valid)
    {
        if (offset + 1 >= buffer.Length)
            return PackValueStartKind.NeedMoreData;

        var second = buffer[offset + 1];
        foreach (var candidate in valid)
        {
            if (second == candidate)
                return PackValueStartKind.CanStartValue;
        }

        return PackValueStartKind.CannotStartValue;
    }

    private static bool MatchesKeywordPrefix(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> keyword)
    {
        if (candidate.Length > keyword.Length)
            return false;

        for (var i = 0; i < candidate.Length; i++)
        {
            if (candidate[i] != keyword[i])
                return false;
        }

        return true;
    }

    private static bool IsAlpha(byte b)
        => (b >= (byte)'a' && b <= (byte)'z') || (b >= (byte)'A' && b <= (byte)'Z');

    private static bool IsDigit(byte b)
        => b >= (byte)'0' && b <= (byte)'9';
}

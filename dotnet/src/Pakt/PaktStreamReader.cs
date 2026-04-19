using System.Runtime.CompilerServices;
using Pakt.Serialization;

namespace Pakt;

/// <summary>
/// Reads PAKT statements from a stream using real async I/O.
/// The parser core is synchronous over buffered bytes; async boundaries occur
/// only at <see cref="Stream.ReadAsync"/> calls when more data is needed.
/// </summary>
public sealed class PaktStreamReader : IAsyncDisposable
{
    private readonly PaktFramedSource _source;
    private readonly PaktSerializerContext _context;
    private readonly DeserializeOptions _options;
    private readonly PaktReaderOptions _readerOptions;

    private PaktPosition _offsetPosition;
    private bool _disposed;

    private string? _statementName;
    private PaktType? _statementType;
    private PaktPosition _statementPosition;
    private bool _isPack;
    private bool _hasStatement;

    private int _packIndex;

    private PaktStreamReader(
        PaktFramedSource source,
        PaktSerializerContext context,
        DeserializeOptions options,
        PaktReaderOptions readerOptions)
    {
        _source = source;
        _context = context;
        _options = options;
        _readerOptions = readerOptions;
        _offsetPosition = new PaktPosition(1, 1);
        _statementPosition = PaktPosition.None;
    }

    /// <summary>
    /// Creates a stream reader. The reader manages its own buffer over the stream.
    /// </summary>
    public static PaktStreamReader Create(
        Stream stream,
        PaktSerializerContext context,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);

        var source = new PaktFramedSource(stream);
        return new PaktStreamReader(
            source,
            context,
            options ?? context.Options,
            PaktReaderOptions.Default);
    }

    /// <summary>The current statement name.</summary>
    public string StatementName => _statementName
        ?? throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

    /// <summary>The current statement's declared PAKT type.</summary>
    public PaktType StatementType => _statementType
        ?? throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

    /// <summary>The source position of the current statement header.</summary>
    public PaktPosition StatementPosition => _hasStatement
        ? _statementPosition
        : throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

    /// <summary>Whether the current statement is a pack.</summary>
    public bool IsPack => _isPack;

    /// <summary>
    /// Advances to the next statement.
    /// </summary>
    public async ValueTask<bool> ReadStatementAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (_hasStatement)
            await SkipAsync(ct).ConfigureAwait(false);

        while (true)
        {
            if (!await EnsureDataAsync(ct).ConfigureAwait(false))
            {
                _hasStatement = false;
                return false;
            }

            var result = TryReadStatementSync();
            switch (result)
            {
                case ParseAttempt.Success:
                    return true;

                case ParseAttempt.Eof:
                    _hasStatement = false;
                    return false;

                case ParseAttempt.NeedMoreData:
                    if (!await _source.FillAsync(ct).ConfigureAwait(false))
                    {
                        _hasStatement = false;
                        return false;
                    }
                    continue;
            }
        }
    }

    /// <summary>
    /// Reads the current statement's value as <typeparamref name="T"/>.
    /// </summary>
    public async ValueTask<T> ReadValueAsync<T>(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");
        if (_isPack)
        {
            throw new InvalidOperationException(
                _statementType!.IsMap
                    ? "Current statement is a map pack. Use ReadMapPackAsync instead."
                    : "Current statement is a list pack. Use ReadPackAsync instead.");
        }

        var value = await ReadValueCoreAsync(typeof(T), _statementType!, _statementName, null, ct)
            .ConfigureAwait(false);
        _hasStatement = false;
        return (T)value!;
    }

    internal async ValueTask<object?> ReadValueAsync(Type targetType, Type? converterType, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");
        if (_isPack)
        {
            throw new InvalidOperationException(
                _statementType!.IsMap
                    ? "Current statement is a map pack. Use ReadMapPackAsync instead."
                    : "Current statement is a list pack. Use ReadPackAsync instead.");
        }

        var value = await ReadValueCoreAsync(targetType, _statementType!, _statementName, converterType, ct)
            .ConfigureAwait(false);
        _hasStatement = false;
        return value;
    }

    /// <summary>
    /// Enumerates the elements of the current list pack as <typeparamref name="T"/>.
    /// </summary>
    public async IAsyncEnumerable<T> ReadPackAsync<T>([EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureActiveListPack();

        try
        {
            while (true)
            {
                if (await IsPackTerminatedAsync(ct).ConfigureAwait(false))
                    break;

                var value = await ReadValueCoreAsync(
                    typeof(T), _statementType!.ListElement!, null, null, ct).ConfigureAwait(false);

                await ConsumePackSeparatorAsync(ct).ConfigureAwait(false);
                _packIndex++;

                yield return (T)value!;
            }
        }
        finally
        {
            if (_hasStatement && _isPack)
                await SkipAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enumerates the entries of the current map pack.
    /// </summary>
    public async IAsyncEnumerable<PaktMapEntry<TKey, TValue>> ReadMapPackAsync<TKey, TValue>(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureActiveMapPack();

        try
        {
            while (true)
            {
                if (await IsPackTerminatedAsync(ct).ConfigureAwait(false))
                    break;

                var key = await ReadValueCoreAsync(
                    typeof(TKey), _statementType!.MapKey!, null, null, ct).ConfigureAwait(false);

                await ConsumeMapSeparatorAsync(ct).ConfigureAwait(false);

                var value = await ReadValueCoreAsync(
                    typeof(TValue), _statementType.MapValue!, null, null, ct).ConfigureAwait(false);

                await ConsumePackSeparatorAsync(ct).ConfigureAwait(false);
                _packIndex++;

                yield return new PaktMapEntry<TKey, TValue>((TKey)key!, (TValue)value!);
            }
        }
        finally
        {
            if (_hasStatement && _isPack)
                await SkipAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Skips the current statement or remaining pack elements.
    /// </summary>
    public async ValueTask SkipAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");

        if (_isPack)
        {
            await SkipRemainingPackAsync(ct).ConfigureAwait(false);
            return;
        }

        await SkipValueCoreAsync(_statementType!, _statementName, ct).ConfigureAwait(false);
        _hasStatement = false;
    }

    internal async IAsyncEnumerable<object?> ReadPackValuesAsync(
        Type elementType, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureActiveListPack();

        try
        {
            while (true)
            {
                if (await IsPackTerminatedAsync(ct).ConfigureAwait(false))
                    break;

                var value = await ReadValueCoreAsync(
                    elementType, _statementType!.ListElement!, null, null, ct).ConfigureAwait(false);

                await ConsumePackSeparatorAsync(ct).ConfigureAwait(false);
                _packIndex++;

                yield return value;
            }
        }
        finally
        {
            if (_hasStatement && _isPack)
                await SkipAsync(ct).ConfigureAwait(false);
        }
    }

    internal async IAsyncEnumerable<object?> ReadMapPackEntriesAsync(
        Type entryType, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        EnsureActiveMapPack();

        if (!PaktMemoryReader.TryGetMapEntryTypesStatic(entryType, out var keyType, out var valueType))
            throw new InvalidOperationException($"Map packs must be read as PaktMapEntry<TKey, TValue>; got '{entryType.Name}'.");

        try
        {
            while (true)
            {
                if (await IsPackTerminatedAsync(ct).ConfigureAwait(false))
                    break;

                var key = await ReadValueCoreAsync(
                    keyType, _statementType!.MapKey!, null, null, ct).ConfigureAwait(false);

                await ConsumeMapSeparatorAsync(ct).ConfigureAwait(false);

                var value = await ReadValueCoreAsync(
                    valueType, _statementType.MapValue!, null, null, ct).ConfigureAwait(false);

                await ConsumePackSeparatorAsync(ct).ConfigureAwait(false);
                _packIndex++;

                yield return Activator.CreateInstance(entryType, key, value);
            }
        }
        finally
        {
            if (_hasStatement && _isPack)
                await SkipAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await _source.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }

    // -----------------------------------------------------------------------
    // Sync parse attempt (PaktReader fully created and disposed in sync scope)
    // -----------------------------------------------------------------------

    private enum ParseAttempt { Success, Eof, NeedMoreData }

    private ParseAttempt TryReadStatementSync()
    {
        var span = CurrentSpan();
        if (span.IsEmpty)
            return ParseAttempt.Eof;

        if (MayBeIncompletePrefix(span) && !_source.UnitComplete)
            return ParseAttempt.NeedMoreData;

        var reader = new PaktReader(span, _readerOptions, _offsetPosition);
        try
        {
            if (!reader.Read())
                return ParseAttempt.Eof;

            if (reader.TokenType != PaktTokenType.AssignStart && reader.TokenType != PaktTokenType.PackStart)
            {
                throw new PaktException(
                    $"Expected statement start, got {reader.TokenType}",
                    reader.Position,
                    PaktErrorCode.Syntax);
            }

            _statementName = reader.StatementName;
            _statementType = reader.StatementType;
            _statementPosition = reader.StatementPosition;
            _isPack = reader.IsPackStatement;
            _offsetPosition = reader.Position;
            _packIndex = 0;
            _hasStatement = true;
            _source.Advance(reader.BytesConsumed);
            return ParseAttempt.Success;
        }
        catch (PaktException) when (!_source.UnitComplete)
        {
            return ParseAttempt.NeedMoreData;
        }
        finally
        {
            reader.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Core value reading with async retry
    // -----------------------------------------------------------------------

    private async ValueTask<object?> ReadValueCoreAsync(
        Type targetType,
        PaktType declaredType,
        string? name,
        Type? converterType,
        CancellationToken ct)
    {
        while (true)
        {
            if (!await EnsureDataAsync(ct).ConfigureAwait(false))
            {
                throw new PaktException(
                    "Expected value, got end of input",
                    _offsetPosition,
                    PaktErrorCode.UnexpectedEof);
            }

            var span = CurrentSpan();
            if (MayBeIncompletePrefix(span) && !_source.UnitComplete)
            {
                if (!await _source.FillAsync(ct).ConfigureAwait(false))
                    break;
                continue;
            }

            // Create, use, and dispose PaktReader fully within this sync block
            object? value;
            int consumed;
            PaktPosition endPosition;
            bool needMore;

            {
                var reader = PaktReader.CreateValueReader(
                    span, declaredType, name, _readerOptions, _offsetPosition);
                try
                {
                    if (!reader.Read())
                    {
                        needMore = !_source.UnitComplete;
                        consumed = 0;
                        endPosition = default;
                        value = null;

                        if (!needMore)
                        {
                            throw new PaktException(
                                "Expected value, got end of input",
                                _offsetPosition,
                                PaktErrorCode.UnexpectedEof);
                        }
                    }
                    else
                    {
                        var context = new PaktConvertContext(_context, _options, _statementName, null);
                        value = PaktDeserializationRuntime.ReadObject(ref reader, targetType, context, converterType);
                        consumed = reader.BytesConsumed;
                        endPosition = reader.Position;
                        needMore = false;
                    }
                }
                catch (PaktException) when (!_source.UnitComplete)
                {
                    needMore = true;
                    consumed = 0;
                    endPosition = default;
                    value = null;
                }
                finally
                {
                    reader.Dispose();
                }
            }

            if (needMore)
            {
                if (!await _source.FillAsync(ct).ConfigureAwait(false))
                {
                    throw new PaktException(
                        "Expected value, got end of input",
                        _offsetPosition,
                        PaktErrorCode.UnexpectedEof);
                }
                continue;
            }

            _source.Advance(consumed);
            _offsetPosition = endPosition;
            return value;
        }

        throw new PaktException(
            "Expected value, got end of input",
            _offsetPosition,
            PaktErrorCode.UnexpectedEof);
    }

    private async ValueTask SkipValueCoreAsync(PaktType declaredType, string? name, CancellationToken ct)
    {
        while (true)
        {
            if (!await EnsureDataAsync(ct).ConfigureAwait(false))
            {
                throw new PaktException(
                    "Expected value, got EOF",
                    _offsetPosition,
                    PaktErrorCode.UnexpectedEof);
            }

            var span = CurrentSpan();
            int consumed;
            PaktPosition endPosition;
            bool needMore;

            {
                var reader = PaktReader.CreateValueReader(
                    span, declaredType, name, _readerOptions, _offsetPosition);
                try
                {
                    if (!reader.Read())
                    {
                        needMore = !_source.UnitComplete;
                        consumed = 0;
                        endPosition = default;

                        if (!needMore)
                        {
                            throw new PaktException(
                                "Expected value, got EOF",
                                _offsetPosition,
                                PaktErrorCode.UnexpectedEof);
                        }
                    }
                    else
                    {
                        reader.SkipValue();
                        consumed = reader.BytesConsumed;
                        endPosition = reader.Position;
                        needMore = false;
                    }
                }
                catch (PaktException) when (!_source.UnitComplete)
                {
                    needMore = true;
                    consumed = 0;
                    endPosition = default;
                }
                finally
                {
                    reader.Dispose();
                }
            }

            if (needMore)
            {
                if (!await _source.FillAsync(ct).ConfigureAwait(false))
                {
                    throw new PaktException(
                        "Expected value, got EOF",
                        _offsetPosition,
                        PaktErrorCode.UnexpectedEof);
                }
                continue;
            }

            _source.Advance(consumed);
            _offsetPosition = endPosition;
            return;
        }
    }

    // -----------------------------------------------------------------------
    // Pack helpers
    // -----------------------------------------------------------------------

    private async ValueTask<bool> IsPackTerminatedAsync(CancellationToken ct)
    {
        while (true)
        {
            if (!await EnsureDataAsync(ct).ConfigureAwait(false))
            {
                _hasStatement = false;
                return true;
            }

            var span = CurrentSpan();
            int cursor = 0;
            var result = PaktUnitSyntax.ProbePackItemStart(span, ref cursor, _source.UnitComplete);

            switch (result)
            {
                case PaktUnitSyntax.PackItemStartKind.HasValue:
                    if (cursor > 0)
                        _source.Advance(cursor);
                    return false;

                case PaktUnitSyntax.PackItemStartKind.Terminated:
                    if (cursor > 0)
                        _source.Advance(cursor);
                    _hasStatement = false;
                    return true;

                default:
                    if (!await _source.FillAsync(ct).ConfigureAwait(false))
                    {
                        _hasStatement = false;
                        return true;
                    }
                    continue;
            }
        }
    }

    private async ValueTask ConsumePackSeparatorAsync(CancellationToken ct)
    {
        while (true)
        {
            var span = CurrentSpan();
            int cursor = 0;
            var result = PaktUnitSyntax.ProbePackBoundary(span, ref cursor, _source.UnitComplete);

            switch (result)
            {
                case PaktUnitSyntax.PackBoundaryKind.Separator:
                    _source.Advance(cursor);
                    return;

                case PaktUnitSyntax.PackBoundaryKind.Terminated:
                    _source.Advance(cursor);
                    _hasStatement = false;
                    return;

                case PaktUnitSyntax.PackBoundaryKind.NeedMoreData:
                    if (!await _source.FillAsync(ct).ConfigureAwait(false))
                        return;
                    continue;

                default:
                    throw new PaktException(
                        "Expected separator between pack items",
                        _offsetPosition,
                        PaktErrorCode.Syntax);
            }
        }
    }

    private async ValueTask ConsumeMapSeparatorAsync(CancellationToken ct)
    {
        while (true)
        {
            var span = CurrentSpan();
            var cursor = PaktUnitSyntax.SkipHorizontalWhitespace(span, 0);

            if (cursor >= span.Length)
            {
                if (!_source.UnitComplete && await _source.FillAsync(ct).ConfigureAwait(false))
                    continue;

                throw new PaktException(
                    "Expected ';' between pack map key and value",
                    _offsetPosition,
                    PaktErrorCode.UnexpectedEof);
            }

            if (span[cursor] != ';')
            {
                throw new PaktException(
                    "Expected ';' between pack map key and value",
                    _offsetPosition,
                    PaktErrorCode.Syntax);
            }

            cursor++;
            cursor = PaktUnitSyntax.SkipHorizontalWhitespace(span, cursor);
            _source.Advance(cursor);
            return;
        }
    }

    private async ValueTask SkipRemainingPackAsync(CancellationToken ct)
    {
        while (true)
        {
            if (await IsPackTerminatedAsync(ct).ConfigureAwait(false))
                return;

            if (_statementType!.IsList)
            {
                await SkipValueCoreAsync(_statementType.ListElement!, $"[{_packIndex}]", ct).ConfigureAwait(false);
                await ConsumePackSeparatorAsync(ct).ConfigureAwait(false);
                _packIndex++;
                continue;
            }

            await SkipValueCoreAsync(_statementType.MapKey!, null, ct).ConfigureAwait(false);
            await ConsumeMapSeparatorAsync(ct).ConfigureAwait(false);
            await SkipValueCoreAsync(_statementType.MapValue!, null, ct).ConfigureAwait(false);
            await ConsumePackSeparatorAsync(ct).ConfigureAwait(false);
            _packIndex++;
        }
    }

    // -----------------------------------------------------------------------
    // Buffer management
    // -----------------------------------------------------------------------

    private async ValueTask<bool> EnsureDataAsync(CancellationToken ct)
    {
        if (_source.Available.Length > 0)
            return true;

        return await _source.FillAsync(ct).ConfigureAwait(false);
    }

    private ReadOnlySpan<byte> CurrentSpan() => _source.Available.Span;

    // -----------------------------------------------------------------------
    // Incomplete prefix detection
    // -----------------------------------------------------------------------

    private static bool MayBeIncompletePrefix(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return true;

        // Buffer ends with single `<` — could be start of `<<`
        if (span[^1] == '<')
            return true;

        if (EndsWithPartialKeyword(span))
            return true;

        // Partial numeric prefix: `0x`, `0b`, `0o` at end
        if (span.Length >= 2 && span[^2] == '0')
        {
            var last = span[^1];
            if (last is (byte)'x' or (byte)'b' or (byte)'o' or (byte)'X' or (byte)'B' or (byte)'O')
                return true;
        }

        // Partial raw string/bin prefix: `r'`, `x'`, `b'` at very end (quote not closed)
        if (span[^1] is (byte)'\'' or (byte)'"')
        {
            if (span.Length >= 2)
            {
                var prefix = span[^2];
                if (prefix is (byte)'r' or (byte)'x' or (byte)'b')
                    return true;
            }
        }

        if (EndsWithIncompleteUtf8(span))
            return true;

        return false;
    }

    private static bool EndsWithPartialKeyword(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> trueKw = "true"u8;
        ReadOnlySpan<byte> falseKw = "false"u8;
        ReadOnlySpan<byte> nilKw = "nil"u8;

        return IsPartialMatch(span, trueKw) || IsPartialMatch(span, falseKw) || IsPartialMatch(span, nilKw);
    }

    private static bool IsPartialMatch(ReadOnlySpan<byte> span, ReadOnlySpan<byte> kw)
    {
        for (int prefixLen = 1; prefixLen < kw.Length; prefixLen++)
        {
            if (span.Length >= prefixLen && span[^prefixLen..].SequenceEqual(kw[..prefixLen]))
            {
                if (span.Length == prefixLen || !IsIdentChar(span[^(prefixLen + 1)]))
                    return true;
            }
        }

        return false;
    }

    private static bool IsIdentChar(byte b)
        => (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == '_' || b == '-';

    private static bool EndsWithIncompleteUtf8(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return false;

        int i = span.Length - 1;
        int continuationCount = 0;

        while (i >= 0 && (span[i] & 0xC0) == 0x80)
        {
            continuationCount++;
            i--;
            if (continuationCount > 3) return false;
        }

        if (i < 0) return continuationCount > 0;

        byte lead = span[i];
        int expectedContinuations;
        if ((lead & 0x80) == 0) expectedContinuations = 0;
        else if ((lead & 0xE0) == 0xC0) expectedContinuations = 1;
        else if ((lead & 0xF0) == 0xE0) expectedContinuations = 2;
        else if ((lead & 0xF8) == 0xF0) expectedContinuations = 3;
        else return false;

        return continuationCount < expectedContinuations;
    }

    // -----------------------------------------------------------------------
    // Guards
    // -----------------------------------------------------------------------

    private void EnsureActiveListPack()
    {
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");
        if (!_isPack || !_statementType!.IsList)
            throw new InvalidOperationException("Current statement is not a list pack. Use ReadValueAsync or ReadMapPackAsync instead.");
    }

    private void EnsureActiveMapPack()
    {
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatementAsync first.");
        if (!_isPack || !_statementType!.IsMap)
            throw new InvalidOperationException("Current statement is not a map pack. Use ReadValueAsync or ReadPackAsync instead.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PaktStreamReader));
    }
}

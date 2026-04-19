using System.Buffers;
using Pakt.Serialization;

namespace Pakt;

/// <summary>
/// Reads PAKT statements one at a time from borrowed or owned memory.
/// This is the sync-only, memory-backed reader. For stream input, use PaktStreamReader.
/// </summary>
public sealed class PaktMemoryReader : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _length;
    private readonly PaktSerializerContext _context;
    private readonly DeserializeOptions _options;
    private readonly PaktReaderOptions _readerOptions;

    private int _offset;
    private PaktPosition _offsetPosition;
    private bool _disposed;

    private string? _statementName;
    private PaktType? _statementType;
    private PaktPosition _statementPosition;
    private bool _isPack;
    private int _valueOffset;
    private PaktPosition _valuePosition;
    private bool _hasStatement;

    private int _packCursor;
    private PaktPosition _packPosition;
    private int _packIndex;

    private PaktMemoryReader(
        ReadOnlyMemory<byte> data,
        IMemoryOwner<byte>? owner,
        int length,
        PaktSerializerContext context,
        DeserializeOptions options,
        PaktReaderOptions readerOptions)
    {
        _data = data;
        _owner = owner;
        _length = length;
        _context = context;
        _options = options;
        _readerOptions = readerOptions;
        _offsetPosition = new PaktPosition(1, 1);
        _valuePosition = _offsetPosition;
        _packPosition = _offsetPosition;
        _statementPosition = PaktPosition.None;
    }

    /// <summary>
    /// Creates a reader over borrowed memory without copying.
    /// The caller retains ownership of the memory for the reader lifetime.
    /// </summary>
    public static PaktMemoryReader Create(
        ReadOnlyMemory<byte> data,
        PaktSerializerContext context,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new PaktMemoryReader(
            data,
            owner: null,
            data.Length,
            context,
            options ?? context.Options,
            PaktReaderOptions.Default);
    }

    /// <summary>
    /// Creates a reader that takes ownership of the supplied memory owner.
    /// The owner is disposed when the reader is disposed.
    /// </summary>
    public static PaktMemoryReader Create(
        IMemoryOwner<byte> owner,
        int length,
        PaktSerializerContext context,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(context);

        if (length < 0 || length > owner.Memory.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                length,
                "Length must be within the owned buffer.");
        }

        return new PaktMemoryReader(
            owner.Memory,
            owner,
            length,
            context,
            options ?? context.Options,
            PaktReaderOptions.Default);
    }

    /// <summary>The current statement name.</summary>
    public string StatementName => _statementName
        ?? throw new InvalidOperationException("No current statement. Call ReadStatement first.");

    /// <summary>The current statement's declared PAKT type.</summary>
    public PaktType StatementType => _statementType
        ?? throw new InvalidOperationException("No current statement. Call ReadStatement first.");

    /// <summary>The source position of the current statement header.</summary>
    public PaktPosition StatementPosition => _hasStatement
        ? _statementPosition
        : throw new InvalidOperationException("No current statement. Call ReadStatement first.");

    /// <summary>Whether the current statement is a pack.</summary>
    public bool IsPack => _isPack;

    /// <summary>
    /// Advances to the next statement.
    /// </summary>
    public bool ReadStatement()
    {
        ThrowIfDisposed();

        if (_hasStatement)
            Skip();

        if (_offset >= _length)
        {
            _hasStatement = false;
            return false;
        }

        var reader = new PaktReader(
            BufferSpan(_offset),
            _readerOptions,
            _offsetPosition);

        try
        {
            if (!reader.Read())
            {
                _hasStatement = false;
                return false;
            }

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
            _valueOffset = _offset + reader.BytesConsumed;
            _valuePosition = reader.Position;
            _packCursor = _valueOffset;
            _packPosition = _valuePosition;
            _packIndex = 0;
            _hasStatement = true;
            return true;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// Reads the current statement's value as <typeparamref name="T"/>.
    /// </summary>
    public T ReadValue<T>()
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatement first.");
        if (_isPack)
        {
            throw new InvalidOperationException(
                _statementType!.IsMap
                    ? "Current statement is a map pack. Use ReadMapPack instead."
                    : "Current statement is a list pack. Use ReadPack instead.");
        }

        var typeInfo = _context.GetTypeInfo<T>();
        if (typeInfo?.Deserialize is not null)
        {
            var reader = PaktReader.CreateValueReader(
                BufferSpan(_valueOffset), _statementType!, _statementName, _readerOptions, _valuePosition);
            try
            {
                if (!reader.Read())
                    throw new PaktException("Expected value, got end of input", _valuePosition, PaktErrorCode.UnexpectedEof);

                var context = new PaktConvertContext(_context, _options, _statementName, null);
                var value = typeInfo.Deserialize(ref reader, context);
                _offset = _valueOffset + reader.BytesConsumed;
                _offsetPosition = reader.Position;
                _hasStatement = false;
                return value;
            }
            finally
            {
                reader.Dispose();
            }
        }

        return (T)ReadValue(typeof(T))!;
    }

    internal object? ReadValue(Type targetType, Type? converterType = null)
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatement first.");
        if (_isPack)
        {
            throw new InvalidOperationException(
                _statementType!.IsMap
                    ? "Current statement is a map pack. Use ReadMapPack instead."
                    : "Current statement is a list pack. Use ReadPack instead.");
        }

        var reader = PaktReader.CreateValueReader(
            BufferSpan(_valueOffset),
            _statementType!,
            _statementName,
            _readerOptions,
            _valuePosition);

        try
        {
            if (!reader.Read())
            {
                throw new PaktException(
                    "Expected value, got end of input",
                    _valuePosition,
                    PaktErrorCode.UnexpectedEof);
            }

            var context = new PaktConvertContext(_context, _options, _statementName, null);
            var value = PaktDeserializationRuntime.ReadObject(ref reader, targetType, context, converterType);
            _offset = _valueOffset + reader.BytesConsumed;
            _offsetPosition = reader.Position;
            _hasStatement = false;
            return value;
        }
        finally
        {
            reader.Dispose();
        }
    }

    /// <summary>
    /// Enumerates the elements of the current list pack as <typeparamref name="T"/>.
    /// </summary>
    public IEnumerable<T> ReadPack<T>()
    {
        ThrowIfDisposed();
        EnsureActiveListPack();

        var typeInfo = _context.GetTypeInfo<T>();
        if (typeInfo?.Deserialize is not null)
            return ReadPackGeneric(typeInfo);

        return ReadPackViaRuntime<T>();
    }

    private IEnumerable<T> ReadPackGeneric<T>(PaktTypeInfo<T> typeInfo)
    {
        try
        {
            var elementType = _statementType!.ListElement!;
            while (true)
            {
                var cursor = _packCursor;
                var position = _packPosition;

                if (IsPackTerminated(ref cursor, ref position))
                {
                    _offset = cursor;
                    _offsetPosition = position;
                    _hasStatement = false;
                    yield break;
                }

                var reader = PaktReader.CreateValueReader(
                    BufferSpan(cursor), elementType, null, _readerOptions, position);
                T value;
                try
                {
                    if (!reader.Read())
                        throw new PaktException("Expected value, got EOF", position, PaktErrorCode.UnexpectedEof);

                    var context = new PaktConvertContext(_context, _options, _statementName, null);
                    value = typeInfo.Deserialize!(ref reader, context);
                    cursor += reader.BytesConsumed;
                    position = reader.Position;
                }
                finally
                {
                    reader.Dispose();
                }

                FinishPackElement(ref cursor, ref position);
                _packCursor = cursor;
                _packPosition = position;
                _packIndex++;

                yield return value;
            }
        }
        finally
        {
            if (_hasStatement && _isPack)
                Skip();
        }
    }

    private IEnumerable<T> ReadPackViaRuntime<T>()
    {
        try
        {
            while (TryReadNextListPackValue(typeof(T), null, out var value))
                yield return (T)value!;
        }
        finally
        {
            if (_hasStatement && _isPack)
                Skip();
        }
    }

    /// <summary>
    /// Enumerates the entries of the current map pack as strongly-typed map entries.
    /// </summary>
    public IEnumerable<PaktMapEntry<TKey, TValue>> ReadMapPack<TKey, TValue>()
    {
        ThrowIfDisposed();
        EnsureActiveMapPack();

        try
        {
            var entryType = typeof(PaktMapEntry<TKey, TValue>);
            while (TryReadNextMapPackValue(entryType, out var value))
                yield return (PaktMapEntry<TKey, TValue>)value!;
        }
        finally
        {
            if (_hasStatement && _isPack)
                Skip();
        }
    }

    internal IEnumerable<object?> ReadPackValues(Type targetType, Type? converterType = null)
    {
        ThrowIfDisposed();
        EnsureActiveListPack();
        return ReadListPackValuesIterator(targetType, converterType);
    }

    internal IEnumerable<object?> ReadMapPackEntries(Type targetType)
    {
        ThrowIfDisposed();
        EnsureActiveMapPack();
        return ReadMapPackEntriesIterator(targetType);
    }

    /// <summary>
    /// Skips the current statement or remaining pack elements.
    /// </summary>
    public void Skip()
    {
        ThrowIfDisposed();
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatement first.");

        if (_isPack)
        {
            SkipRemainingPack();
            return;
        }

        SkipValueAt(
            _valueOffset,
            _valuePosition,
            _statementType!,
            _statementName,
            out var nextOffset,
            out var nextPosition);
        _offset = nextOffset;
        _offsetPosition = nextPosition;
        _hasStatement = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _owner?.Dispose();
        _owner = null;
        _disposed = true;
    }

    private IEnumerable<object?> ReadListPackValuesIterator(Type targetType, Type? converterType)
    {
        try
        {
            while (TryReadNextListPackValue(targetType, converterType, out var value))
                yield return value;
        }
        finally
        {
            if (_hasStatement && _isPack)
                Skip();
        }
    }

    private IEnumerable<object?> ReadMapPackEntriesIterator(Type targetType)
    {
        try
        {
            while (TryReadNextMapPackValue(targetType, out var value))
                yield return value;
        }
        finally
        {
            if (_hasStatement && _isPack)
                Skip();
        }
    }

    private bool TryReadNextListPackValue(Type targetType, Type? converterType, out object? value)
    {
        var cursor = _packCursor;
        var position = _packPosition;

        if (IsPackTerminated(ref cursor, ref position))
        {
            _offset = cursor;
            _offsetPosition = position;
            _hasStatement = false;
            value = null;
            return false;
        }

        value = ReadPackValueAt(
            cursor,
            position,
            targetType,
            _statementType!.ListElement!,
            out cursor,
            out position,
            converterType);

        FinishPackElement(ref cursor, ref position);
        _packCursor = cursor;
        _packPosition = position;
        _packIndex++;
        return true;
    }

    private bool TryReadNextMapPackValue(Type targetType, out object? value)
    {
        if (!TryGetMapEntryTypes(targetType, out var keyType, out var valueType))
        {
            throw new InvalidOperationException(
                $"Map packs must be read as PaktMapEntry<TKey, TValue>; got '{targetType.Name}'.");
        }

        var cursor = _packCursor;
        var position = _packPosition;

        if (IsPackTerminated(ref cursor, ref position))
        {
            _offset = cursor;
            _offsetPosition = position;
            _hasStatement = false;
            value = null;
            return false;
        }

        var key = ReadPackValueAt(
            cursor,
            position,
            keyType,
            _statementType!.MapKey!,
            out cursor,
            out position);

        ConsumeMapSeparator(ref cursor, ref position);

        var mapValue = ReadPackValueAt(
            cursor,
            position,
            valueType,
            _statementType.MapValue!,
            out cursor,
            out position);

        FinishPackElement(ref cursor, ref position);
        _packCursor = cursor;
        _packPosition = position;
        _packIndex++;
        value = Activator.CreateInstance(targetType, key, mapValue)!;
        return true;
    }

    private object? ReadPackValueAt(
        int startOffset,
        PaktPosition startPosition,
        Type targetType,
        PaktType declaredType,
        out int nextOffset,
        out PaktPosition nextPosition,
        Type? converterType = null)
    {
        var reader = PaktReader.CreateValueReader(
            BufferSpan(startOffset),
            declaredType,
            null,
            _readerOptions,
            startPosition);

        try
        {
            if (!reader.Read())
            {
                throw new PaktException(
                    "Expected value, got EOF",
                    startPosition,
                    PaktErrorCode.UnexpectedEof);
            }

            var context = new PaktConvertContext(_context, _options, _statementName, null);
            var value = PaktDeserializationRuntime.ReadObject(ref reader, targetType, context, converterType);
            nextOffset = startOffset + reader.BytesConsumed;
            nextPosition = reader.Position;
            return value;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private void FinishPackElement(ref int cursor, ref PaktPosition position)
    {
        var startOffset = cursor;
        switch (PaktUnitSyntax.ProbePackBoundary(BufferSpan(), ref cursor, unitComplete: true))
        {
            case PaktUnitSyntax.PackBoundaryKind.Separator:
                position = AdvancePosition(position, startOffset, cursor);
                return;

            case PaktUnitSyntax.PackBoundaryKind.Terminated:
                position = AdvancePosition(position, startOffset, cursor);
                _offset = cursor;
                _offsetPosition = position;
                _hasStatement = false;
                return;

            default:
                position = AdvancePosition(position, startOffset, cursor);
                throw new PaktException(
                    "Expected separator between pack items",
                    position,
                    PaktErrorCode.Syntax);
        }
    }

    private void SkipRemainingPack()
    {
        var cursor = _packCursor;
        var position = _packPosition;

        while (true)
        {
            if (IsPackTerminated(ref cursor, ref position))
            {
                _offset = cursor;
                _offsetPosition = position;
                _hasStatement = false;
                return;
            }

            if (_statementType!.IsList)
            {
                SkipValueAt(
                    cursor,
                    position,
                    _statementType.ListElement!,
                    $"[{_packIndex}]",
                    out cursor,
                    out position);
                FinishPackElement(ref cursor, ref position);
                _packIndex++;
                continue;
            }

            SkipValueAt(
                cursor,
                position,
                _statementType.MapKey!,
                null,
                out cursor,
                out position);
            ConsumeMapSeparator(ref cursor, ref position);
            SkipValueAt(
                cursor,
                position,
                _statementType.MapValue!,
                null,
                out cursor,
                out position);
            FinishPackElement(ref cursor, ref position);
            _packIndex++;
        }
    }

    private void SkipValueAt(
        int startOffset,
        PaktPosition startPosition,
        PaktType declaredType,
        string? name,
        out int nextOffset,
        out PaktPosition nextPosition)
    {
        var reader = PaktReader.CreateValueReader(
            BufferSpan(startOffset),
            declaredType,
            name,
            _readerOptions,
            startPosition);

        try
        {
            if (!reader.Read())
            {
                throw new PaktException(
                    "Expected value, got EOF",
                    startPosition,
                    PaktErrorCode.UnexpectedEof);
            }

            reader.SkipValue();
            nextOffset = startOffset + reader.BytesConsumed;
            nextPosition = reader.Position;
        }
        finally
        {
            reader.Dispose();
        }
    }

    private bool IsPackTerminated(ref int cursor, ref PaktPosition position)
    {
        var startOffset = cursor;
        var result = PaktUnitSyntax.ProbePackItemStart(BufferSpan(), ref cursor, unitComplete: true);
        position = AdvancePosition(position, startOffset, cursor);

        return result switch
        {
            PaktUnitSyntax.PackItemStartKind.HasValue => false,
            _ => true,
        };
    }

    private void ConsumeMapSeparator(ref int cursor, ref PaktPosition position)
    {
        var startOffset = cursor;
        cursor = PaktUnitSyntax.SkipHorizontalWhitespace(BufferSpan(), cursor);
        position = AdvancePosition(position, startOffset, cursor);

        if (cursor >= _length)
        {
            throw new PaktException(
                "Expected ';' between pack map key and value",
                position,
                PaktErrorCode.UnexpectedEof);
        }

        if (BufferSpan()[cursor] != ';')
        {
            throw new PaktException(
                "Expected ';' between pack map key and value",
                position,
                PaktErrorCode.Syntax);
        }

        position = AdvancePosition(position, cursor, cursor + 1);
        cursor++;

        startOffset = cursor;
        cursor = PaktUnitSyntax.SkipHorizontalWhitespace(BufferSpan(), cursor);
        position = AdvancePosition(position, startOffset, cursor);
    }

    private PaktPosition AdvancePosition(PaktPosition position, int startOffset, int endOffset)
    {
        var line = position.Line;
        var column = position.Column;
        var span = _data.Span;

        for (var i = startOffset; i < endOffset; i++)
        {
            var b = span[i];
            if (b == '\n')
            {
                line++;
                column = 1;
                continue;
            }

            if (b == '\r')
            {
                if (i + 1 < endOffset && span[i + 1] == '\n')
                    i++;
                line++;
                column = 1;
                continue;
            }

            column++;
        }

        return new PaktPosition(line, column);
    }

    private ReadOnlySpan<byte> BufferSpan(int startOffset = 0)
        => _data.Span.Slice(startOffset, _length - startOffset);

    private void EnsureActiveListPack()
    {
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatement first.");
        if (!_isPack || !_statementType!.IsList)
            throw new InvalidOperationException("Current statement is not a list pack. Use ReadValue or ReadMapPack instead.");
    }

    private void EnsureActiveMapPack()
    {
        if (!_hasStatement)
            throw new InvalidOperationException("No current statement. Call ReadStatement first.");
        if (!_isPack || !_statementType!.IsMap)
            throw new InvalidOperationException("Current statement is not a map pack. Use ReadValue or ReadPack instead.");
    }

    internal static bool TryGetMapEntryTypesStatic(Type type, out Type keyType, out Type valueType)
        => TryGetMapEntryTypes(type, out keyType, out valueType);

    private static bool TryGetMapEntryTypes(Type type, out Type keyType, out Type valueType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PaktMapEntry<,>))
        {
            var args = type.GetGenericArguments();
            keyType = args[0];
            valueType = args[1];
            return true;
        }

        keyType = null!;
        valueType = null!;
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PaktMemoryReader));
    }
}

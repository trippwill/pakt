using System;
using Pakt.Serialization;

namespace Pakt;

/// <summary>
/// Extension helpers for navigating composite PAKT values.
/// </summary>
public static class PaktReaderExtensions
{
    /// <summary>
    /// Visits the fields of the current struct value in declaration order.
    /// The reader must be positioned at <see cref="PaktTokenType.StructStart"/>.
    /// For each callback, the reader is positioned at the field's value token.
    /// Return <c>false</c> to stop early; the remaining fields are drained automatically.
    /// If a callback leaves a composite field untouched, it is skipped automatically before
    /// advancing to the next field. If a callback starts traversing a composite field, it must
    /// finish consuming that field (for example via <see cref="SkipValue(ref PaktReader)"/>,
    /// <see cref="PaktConvertContext.ReadAs{T}(ref PaktReader)"/>, or nested navigation helpers).
    /// </summary>
    public static void StructFields(this ref PaktReader reader, PaktStructFieldHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        PaktReaderNavigation.EnsureContainer(ref reader, PaktTokenType.StructStart, nameof(StructFields));

        while (true)
        {
            if (!reader.Read())
                PaktReaderNavigation.ThrowUnexpectedEof("struct");

            if (reader.TokenType == PaktTokenType.StructEnd)
                return;

            if (!handler(ref reader, new PaktFieldEntry(reader.CurrentName!, reader.CurrentType!)))
            {
                PaktReaderNavigation.SkipUntouchedComposite(ref reader);
                PaktReaderNavigation.DrainSequenceRemainder(ref reader, PaktTokenType.StructEnd, "struct");
                return;
            }

            PaktReaderNavigation.SkipUntouchedComposite(ref reader);
        }
    }

    /// <summary>
    /// Visits the elements of the current tuple value in order.
    /// The reader must be positioned at <see cref="PaktTokenType.TupleStart"/>.
    /// For each callback, the reader is positioned at the element's value token.
    /// Return <c>false</c> to stop early; the remaining elements are drained automatically.
    /// If a callback leaves a composite element untouched, it is skipped automatically before
    /// advancing to the next element. If a callback starts traversing a composite element, it must
    /// finish consuming that element before returning.
    /// </summary>
    public static void TupleElements(this ref PaktReader reader, PaktTupleElementHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        PaktReaderNavigation.EnsureContainer(ref reader, PaktTokenType.TupleStart, nameof(TupleElements));

        var index = 0;
        while (true)
        {
            if (!reader.Read())
                PaktReaderNavigation.ThrowUnexpectedEof("tuple");

            if (reader.TokenType == PaktTokenType.TupleEnd)
                return;

            if (!handler(ref reader, new PaktTupleEntry(index, reader.CurrentType!)))
            {
                PaktReaderNavigation.SkipUntouchedComposite(ref reader);
                PaktReaderNavigation.DrainSequenceRemainder(ref reader, PaktTokenType.TupleEnd, "tuple");
                return;
            }

            PaktReaderNavigation.SkipUntouchedComposite(ref reader);
            index++;
        }
    }

    /// <summary>
    /// Visits the elements of the current list value as <typeparamref name="T"/>.
    /// The reader must be positioned at <see cref="PaktTokenType.ListStart"/>.
    /// Return <c>false</c> to stop early; the remaining elements are drained automatically.
    /// </summary>
    public static void ListElements<T>(
        this ref PaktReader reader,
        PaktConvertContext context,
        PaktValueHandler<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        PaktReaderNavigation.EnsureContainer(ref reader, PaktTokenType.ListStart, nameof(ListElements));

        while (true)
        {
            if (!reader.Read())
                PaktReaderNavigation.ThrowUnexpectedEof("list");

            if (reader.TokenType == PaktTokenType.ListEnd)
                return;

            if (!handler(context.ReadAs<T>(ref reader)))
            {
                PaktReaderNavigation.DrainSequenceRemainder(ref reader, PaktTokenType.ListEnd, "list");
                return;
            }
        }
    }

    /// <summary>
    /// Visits the elements of the current list value as <typeparamref name="T"/>.
    /// The reader must be positioned at <see cref="PaktTokenType.ListStart"/>.
    /// Return <c>false</c> to stop early; the remaining elements are drained automatically.
    /// </summary>
    public static void ListElements<T>(
        this ref PaktReader reader,
        PaktSerializerContext serializerContext,
        PaktValueHandler<T> handler,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serializerContext);
        var context = new PaktConvertContext(
            serializerContext,
            options ?? serializerContext.Options,
            reader.StatementName,
            reader.CurrentName);
        reader.ListElements(context, handler);
    }

    /// <summary>
    /// Visits the entries of the current map value.
    /// The reader must be positioned at <see cref="PaktTokenType.MapStart"/>.
    /// Return <c>false</c> to stop early; the remaining entries are drained automatically.
    /// </summary>
    public static void MapEntries<TKey, TValue>(
        this ref PaktReader reader,
        PaktConvertContext context,
        PaktMapEntryHandler<TKey, TValue> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        PaktReaderNavigation.EnsureContainer(ref reader, PaktTokenType.MapStart, nameof(MapEntries));

        while (true)
        {
            if (!reader.Read())
                PaktReaderNavigation.ThrowUnexpectedEof("map");

            if (reader.TokenType == PaktTokenType.MapEnd)
                return;

            var key = context.ReadAs<TKey>(ref reader);

            if (!reader.Read())
                PaktReaderNavigation.ThrowUnexpectedEof("map");

            if (reader.TokenType == PaktTokenType.MapEnd)
            {
                throw new InvalidOperationException(
                    "Encountered map end while reading a map value; each key must be followed by a value.");
            }

            var value = context.ReadAs<TValue>(ref reader);
            if (!handler(new PaktMapEntry<TKey, TValue>(key, value)))
            {
                PaktReaderNavigation.DrainMapRemainder(ref reader);
                return;
            }
        }
    }

    /// <summary>
    /// Visits the entries of the current map value.
    /// The reader must be positioned at <see cref="PaktTokenType.MapStart"/>.
    /// Return <c>false</c> to stop early; the remaining entries are drained automatically.
    /// </summary>
    public static void MapEntries<TKey, TValue>(
        this ref PaktReader reader,
        PaktSerializerContext serializerContext,
        PaktMapEntryHandler<TKey, TValue> handler,
        DeserializeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(serializerContext);
        var context = new PaktConvertContext(
            serializerContext,
            options ?? serializerContext.Options,
            reader.StatementName,
            reader.CurrentName);
        reader.MapEntries(context, handler);
    }

    /// <summary>
    /// Skips the current value (scalar or composite) entirely.
    /// </summary>
    public static void SkipValue(this ref PaktReader reader)
    {
        switch (reader.TokenType)
        {
            case PaktTokenType.ScalarValue:
            case PaktTokenType.Nil:
                return;

            case PaktTokenType.StructStart:
            case PaktTokenType.ListStart:
            case PaktTokenType.MapStart:
            case PaktTokenType.TupleStart:
                var depth = 1;
                while (depth > 0 && reader.Read())
                {
                    switch (reader.TokenType)
                    {
                        case PaktTokenType.StructStart:
                        case PaktTokenType.ListStart:
                        case PaktTokenType.MapStart:
                        case PaktTokenType.TupleStart:
                            depth++;
                            break;

                        case PaktTokenType.StructEnd:
                        case PaktTokenType.ListEnd:
                        case PaktTokenType.MapEnd:
                        case PaktTokenType.TupleEnd:
                            depth--;
                            break;
                    }
                }
                return;

            default:
                throw new InvalidOperationException($"Reader is not positioned at a value token: {reader.TokenType}");
        }
    }
}

/// <summary>
/// Callback for struct field traversal.
/// </summary>
public delegate bool PaktStructFieldHandler(ref PaktReader reader, PaktFieldEntry field);

/// <summary>
/// Callback for tuple element traversal.
/// </summary>
public delegate bool PaktTupleElementHandler(ref PaktReader reader, PaktTupleEntry element);

/// <summary>
/// Callback for typed list element traversal.
/// </summary>
public delegate bool PaktValueHandler<T>(T value);

/// <summary>
/// Callback for typed map entry traversal.
/// </summary>
public delegate bool PaktMapEntryHandler<TKey, TValue>(PaktMapEntry<TKey, TValue> entry);

/// <summary>
/// A named field within a struct value.
/// </summary>
public readonly struct PaktFieldEntry
{
    /// <summary>
    /// Initializes a new <see cref="PaktFieldEntry"/>.
    /// </summary>
    public PaktFieldEntry(string name, PaktType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>The field name from the declared struct type.</summary>
    public string Name { get; }

    /// <summary>The declared PAKT type of the field.</summary>
    public PaktType Type { get; }
}

/// <summary>
/// A tuple element within a tuple value.
/// </summary>
public readonly struct PaktTupleEntry
{
    /// <summary>
    /// Initializes a new <see cref="PaktTupleEntry"/>.
    /// </summary>
    public PaktTupleEntry(int index, PaktType type)
    {
        Index = index;
        Type = type;
    }

    /// <summary>The zero-based element index.</summary>
    public int Index { get; }

    /// <summary>The declared PAKT type of the element.</summary>
    public PaktType Type { get; }
}

internal static class PaktReaderNavigation
{
    public static void EnsureContainer(ref PaktReader reader, PaktTokenType startToken, string methodName)
    {
        if (reader.TokenType != startToken)
        {
            throw new InvalidOperationException(
                $"{methodName} requires the reader to be positioned at {startToken}; current token is {reader.TokenType}.");
        }
    }

    public static bool IsCompositeStart(PaktTokenType token)
        => token is PaktTokenType.StructStart
            or PaktTokenType.ListStart
            or PaktTokenType.MapStart
            or PaktTokenType.TupleStart;

    public static void SkipUntouchedComposite(ref PaktReader reader)
    {
        if (IsCompositeStart(reader.TokenType))
            reader.SkipValue();
    }

    public static void DrainSequenceRemainder(
        ref PaktReader reader,
        PaktTokenType endToken,
        string containerName)
    {
        while (true)
        {
            if (!reader.Read())
                ThrowUnexpectedEof(containerName);

            if (reader.TokenType == endToken)
                return;

            reader.SkipValue();
        }
    }

    public static void DrainMapRemainder(ref PaktReader reader)
    {
        while (true)
        {
            if (!reader.Read())
                ThrowUnexpectedEof("map");

            if (reader.TokenType == PaktTokenType.MapEnd)
                return;

            reader.SkipValue();

            if (!reader.Read())
                ThrowUnexpectedEof("map");

            if (reader.TokenType == PaktTokenType.MapEnd)
            {
                throw new InvalidOperationException(
                    "Encountered map end while draining map entries; each key must be followed by a value.");
            }

            reader.SkipValue();
        }
    }

    public static void ThrowUnexpectedEof(string containerName)
        => throw new InvalidOperationException($"Unexpected end of input while reading {containerName}.");
}

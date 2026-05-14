namespace Pakt;

/// <summary>
/// Base class for source-generated serialization contexts.
/// Subclass, mark as <c>partial</c>, and apply <see cref="PaktSerializableAttribute"/>.
/// </summary>
public abstract class PaktSerializerContext
{
    public PaktSerializationOptions Options { get; }

    protected PaktSerializerContext(PaktSerializationOptions? options = null)
        => Options = options ?? PaktSerializationOptions.Default;

    /// <summary>Get generated type info for <typeparamref name="T"/>, or null if not registered.</summary>
    public abstract PaktTypeInfo<T>? GetTypeInfo<T>();

    /// <summary>Get generated type info by runtime type, or null if not registered.</summary>
    public abstract PaktTypeInfo? GetTypeInfo(Type type);
}
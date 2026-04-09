namespace Pakt.Serialization;

/// <summary>
/// Base class for source-generated serializer contexts.
/// Derive from this class and apply <see cref="PaktSerializableAttribute"/> to register types.
/// The source generator will produce a partial class with a <c>Default</c> singleton
/// and typed <see cref="PaktTypeInfo{T}"/> properties.
/// </summary>
/// <example>
/// <code>
/// [PaktSerializable(typeof(Server))]
/// [PaktSerializable(typeof(LogEntry))]
/// public partial class AppPaktContext : PaktSerializerContext { }
///
/// // Usage:
/// var server = reader.Deserialize&lt;Server&gt;(AppPaktContext.Default.Server);
/// </code>
/// </example>
public abstract class PaktSerializerContext
{
    /// <summary>
    /// Gets the serializer options associated with this context.
    /// </summary>
    public PaktSerializerOptions Options { get; }

    /// <summary>
    /// Initializes a new <see cref="PaktSerializerContext"/> with the specified options.
    /// </summary>
    protected PaktSerializerContext(PaktSerializerOptions? options = null)
    {
        Options = options ?? PaktSerializerOptions.Default;
    }
}

namespace Pakt;

/// <summary>
/// Represents a single entry in a map pack.
/// </summary>
public readonly record struct PaktMapEntry<TKey, TValue>(TKey Key, TValue Value);

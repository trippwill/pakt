namespace Pakt;

/// <summary>
/// Internal parse phase for <see cref="PaktReader"/>.
/// </summary>
internal enum PaktReaderPhase : byte
{
    /// <summary>Before any tokens have been read.</summary>
    Start,

    /// <summary>Between statements — expect ident (next statement) or end-of-unit.</summary>
    ExpectStatementOrEnd,

    /// <summary>Inside a type annotation — scanning for annotation boundary.</summary>
    InAnnotation,

    /// <summary>Expect '=' or '&lt;&lt;' operator after annotation.</summary>
    ExpectOperator,

    /// <summary>Reading the value of an assign statement.</summary>
    InAssignValue,

    /// <summary>Reading values in a pack body.</summary>
    InPackValue,

    /// <summary>Parsing complete.</summary>
    Done,
}

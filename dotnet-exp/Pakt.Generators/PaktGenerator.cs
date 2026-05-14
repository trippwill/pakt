using Microsoft.CodeAnalysis;

namespace Pakt.Generators;

[Generator]
public sealed class PaktGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes with [PaktSerializable(...)]
        var typeDeclarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Pakt.PaktSerializableAttribute",
            predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
            transform: static (ctx, ct) => ctx)
            .Collect();

        context.RegisterSourceOutput(typeDeclarations, static (spc, contexts) =>
        {
            // TODO: Phase 3+4 — build type models and emit deserializers
        });
    }
}

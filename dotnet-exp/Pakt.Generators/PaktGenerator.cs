using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Pakt.Generators.Emitters;
using Pakt.Generators.Models;
using Pakt.Generators.Parser;

namespace Pakt.Generators;

[Generator]
public sealed class PaktGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contextDeclarations = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Pakt.PaktSerializableAttribute",
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) =>
            {
                var contextSymbol = ctx.TargetSymbol as INamedTypeSymbol;
                if (contextSymbol is null) return default;

                var types = new List<(INamedTypeSymbol ContextSymbol, INamedTypeSymbol TypeSymbol)>();
                foreach (var attr in ctx.Attributes)
                {
                    if (attr.ConstructorArguments.Length > 0
                        && attr.ConstructorArguments[0].Value is INamedTypeSymbol typeSymbol)
                    {
                        types.Add((contextSymbol, typeSymbol));
                    }
                }

                return (ContextSymbol: contextSymbol, Types: types);
            })
            .Where(x => x.ContextSymbol is not null)
            .Collect();

        var compilationAndContexts = contextDeclarations.Combine(context.CompilationProvider);

        context.RegisterSourceOutput(compilationAndContexts, static (spc, source) =>
        {
            var (contexts, compilation) = source;

            // Group by context class
            var grouped = new Dictionary<string, (INamedTypeSymbol Context, List<SerializableTypeModel> Types)>(System.StringComparer.Ordinal);
            foreach (var entry in contexts)
            {
                foreach (var (ctxSym, typeSym) in entry.Types)
                {
                    string key = ctxSym.ToDisplayString();
                    if (!grouped.TryGetValue(key, out var group))
                    {
                        group = (ctxSym, new List<SerializableTypeModel>());
                        grouped[key] = group;
                    }

                    var model = TypeModelBuilder.Build(typeSym, compilation);
                    if (model is not null)
                    {
                        group.Types.Add(model);
                    }
                }
            }

            // Emit one source file per context class
            foreach (var kvp in grouped)
            {
                var ctxSym = kvp.Value.Context;
                var types = kvp.Value.Types;
                if (types.Count == 0) continue;

                string ctxName = ctxSym.Name;
                string? ctxNs = ctxSym.ContainingNamespace?.IsGlobalNamespace == true
                    ? null
                    : ctxSym.ContainingNamespace?.ToDisplayString();

                string source2 = ContextEmitter.EmitContext(ctxName, ctxNs, types);
                spc.AddSource($"{ctxName}.g.cs", source2);
            }
        });
    }
}
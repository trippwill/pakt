using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Pakt.Generators.Emitters;
using Pakt.Generators.Model;
using Pakt.Generators.Parser;

namespace Pakt.Generators
{
    /// <summary>
    /// Incremental source generator that produces serialization/deserialization code
    /// for types registered via [PaktSerializable] on PaktSerializerContext subclasses.
    /// </summary>
    [Generator]
    public sealed class PaktGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // ForAttributeWithMetadataName calls transform once per target node,
            // with ALL matching attributes in ctx.Attributes.
            var pipeline = context.SyntaxProvider.ForAttributeWithMetadataName(
                "Pakt.Serialization.PaktSerializableAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => Transform(ctx, ct))
                .Where(static x => x.Types.Length > 0);

            context.RegisterSourceOutput(pipeline, static (spc, registration) =>
            {
                Execute(spc, registration);
            });
        }

        private static ContextRegistration Transform(
            GeneratorAttributeSyntaxContext ctx,
            CancellationToken ct)
        {
            var classSymbol = ctx.TargetSymbol as INamedTypeSymbol;
            if (classSymbol is null) return default;

            if (!DerivesFrom(classSymbol, "Pakt.Serialization.PaktSerializerContext"))
                return default;

            var diagnostics = new List<Diagnostic>();
            var types = new List<SerializableTypeModel>();

            // Process ALL [PaktSerializable] attributes on this class
            foreach (var attr in ctx.Attributes)
            {
                ct.ThrowIfCancellationRequested();

                if (attr.ConstructorArguments.Length == 0) continue;
                var typeArg = attr.ConstructorArguments[0];
                if (typeArg.Kind != TypedConstantKind.Type) continue;

                var targetType = typeArg.Value as INamedTypeSymbol;
                if (targetType is null) continue;

                var typeModel = TypeModelBuilder.Build(targetType, diagnostics, ct);
                if (typeModel is not null)
                    types.Add(typeModel);
            }

            var contextName = classSymbol.Name;
            var contextNs = classSymbol.ContainingNamespace?.IsGlobalNamespace == true
                ? ""
                : classSymbol.ContainingNamespace?.ToDisplayString() ?? "";

            return new ContextRegistration(
                contextName,
                contextNs,
                types.ToArray(),
                diagnostics.ToArray());
        }

        private static void Execute(SourceProductionContext spc, ContextRegistration reg)
        {
            foreach (var diag in reg.Diagnostics)
            {
                spc.ReportDiagnostic(diag);
            }

            if (reg.Types.Length == 0) return;

            var types = new List<SerializableTypeModel>();
            for (int i = 0; i < reg.Types.Length; i++)
                types.Add(reg.Types[i]);

            var source = PaktSourceEmitter.Emit(reg.ContextName, reg.ContextNamespace, types);
            spc.AddSource($"{reg.ContextName}.g.cs", source);
        }

        private static bool DerivesFrom(INamedTypeSymbol symbol, string baseTypeFqn)
        {
            var current = symbol.BaseType;
            while (current is not null)
            {
                if (current.ToDisplayString() == baseTypeFqn)
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        /// <summary>Pipeline data for one context class with all its registered types.</summary>
        private readonly struct ContextRegistration : IEquatable<ContextRegistration>
        {
            public readonly string ContextName;
            public readonly string ContextNamespace;
            public readonly EquatableArray<SerializableTypeModel> Types;
            public readonly Diagnostic[] Diagnostics;

            public ContextRegistration(
                string contextName,
                string contextNamespace,
                SerializableTypeModel[]? types,
                Diagnostic[]? diagnostics)
            {
                ContextName = contextName;
                ContextNamespace = contextNamespace;
                Types = new EquatableArray<SerializableTypeModel>(types ?? Array.Empty<SerializableTypeModel>());
                Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
            }

            public bool Equals(ContextRegistration other)
            {
                return ContextName == other.ContextName
                    && ContextNamespace == other.ContextNamespace
                    && Types.Equals(other.Types);
            }

            public override bool Equals(object? obj) =>
                obj is ContextRegistration other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + (ContextName?.GetHashCode() ?? 0);
                    hash = hash * 31 + (ContextNamespace?.GetHashCode() ?? 0);
                    hash = hash * 31 + Types.GetHashCode();
                    return hash;
                }
            }
        }
    }
}

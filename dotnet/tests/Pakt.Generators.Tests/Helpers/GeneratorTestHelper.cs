using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Pakt.Generators.Tests.Helpers
{
    internal static class GeneratorTestHelper
    {
        public static (GeneratorDriverRunResult Result, Compilation OutputCompilation)
            RunGenerator(string source)
        {
            var compilation = CreateCompilation(source);
            var generator = new PaktGenerator();
            var driver = CSharpGeneratorDriver.Create(generator);
            driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
                compilation, out var outputCompilation, out var diagnostics);

            var result = driver.GetRunResult();
            return (result, outputCompilation);
        }

        public static Compilation CreateCompilation(string source)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source);
            var references = GetMetadataReferences();

            return CSharpCompilation.Create(
                "TestCompilation",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithNullableContextOptions(NullableContextOptions.Enable));
        }

        private static List<MetadataReference> GetMetadataReferences()
        {
            var refs = new List<MetadataReference>();

            // Core runtime assemblies
            var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string);
            if (trustedAssemblies != null)
            {
                foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            refs.Add(MetadataReference.CreateFromFile(path));
                        }
                        catch
                        {
                            // Skip problematic assemblies
                        }
                    }
                }
            }

            // Pakt assembly
            var paktAssembly = typeof(Pakt.PaktType).Assembly;
            if (!refs.Any(r => r.Display?.Contains("Pakt.dll") == true))
            {
                refs.Add(MetadataReference.CreateFromFile(paktAssembly.Location));
            }

            return refs;
        }
    }
}

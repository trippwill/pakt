using System.Linq;
using Microsoft.CodeAnalysis;
using Pakt.Generators.Tests.Helpers;

namespace Pakt.Generators.Tests;

public class GeneratorSnapshotTests
{
    [Fact]
    public void SimpleType_GeneratesValidCode()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class Server
    {
        public string Host { get; set; } = """";
        public int Port { get; set; }
    }

    [PaktSerializable(typeof(Server))]
    public partial class TestContext : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.NotEmpty(result.GeneratedTrees);

        var generatedSource = result.GeneratedTrees.First().GetText().ToString();
        Assert.Contains("DeserializeServer", generatedSource);
        Assert.Contains("SerializeServer", generatedSource);
        Assert.Contains("PaktTypeInfo<global::TestApp.Server>", generatedSource);
    }

    [Fact]
    public void GeneratedCode_HasDefaultSingleton()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class Item { public string Name { get; set; } = """"; }

    [PaktSerializable(typeof(Item))]
    public partial class MyCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("public static MyCtx Default", generatedSource);
        Assert.Contains("PaktTypeInfo<global::TestApp.Item>", generatedSource);
    }

    [Fact]
    public void PaktPropertyAttribute_OverridesFieldName()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class Config
    {
        [PaktProperty(""host_name"")]
        public string HostName { get; set; } = """";
    }

    [PaktSerializable(typeof(Config))]
    public partial class CfgCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("\"host_name\"", generatedSource);
        Assert.DoesNotContain("\"hostName\"", generatedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void PaktIgnore_ExcludesProperty()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class WithIgnored
    {
        public string Name { get; set; } = """";
        [PaktIgnore]
        public string Internal { get; set; } = """";
    }

    [PaktSerializable(typeof(WithIgnored))]
    public partial class IgnCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("\"name\"", generatedSource);
        // The ignored property should not appear in the switch cases
        Assert.DoesNotContain("case \"internal\"", generatedSource);
    }

    [Fact]
    public void NullableProperty_GeneratesNilCheck()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class WithNullable
    {
        public string? OptionalName { get; set; }
        public int? OptionalAge { get; set; }
    }

    [PaktSerializable(typeof(WithNullable))]
    public partial class NullCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("PaktTokenType.Nil", generatedSource);
        Assert.Contains("WriteNilValue", generatedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void ListProperty_GeneratesListSerialization()
    {
        var source = @"
using System.Collections.Generic;
using Pakt.Serialization;

namespace TestApp
{
    public class WithList
    {
        public List<string> Tags { get; set; } = new();
    }

    [PaktSerializable(typeof(WithList))]
    public partial class ListCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("WriteListStart", generatedSource);
        Assert.Contains("WriteListEnd", generatedSource);
        Assert.Contains("PaktTokenType.ListEnd", generatedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void DictionaryProperty_GeneratesMapSerialization()
    {
        var source = @"
using System.Collections.Generic;
using Pakt.Serialization;

namespace TestApp
{
    public class WithMap
    {
        public Dictionary<string, int> Scores { get; set; } = new();
    }

    [PaktSerializable(typeof(WithMap))]
    public partial class MapCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("WriteMapStart", generatedSource);
        Assert.Contains("WriteMapEnd", generatedSource);
        Assert.Contains("WriteMapKeySeparator", generatedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void NestedType_GeneratesNestedSerialization()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class Address
    {
        public string City { get; set; } = """";
        public int Zip { get; set; }
    }

    public class Person
    {
        public string Name { get; set; } = """";
        public Address Home { get; set; } = new();
    }

    [PaktSerializable(typeof(Address))]
    [PaktSerializable(typeof(Person))]
    public partial class NestedCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("DeserializeAddress", generatedSource);
        Assert.Contains("DeserializePerson", generatedSource);
        Assert.Contains("SerializeAddress", generatedSource);
        Assert.Contains("SerializePerson", generatedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void MultipleTypes_AllRegistered()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class TypeA { public string A { get; set; } = """"; }
    public class TypeB { public int B { get; set; } }

    [PaktSerializable(typeof(TypeA))]
    [PaktSerializable(typeof(TypeB))]
    public partial class MultiCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("PaktTypeInfo<global::TestApp.TypeA>", generatedSource);
        Assert.Contains("PaktTypeInfo<global::TestApp.TypeB>", generatedSource);
        Assert.Contains("DeserializeTypeA", generatedSource);
        Assert.Contains("DeserializeTypeB", generatedSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void SkipHelper_IsGenerated()
    {
        var source = @"
using Pakt.Serialization;

namespace TestApp
{
    public class Simple { public string X { get; set; } = """"; }

    [PaktSerializable(typeof(Simple))]
    public partial class SkipCtx : PaktSerializerContext { }
}";

        var (result, _) = GeneratorTestHelper.RunGenerator(source);
        var generatedSource = result.GeneratedTrees.First().GetText().ToString();

        Assert.Contains("SkipCurrentValue", generatedSource);
    }
}

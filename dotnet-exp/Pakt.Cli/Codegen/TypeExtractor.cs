using System.Buffers;
using System.Text;

using Pakt;

namespace Pakt.Cli.Codegen;

/// <summary>
/// Extracted type information from a .pakt file's annotations.
/// </summary>
internal sealed class PaktTypeModel
{
    public string FileName { get; init; } = "";
    public List<StatementInfo> Statements { get; } = [];
    public Dictionary<string, EnumInfo> Enums { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, StructInfo> Structs { get; } = new(StringComparer.Ordinal);
}

internal sealed class StatementInfo
{
    public required string PaktName { get; init; }
    public required string CSharpName { get; init; }
    public required CSharpType Type { get; init; }
}

internal sealed class StructInfo
{
    public required string CSharpName { get; init; }
    public List<FieldInfo> Fields { get; } = [];
}

internal sealed class FieldInfo
{
    public required string PaktName { get; init; }
    public required string CSharpName { get; init; }
    public required CSharpType Type { get; init; }
}

internal sealed class EnumInfo
{
    public required string CSharpName { get; init; }
    public List<string> Members { get; } = [];
}

internal sealed class CSharpType
{
    public required string TypeName { get; init; }
    public bool IsNullable { get; init; }
    public bool IsEnum { get; init; }
    public bool IsStruct { get; init; }
    public bool IsList { get; init; }
    public bool IsMap { get; init; }
    public bool IsTuple { get; init; }
    public CSharpType? ElementType { get; init; }
    public CSharpType? KeyType { get; init; }
    public CSharpType? ValueType { get; init; }
    public List<CSharpType>? TupleElements { get; init; }
}

/// <summary>
/// Extracts type information from a .pakt file by reading annotations.
/// </summary>
internal static class TypeExtractor
{
    public static PaktTypeModel Extract(byte[] data, string fileName)
    {
        var model = new PaktTypeModel { FileName = fileName };
        var seq = new ReadOnlySequence<byte>(data);
        var reader = new PaktReader(seq, isFinalBlock: true);

        string? currentName = null;
        ReadOnlySequence<byte> currentAnnotation = default;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case PaktTokenType.StatementName:
                    currentName = Encoding.UTF8.GetString(reader.ValueSequence);
                    break;

                case PaktTokenType.TypeAnnotation:
                    currentAnnotation = reader.ValueSequence;
                    break;

                case PaktTokenType.AssignOperator:
                    if (currentName != null && currentAnnotation.Length > 0)
                    {
                        ReadOnlySpan<byte> annoBytes = currentAnnotation.IsSingleSegment
                            ? currentAnnotation.FirstSpan
                            : CopyToArray(currentAnnotation);

                        var csType = ParseAnnotationType(annoBytes, model);
                        model.Statements.Add(new StatementInfo
                        {
                            PaktName = currentName,
                            CSharpName = ToPascalCase(currentName),
                            Type = csType,
                        });
                    }
                    // Skip the value — we only care about annotations
                    SkipValue(ref reader);
                    currentName = null;
                    currentAnnotation = default;
                    break;
            }
        }

        return model;
    }

    private static CSharpType ParseAnnotationType(ReadOnlySpan<byte> anno, PaktTypeModel model)
    {
        int pos = 0;
        return ParseTypeAt(anno, ref pos, model);
    }

    private static CSharpType ParseTypeAt(ReadOnlySpan<byte> b, ref int pos, PaktTypeModel model)
    {
        SkipLayout(b, ref pos);
        if (pos >= b.Length) return new CSharpType { TypeName = "object" };

        byte first = b[pos];
        CSharpType result;

        if (first == (byte)'{')
            result = ParseStructType(b, ref pos, model);
        else if (first == (byte)'(')
            result = ParseTupleType(b, ref pos, model);
        else if (first == (byte)'[')
            result = ParseListType(b, ref pos, model);
        else if (first == (byte)'<')
            result = ParseMapType(b, ref pos, model);
        else if (first == (byte)'|')
            result = ParseAtomSetType(b, ref pos, model);
        else
            result = ParseScalarType(b, ref pos);

        // Nullable?
        SkipLayout(b, ref pos);
        if (pos < b.Length && b[pos] == (byte)'?')
        {
            pos++;
            return new CSharpType
            {
                TypeName = result.TypeName + "?",
                IsNullable = true,
                IsEnum = result.IsEnum,
                IsStruct = result.IsStruct,
                IsList = result.IsList,
                IsMap = result.IsMap,
                IsTuple = result.IsTuple,
                ElementType = result.ElementType,
                KeyType = result.KeyType,
                ValueType = result.ValueType,
                TupleElements = result.TupleElements,
            };
        }

        return result;
    }

    private static CSharpType ParseScalarType(ReadOnlySpan<byte> b, ref int pos)
    {
        int start = pos;
        while (pos < b.Length && (IsIdentPart(b[pos]) || IsDigit(b[pos])))
            pos++;

        ReadOnlySpan<byte> name = b[start..pos];
        string csType = MapScalarType(name);
        return new CSharpType { TypeName = csType };
    }

    private static CSharpType ParseAtomSetType(ReadOnlySpan<byte> b, ref int pos, PaktTypeModel model)
    {
        pos++; // skip |
        var members = new List<string>();
        while (pos < b.Length && b[pos] != (byte)'|')
        {
            SkipLayout(b, ref pos);
            if (pos >= b.Length || b[pos] == (byte)'|') break;

            int start = pos;
            while (pos < b.Length && IsIdentPart(b[pos])) pos++;
            string member = Encoding.UTF8.GetString(b[start..pos]);
            members.Add(member);
        }
        if (pos < b.Length) pos++; // skip closing |

        // Generate a unique enum name from the members
        string enumName = GenerateEnumName(members);
        if (!model.Enums.ContainsKey(enumName))
        {
            model.Enums[enumName] = new EnumInfo
            {
                CSharpName = enumName,
            };
            model.Enums[enumName].Members.AddRange(members);
        }

        return new CSharpType { TypeName = enumName, IsEnum = true };
    }

    private static CSharpType ParseStructType(ReadOnlySpan<byte> b, ref int pos, PaktTypeModel model)
    {
        pos++; // skip {
        var fields = new List<FieldInfo>();
        SkipLayout(b, ref pos);

        while (pos < b.Length && b[pos] != (byte)'}')
        {
            SkipLayout(b, ref pos);
            if (pos >= b.Length || b[pos] == (byte)'}') break;

            // Field name
            int nameStart = pos;
            while (pos < b.Length && IsIdentPart(b[pos])) pos++;
            string fieldName = Encoding.UTF8.GetString(b[nameStart..pos]);

            // Colon
            if (pos < b.Length && b[pos] == (byte)':') pos++;

            // Field type
            var fieldType = ParseTypeAt(b, ref pos, model);
            fields.Add(new FieldInfo
            {
                PaktName = fieldName,
                CSharpName = ToPascalCase(fieldName),
                Type = fieldType,
            });

            SkipLayout(b, ref pos);
        }
        if (pos < b.Length) pos++; // skip }

        // Generate struct name from field names
        string structName = "Struct_" + string.Join("_", fields.Select(f => f.CSharpName).Take(3));
        if (!model.Structs.ContainsKey(structName))
        {
            var info = new StructInfo { CSharpName = structName };
            info.Fields.AddRange(fields);
            model.Structs[structName] = info;
        }

        return new CSharpType { TypeName = structName, IsStruct = true };
    }

    private static CSharpType ParseTupleType(ReadOnlySpan<byte> b, ref int pos, PaktTypeModel model)
    {
        pos++; // skip (
        var elements = new List<CSharpType>();
        SkipLayout(b, ref pos);

        while (pos < b.Length && b[pos] != (byte)')')
        {
            SkipLayout(b, ref pos);
            if (pos >= b.Length || b[pos] == (byte)')') break;
            elements.Add(ParseTypeAt(b, ref pos, model));
            SkipLayout(b, ref pos);
        }
        if (pos < b.Length) pos++; // skip )

        string typeName = "(" + string.Join(", ", elements.Select(e => e.TypeName)) + ")";
        return new CSharpType { TypeName = typeName, IsTuple = true, TupleElements = elements };
    }

    private static CSharpType ParseListType(ReadOnlySpan<byte> b, ref int pos, PaktTypeModel model)
    {
        pos++; // skip [
        SkipLayout(b, ref pos);
        var elemType = ParseTypeAt(b, ref pos, model);
        SkipLayout(b, ref pos);
        if (pos < b.Length && b[pos] == (byte)']') pos++;

        return new CSharpType
        {
            TypeName = $"List<{elemType.TypeName}>",
            IsList = true,
            ElementType = elemType,
        };
    }

    private static CSharpType ParseMapType(ReadOnlySpan<byte> b, ref int pos, PaktTypeModel model)
    {
        pos++; // skip <
        SkipLayout(b, ref pos);
        var keyType = ParseTypeAt(b, ref pos, model);
        SkipLayout(b, ref pos);
        if (pos < b.Length && b[pos] == (byte)'=') pos++; // skip =
        SkipLayout(b, ref pos);
        var valType = ParseTypeAt(b, ref pos, model);
        SkipLayout(b, ref pos);
        if (pos < b.Length && b[pos] == (byte)'>') pos++;

        return new CSharpType
        {
            TypeName = $"Dictionary<{keyType.TypeName}, {valType.TypeName}>",
            IsMap = true,
            KeyType = keyType,
            ValueType = valType,
        };
    }

    // ── Helpers ──

    private static string MapScalarType(ReadOnlySpan<byte> name)
    {
        if (name.SequenceEqual("str"u8)) return "string";
        if (name.SequenceEqual("int"u8)) return "int";
        if (name.SequenceEqual("dec"u8)) return "decimal";
        if (name.SequenceEqual("float"u8)) return "double";
        if (name.SequenceEqual("bool"u8)) return "bool";
        if (name.SequenceEqual("date"u8)) return "DateOnly";
        if (name.SequenceEqual("ts"u8)) return "DateTimeOffset";
        if (name.SequenceEqual("uuid"u8)) return "Guid";
        if (name.SequenceEqual("bin"u8)) return "byte[]";
        return "object";
    }

    private static string GenerateEnumName(List<string> members)
    {
        // Use first member + "Kind" if short, otherwise hash
        if (members.Count > 0 && members.Count <= 5)
            return ToPascalCase(members[0]) + "Kind";
        return "Enum_" + Math.Abs(string.Join("_", members).GetHashCode(StringComparison.Ordinal)).ToString("X8");
    }

    internal static string ToPascalCase(string kebab)
    {
        if (string.IsNullOrEmpty(kebab)) return kebab;
        var sb = new StringBuilder(kebab.Length);
        bool capitalize = true;
        foreach (char c in kebab)
        {
            if (c is '-' or '_')
            {
                capitalize = true;
                continue;
            }
            sb.Append(capitalize ? char.ToUpperInvariant(c) : c);
            capitalize = false;
        }
        return sb.ToString();
    }

    private static void SkipLayout(ReadOnlySpan<byte> b, ref int pos)
    {
        while (pos < b.Length && IsLayout(b[pos])) pos++;
    }

    private static bool IsLayout(byte b) =>
        b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',';

    private static bool IsIdentPart(byte b) =>
        (uint)(b - (byte)'a') <= 25 || (uint)(b - (byte)'A') <= 25
        || (uint)(b - (byte)'0') <= 9 || b == (byte)'_' || b == (byte)'-';

    private static bool IsDigit(byte b) => (uint)(b - (byte)'0') <= 9;

    private static void SkipValue(ref PaktReader reader)
    {
        int depth = 0;
        while (reader.Read())
        {
            var t = reader.TokenType;
            if (t is PaktTokenType.StructStart or PaktTokenType.TupleStart
                or PaktTokenType.ListStart or PaktTokenType.MapStart)
                depth++;
            else if (t is PaktTokenType.StructEnd or PaktTokenType.TupleEnd
                or PaktTokenType.ListEnd or PaktTokenType.MapEnd)
            {
                depth--;
                if (depth <= 0) return;
            }
            else if (depth == 0) return;
            if (t is PaktTokenType.EndOfUnit or PaktTokenType.StatementName) return;
        }
    }

    private static byte[] CopyToArray(ReadOnlySequence<byte> seq)
    {
        var buf = new byte[(int)seq.Length];
        seq.CopyTo(buf);
        return buf;
    }
}
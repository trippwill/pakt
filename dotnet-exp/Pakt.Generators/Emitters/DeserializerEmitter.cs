using System.Text;
using Pakt.Generators.Models;

namespace Pakt.Generators.Emitters;

/// <summary>
/// Emits source code for positional deserialize methods targeting <c>IPaktReader</c>.
/// </summary>
internal static class DeserializerEmitter
{
    public static string EmitDeserializeMethod(SerializableTypeModel model)
    {
        var sb = new StringBuilder(1024);
        string typeFqn = model.FullyQualifiedName;

        sb.AppendLine($"    public static {typeFqn} Deserialize{model.Name}(global::Pakt.PaktMemoryReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine($"        reader.ExpectToken(global::Pakt.PaktTokenType.StructStart);");

        // Emit positional field reads
        foreach (var prop in model.Properties)
        {
            if (prop.IsIgnored) continue;
            EmitFieldRead(sb, prop, model);
        }

        sb.AppendLine($"        reader.ExpectToken(global::Pakt.PaktTokenType.StructEnd);");
        sb.AppendLine();

        // Construct the result
        sb.AppendLine($"        return new {typeFqn}");
        sb.AppendLine("        {");
        bool first = true;
        foreach (var prop in model.Properties)
        {
            if (prop.IsIgnored) continue;
            if (!first) sb.AppendLine(",");
            sb.Append($"            {prop.ClrName} = __{prop.ClrName}");
            first = false;
        }
        sb.AppendLine();
        sb.AppendLine("        };");
        sb.AppendLine("    }");

        return sb.ToString();
    }

    private static void EmitFieldRead(StringBuilder sb, PropertyModel prop, SerializableTypeModel model)
    {
        string varName = $"__{prop.ClrName}";

        if (prop.IsNullable)
        {
            sb.AppendLine($"        var {varName} = reader.TryReadNil() ? default({prop.ClrTypeFqn}) : {EmitScalarRead(prop)};");
            return;
        }

        switch (prop.Kind)
        {
            case PaktTypeKind.String:
            case PaktTypeKind.Int:
            case PaktTypeKind.Long:
            case PaktTypeKind.Decimal:
            case PaktTypeKind.Double:
            case PaktTypeKind.Float:
            case PaktTypeKind.Bool:
            case PaktTypeKind.Guid:
            case PaktTypeKind.DateOnly:
            case PaktTypeKind.DateTimeOffset:
            case PaktTypeKind.ByteArray:
                sb.AppendLine($"        var {varName} = {EmitScalarRead(prop)};");
                break;

            case PaktTypeKind.Struct:
                sb.AppendLine($"        var {varName} = Deserialize{GetTypeName(prop.NestedTypeFqn!)}(reader);");
                break;

            case PaktTypeKind.List:
                EmitListRead(sb, prop, varName);
                break;

            case PaktTypeKind.Map:
                EmitMapRead(sb, prop, varName);
                break;

            default:
                sb.AppendLine($"        var {varName} = default({prop.ClrTypeFqn}); // unsupported kind: {prop.Kind}");
                break;
        }
    }

    private static string EmitScalarRead(PropertyModel prop)
    {
        return prop.Kind switch
        {
            PaktTypeKind.String => "reader.ReadString()",
            PaktTypeKind.Int => "reader.ReadInt32()",
            PaktTypeKind.Long => "reader.ReadInt64()",
            PaktTypeKind.Decimal => "reader.ReadDecimal()",
            PaktTypeKind.Double => "reader.ReadDouble()",
            PaktTypeKind.Float => "(float)reader.ReadDouble()",
            PaktTypeKind.Bool => "reader.ReadBool()",
            _ => $"default({prop.ClrTypeFqn})", // TODO: Guid, DateOnly, DateTimeOffset, byte[]
        };
    }

    private static void EmitListRead(StringBuilder sb, PropertyModel prop, string varName)
    {
        string elemFqn = prop.ElementTypeFqn ?? "object";
        var elemKind = ClassifyElementKind(elemFqn);
        sb.AppendLine($"        reader.ExpectToken(global::Pakt.PaktTokenType.ListStart);");
        sb.AppendLine($"        var {varName} = new global::System.Collections.Generic.List<{elemFqn}>();");
        sb.AppendLine($"        while (reader.Read())");
        sb.AppendLine($"        {{");
        sb.AppendLine($"            if (reader.TokenType == global::Pakt.PaktTokenType.ListEnd) break;");
        sb.AppendLine($"            {varName}.Add({EmitScalarReadForKind(elemKind, elemFqn)});");
        sb.AppendLine($"        }}");
    }

    private static void EmitMapRead(StringBuilder sb, PropertyModel prop, string varName)
    {
        string keyFqn = prop.KeyTypeFqn ?? "string";
        string valFqn = prop.ValueTypeFqn ?? "object";
        var valKind = ClassifyElementKind(valFqn);
        sb.AppendLine($"        reader.ExpectToken(global::Pakt.PaktTokenType.MapStart);");
        sb.AppendLine($"        var {varName} = new global::System.Collections.Generic.Dictionary<{keyFqn}, {valFqn}>();");
        sb.AppendLine($"        while (reader.Read())");
        sb.AppendLine($"        {{");
        sb.AppendLine($"            if (reader.TokenType == global::Pakt.PaktTokenType.MapEnd) break;");
        sb.AppendLine($"            var __key = reader.ReadString();");
        sb.AppendLine($"            reader.ExpectToken(global::Pakt.PaktTokenType.MapEntryBind);");
        sb.AppendLine($"            var __val = {EmitScalarReadForKind(valKind, valFqn)};");
        sb.AppendLine($"            {varName}[__key] = __val;");
        sb.AppendLine($"        }}");
    }

    private static string EmitScalarReadForKind(PaktTypeKind kind, string fqn)
    {
        return kind switch
        {
            PaktTypeKind.String => "reader.ReadString()",
            PaktTypeKind.Int => "reader.ReadInt32()",
            PaktTypeKind.Long => "reader.ReadInt64()",
            PaktTypeKind.Decimal => "reader.ReadDecimal()",
            PaktTypeKind.Double => "reader.ReadDouble()",
            PaktTypeKind.Float => "(float)reader.ReadDouble()",
            PaktTypeKind.Bool => "reader.ReadBool()",
            _ => $"default({fqn})",
        };
    }

    private static PaktTypeKind ClassifyElementKind(string fqn)
    {
        if (fqn.Contains("System.String", System.StringComparison.Ordinal)) return PaktTypeKind.String;
        if (fqn.Contains("System.Int32", System.StringComparison.Ordinal) || string.Equals(fqn, "int", System.StringComparison.Ordinal)) return PaktTypeKind.Int;
        if (fqn.Contains("System.Int64", System.StringComparison.Ordinal) || string.Equals(fqn, "long", System.StringComparison.Ordinal)) return PaktTypeKind.Long;
        if (fqn.Contains("System.Boolean", System.StringComparison.Ordinal) || string.Equals(fqn, "bool", System.StringComparison.Ordinal)) return PaktTypeKind.Bool;
        if (fqn.Contains("System.Double", System.StringComparison.Ordinal) || string.Equals(fqn, "double", System.StringComparison.Ordinal)) return PaktTypeKind.Double;
        if (fqn.Contains("System.Decimal", System.StringComparison.Ordinal) || string.Equals(fqn, "decimal", System.StringComparison.Ordinal)) return PaktTypeKind.Decimal;
        return PaktTypeKind.String;
    }

    private static string GetTypeName(string fqn)
    {
        int lastDot = fqn.LastIndexOf('.');
        return lastDot >= 0 ? fqn.Substring(lastDot + 1) : fqn;
    }
}

using System.Text;

using Pakt.Generators.Models;

namespace Pakt.Generators.Emitters;

/// <summary>
/// Emits source code for deserialize methods targeting <c>PaktValidatingReader</c>.
/// Two methods per type:
/// - <c>DeserializeValue{Name}</c> — positional struct value read (inside composites)
/// - <c>DeserializeUnit{Name}</c> — statement-name-matching unit read (top level)
/// </summary>
internal static class DeserializerEmitter
{
    /// <summary>
    /// Emit a positional value deserializer: reads a struct value ({...}) where
    /// fields are positional and matched by the type annotation order.
    /// </summary>
    public static string EmitValueDeserializeMethod(SerializableTypeModel model)
    {
        var sb = new StringBuilder(1024);
        string typeFqn = model.FullyQualifiedName;

        sb.AppendLine($"    private static {typeFqn} DeserializeValue{model.Name}(ref global::Pakt.PaktValidatingReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine($"        reader.Read(); // StructStart");

        foreach (var prop in model.Properties)
        {
            if (prop.IsIgnored) continue;
            EmitFieldRead(sb, prop);
        }

        sb.AppendLine($"        reader.Read(); // StructEnd");
        sb.AppendLine();
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

    /// <summary>
    /// Emit a unit-level deserializer: reads statement-by-statement, matches names,
    /// and applies serialization policies.
    /// </summary>
    public static string EmitUnitDeserializeMethod(SerializableTypeModel model) =>
        EmitUnitDeserializeMethodCore(model, "DeserializeUnit", "global::Pakt.PaktValidatingReader");

    public static string EmitRawUnitDeserializeMethod(SerializableTypeModel model) =>
        EmitUnitDeserializeMethodCore(model, "RawDeserializeUnit", "global::Pakt.PaktSequenceReader");

    private static string EmitUnitDeserializeMethodCore(SerializableTypeModel model, string methodPrefix, string readerType)
    {
        var sb = new StringBuilder(2048);
        string typeFqn = model.FullyQualifiedName;
        var activeProps = model.Properties.Where(p => !p.IsIgnored).ToList();

        // Exp 3: Use PaktSequenceReader directly — generated code IS the validation
        sb.AppendLine($"    private static {typeFqn} {methodPrefix}{model.Name}(ref {readerType} reader, global::Pakt.PaktSerializationOptions options)");
        sb.AppendLine("    {");

        // Declare locals for each property
        foreach (var prop in activeProps)
        {
            string defaultVal = GetDefaultValue(prop);
            sb.AppendLine($"        {prop.ClrTypeFqn} __{prop.ClrName} = {defaultVal};");
        }

        // Exp 2: Bitmask duplicate tracking instead of HashSet<string>
        sb.AppendLine($"        int __seen = 0;");
        sb.AppendLine();

        // Statement reading loop
        sb.AppendLine("        while (reader.Read())");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.TokenType == global::Pakt.PaktTokenType.EndOfUnit) break;");
        sb.AppendLine("            if (reader.TokenType != global::Pakt.PaktTokenType.StatementName) continue;");
        sb.AppendLine();

        // Exp 1: Get raw bytes instead of allocating string
        sb.AppendLine("            // Zero-alloc statement name matching");
        sb.AppendLine("            var __nameSeq = reader.ValueSequence;");
        sb.AppendLine("            ReadOnlySpan<byte> __nameSpan = __nameSeq.IsSingleSegment");
        sb.AppendLine("                ? __nameSeq.FirstSpan");
        sb.AppendLine("                : stackalloc byte[0];");
        sb.AppendLine("            if (!__nameSeq.IsSingleSegment)");
        sb.AppendLine("            {");
        sb.AppendLine("                Span<byte> __nameBuf = stackalloc byte[(int)__nameSeq.Length];");
        sb.AppendLine("                global::System.Buffers.BuffersExtensions.CopyTo(__nameSeq, __nameBuf);");
        sb.AppendLine("                __nameSpan = __nameBuf;");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            // Skip annotation and operator");
        sb.AppendLine("            reader.Read(); // TypeAnnotationStart");
        sb.AppendLine("            reader.Read(); // AssignOperator");
        sb.AppendLine();

        // Exp 1 + 4: Byte-span matching with first-byte dispatch
        sb.AppendLine("            // First-byte dispatch + byte-span matching");

        // Group properties by first byte for efficient dispatch
        var byFirstByte = activeProps
            .GroupBy(p => (byte)p.PaktName[0])
            .OrderBy(g => g.Key)
            .ToList();

        sb.AppendLine("            switch (__nameSpan.Length > 0 ? __nameSpan[0] : (byte)0)");
        sb.AppendLine("            {");

        foreach (var group in byFirstByte)
        {
            sb.AppendLine($"                case (byte)'{(char)group.Key}':");
            foreach (var prop in group)
            {
                int propIndex = activeProps.IndexOf(prop);
                int bit = 1 << propIndex;
                sb.AppendLine($"                    if (__nameSpan.SequenceEqual(\"{prop.PaktName}\"u8))");
                sb.AppendLine($"                    {{");

                // Exp 2: Bitmask duplicate check
                sb.AppendLine($"                        if ((__seen & {bit}) != 0)");
                sb.AppendLine($"                        {{");
                sb.AppendLine($"                            if (options.DuplicateStatements == global::Pakt.DuplicatePolicy.Error)");
                sb.AppendLine($"                                throw new global::Pakt.PaktParseException(\"Duplicate statement: {prop.PaktName}\", (int)global::Pakt.PaktErrorCode.Syntax, default);");
                sb.AppendLine($"                            if (options.DuplicateStatements == global::Pakt.DuplicatePolicy.FirstWins)");
                sb.AppendLine($"                            {{");
                sb.AppendLine($"                                global::Pakt.PaktUnitDeserializer.SkipStatementValue(ref reader);");
                sb.AppendLine($"                                break;");
                sb.AppendLine($"                            }}");
                sb.AppendLine($"                        }}");
                sb.AppendLine($"                        __seen |= {bit};");

                EmitStatementValueRead(sb, prop);
                sb.AppendLine($"                        break;");
                sb.AppendLine($"                    }}");
            }
            // If none matched in this first-byte group, fall through to unknown
            sb.AppendLine($"                    goto default;");
        }

        // Unknown statement handling
        sb.AppendLine("                default:");
        sb.AppendLine("                    if (options.UnknownStatements == global::Pakt.UnknownMemberPolicy.Error)");
        sb.AppendLine("                    {");
        sb.AppendLine("                        string __unknownName = global::System.Text.Encoding.UTF8.GetString(__nameSpan);");
        sb.AppendLine("                        throw new global::Pakt.PaktParseException($\"Unknown statement: {__unknownName}\", (int)global::Pakt.PaktErrorCode.Syntax, default);");
        sb.AppendLine("                    }");
        sb.AppendLine("                    global::Pakt.PaktUnitDeserializer.SkipStatementValue(ref reader);");
        sb.AppendLine("                    break;");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        // Missing statement check — bitmask
        sb.AppendLine("        if (options.MissingStatements == global::Pakt.MissingMemberPolicy.Error)");
        sb.AppendLine("        {");
        int allBits = (1 << activeProps.Count) - 1;
        sb.AppendLine($"            if (__seen != {allBits})");
        sb.AppendLine("            {");
        for (int i = 0; i < activeProps.Count; i++)
        {
            int bit = 1 << i;
            sb.AppendLine($"                if ((__seen & {bit}) == 0)");
            sb.AppendLine($"                    throw new global::Pakt.PaktParseException(\"Missing statement: {activeProps[i].PaktName}\", (int)global::Pakt.PaktErrorCode.Syntax, default);");
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine();

        // Construct result
        sb.AppendLine($"        return new {typeFqn}");
        sb.AppendLine("        {");
        bool first2 = true;
        foreach (var prop in activeProps)
        {
            if (!first2) sb.AppendLine(",");
            sb.Append($"            {prop.ClrName} = __{prop.ClrName}");
            first2 = false;
        }
        sb.AppendLine();
        sb.AppendLine("        };");
        sb.AppendLine("    }");

        return sb.ToString();
    }

    // ── Field-level reading (positional) ──

    private static void EmitFieldRead(StringBuilder sb, PropertyModel prop)
    {
        string varName = $"__{prop.ClrName}";

        if (prop.IsNullable)
        {
            sb.AppendLine($"        reader.Read();");
            sb.AppendLine($"        var {varName} = reader.TokenType == global::Pakt.PaktTokenType.Nil ? default({prop.ClrTypeFqn}) : {EmitGetForCurrentToken(prop)};");
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
                sb.AppendLine($"        reader.Read();");
                sb.AppendLine($"        var {varName} = {EmitGetForCurrentToken(prop)};");
                break;

            case PaktTypeKind.Struct:
                sb.AppendLine($"        var {varName} = DeserializeValue{GetTypeName(prop.NestedTypeFqn!)}(ref reader);");
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

    // ── Statement-level value reading (unit deserializer) ──

    private static void EmitStatementValueRead(StringBuilder sb, PropertyModel prop)
    {
        string varName = $"__{prop.ClrName}";

        // All values (including streaming collections) start with a Read() to get the first token
        sb.AppendLine($"                    reader.Read(); // advance to value");
        EmitAssignValueRead(sb, prop, varName, "                    ");
    }

    private static void EmitAssignValueRead(StringBuilder sb, PropertyModel prop, string varName, string indent)
    {
        if (prop.IsNullable)
        {
            sb.AppendLine($"{indent}if (reader.TokenType == global::Pakt.PaktTokenType.Nil)");
            sb.AppendLine($"{indent}    {varName} = default;");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}    {varName} = {EmitGetForCurrentToken(prop)};");
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
                sb.AppendLine($"{indent}{varName} = {EmitGetForCurrentToken(prop)};");
                break;

            case PaktTypeKind.Struct:
                sb.AppendLine($"{indent}{varName} = DeserializeValue{GetTypeName(prop.NestedTypeFqn!)}(ref reader);");
                break;

            case PaktTypeKind.List:
                EmitListReadInline(sb, prop, varName, indent, declare: false);
                break;

            case PaktTypeKind.Map:
                EmitMapReadInline(sb, prop, varName, indent, declare: false);
                break;

            default:
                sb.AppendLine($"{indent}{varName} = default; // unsupported");
                break;
        }
    }

    // ── List reading ──

    private static void EmitListRead(StringBuilder sb, PropertyModel prop, string varName)
    {
        EmitListReadInline(sb, prop, varName, "        ");
    }

    private static void EmitListReadInline(StringBuilder sb, PropertyModel prop, string varName, string indent, bool declare = true)
    {
        string elemFqn = prop.ElementTypeFqn ?? "object";
        var elemKind = ClassifyElementKind(elemFqn);
        sb.AppendLine($"{indent}// ListStart already current or next Read()");
        sb.AppendLine($"{indent}if (reader.TokenType != global::Pakt.PaktTokenType.ListStart) reader.Read();");
        string decl = declare ? "var " : "";
        sb.AppendLine($"{indent}{decl}{varName} = new global::System.Collections.Generic.List<{elemFqn}>();");
        sb.AppendLine($"{indent}while (reader.Read())");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (reader.TokenType == global::Pakt.PaktTokenType.ListEnd) break;");
        sb.AppendLine($"{indent}    {varName}.Add({EmitGetForKind(elemKind, elemFqn)});");
        sb.AppendLine($"{indent}}}");
    }

    // ── Map reading ──

    private static void EmitMapRead(StringBuilder sb, PropertyModel prop, string varName)
    {
        EmitMapReadInline(sb, prop, varName, "        ");
    }

    private static void EmitMapReadInline(StringBuilder sb, PropertyModel prop, string varName, string indent, bool declare = true)
    {
        string keyFqn = prop.KeyTypeFqn ?? "string";
        string valFqn = prop.ValueTypeFqn ?? "object";
        var valKind = ClassifyElementKind(valFqn);
        sb.AppendLine($"{indent}if (reader.TokenType != global::Pakt.PaktTokenType.MapStart) reader.Read();");
        string decl = declare ? "var " : "";
        sb.AppendLine($"{indent}{decl}{varName} = new global::System.Collections.Generic.Dictionary<{keyFqn}, {valFqn}>();");
        sb.AppendLine($"{indent}while (reader.Read())");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (reader.TokenType == global::Pakt.PaktTokenType.MapEnd) break;");
        sb.AppendLine($"{indent}    var __key = reader.GetString();");
        sb.AppendLine($"{indent}    reader.Read(); // MapEntryBind");
        sb.AppendLine($"{indent}    reader.Read(); // value token");
        sb.AppendLine($"{indent}    var __val = {EmitGetForKind(valKind, valFqn)};");
        sb.AppendLine($"{indent}    {varName}[__key] = __val;");
        sb.AppendLine($"{indent}}}");
    }

    // ── Helpers ──

    private static string EmitGetForCurrentToken(PropertyModel prop)
    {
        return prop.Kind switch
        {
            PaktTypeKind.String => "reader.GetString()",
            PaktTypeKind.Int => "reader.GetInt32()",
            PaktTypeKind.Long => "reader.GetInt64()",
            PaktTypeKind.Decimal => "reader.GetDecimal()",
            PaktTypeKind.Double => "reader.GetDouble()",
            PaktTypeKind.Float => "reader.GetFloat()",
            PaktTypeKind.Bool => "reader.GetBool()",
            PaktTypeKind.Guid => "reader.GetGuid()",
            PaktTypeKind.DateOnly => "reader.GetDate()",
            PaktTypeKind.DateTimeOffset => "reader.GetTimestamp()",
            PaktTypeKind.ByteArray => "reader.GetBytes()",
            _ => $"default({prop.ClrTypeFqn})!",
        };
    }

    private static string EmitGetForKind(PaktTypeKind kind, string fqn)
    {
        return kind switch
        {
            PaktTypeKind.String => "reader.GetString()",
            PaktTypeKind.Int => "reader.GetInt32()",
            PaktTypeKind.Long => "reader.GetInt64()",
            PaktTypeKind.Decimal => "reader.GetDecimal()",
            PaktTypeKind.Double => "reader.GetDouble()",
            PaktTypeKind.Float => "reader.GetFloat()",
            PaktTypeKind.Bool => "reader.GetBool()",
            _ => $"default({fqn})!",
        };
    }

    private static string GetDefaultValue(PropertyModel prop)
    {
        if (prop.IsNullable) return "default";
        return prop.Kind switch
        {
            PaktTypeKind.String => "\"\"",
            PaktTypeKind.Int => "0",
            PaktTypeKind.Long => "0L",
            PaktTypeKind.Decimal => "0m",
            PaktTypeKind.Double => "0d",
            PaktTypeKind.Float => "0f",
            PaktTypeKind.Bool => "false",
            PaktTypeKind.Guid => "default",
            PaktTypeKind.DateOnly => "default",
            PaktTypeKind.DateTimeOffset => "default",
            PaktTypeKind.ByteArray => "global::System.Array.Empty<byte>()",
            PaktTypeKind.List => $"new global::System.Collections.Generic.List<{prop.ElementTypeFqn ?? "object"}>()",
            PaktTypeKind.Map => $"new global::System.Collections.Generic.Dictionary<{prop.KeyTypeFqn ?? "string"}, {prop.ValueTypeFqn ?? "object"}>()",
            PaktTypeKind.Struct => $"default({prop.ClrTypeFqn})!",
            _ => "default!",
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
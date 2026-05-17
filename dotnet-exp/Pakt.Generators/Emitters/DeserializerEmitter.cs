using System.Text;

using Pakt.Generators.Models;

namespace Pakt.Generators.Emitters;

/// <summary>
/// Emits source code for deserialize methods targeting <c>PaktReader</c>.
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

        sb.AppendLine($"    private static {typeFqn} DeserializeValue{model.Name}(ref global::Pakt.PaktReader reader)");
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
    public static string EmitUnitDeserializeMethod(SerializableTypeModel model)
    {
        var sb = new StringBuilder(2048);
        string typeFqn = model.FullyQualifiedName;
        var activeProps = model.Properties.Where(p => !p.IsIgnored).ToList();

        // Exp 3: Use PaktReader directly — generated code IS the validation
        sb.AppendLine($"    private static {typeFqn} DeserializeUnit{model.Name}(ref global::Pakt.PaktReader reader, global::Pakt.PaktSerializationOptions options)");
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
        sb.AppendLine("            reader.Read(); // TypeAnnotation");
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

    /// <summary>
    /// Emit an async unit-level deserializer using <c>PaktPipeSource</c> for
    /// incremental stream-backed deserialization with automatic buffer management.
    /// <para>
    /// Unlike the sync version, this processes tokens one-by-one through a single
    /// <c>while (reader.Read())</c> loop with switch on TokenType. This makes the
    /// generated code resilient to buffer boundaries: every Read() is checked via
    /// the while condition, and we track "safe consumed" (the position after the
    /// last complete token) for <c>AdvanceTo</c>.
    /// </para>
    /// </summary>
    public static string EmitUnitDeserializeAsyncMethod(SerializableTypeModel model)
    {
        var sb = new StringBuilder(4096);
        string typeFqn = model.FullyQualifiedName;
        var activeProps = model.Properties.Where(p => !p.IsIgnored).ToList();

        sb.AppendLine($"    private static async global::System.Threading.Tasks.ValueTask<{typeFqn}> DeserializeUnitAsync{model.Name}(global::Pakt.PaktPipeSource source, global::Pakt.PaktSerializationOptions options, global::System.Threading.CancellationToken ct)");
        sb.AppendLine("    {");

        // Declare locals for each property
        foreach (var prop in activeProps)
        {
            string defaultVal = GetDefaultValue(prop);
            sb.AppendLine($"        {prop.ClrTypeFqn} __{prop.ClrName} = {defaultVal};");
        }

        sb.AppendLine($"        int __seen = 0;");
        sb.AppendLine($"        bool __done = false;");
        sb.AppendLine();

        // Statement tracking (persists across refills)
        sb.AppendLine("        int __matchedIdx = -1;");
        sb.AppendLine("        int __skipDepth = 0;");
        sb.AppendLine("        bool __skipping = false;");

        // List accumulation variables
        var listProps = activeProps.Where(p => p.Kind == PaktTypeKind.List).ToList();
        foreach (var lp in listProps)
        {
            string elemFqn = lp.ElementTypeFqn ?? "object";
            sb.AppendLine($"        global::System.Collections.Generic.List<{elemFqn}>? __listAccum_{lp.ClrName} = null;");
        }

        // Map accumulation variables
        var mapProps = activeProps.Where(p => p.Kind == PaktTypeKind.Map).ToList();
        foreach (var mp in mapProps)
        {
            string keyFqn = mp.KeyTypeFqn ?? "string";
            string valFqn = mp.ValueTypeFqn ?? "object";
            sb.AppendLine($"        global::System.Collections.Generic.Dictionary<{keyFqn}, {valFqn}>? __mapAccum_{mp.ClrName} = null;");
            sb.AppendLine($"        string __mapKey_{mp.ClrName} = \"\";");
        }

        sb.AppendLine();

        // Outer refill loop
        sb.AppendLine("        while (!__done)");
        sb.AppendLine("        {");
        sb.AppendLine("            long __consumed;");
        sb.AppendLine("            global::Pakt.PaktReaderState __state;");
        sb.AppendLine("            {");
        sb.AppendLine("                var reader = source.CreateReader();");
        sb.AppendLine("                long __safeConsumed = 0;");
        sb.AppendLine("                global::Pakt.PaktReaderState __safeState = source.ReaderState;");
        sb.AppendLine();
        sb.AppendLine("                while (reader.Read())");
        sb.AppendLine("                {");
        sb.AppendLine("                    __safeConsumed = reader.BytesConsumed;");
        sb.AppendLine("                    __safeState = reader.CurrentState;");
        sb.AppendLine();

        // Token dispatch
        sb.AppendLine("                    // Skip mode: consume tokens until statement ends");
        sb.AppendLine("                    if (__skipping)");
        sb.AppendLine("                    {");
        sb.AppendLine("                        if (reader.TokenType is global::Pakt.PaktTokenType.StructStart or global::Pakt.PaktTokenType.ListStart or global::Pakt.PaktTokenType.TupleStart or global::Pakt.PaktTokenType.MapStart)");
        sb.AppendLine("                            { __skipDepth++; continue; }");
        sb.AppendLine("                        else if (reader.TokenType is global::Pakt.PaktTokenType.StructEnd or global::Pakt.PaktTokenType.ListEnd or global::Pakt.PaktTokenType.TupleEnd or global::Pakt.PaktTokenType.MapEnd)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            __skipDepth--;");
        sb.AppendLine("                            if (__skipDepth <= 0) { __skipping = false; __skipDepth = 0; }");
        sb.AppendLine("                            continue;");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else if (reader.TokenType == global::Pakt.PaktTokenType.EndOfUnit)");
        sb.AppendLine("                            { __skipping = false; __skipDepth = 0; __done = true; break; }");
        sb.AppendLine("                        else if (reader.TokenType == global::Pakt.PaktTokenType.StatementName)");
        sb.AppendLine("                            { __skipping = false; __skipDepth = 0; }");
        sb.AppendLine("                        else if (__skipDepth == 0)");
        sb.AppendLine("                            { __skipping = false; continue; }");
        sb.AppendLine("                        else");
        sb.AppendLine("                            continue;");
        sb.AppendLine("                    }");
        sb.AppendLine();

        sb.AppendLine("                    switch (reader.TokenType)");
        sb.AppendLine("                    {");

        // EndOfUnit
        sb.AppendLine("                        case global::Pakt.PaktTokenType.EndOfUnit:");
        sb.AppendLine("                            __done = true;");
        sb.AppendLine("                            break;");
        sb.AppendLine();

        // StatementName
        sb.AppendLine("                        case global::Pakt.PaktTokenType.StatementName:");
        sb.AppendLine("                        {");
        sb.AppendLine("                            var __nameSeq = reader.ValueSequence;");
        sb.AppendLine("                            global::System.ReadOnlySpan<byte> __nameSpan;");
        sb.AppendLine("                            if (__nameSeq.IsSingleSegment)");
        sb.AppendLine("                                __nameSpan = __nameSeq.FirstSpan;");
        sb.AppendLine("                            else");
        sb.AppendLine("                            {");
        sb.AppendLine("                                byte[] __nameBuf = new byte[(int)__nameSeq.Length];");
        sb.AppendLine("                                global::System.Buffers.BuffersExtensions.CopyTo(__nameSeq, __nameBuf);");
        sb.AppendLine("                                __nameSpan = __nameBuf;");
        sb.AppendLine("                            }");
        sb.AppendLine();

        // Name matching → __matchedIdx
        EmitNameMatchingToIndex(sb, activeProps);
        sb.AppendLine();

        // Duplicate check
        sb.AppendLine("                            if (__matchedIdx >= 0 && (__seen & (1 << __matchedIdx)) != 0)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                if (options.DuplicateStatements == global::Pakt.DuplicatePolicy.Error)");
        sb.AppendLine("                                {");
        sb.AppendLine("                                    string __dupName = global::System.Text.Encoding.UTF8.GetString(__nameSpan);");
        sb.AppendLine("                                    throw new global::Pakt.PaktParseException($\"Duplicate statement: {__dupName}\", (int)global::Pakt.PaktErrorCode.Syntax, default);");
        sb.AppendLine("                                }");
        sb.AppendLine("                                if (options.DuplicateStatements == global::Pakt.DuplicatePolicy.FirstWins)");
        sb.AppendLine("                                    __matchedIdx = -1;");
        sb.AppendLine("                            }");
        sb.AppendLine();

        // Unknown check
        sb.AppendLine("                            if (__matchedIdx == -1)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                if (options.UnknownStatements == global::Pakt.UnknownMemberPolicy.Error)");
        sb.AppendLine("                                {");
        sb.AppendLine("                                    string __unknownName = global::System.Text.Encoding.UTF8.GetString(__nameSpan);");
        sb.AppendLine("                                    throw new global::Pakt.PaktParseException($\"Unknown statement: {__unknownName}\", (int)global::Pakt.PaktErrorCode.Syntax, default);");
        sb.AppendLine("                                }");
        sb.AppendLine("                            }");
        sb.AppendLine("                            break;");
        sb.AppendLine("                        }");
        sb.AppendLine();

        // TypeAnnotation + AssignOperator — skip
        sb.AppendLine("                        case global::Pakt.PaktTokenType.TypeAnnotation:");
        sb.AppendLine("                            break;");
        sb.AppendLine("                        case global::Pakt.PaktTokenType.AssignOperator:");
        sb.AppendLine("                            if (__matchedIdx == -1) __skipping = true;");
        sb.AppendLine("                            break;");
        sb.AppendLine();

        // Nil handling
        sb.AppendLine("                        case global::Pakt.PaktTokenType.Nil:");
        var nullableProps = activeProps.Where(p => p.IsNullable).ToList();
        if (nullableProps.Count > 0)
        {
            sb.AppendLine("                            switch (__matchedIdx)");
            sb.AppendLine("                            {");
            foreach (var np in nullableProps)
            {
                int idx = activeProps.IndexOf(np);
                int bit = 1 << idx;
                sb.AppendLine($"                                case {idx}: __{np.ClrName} = default; __seen |= {bit}; break;");
            }
            sb.AppendLine("                            }");
        }
        sb.AppendLine("                            __matchedIdx = -1;");
        sb.AppendLine("                            break;");
        sb.AppendLine();

        // Scalar value tokens — assign based on __matchedIdx
        EmitScalarValueCases(sb, activeProps);

        // List start/end
        if (listProps.Count > 0)
        {
            sb.AppendLine("                        case global::Pakt.PaktTokenType.ListStart:");
            sb.AppendLine("                            switch (__matchedIdx)");
            sb.AppendLine("                            {");
            foreach (var lp in listProps)
            {
                int idx = activeProps.IndexOf(lp);
                string elemFqn = lp.ElementTypeFqn ?? "object";
                sb.AppendLine($"                                case {idx}: __listAccum_{lp.ClrName} = new global::System.Collections.Generic.List<{elemFqn}>(); break;");
            }
            sb.AppendLine("                                default: __skipping = true; __skipDepth = 1; break;");
            sb.AppendLine("                            }");
            sb.AppendLine("                            break;");
            sb.AppendLine();

            sb.AppendLine("                        case global::Pakt.PaktTokenType.ListEnd:");
            foreach (var lp in listProps)
            {
                int idx = activeProps.IndexOf(lp);
                int bit = 1 << idx;
                sb.AppendLine($"                            if (__listAccum_{lp.ClrName} is not null)");
                sb.AppendLine($"                            {{ __{lp.ClrName} = __listAccum_{lp.ClrName}; __seen |= {bit}; __listAccum_{lp.ClrName} = null; }}");
            }
            sb.AppendLine("                            __matchedIdx = -1;");
            sb.AppendLine("                            break;");
            sb.AppendLine();
        }

        // Map start/end/bind
        if (mapProps.Count > 0)
        {
            sb.AppendLine("                        case global::Pakt.PaktTokenType.MapStart:");
            sb.AppendLine("                            switch (__matchedIdx)");
            sb.AppendLine("                            {");
            foreach (var mp in mapProps)
            {
                int idx = activeProps.IndexOf(mp);
                string keyFqn = mp.KeyTypeFqn ?? "string";
                string valFqn = mp.ValueTypeFqn ?? "object";
                sb.AppendLine($"                                case {idx}: __mapAccum_{mp.ClrName} = new global::System.Collections.Generic.Dictionary<{keyFqn}, {valFqn}>(); break;");
            }
            sb.AppendLine("                                default: __skipping = true; __skipDepth = 1; break;");
            sb.AppendLine("                            }");
            sb.AppendLine("                            break;");
            sb.AppendLine();

            sb.AppendLine("                        case global::Pakt.PaktTokenType.MapEnd:");
            foreach (var mp in mapProps)
            {
                int idx = activeProps.IndexOf(mp);
                int bit = 1 << idx;
                sb.AppendLine($"                            if (__mapAccum_{mp.ClrName} is not null)");
                sb.AppendLine($"                            {{ __{mp.ClrName} = __mapAccum_{mp.ClrName}; __seen |= {bit}; __mapAccum_{mp.ClrName} = null; }}");
            }
            sb.AppendLine("                            __matchedIdx = -1;");
            sb.AppendLine("                            break;");
            sb.AppendLine();

            sb.AppendLine("                        case global::Pakt.PaktTokenType.MapEntryBind:");
            sb.AppendLine("                            break;");
            sb.AppendLine();
        }

        // Default (StructStart/End, TupleStart/End, etc.)
        sb.AppendLine("                        default:");
        sb.AppendLine("                            break;");

        sb.AppendLine("                    }"); // end switch
        sb.AppendLine("                    if (__done) break;");
        sb.AppendLine("                }"); // end inner while
        sb.AppendLine();
        sb.AppendLine("                __consumed = __safeConsumed;");
        sb.AppendLine("                __state = __safeState;");
        sb.AppendLine("            }"); // end scoped block
        sb.AppendLine();
        sb.AppendLine("            if (__done) break;");
        sb.AppendLine("            if (source.IsFinalBlock) break;");
        sb.AppendLine("            ct.ThrowIfCancellationRequested();");
        sb.AppendLine("            if (!await source.AdvanceAndRefillAsync(__consumed, __state, ct).ConfigureAwait(false))");
        sb.AppendLine("                break;");
        sb.AppendLine("        }"); // end outer while
        sb.AppendLine();

        // Missing statement check
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

        // Construct result
        sb.AppendLine($"        return new {typeFqn}");
        sb.AppendLine("        {");
        bool first = true;
        foreach (var prop in activeProps)
        {
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
    /// Emit name matching code that sets <c>__matchedIdx</c> based on the statement name bytes.
    /// </summary>
    private static void EmitNameMatchingToIndex(StringBuilder sb, List<PropertyModel> activeProps)
    {
        var byFirstByte = activeProps
            .Select((p, i) => (Prop: p, Index: i))
            .GroupBy(x => (byte)x.Prop.PaktName[0])
            .OrderBy(g => g.Key)
            .ToList();

        sb.AppendLine("                            __matchedIdx = -1;");
        bool firstGroup = true;
        foreach (var group in byFirstByte)
        {
            string elsePrefix = firstGroup ? "" : "else ";
            sb.AppendLine($"                            {elsePrefix}if (__nameSpan.Length > 0 && __nameSpan[0] == (byte)'{(char)group.Key}')");
            sb.AppendLine("                            {");
            foreach (var item in group)
            {
                sb.AppendLine($"                                if (__nameSpan.SequenceEqual(\"{item.Prop.PaktName}\"u8))");
                sb.AppendLine($"                                    __matchedIdx = {item.Index};");
            }
            sb.AppendLine("                            }");
            firstGroup = false;
        }
    }

    /// <summary>
    /// Emit switch cases for scalar token types, assigning to the matched property.
    /// Also handles list element accumulation for scalar elements.
    /// </summary>
    private static void EmitScalarValueCases(StringBuilder sb, List<PropertyModel> activeProps)
    {
        // Group scalar props by their PaktTokenType
        // Also consider list element types and map value types
        var listProps = activeProps.Where(p => p.Kind == PaktTypeKind.List).ToList();
        var mapProps = activeProps.Where(p => p.Kind == PaktTypeKind.Map).ToList();

        // String
        EmitScalarCase(sb, activeProps, listProps, mapProps, "String", "reader.GetString()");
        // Int
        EmitScalarCase(sb, activeProps, listProps, mapProps, "Int", "reader.GetInt32()");
        // Decimal
        EmitScalarCase(sb, activeProps, listProps, mapProps, "Decimal", "reader.GetDecimal()");
        // Float
        EmitScalarCase(sb, activeProps, listProps, mapProps, "Float", "reader.GetFloat()");
        // Bool
        EmitScalarCase(sb, activeProps, listProps, mapProps, "Bool", "reader.GetBool()");
    }

    private static void EmitScalarCase(
        StringBuilder sb,
        List<PropertyModel> activeProps,
        List<PropertyModel> listProps,
        List<PropertyModel> mapProps,
        string tokenName,
        string getExpr)
    {
        var tokenType = $"global::Pakt.PaktTokenType.{tokenName}";
        var matchingKinds = tokenName switch
        {
            "String" => new[] { PaktTypeKind.String },
            "Int" => new[] { PaktTypeKind.Int, PaktTypeKind.Long },
            "Decimal" => new[] { PaktTypeKind.Decimal },
            "Float" => new[] { PaktTypeKind.Double, PaktTypeKind.Float },
            "Bool" => new[] { PaktTypeKind.Bool },
            _ => System.Array.Empty<PaktTypeKind>(),
        };

        // Scalar direct assignments
        var directScalars = activeProps
            .Select((p, i) => (Prop: p, Index: i))
            .Where(x => matchingKinds.Contains(x.Prop.Kind) && !x.Prop.IsNullable)
            .ToList();

        // List element accumulations
        var listAccums = listProps
            .Where(lp => IsElementKindMatch(lp.ElementTypeFqn, tokenName))
            .ToList();

        // Map value accumulations (key is always String for now)
        var mapKeyAccums = mapProps
            .Where(mp => string.Equals(tokenName, "String", System.StringComparison.Ordinal) && IsElementKindMatch(mp.KeyTypeFqn, "String"))
            .ToList();
        var mapValAccums = mapProps
            .Where(mp => IsElementKindMatch(mp.ValueTypeFqn, tokenName))
            .ToList();

        // Only emit case if there's something to handle
        if (directScalars.Count == 0 && listAccums.Count == 0 && mapKeyAccums.Count == 0 && mapValAccums.Count == 0)
            return;

        sb.AppendLine($"                        case {tokenType}:");
        sb.AppendLine("                        {");

        // Long needs special getter
        string getExprLong = string.Equals(tokenName, "Int", System.StringComparison.Ordinal) ? "reader.GetInt64()" : getExpr;
        string getExprFloat = string.Equals(tokenName, "Float", System.StringComparison.Ordinal) ? "reader.GetFloat()" : getExpr;

        // List element accumulation (checked first — takes priority when inside a list)
        foreach (var lp in listAccums)
        {
            string elemGet = GetListElemGetExpr(lp.ElementTypeFqn, tokenName);
            sb.AppendLine($"                            if (__listAccum_{lp.ClrName} is not null) {{ __listAccum_{lp.ClrName}.Add({elemGet}); break; }}");
        }

        // Map key/value accumulation
        foreach (var mp in mapKeyAccums)
        {
            sb.AppendLine($"                            if (__mapAccum_{mp.ClrName} is not null && __mapKey_{mp.ClrName}.Length == 0) {{ __mapKey_{mp.ClrName} = reader.GetString(); break; }}");
        }
        foreach (var mp in mapValAccums)
        {
            string valGet = GetListElemGetExpr(mp.ValueTypeFqn, tokenName);
            sb.AppendLine($"                            if (__mapAccum_{mp.ClrName} is not null && __mapKey_{mp.ClrName}.Length > 0) {{ __mapAccum_{mp.ClrName}[__mapKey_{mp.ClrName}] = {valGet}; __mapKey_{mp.ClrName} = \"\"; break; }}");
        }

        // Direct scalar assignment
        if (directScalars.Count > 0)
        {
            sb.AppendLine("                            switch (__matchedIdx)");
            sb.AppendLine("                            {");
            foreach (var item in directScalars)
            {
                int bit = 1 << item.Index;
                string actualGet = item.Prop.Kind switch
                {
                    PaktTypeKind.Long => getExprLong,
                    PaktTypeKind.Float => getExprFloat,
                    _ => getExpr,
                };
                sb.AppendLine($"                                case {item.Index}: __{item.Prop.ClrName} = {actualGet}; __seen |= {bit}; break;");
            }
            sb.AppendLine("                            }");
        }

        sb.AppendLine("                            __matchedIdx = -1;");
        sb.AppendLine("                            break;");
        sb.AppendLine("                        }");
        sb.AppendLine();
    }

    private static bool IsElementKindMatch(string? elemFqn, string tokenName)
    {
        if (elemFqn is null) return false;
        return tokenName switch
        {
            "String" => elemFqn.Contains("String", System.StringComparison.Ordinal) || string.Equals(elemFqn, "string", System.StringComparison.Ordinal),
            "Int" => elemFqn.Contains("Int32", System.StringComparison.Ordinal) || string.Equals(elemFqn, "int", System.StringComparison.Ordinal)
                   || elemFqn.Contains("Int64", System.StringComparison.Ordinal) || string.Equals(elemFqn, "long", System.StringComparison.Ordinal),
            "Decimal" => elemFqn.Contains("Decimal", System.StringComparison.Ordinal) || string.Equals(elemFqn, "decimal", System.StringComparison.Ordinal),
            "Float" => elemFqn.Contains("Double", System.StringComparison.Ordinal) || string.Equals(elemFqn, "double", System.StringComparison.Ordinal)
                     || elemFqn.Contains("Single", System.StringComparison.Ordinal) || string.Equals(elemFqn, "float", System.StringComparison.Ordinal),
            "Bool" => elemFqn.Contains("Boolean", System.StringComparison.Ordinal) || string.Equals(elemFqn, "bool", System.StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string GetListElemGetExpr(string? elemFqn, string tokenName)
    {
        if (elemFqn is null) return "default!";
        if (string.Equals(tokenName, "Int", System.StringComparison.Ordinal) && (elemFqn.Contains("Int64", System.StringComparison.Ordinal) || string.Equals(elemFqn, "long", System.StringComparison.Ordinal)))
            return "reader.GetInt64()";
        if (string.Equals(tokenName, "Float", System.StringComparison.Ordinal) && (elemFqn.Contains("Single", System.StringComparison.Ordinal) || string.Equals(elemFqn, "float", System.StringComparison.Ordinal)))
            return "reader.GetFloat()";
        return tokenName switch
        {
            "String" => "reader.GetString()",
            "Int" => "reader.GetInt32()",
            "Decimal" => "reader.GetDecimal()",
            "Float" => "reader.GetDouble()",
            "Bool" => "reader.GetBool()",
            _ => "default!",
        };
    }

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
        EmitStatementValueRead(sb, prop, "                    ");
    }

    private static void EmitStatementValueRead(StringBuilder sb, PropertyModel prop, string indent)
    {
        string varName = $"__{prop.ClrName}";

        // All values (including streaming collections) start with a Read() to get the first token
        sb.AppendLine($"{indent}reader.Read(); // advance to value");
        EmitAssignValueRead(sb, prop, varName, indent);
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
        sb.AppendLine($"{indent}    if (reader.TokenType == global::Pakt.PaktTokenType.ListEnd || reader.TokenType == global::Pakt.PaktTokenType.EndOfUnit) break;");
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
        sb.AppendLine($"{indent}    if (reader.TokenType == global::Pakt.PaktTokenType.MapEnd || reader.TokenType == global::Pakt.PaktTokenType.EndOfUnit) break;");
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
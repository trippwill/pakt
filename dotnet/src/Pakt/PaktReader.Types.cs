using System.Collections.Immutable;
using System.Text;

namespace Pakt;

public ref partial struct PaktReader
{
    // -----------------------------------------------------------------------
    // Type annotation parsing (recursive descent)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a ':' followed by a type annotation and optional '?' nullable suffix.
    /// </summary>
    private PaktType ReadTypeAnnotation()
    {
        ExpectByte((byte)':');
        var typ = ReadType();

        if (_consumed < _buffer.Length && _buffer[_consumed] == '?')
        {
            _consumed++;
            _bytePositionInLine++;
            return MakeNullable(typ);
        }

        return typ;
    }

    /// <summary>
    /// Dispatches on the next byte to parse a type (scalar, atom set, or composite).
    /// </summary>
    private PaktType ReadType()
    {
        SkipWSAndNewlines();

        if (_consumed >= _buffer.Length)
            ThrowError("Expected type, got EOF", PaktErrorCode.UnexpectedEof);

        byte b = _buffer[_consumed];
        return b switch
        {
            (byte)'|' => ReadAtomSetType(),
            (byte)'{' => ReadStructType(),
            (byte)'(' => ReadTupleType(),
            (byte)'[' => ReadListType(),
            (byte)'<' => ReadMapType(),
            _ when IsAlpha(b) || b == '_' => ReadScalarTypeName(),
            _ => throw new PaktException(
                $"Unexpected character in type: '{(char)b}'", Position, PaktErrorCode.Syntax),
        };
    }

    private PaktType ReadScalarTypeName()
    {
        var ident = ReadIdent();
        var kind = LookupScalarType(ident);
        return PaktType.Scalar(kind);
    }

    private static PaktScalarType LookupScalarType(string name) => name switch
    {
        "str" => PaktScalarType.Str,
        "int" => PaktScalarType.Int,
        "dec" => PaktScalarType.Dec,
        "float" => PaktScalarType.Float,
        "bool" => PaktScalarType.Bool,
        "uuid" => PaktScalarType.Uuid,
        "date" => PaktScalarType.Date,
        "time" => PaktScalarType.Time,
        "datetime" => PaktScalarType.DateTime,
        "bin" => PaktScalarType.Bin,
        _ => throw new PaktException($"Unknown scalar type '{name}'", PaktPosition.None, PaktErrorCode.Syntax),
    };

    private PaktType ReadAtomSetType()
    {
        ExpectByte((byte)'|');
        SkipWSAndNewlines();

        var first = ReadIdent();
        var members = ImmutableArray.CreateBuilder<string>();
        members.Add(first);

        while (true)
        {
            SkipWSAndNewlines();
            if (_consumed >= _buffer.Length)
                ThrowError("Unterminated atom set", PaktErrorCode.UnexpectedEof);

            byte b = _buffer[_consumed];
            if (b == '|')
            {
                _consumed++;
                _bytePositionInLine++;
                break;
            }
            if (b != ',')
                ThrowError($"Expected ',' or '|' in atom set, got '{(char)b}'", PaktErrorCode.Syntax);
            _consumed++;
            _bytePositionInLine++;
            SkipWSAndNewlines();
            members.Add(ReadIdent());
        }

        return PaktType.AtomSet(members.ToImmutable());
    }

    private PaktType ReadStructType()
    {
        ExpectByte((byte)'{');
        SkipWSAndNewlines();

        var fields = ImmutableArray.CreateBuilder<PaktField>();
        var first = ReadFieldDecl();
        fields.Add(first);

        while (true)
        {
            SkipWSAndNewlines();
            if (_consumed >= _buffer.Length)
                ThrowError("Unterminated struct type", PaktErrorCode.UnexpectedEof);

            byte b = _buffer[_consumed];
            if (b == '}')
            {
                _consumed++;
                _bytePositionInLine++;
                break;
            }
            if (b != ',')
                ThrowError($"Expected ',' or '}}' in struct type, got '{(char)b}'", PaktErrorCode.Syntax);
            _consumed++;
            _bytePositionInLine++;
            SkipWSAndNewlines();
            fields.Add(ReadFieldDecl());
        }

        return PaktType.Struct(fields.ToImmutable());
    }

    private PaktField ReadFieldDecl()
    {
        var name = ReadIdent();
        ExpectByte((byte)':');
        var typ = ReadType();

        if (_consumed < _buffer.Length && _buffer[_consumed] == '?')
        {
            _consumed++;
            _bytePositionInLine++;
            typ = MakeNullable(typ);
        }

        return new PaktField(name, typ);
    }

    private PaktType ReadTupleType()
    {
        ExpectByte((byte)'(');
        SkipWSAndNewlines();

        var elements = ImmutableArray.CreateBuilder<PaktType>();
        elements.Add(ReadTypeWithNullable());

        while (true)
        {
            SkipWSAndNewlines();
            if (_consumed >= _buffer.Length)
                ThrowError("Unterminated tuple type", PaktErrorCode.UnexpectedEof);

            byte b = _buffer[_consumed];
            if (b == ')')
            {
                _consumed++;
                _bytePositionInLine++;
                break;
            }
            if (b != ',')
                ThrowError($"Expected ',' or ')' in tuple type, got '{(char)b}'", PaktErrorCode.Syntax);
            _consumed++;
            _bytePositionInLine++;
            SkipWSAndNewlines();
            elements.Add(ReadTypeWithNullable());
        }

        return PaktType.Tuple(elements.ToImmutable());
    }

    private PaktType ReadListType()
    {
        ExpectByte((byte)'[');
        SkipWSAndNewlines();
        var elemType = ReadTypeWithNullable();
        SkipWSAndNewlines();
        ExpectByte((byte)']');
        return PaktType.List(elemType);
    }

    private PaktType ReadMapType()
    {
        ExpectByte((byte)'<');
        SkipWSAndNewlines();
        var keyType = ReadTypeWithNullable();
        SkipWSAndNewlines();
        ExpectByte((byte)';');
        SkipWSAndNewlines();
        var valType = ReadTypeWithNullable();
        SkipWSAndNewlines();
        ExpectByte((byte)'>');
        return PaktType.Map(keyType, valType);
    }

    /// <summary>
    /// Reads a type followed by optional '?' nullable suffix.
    /// </summary>
    private PaktType ReadTypeWithNullable()
    {
        var typ = ReadType();
        if (_consumed < _buffer.Length && _buffer[_consumed] == '?')
        {
            _consumed++;
            _bytePositionInLine++;
            return MakeNullable(typ);
        }
        return typ;
    }

    private static PaktType MakeNullable(PaktType type)
    {
        // Create a nullable version of the type
        if (type.IsStruct)
            return PaktType.Struct(type.StructFields, nullable: true);
        if (type.IsTuple)
            return PaktType.Tuple(type.TupleElements, nullable: true);
        if (type.IsList)
            return PaktType.List(type.ListElement!, nullable: true);
        if (type.IsMap)
            return PaktType.Map(type.MapKey!, type.MapValue!, nullable: true);
        if (type.IsAtomSet)
            return PaktType.AtomSet(type.AtomMembers, nullable: true);
        if (type.IsScalar)
            return PaktType.Scalar(type.ScalarKind, nullable: true);
        return type;
    }
}

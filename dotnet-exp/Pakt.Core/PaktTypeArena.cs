using System.Buffers;
using System.Runtime.InteropServices;

namespace Pakt;

internal sealed class PaktTypeArena
{
    private readonly List<PaktTypeNode> _types = [];
    private readonly List<PaktTypeRef> _members = [];
    private byte[] _nameBuffer = new byte[256];
    private int _nameUsed;

    /// <summary>
    /// The backing name buffer. Valid only during type event drain;
    /// callers must not hold references across Step() calls.
    /// </summary>
    internal byte[] NameBuffer => _nameBuffer;

    public PaktTypeRef Add(PaktTypeNode node)
    {
        _types.Add(node);
        return new PaktTypeRef(_types.Count - 1);
    }

    public PaktTypeNode Get(PaktTypeRef type)
        => _types[type.Id];

    public ReadOnlySpan<PaktTypeRef> GetMembers(PaktTypeNode node)
        => CollectionsMarshal.AsSpan(_members)
            .Slice(node.FirstMemberIndex, node.MemberCount);

    public PaktTypeRef AddStruct(ReadOnlySpan<PaktTypeRef> memberTypes)
    {
        int first = _members.Count;

        foreach (PaktTypeRef type in memberTypes)
            _members.Add(type);

        return Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.Struct,
            FirstMemberIndex = first,
            MemberCount = memberTypes.Length,
        });
    }

    public PaktTypeRef AddTuple(ReadOnlySpan<PaktTypeRef> memberTypes)
    {
        int first = _members.Count;

        foreach (PaktTypeRef type in memberTypes)
            _members.Add(type);

        return Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.Tuple,
            FirstMemberIndex = first,
            MemberCount = memberTypes.Length,
        });
    }

    public PaktTypeRef AddAtomSet(int memberCount)
    {
        return Add(new PaktTypeNode
        {
            Kind = PaktTypeKind.AtomSet,
            MemberCount = memberCount,
        });
    }

    /// <summary>
    /// Appends name bytes to the contiguous name buffer.
    /// Returns the start offset within the buffer.
    /// </summary>
    public int AppendName(ReadOnlySpan<byte> utf8)
    {
        int start = _nameUsed;
        EnsureNameCapacity(utf8.Length);
        utf8.CopyTo(_nameBuffer.AsSpan(_nameUsed));
        _nameUsed += utf8.Length;
        return start;
    }

    /// <summary>
    /// Appends name bytes from a <see cref="ReadOnlySequence{T}"/> to the contiguous name buffer.
    /// Returns the start offset within the buffer.
    /// </summary>
    public int AppendName(in ReadOnlySequence<byte> utf8)
    {
        int start = _nameUsed;
        int length = (int)utf8.Length;
        EnsureNameCapacity(length);
        utf8.CopyTo(_nameBuffer.AsSpan(_nameUsed));
        _nameUsed += length;
        return start;
    }

    public ReadOnlySpan<byte> GetNameSpan(int start, int length)
        => _nameBuffer.AsSpan(start, length);

    /// <summary>
    /// Resets the name buffer write position. Safe to call only after
    /// all pending type events referencing names have been drained.
    /// </summary>
    public void ClearNames()
        => _nameUsed = 0;

    private void EnsureNameCapacity(int additionalBytes)
    {
        int required = _nameUsed + additionalBytes;
        if (required > _nameBuffer.Length)
        {
            int newSize = Math.Max(_nameBuffer.Length * 2, required);
            Array.Resize(ref _nameBuffer, newSize);
        }
    }
}

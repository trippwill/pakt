using System.Diagnostics;

namespace Pakt;

/// <summary>
/// Map entry parsing phase within a validation frame.
/// </summary>
internal enum MapPhase : byte
{
    /// <summary>Expect a key value or closing delimiter.</summary>
    ExpectKeyOrClose,

    /// <summary>Expect '=>' bind operator.</summary>
    ExpectBind,

    /// <summary>Expect the value after '=>'.</summary>
    ExpectValue,
}

/// <summary>
/// Tracks validation state for one nesting level of a composite value.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
internal struct ValidationFrame
{
    /// <summary>Index of the ValidationNode describing this composite's type.</summary>
    public int TypeNodeIndex;

    /// <summary>
    /// Current child position (struct field index, tuple element index).
    /// For lists: count of elements seen. For maps: count of complete entries.
    /// </summary>
    public int ChildIndex;

    /// <summary>For maps: current key/value phase.</summary>
    public MapPhase Phase;
}

/// <summary>
/// Snapshot of a <see cref="ValidationFrame"/> for state serialization.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
internal readonly struct ValidationFrameSnapshot
{
    public readonly int TypeNodeIndex;
    public readonly int ChildIndex;
    public readonly MapPhase Phase;

    public ValidationFrameSnapshot(int typeNodeIndex, int childIndex, MapPhase phase)
    {
        TypeNodeIndex = typeNodeIndex;
        ChildIndex = childIndex;
        Phase = phase;
    }

    public ValidationFrameSnapshot(ref ValidationFrame frame)
    {
        TypeNodeIndex = frame.TypeNodeIndex;
        ChildIndex = frame.ChildIndex;
        Phase = frame.Phase;
    }
}

/// <summary>
/// Stack of <see cref="ValidationFrame"/> values for composite nesting during validation.
/// </summary>
internal struct ValidationFrameStack
{
    private ValidationFrame[] _frames;
    private int _depth;

    public int Depth => _depth;

    public void Push(ValidationFrame frame)
    {
        _frames ??= new ValidationFrame[4];
        if (_depth == _frames.Length)
            Array.Resize(ref _frames, _frames.Length * 2);

        _frames[_depth++] = frame;
    }

    public ref ValidationFrame Peek()
    {
        Debug.Assert(_depth > 0);
        return ref _frames[_depth - 1];
    }

    public ValidationFrame Pop()
    {
        Debug.Assert(_depth > 0);
        return _frames[--_depth];
    }

    public void Clear() => _depth = 0;

    /// <summary>Take a snapshot of the current stack for state serialization.</summary>
    public ValidationFrameSnapshot[] ToSnapshots()
    {
        if (_depth == 0) return [];
        var snapshots = new ValidationFrameSnapshot[_depth];
        for (int i = 0; i < _depth; i++)
            snapshots[i] = new ValidationFrameSnapshot(ref _frames[i]);
        return snapshots;
    }

    /// <summary>Restore the stack from a snapshot array.</summary>
    public void RestoreFrom(ValidationFrameSnapshot[] snapshots, int count)
    {
        if (count == 0) { _depth = 0; return; }
        _frames ??= new ValidationFrame[Math.Max(4, count)];
        if (_frames.Length < count)
            Array.Resize(ref _frames, count);

        for (int i = 0; i < count; i++)
        {
            _frames[i] = new ValidationFrame
            {
                TypeNodeIndex = snapshots[i].TypeNodeIndex,
                ChildIndex = snapshots[i].ChildIndex,
                Phase = snapshots[i].Phase,
            };
        }

        _depth = count;
    }
}
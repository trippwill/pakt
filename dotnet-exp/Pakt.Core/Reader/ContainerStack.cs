using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Pakt;

/// <summary>
/// Container kind for nesting depth tracking.
/// Encoded as 2 bits per level in <see cref="ContainerStack"/>.
/// </summary>
internal enum ContainerKind : byte
{
    Struct = 0b00,
    Tuple = 0b01,
    List = 0b10,
    Map = 0b11,
}

/// <summary>
/// Allocation-free container-type stack using 2 bits per level.
/// Supports 32 levels in a <see cref="ulong"/> before spilling to heap.
/// Adapted from System.Text.Json's BitStack pattern.
/// </summary>
internal struct ContainerStack
{
    private const int AllocationFreeMaxDepth = 32; // 64 bits / 2 bits per level
    private ulong _register;
    private int[]? _overflow;
    private int _currentDepth;

    public int CurrentDepth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _currentDepth;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(ContainerKind kind)
    {
        if (_currentDepth < AllocationFreeMaxDepth)
        {
            _register = (_register << 2) | (ulong)kind;
        }
        else
        {
            PushOverflow(kind);
        }

        _currentDepth++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ContainerKind Pop()
    {
        Debug.Assert(_currentDepth > 0);
        _currentDepth--;

        if (_currentDepth < AllocationFreeMaxDepth)
        {
            var kind = (ContainerKind)(_register & 0b11);
            _register >>= 2;
            return kind;
        }

        return PopOverflow();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ContainerKind Peek()
    {
        Debug.Assert(_currentDepth > 0);

        if (_currentDepth <= AllocationFreeMaxDepth)
        {
            return (ContainerKind)(_register & 0b11);
        }

        return PeekOverflow();
    }

    private void PushOverflow(ContainerKind kind)
    {
        int overflowIdx = _currentDepth - AllocationFreeMaxDepth;
        int arrayIdx = overflowIdx / 16;  // 16 levels per int (32 bits / 2 bits)
        int bitIdx = (overflowIdx % 16) * 2;

        if (_overflow is null || _overflow.Length <= arrayIdx)
        {
            int newLen = Math.Max(4, arrayIdx + 1);
            Array.Resize(ref _overflow, newLen);
        }

        _overflow[arrayIdx] = (_overflow[arrayIdx] & ~(0b11 << bitIdx)) | ((int)kind << bitIdx);
    }

    private ContainerKind PopOverflow()
    {
        int overflowIdx = _currentDepth - AllocationFreeMaxDepth;
        int arrayIdx = overflowIdx / 16;
        int bitIdx = (overflowIdx % 16) * 2;
        return (ContainerKind)((_overflow![arrayIdx] >> bitIdx) & 0b11);
    }

    private ContainerKind PeekOverflow()
    {
        int overflowIdx = _currentDepth - AllocationFreeMaxDepth - 1;
        int arrayIdx = overflowIdx / 16;
        int bitIdx = (overflowIdx % 16) * 2;
        return (ContainerKind)((_overflow![arrayIdx] >> bitIdx) & 0b11);
    }
}
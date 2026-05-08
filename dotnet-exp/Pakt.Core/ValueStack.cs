using System.Diagnostics;

namespace Pakt;

internal sealed class ValueStack
{
    private readonly ValueFrame[] _frames;

    public ValueStack(int maxDepth)
    {
        Debug.Assert(maxDepth > 0, "Max depth must be positive.");
        _frames = new ValueFrame[maxDepth];
    }

    public int Depth { get; private set; }

    public bool IsEmpty => Depth == 0;

    public ref ValueFrame Top
    {
        get
        {
            Debug.Assert(Depth > 0, "Value stack is empty.");
            return ref _frames[Depth - 1];
        }
    }

    public bool TryPush(ValueFrame frame)
    {
        if (Depth >= _frames.Length)
            return false;

        _frames[Depth++] = frame;
        return true;
    }

    public void Pop()
    {
        Depth--;
        Debug.Assert(Depth >= 0, "Value stack underflow.");
    }

    public void Clear() => Depth = 0;
}
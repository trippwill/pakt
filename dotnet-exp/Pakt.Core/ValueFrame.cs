using System.Runtime.InteropServices;

namespace Pakt;

[StructLayout(LayoutKind.Auto)]
internal struct ValueFrame
{
    public PaktTypeRef TypeRef;
    public int Index;
    public FrameFlags Flags;
}

[Flags]
internal enum FrameFlags : byte
{
    None = 0,
    Opened = 1,
    ExpectKey = 2,
    Pack = 4,
}

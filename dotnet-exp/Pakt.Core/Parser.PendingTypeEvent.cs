using System.Runtime.InteropServices;

namespace Pakt;

sealed partial class Parser
{
    [StructLayout(LayoutKind.Auto)]
    private struct PendingTypeEvent
    {
        public PaktEvent.Kind Kind;
        public long Offset;
        public PaktTypeKind TypeKind;
        public int NameStart;
        public int NameLength;

        public static PendingTypeEvent Simple(PaktEvent.Kind kind, long offset)
            => new() { Kind = kind, Offset = offset, NameStart = -1 };

        public static PendingTypeEvent Typed(
            PaktEvent.Kind kind, long offset, PaktTypeKind typeKind)
            => new() { Kind = kind, Offset = offset, TypeKind = typeKind, NameStart = -1 };

        public static PendingTypeEvent Named(
            PaktEvent.Kind kind, long offset, PaktTypeKind typeKind,
            int nameStart, int nameLength)
            => new() { Kind = kind, Offset = offset, TypeKind = typeKind, NameStart = nameStart, NameLength = nameLength };
    }
}

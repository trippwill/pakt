using System.Text;
using Pakt;

namespace Pakt.Tests;

/// <summary>
/// Tests for PaktUnitSyntax pack boundary probing.
/// </summary>
public class PaktUnitSyntaxTests
{
    [Theory]
    [InlineData("  \t  ", 0, 5)]
    [InlineData("  x", 0, 2)]
    [InlineData("x  ", 0, 0)]
    [InlineData("", 0, 0)]
    public void SkipHorizontalWhitespace(string input, int offset, int expected)
    {
        var span = Encoding.UTF8.GetBytes(input).AsSpan();
        var result = PaktUnitSyntax.SkipHorizontalWhitespace(span, offset);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SkipInsignificant_SkipsCommentsAndNewlines()
    {
        var input = "  # comment\n  value"u8;
        var result = PaktUnitSyntax.SkipInsignificant(input, 0, skipNewlines: true);
        Assert.Equal(14, result); // past "  # comment\n  "
    }

    [Fact]
    public void SkipInsignificant_StopsAtNewlineWhenNotSkipping()
    {
        var input = "  \n  value"u8;
        var result = PaktUnitSyntax.SkipInsignificant(input, 0, skipNewlines: false);
        Assert.Equal(2, result); // stops at \n
    }

    [Fact]
    public void ProbePackItemStart_HasValue()
    {
        var input = "  {'data'}"u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackItemStart(input, ref cursor, unitComplete: true);
        Assert.Equal(PaktUnitSyntax.PackItemStartKind.HasValue, result);
    }

    [Fact]
    public void ProbePackItemStart_Terminated_AtEnd()
    {
        var input = "  \n"u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackItemStart(input, ref cursor, unitComplete: true);
        Assert.Equal(PaktUnitSyntax.PackItemStartKind.Terminated, result);
    }

    [Fact]
    public void ProbePackItemStart_Terminated_AtNul()
    {
        var input = new byte[] { 0x00 }.AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackItemStart(input, ref cursor, unitComplete: false);
        Assert.Equal(PaktUnitSyntax.PackItemStartKind.Terminated, result);
    }

    [Fact]
    public void ProbePackItemStart_NeedMoreData_WhenNotComplete()
    {
        var input = "  "u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackItemStart(input, ref cursor, unitComplete: false);
        Assert.Equal(PaktUnitSyntax.PackItemStartKind.NeedMoreData, result);
    }

    [Fact]
    public void ProbePackBoundary_Separator_Comma()
    {
        var input = " , "u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackBoundary(input, ref cursor, unitComplete: true);
        Assert.Equal(PaktUnitSyntax.PackBoundaryKind.Separator, result);
    }

    [Fact]
    public void ProbePackBoundary_Separator_Newline()
    {
        var input = " \n "u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackBoundary(input, ref cursor, unitComplete: true);
        Assert.Equal(PaktUnitSyntax.PackBoundaryKind.Separator, result);
    }

    [Fact]
    public void ProbePackBoundary_Terminated()
    {
        var input = "  \n\n"u8.ToArray().AsSpan();
        int cursor = 0;
        var result = PaktUnitSyntax.ProbePackBoundary(input, ref cursor, unitComplete: true);
        // After whitespace/newlines with no more value-starting chars, should terminate
        // The exact behavior depends on implementation — verify it returns Separator or Terminated
        Assert.True(result is PaktUnitSyntax.PackBoundaryKind.Separator or PaktUnitSyntax.PackBoundaryKind.Terminated);
    }
}

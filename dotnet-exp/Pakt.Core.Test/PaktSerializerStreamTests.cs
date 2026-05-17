using System.Text;

namespace Pakt.Core.Test;

/// <summary>
/// Stream that yields data in fixed-size chunks, forcing multi-refill
/// behavior in the <see cref="PaktPipeSource"/> / <see cref="PaktSerializer"/> pipeline.
/// </summary>
public sealed class ThrottledStream : Stream
{
    private readonly byte[] _data;
    private int _position;
    private readonly int _chunkSize;

    public ThrottledStream(byte[] data, int chunkSize)
    {
        _data = data;
        _chunkSize = chunkSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _data.Length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _data.Length) return 0;
        int toRead = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
        Array.Copy(_data, _position, buffer, offset, toRead);
        _position += toRead;
        return toRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= _data.Length) return 0;
        int toRead = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
        _data.AsSpan(_position, toRead).CopyTo(buffer.Span);
        _position += toRead;
        await Task.Yield(); // force truly async
        return toRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// ── Tests ──

public class PaktSerializerStreamTests
{
    // ═══════════════════ SimpleConfig via stream ═══════════════════

    [Fact]
    public async Task SimpleConfig_Stream_ChunkSize1()
    {
        byte[] pakt = "name:str = 'my-app'\nversion:int = 42\ndebug:bool = true"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 1);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("my-app", result.Name);
        Assert.Equal(42, result.Version);
        Assert.True(result.Debug);
    }

    [Fact]
    public async Task SimpleConfig_Stream_ChunkSize8()
    {
        byte[] pakt = "name:str = 'my-app'\nversion:int = 42\ndebug:bool = true"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 8);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("my-app", result.Name);
        Assert.Equal(42, result.Version);
        Assert.True(result.Debug);
    }

    [Fact]
    public async Task SimpleConfig_Stream_ChunkSize64()
    {
        byte[] pakt = "name:str = 'my-app'\nversion:int = 42\ndebug:bool = true"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 64);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("my-app", result.Name);
        Assert.Equal(42, result.Version);
        Assert.True(result.Debug);
    }

    // ═══════════════════ ConfigWithList via stream ═══════════════════

    [Fact]
    public async Task ConfigWithList_ClosedList_Stream()
    {
        byte[] pakt = "name:str = 'test'\nscores:[int] = [90 85 92]"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 8);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<ConfigWithList>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("test", result.Name);
        Assert.Equal([90, 85, 92], result.Scores);
    }

    [Fact]
    public async Task ConfigWithList_StreamingPack_10K()
    {
        // Build a large streaming list: scores:[int] = ~[1 2 3 ... 10000]
        var sb = new StringBuilder();
        sb.Append("name:str = 'bench'\nscores:[int] = ~[");
        for (int i = 1; i <= 10_000; i++)
        {
            if (i > 1) sb.Append(' ');
            sb.Append(i);
        }
        sb.Append(']');
        byte[] pakt = Encoding.UTF8.GetBytes(sb.ToString());
        using var stream = new ThrottledStream(pakt, chunkSize: 128);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<ConfigWithList>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("bench", result.Name);
        Assert.Equal(10_000, result.Scores.Count);
        Assert.Equal(1, result.Scores[0]);
        Assert.Equal(5000, result.Scores[4999]);
        Assert.Equal(10_000, result.Scores[9999]);
    }

    // ═══════════════════ Memory fast path via PaktSerializer ═══════════════════

    [Fact]
    public void SimpleConfig_Memory_ViaSerializer()
    {
        byte[] pakt = "name:str = 'sync-test'\nversion:int = 99\ndebug:bool = false"u8.ToArray();

        var result = PaktSerializer.Deserialize<SimpleConfig>(
            pakt, TestSerializerContext.Default);

        Assert.Equal("sync-test", result.Name);
        Assert.Equal(99, result.Version);
        Assert.False(result.Debug);
    }

    // ═══════════════════ Out-of-order via stream ═══════════════════

    [Fact]
    public async Task SimpleConfig_OutOfOrder_Stream()
    {
        byte[] pakt = "debug:bool = false\nname:str = 'test'\nversion:int = 7"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 4);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("test", result.Name);
        Assert.Equal(7, result.Version);
        Assert.False(result.Debug);
    }

    // ═══════════════════ Large buffer (no refill needed) ═══════════════════

    [Fact]
    public async Task SimpleConfig_Stream_LargeBuffer()
    {
        byte[] pakt = "name:str = 'big-buf'\nversion:int = 1\ndebug:bool = true"u8.ToArray();
        using var stream = new MemoryStream(pakt);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("big-buf", result.Name);
        Assert.Equal(1, result.Version);
        Assert.True(result.Debug);
    }

    // ═══════════════════ Map via stream ═══════════════════

    [Fact]
    public async Task ConfigWithMap_Stream_ChunkSize8()
    {
        byte[] pakt = "name:str = 'map-test'\nages:<str = int> = <'alice' = 30 'bob' = 25>"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 8);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<ConfigWithMap>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("map-test", result.Name);
        Assert.Equal(30, result.Ages["alice"]);
        Assert.Equal(25, result.Ages["bob"]);
    }

    [Fact]
    public async Task ConfigWithMap_Stream_ChunkSize1()
    {
        byte[] pakt = "name:str = 'tiny'\nages:<str = int> = <'x' = 1>"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 1);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<ConfigWithMap>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("tiny", result.Name);
        Assert.Equal(1, result.Ages["x"]);
    }

    // ═══════════════════ Streaming EOF (no close delimiter) ═══════════════════

    [Fact]
    public async Task ConfigWithList_StreamingEof_Stream()
    {
        // ~[ without ] — EOF terminates the collection
        byte[] pakt = "name:str = 'eof'\nscores:[int] = ~[10 20 30"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 8);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<ConfigWithList>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("eof", result.Name);
        Assert.Equal([10, 20, 30], result.Scores);
    }

    // ═══════════════════ Policies via stream ═══════════════════

    [Fact]
    public async Task DuplicateStatement_LastWins_Stream()
    {
        byte[] pakt = "name:str = 'first'\nversion:int = 1\ndebug:bool = false\nname:str = 'second'"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 8);
        var ct = TestContext.Current.CancellationToken;
        var opts = new PaktSerializationOptions { DuplicateStatements = DuplicatePolicy.LastWins };

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, options: opts, ct: ct);

        Assert.Equal("second", result.Name);
    }

    [Fact]
    public async Task DuplicateStatement_FirstWins_Stream()
    {
        byte[] pakt = "name:str = 'first'\nversion:int = 1\ndebug:bool = false\nname:str = 'second'"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 8);
        var ct = TestContext.Current.CancellationToken;
        var opts = new PaktSerializationOptions { DuplicateStatements = DuplicatePolicy.FirstWins };

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, options: opts, ct: ct);

        Assert.Equal("first", result.Name);
    }

    [Fact]
    public async Task UnknownStatement_Skipped_Stream()
    {
        byte[] pakt = "name:str = 'known'\nunknown:str = 'skip-me'\nversion:int = 5\ndebug:bool = true"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 4);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("known", result.Name);
        Assert.Equal(5, result.Version);
        Assert.True(result.Debug);
    }

    // ═══════════════════ Memory path via PaktSerializer ═══════════════════

    [Fact]
    public void ConfigWithList_Memory_ViaSerializer()
    {
        byte[] pakt = "name:str = 'mem'\nscores:[int] = [1 2 3]"u8.ToArray();

        var result = PaktSerializer.Deserialize<ConfigWithList>(
            pakt, TestSerializerContext.Default);

        Assert.Equal("mem", result.Name);
        Assert.Equal([1, 2, 3], result.Scores);
    }

    [Fact]
    public void ConfigWithMap_Memory_ViaSerializer()
    {
        byte[] pakt = "name:str = 'mem'\nages:<str = int> = <'a' = 1>"u8.ToArray();

        var result = PaktSerializer.Deserialize<ConfigWithMap>(
            pakt, TestSerializerContext.Default);

        Assert.Equal("mem", result.Name);
        Assert.Equal(1, result.Ages["a"]);
    }

    // ═══════════════════ Additional chunk sizes ═══════════════════

    [Fact]
    public async Task SimpleConfig_Stream_ChunkSize3()
    {
        byte[] pakt = "name:str = 'odd'\nversion:int = 7\ndebug:bool = false"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 3);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("odd", result.Name);
        Assert.Equal(7, result.Version);
        Assert.False(result.Debug);
    }

    [Fact]
    public async Task SimpleConfig_Stream_ChunkSize16()
    {
        byte[] pakt = "name:str = 'med'\nversion:int = 16\ndebug:bool = true"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 16);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<SimpleConfig>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("med", result.Name);
        Assert.Equal(16, result.Version);
        Assert.True(result.Debug);
    }

    [Fact]
    public async Task ConfigWithList_ClosedList_Stream_ChunkSize1()
    {
        byte[] pakt = "name:str = 'tiny'\nscores:[int] = [1 2]"u8.ToArray();
        using var stream = new ThrottledStream(pakt, chunkSize: 1);
        var ct = TestContext.Current.CancellationToken;

        var result = await PaktSerializer.DeserializeAsync<ConfigWithList>(
            stream, TestSerializerContext.Default, ct: ct);

        Assert.Equal("tiny", result.Name);
        Assert.Equal([1, 2], result.Scores);
    }
}
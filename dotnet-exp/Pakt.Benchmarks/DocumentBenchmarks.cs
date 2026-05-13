using System.Globalization;
using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Pakt.Benchmarks;

/// <summary>
/// PAKT unit vs JSON document — configuration/document style data.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class DocumentBenchmarks
{
    private byte[] _paktSmall = [];
    private byte[] _jsonSmall = [];
    private byte[] _paktWide = [];
    private byte[] _jsonWide = [];
    private byte[] _paktNested = [];
    private byte[] _jsonNested = [];
    private byte[] _paktCollections = [];
    private byte[] _jsonCollections = [];

    [GlobalSetup]
    public void Setup()
    {
        _paktSmall = GeneratePaktSmall();
        _jsonSmall = GenerateJsonSmall();
        _paktWide = GeneratePaktWide();
        _jsonWide = GenerateJsonWide();
        _paktNested = GeneratePaktNested();
        _jsonNested = GenerateJsonNested();
        _paktCollections = GeneratePaktCollections();
        _jsonCollections = GenerateJsonCollections();
    }

    // ── Small: 10-field struct ──────────────────────────────────────

    [Benchmark(Description = "PAKT small (10 fields)")]
    public async Task<int> PaktSmall()
        => await DrainPakt(_paktSmall).ConfigureAwait(false);

    [Benchmark(Baseline = true, Description = "JSON small (10 fields)")]
    public int JsonSmall()
        => TokenizeJson(_jsonSmall);

    // ── Wide: 100 scalar statements ─────────────────────────────────

    [Benchmark(Description = "PAKT wide (100 stmts)")]
    public async Task<int> PaktWide()
        => await DrainPakt(_paktWide).ConfigureAwait(false);

    [Benchmark(Description = "JSON wide (100 props)")]
    public int JsonWide()
        => TokenizeJson(_jsonWide);

    // ── Nested: 3-level struct ──────────────────────────────────────

    [Benchmark(Description = "PAKT nested (3 levels)")]
    public async Task<int> PaktNested()
        => await DrainPakt(_paktNested).ConfigureAwait(false);

    [Benchmark(Description = "JSON nested (3 levels)")]
    public int JsonNested()
        => TokenizeJson(_jsonNested);

    // ── Collections: list + map ─────────────────────────────────────

    [Benchmark(Description = "PAKT collections")]
    public async Task<int> PaktCollections()
        => await DrainPakt(_paktCollections).ConfigureAwait(false);

    [Benchmark(Description = "JSON collections")]
    public int JsonCollections()
        => TokenizeJson(_jsonCollections);

    // ── Helpers ─────────────────────────────────────────────────────

    private static async Task<int> DrainPakt(byte[] data)
    {
        PipeReader pipe = PipeReader.Create(new ReadOnlySequence<byte>(data));
        PaktReader reader = PaktReader.Create(pipe);
        int count = 0;

        await reader.DrainAsync((scoped in PaktEvent evt) =>
        {
            count++;
            return PaktReader.HandlerResult.Continue;
        }).ConfigureAwait(false);

        return count;
    }

    private static int TokenizeJson(byte[] data)
    {
        Utf8JsonReader jsonReader = new(data);
        int count = 0;
        while (jsonReader.Read())
            count++;
        return count;
    }

    // ── Data generators ─────────────────────────────────────────────

    private static byte[] GeneratePaktSmall()
    {
        return Encoding.UTF8.GetBytes("""
            doc:{name:str version:int debug:bool rate:float host:str port:int max_retry:int timeout:int verbose:bool label:str} = {
                'my-app' 42 true 3.14 'localhost' 8080 3 30 false 'production'
            }
            """);
    }

    private static byte[] GenerateJsonSmall()
    {
        return Encoding.UTF8.GetBytes("""
            {"name":"my-app","version":42,"debug":true,"rate":3.14,"host":"localhost","port":8080,"max_retry":3,"timeout":30,"verbose":false,"label":"production"}
            """);
    }

    private static byte[] GeneratePaktWide()
    {
        StringBuilder sb = new();
        for (int i = 0; i < 100; i++)
            sb.AppendLine(CultureInfo.InvariantCulture, $"field_{i:D3}:int = {i * 17}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] GenerateJsonWide()
    {
        StringBuilder sb = new();
        sb.Append('{');
        for (int i = 0; i < 100; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"\"field_{i:D3}\":{i * 17}");
        }

        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] GeneratePaktNested()
    {
        return Encoding.UTF8.GetBytes(
            "config:{server:{host:str port:int tls:{enabled:bool cert:str key:str}} db:{host:str port:int name:str pool:{min:int max:int timeout:int}} cache:{host:str port:int ttl:int}} = { { 'api.example.com' 443 { true '/etc/ssl/cert.pem' '/etc/ssl/key.pem' } } { 'db.internal' 5432 'myapp' { 5 50 30 } } { 'cache.internal' 6379 3600 } }");
    }

    private static byte[] GenerateJsonNested()
    {
        return Encoding.UTF8.GetBytes(
            """{"server":{"host":"api.example.com","port":443,"tls":{"enabled":true,"cert":"/etc/ssl/cert.pem","key":"/etc/ssl/key.pem"}},"db":{"host":"db.internal","port":5432,"name":"myapp","pool":{"min":5,"max":50,"timeout":30}},"cache":{"host":"cache.internal","port":6379,"ttl":3600}}""");
    }

    private static byte[] GeneratePaktCollections()
    {
        StringBuilder sb = new();
        sb.AppendLine("tags:[str] = ['alpha' 'beta' 'gamma' 'delta' 'epsilon']");
        sb.AppendLine("scores:[int] = [95 87 92 78 88 91 85 93 76 99]");
        sb.AppendLine("headers:<str => str> = <'content-type' => 'application/json' 'accept' => 'text/html' 'x-request-id' => 'abc-123'>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] GenerateJsonCollections()
    {
        return Encoding.UTF8.GetBytes("""
            {"tags":["alpha","beta","gamma","delta","epsilon"],"scores":[95,87,92,78,88,91,85,93,76,99],"headers":{"content-type":"application/json","accept":"text/html","x-request-id":"abc-123"}}
            """);
    }
}

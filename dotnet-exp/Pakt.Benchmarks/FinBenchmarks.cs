using System.Globalization;
using System.Text;

using BenchmarkDotNet.Attributes;

namespace Pakt.Benchmarks;

/// <summary>
/// Financial trade dataset — heavy on typed numerics (ts, uuid, dec, int).
/// 10 fields per entry: timestamp, ticker, side, quantity, price, fees, filled, venue, order_id, tags.
/// This workload stresses numeric scanning and classification.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class FinBenchmarks
{
    [Params(1_000, 10_000)]
    public int TradeCount { get; set; }

    private byte[] _paktBytes = [];

    [GlobalSetup]
    public void Setup()
    {
        _paktBytes = GenerateFinPakt(TradeCount);
    }

    [Benchmark(Baseline = true, Description = "PAKT raw decode")]
    public int RawDecode()
    {
        var seq = new System.Buffers.ReadOnlySequence<byte>(_paktBytes);
        var reader = new PaktSequenceReader(seq, isFinalBlock: true);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark(Description = "PAKT validating decode")]
    public int ValidatingDecode()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_paktBytes);
        int count = 0;
        while (reader.Read()) count++;
        return count;
    }

    [Benchmark(Description = "PAKT materialize")]
    public int Materialize()
    {
        var reader = new PaktValidatingReader((ReadOnlyMemory<byte>)_paktBytes);
        int count = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case PaktTokenType.String:
                    _ = reader.GetString();
                    count++;
                    break;
                case PaktTokenType.Int:
                    _ = reader.GetInt64();
                    count++;
                    break;
                case PaktTokenType.Decimal:
                    _ = reader.GetDecimal();
                    count++;
                    break;
                case PaktTokenType.Bool:
                    _ = reader.GetBool();
                    count++;
                    break;
                case PaktTokenType.Uuid:
                    _ = reader.GetGuid();
                    count++;
                    break;
                case PaktTokenType.Timestamp:
                    _ = reader.GetTimestamp();
                    count++;
                    break;
                case PaktTokenType.Atom:
                    _ = reader.GetAtom();
                    count++;
                    break;
            }
        }
        return count;
    }

    private static byte[] GenerateFinPakt(int n)
    {
        var rng = new Random(77);

        string[] tickers = ["AAPL", "GOOG", "MSFT", "AMZN", "NVDA", "META", "TSLA", "JPM", "V", "UNH"];
        string[] venues = ["NYSE", "NASDAQ", "BATS", "IEX", "EDGX", "MEMX"];
        string[] tagPool = ["algo", "manual", "dark-pool", "pre-market", "block", "sweep"];

        var baseTime = new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.FromHours(-5));
        var pb = new StringBuilder();

        pb.AppendLine("account:str = 'ACCT-7734-PRIME'");
        pb.AppendLine(CultureInfo.InvariantCulture, $"as-of:ts = {baseTime.AddSeconds(n * 3).ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");

        pb.AppendLine("trades:[{timestamp:ts ticker:str quantity:int price:dec fees:dec filled:bool venue:str order-id:uuid}] = ~[");

        for (int i = 0; i < n; i++)
        {
            var ticker = tickers[rng.Next(tickers.Length)];
            _ = rng.NextDouble(); // consume side rng
            var qty = rng.Next(9900) + 100;
            var price = rng.Next(400) + 10 + rng.Next(100) / 100m;
            var fees = (rng.Next(500) + 1) / 100m;
            var filled = rng.NextDouble() < 0.92;
            var venue = venues[rng.Next(venues.Length)];
            var orderId = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            var ts = baseTime.AddSeconds(i * 3 + rng.Next(3)).ToUniversalTime();

            pb.AppendLine(CultureInfo.InvariantCulture,
                $"    {{ {ts:yyyy-MM-ddTHH:mm:ssZ} '{ticker}' {qty} {price:0.00} {fees:0.00} {(filled ? "true" : "false")} '{venue}' {orderId} }}");
        }

        return Encoding.UTF8.GetBytes(pb.ToString());
    }
}

using BotCripto.Models;

namespace BotCripto.Strategies;

// Strategia SMA 200: long quando il prezzo rompe al rialzo la media mobile semplice a 200 periodi,
// con candela rialzista di conferma e mercato (SPY) sopra la propria SMA 200.
public class Sma200Strategy
{
    public const int SmaPeriod = 200;
    private static decimal Env(string name, decimal def) =>
        decimal.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

    private static readonly int SlopeLookback = (int)Env("SMA_SLOPE", 20);          // la SMA200 deve essere in salita rispetto a N candele fa
    private static readonly decimal StopBufferBelowSma = Env("SMA_STOP", 0.75m);   // stop sotto il minimo tra SMA200 e minimo della candela
    private static readonly bool AllowShorts = Env("SMA_SHORTS", 0) != 0;
    private static readonly decimal VolumeMultiplier = Env("SMA_VOL", 1.5m);         // 0 = filtro volume disattivato
    private static readonly decimal MinBodyStrength = Env("SMA_BODY", 0.5m);
    public static readonly int MaxOpenPositions = (int)Env("SMA_MAXPOS", 20);          // 0 = nessun limite
    private static readonly decimal CrashDrop = Env("SMA_CRASH", 0);                    // 0 = disattivato: blocca i nuovi ingressi se SPY è sceso di oltre X dal massimo recente
    private static readonly int CrashDays = (int)Env("SMA_CRASHDAYS", 10);

    // Regime di mercato: true se l'indice di mercato (SPY) chiude sopra la sua SMA200, false se sotto,
    // null se i dati non sono sufficienti.
    public static bool? IsMarketBullish(List<Candle> marketCandles)
    {
        var sma = TechnicalIndicators.CalculateSMA(marketCandles.Select(c => c.Close).ToList(), SmaPeriod);
        if (sma.Count == 0)
            return null;

        // Filtro crollo: se SPY è sceso di oltre CrashDrop dal massimo degli ultimi CrashDays giorni, regime "sconosciuto" (nessun nuovo segnale)
        if (CrashDrop > 0)
        {
            var recentHigh = marketCandles.TakeLast(CrashDays).Max(c => c.Close);
            if (marketCandles.Last().Close < recentHigh * (1 - CrashDrop))
                return null;
        }
        return marketCandles.Last().Close > sma.Last();
    }

    // Long e short: long solo con mercato rialzista, short solo con mercato ribassista; se il regime è sconosciuto il segnale viene scartato.
    public AnalysisResult Analyze(string symbol, List<Candle> candles, bool? marketBullish)
    {
        var result = new AnalysisResult
        {
            Symbol = symbol,
            StrategyName = "SMA 200 Breakout",
            IsSignal = false,
            Signal = "No Signal",
            AnalysisTime = DateTime.UtcNow
        };

        if (candles.Count < SmaPeriod + SlopeLookback)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var closes = candles.Select(c => c.Close).ToList();
        var sma = TechnicalIndicators.CalculateSMA(closes, SmaPeriod);
        if (sma.Count < SlopeLookback + 1)
        {
            result.Rejection = RejectionReason.InsufficientData;
            return result;
        }

        var lastCandle = candles.Last();
        var currentPrice = lastCandle.Close;
        var prevPrice = candles[^2].Close;
        var lastSma = sma[^1];
        var prevSma = sma[^2];
        var slopeSma = sma[^(SlopeLookback + 1)];

        result.CurrentPrice = currentPrice;
        result.Indicators["SMA200"] = lastSma;

        // 1) Rottura della SMA200: al rialzo (long) o al ribasso (short)
        bool isLong = prevPrice <= prevSma && currentPrice > lastSma;
        bool isShort = AllowShorts && prevPrice >= prevSma && currentPrice < lastSma;
        if (!isLong && !isShort)
        {
            result.Signal = "Rejected - No SMA200 breakout";
            result.Rejection = RejectionReason.NoBreakout;
            return result;
        }

        // 2) SMA200 nella stessa direzione del segnale (in salita per long, in discesa per short)
        if (isLong ? lastSma <= slopeSma : lastSma >= slopeSma)
        {
            result.Signal = isLong ? "Rejected - SMA200 not rising" : "Rejected - SMA200 not falling";
            result.Rejection = RejectionReason.TrendNotAligned;
            return result;
        }

        // 3) Candela di conferma nella direzione del segnale con corpo solido (>= 50% del range)
        var body = isLong ? lastCandle.Close - lastCandle.Open : lastCandle.Open - lastCandle.Close;
        var range = lastCandle.High - lastCandle.Low;
        var bodyStrength = range > 0 ? body / range : 0;
        if (body <= 0 || bodyStrength < MinBodyStrength)
        {
            result.Signal = isLong ? "Rejected - Last candle not bullish enough" : "Rejected - Last candle not bearish enough";
            result.Rejection = RejectionReason.CandleNotStrong;
            return result;
        }
        result.Indicators["CandleBodyStrength"] = bodyStrength;

        // 3b) Filtro volume (opzionale): volume dell'ultima candela >= N x media a 20 giorni
        if (VolumeMultiplier > 0)
        {
            var avgVolume = candles.TakeLast(20).Average(c => c.Volume);
            if (avgVolume <= 0 || lastCandle.Volume < avgVolume * VolumeMultiplier)
            {
                result.Signal = "Rejected - Volume insufficient";
                result.Rejection = RejectionReason.VolumeInsufficient;
                return result;
            }
        }

        // 4) Filtro regime di mercato: long solo con SPY sopra SMA200, short solo con SPY sotto
        if (marketBullish == null || marketBullish != isLong)
        {
            result.Signal = "Rejected - Against market regime";
            result.Rejection = RejectionReason.AgainstMarketRegime;
            return result;
        }

        result.IsSignal = true;
        if (isLong)
        {
            result.Signal = "🟢 BUY - SMA 200 Breakout";
            result.Indicators["StopLoss"] = Math.Min(lastSma, lastCandle.Low) * StopBufferBelowSma;
        }
        else
        {
            result.Signal = "🔴 SELL - SMA 200 Breakdown";
            result.Indicators["StopLoss"] = Math.Max(lastSma, lastCandle.High) * (2m - StopBufferBelowSma);
        }
        result.Indicators["SignalStrength"] = Math.Min(1m, bodyStrength);

        return result;
    }
}

using AiyoPerps.Core;
using AiyoPerps.Models;
using System;
using System.Globalization;

namespace AiyoPerps.Services;

internal static class CandleStatusFormatter
{
    private const int LivePrefixWidth = 4;
    private const int HoverPrefixWidth = 11;
    private const int PriceFieldWidth = 10;
    private const int VolumeFieldWidth = 11;

    public static string FormatLive(Candle candle)
    {
        return Build(
            candle.Interval.ToString(),
            LivePrefixWidth,
            candle.Open,
            candle.High,
            candle.Low,
            candle.Close,
            candle.Volume);
    }

    public static string FormatHover(CandleViewPoint candle)
    {
        return Build(
            candle.OpenTime.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            HoverPrefixWidth,
            candle.Open,
            candle.High,
            candle.Low,
            candle.Close,
            null);
    }

    private static string Build(
        string prefix,
        int prefixWidth,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        decimal? volume)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix.PadRight(prefixWidth)} {FormatField("O", open, PriceFieldWidth)} {FormatField("H", high, PriceFieldWidth)} {FormatField("L", low, PriceFieldWidth)} {FormatField("C", close, PriceFieldWidth)} {FormatField("V", volume, VolumeFieldWidth)}");
    }

    private static string FormatField(string label, decimal value, int width)
    {
        return $"{label}:{Trim(value).PadRight(width)}";
    }

    private static string FormatField(string label, decimal? value, int width)
    {
        return $"{label}:{(value.HasValue ? Trim(value.Value) : "-").PadRight(width)}";
    }

    private static string Trim(decimal value, int maxDecimals = 8)
    {
        var decimals = Math.Max(0, maxDecimals);
        var rounded = decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (decimals == 0)
        {
            var whole = rounded.ToString("0", CultureInfo.InvariantCulture);
            return whole == "-0" ? "0" : whole;
        }

        var text = rounded.ToString($"0.{new string('#', decimals)}", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }
}

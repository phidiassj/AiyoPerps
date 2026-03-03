using AiyoPerps.Models;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AiyoPerps.Controls;

public sealed class CandleChartControl : Control
{
    private const double PriceAxisWidth = 60;
    private const double TimeAxisHeight = 26;
    private const double PlotPadding = 6;
    private const double RightEdgeGap = 14;
    private const decimal AxisSnapUnit = 5m;
    private const int MinVisibleCandles = 20;
    private const int MaxVisibleCandles = 400;

    private int _visibleCandles = 120;
    private int _hoverVisibleIndex = -1;
    private Point? _hoverPoint;

    public static readonly StyledProperty<IReadOnlyList<CandleViewPoint>?> CandlesProperty =
        AvaloniaProperty.Register<CandleChartControl, IReadOnlyList<CandleViewPoint>?>(nameof(Candles));

    public static readonly StyledProperty<string?> HoverCandleStatusProperty =
        AvaloniaProperty.Register<CandleChartControl, string?>(
            nameof(HoverCandleStatus),
            defaultBindingMode: BindingMode.TwoWay);

    static CandleChartControl()
    {
        AffectsRender<CandleChartControl>(CandlesProperty, HoverCandleStatusProperty);
    }

    public IReadOnlyList<CandleViewPoint>? Candles
    {
        get => GetValue(CandlesProperty);
        set => SetValue(CandlesProperty, value);
    }

    public string? HoverCandleStatus
    {
        get => GetValue(HoverCandleStatusProperty);
        set => SetValue(HoverCandleStatusProperty, value);
    }

    public CandleChartControl()
    {
        Focusable = true;
        ClipToBounds = true;
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChangedRouted, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        PointerWheelChanged += OnPointerWheelChangedDirect;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        context.DrawRectangle(Brushes.Transparent, null, Bounds);

        if (!TryBuildRenderState(Candles, Bounds, out var state))
        {
            return;
        }

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#1E4B5C")), 1);
        var upPen = new Pen(new SolidColorBrush(Color.Parse("#39C7A5")), 1);
        var downPen = new Pen(new SolidColorBrush(Color.Parse("#E05A73")), 1);
        var upBrush = new SolidColorBrush(Color.Parse("#39C7A5"));
        var downBrush = new SolidColorBrush(Color.Parse("#E05A73"));
        var axisTextBrush = new SolidColorBrush(Color.Parse("#84AFC0"));
        var axisLinePen = new Pen(new SolidColorBrush(Color.Parse("#2A5D73")), 1);

        DrawPriceGridAndAxis(context, state.PlotArea, state.PriceAxisArea, state.MinPrice, state.MaxPrice, state.PriceTickStep, gridPen, axisLinePen, axisTextBrush);

        for (var i = 0; i < state.Visible.Count; i++)
        {
            var candle = state.Visible[i];
            var x = GetCandleCenterX(state.PlotArea, state.CandleStep, i);

            var yHigh = Map(candle.High, state.MinPrice, state.MaxPrice, state.PlotArea);
            var yLow = Map(candle.Low, state.MinPrice, state.MaxPrice, state.PlotArea);
            var yOpen = Map(candle.Open, state.MinPrice, state.MaxPrice, state.PlotArea);
            var yClose = Map(candle.Close, state.MinPrice, state.MaxPrice, state.PlotArea);

            var isUp = candle.Close >= candle.Open;
            var pen = isUp ? upPen : downPen;
            var brush = isUp ? upBrush : downBrush;

            context.DrawLine(pen, new Point(x, yHigh), new Point(x, yLow));

            var top = Math.Min(yOpen, yClose);
            var bodyHeight = Math.Max(1.0, Math.Abs(yClose - yOpen));
            var body = new Rect(x - (state.CandleWidth / 2.0), top, state.CandleWidth, bodyHeight);
            context.DrawRectangle(brush, null, body);
        }

        DrawTimeAxis(context, state.Visible, state.PlotArea, state.TimeAxisArea, state.CandleStep, axisLinePen, axisTextBrush);
        DrawCurrentPriceMarker(context, state);
        DrawCrosshair(context, state);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHoverState(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHoverState();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!e.Handled)
        {
            HandleZoom(e);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    private void OnPointerWheelChangedRouted(object? sender, PointerWheelEventArgs e)
    {
        if (!e.Handled)
        {
            HandleZoom(e);
        }
    }

    private void OnPointerWheelChangedDirect(object? sender, PointerWheelEventArgs e)
    {
        if (!e.Handled)
        {
            HandleZoom(e);
        }
    }

    private void HandleZoom(PointerWheelEventArgs e)
    {
        var candles = Candles;
        if (candles is null || candles.Count == 0)
        {
            return;
        }

        var delta = Math.Abs(e.Delta.Y) > 0.0001 ? e.Delta.Y : e.Delta.X;
        var maxAvailable = Math.Max(MinVisibleCandles, candles.Count);
        var currentVisible = Math.Clamp(_visibleCandles, MinVisibleCandles, maxAvailable);
        var nextVisible = currentVisible;

        if (delta > 0)
        {
            nextVisible = Math.Max(MinVisibleCandles, (int)Math.Floor(currentVisible * 0.85));
        }
        else if (delta < 0)
        {
            nextVisible = Math.Min(maxAvailable, (int)Math.Ceiling(currentVisible * 1.15));
        }

        nextVisible = Math.Clamp(nextVisible, MinVisibleCandles, maxAvailable);
        if (nextVisible != currentVisible)
        {
            _visibleCandles = nextVisible;
            if (_hoverPoint.HasValue)
            {
                UpdateHoverState(_hoverPoint.Value);
            }

            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void UpdateHoverState(Point pointer)
    {
        if (!TryBuildRenderState(Candles, Bounds, out var state))
        {
            ClearHoverState();
            return;
        }

        if (!state.PlotArea.Contains(pointer))
        {
            ClearHoverState();
            return;
        }

        var x = Math.Clamp(pointer.X, state.PlotArea.Left, state.PlotArea.Right - state.RightGap);
        var y = Math.Clamp(pointer.Y, state.PlotArea.Top, state.PlotArea.Bottom);
        var index = (int)Math.Round((x - state.PlotArea.X - (state.CandleStep / 2.0)) / state.CandleStep);
        index = Math.Clamp(index, 0, state.Visible.Count - 1);

        _hoverPoint = new Point(x, y);
        _hoverVisibleIndex = index;

        var candle = state.Visible[index];
        HoverCandleStatus = FormatHoverStatus(candle);
        InvalidateVisual();
    }

    private void ClearHoverState()
    {
        if (_hoverVisibleIndex == -1 && _hoverPoint is null && string.IsNullOrWhiteSpace(HoverCandleStatus))
        {
            return;
        }

        _hoverVisibleIndex = -1;
        _hoverPoint = null;
        HoverCandleStatus = null;
        InvalidateVisual();
    }

    private static string FormatHoverStatus(CandleViewPoint candle)
    {
        return $"{candle.OpenTime.ToLocalTime():MM-dd HH:mm}   O:{FormatNumber(candle.Open)}   H:{FormatNumber(candle.High)}   L:{FormatNumber(candle.Low)}   C:{FormatNumber(candle.Close)}";
    }

    private bool TryBuildRenderState(IReadOnlyList<CandleViewPoint>? candles, Rect bounds, out RenderState state)
    {
        state = default;
        if (candles is null || candles.Count == 0)
        {
            return false;
        }

        _visibleCandles = Math.Clamp(_visibleCandles, MinVisibleCandles, MaxVisibleCandles);
        var visibleCount = Math.Min(_visibleCandles, candles.Count);
        var visible = candles.TakeLast(visibleCount).ToList();

        var plotArea = new Rect(
            PlotPadding,
            PlotPadding,
            Math.Max(0, bounds.Width - PriceAxisWidth - PlotPadding * 2),
            Math.Max(0, bounds.Height - TimeAxisHeight - PlotPadding * 2));

        if (plotArea.Width <= 0 || plotArea.Height <= 0)
        {
            return false;
        }

        var priceAxisArea = new Rect(plotArea.Right, plotArea.Y, PriceAxisWidth, plotArea.Height);
        var timeAxisArea = new Rect(plotArea.X, plotArea.Bottom, plotArea.Width, TimeAxisHeight);
        var drawableWidth = Math.Max(1, plotArea.Width - RightEdgeGap);
        var candleStep = drawableWidth / visible.Count;
        var candleWidth = Math.Clamp(candleStep * 0.72, 2.0, 14.0);

        var (minPrice, maxPrice, priceTickStep) = BuildPriceRange(visible);

        state = new RenderState(
            visible,
            plotArea,
            priceAxisArea,
            timeAxisArea,
            minPrice,
            maxPrice,
            priceTickStep,
            candleStep,
            candleWidth,
            RightEdgeGap);
        return true;
    }

    private static (decimal MinPrice, decimal MaxPrice, decimal PriceTickStep) BuildPriceRange(IReadOnlyList<CandleViewPoint> visible)
    {
        var rawMax = visible.Max(x => x.High);
        var rawMin = visible.Min(x => x.Low);
        var range = Math.Max(1m, rawMax - rawMin);
        var pad = Math.Max(AxisSnapUnit, range * 0.05m);

        var minCandidate = rawMin - pad;
        var maxCandidate = rawMax + pad;

        var rawTickStep = Math.Max(AxisSnapUnit, (maxCandidate - minCandidate) / 5m);
        var tickStep = decimal.Ceiling(rawTickStep / AxisSnapUnit) * AxisSnapUnit;
        tickStep = Math.Max(AxisSnapUnit, tickStep);

        var min = decimal.Floor(minCandidate / tickStep) * tickStep;
        var max = decimal.Ceiling(maxCandidate / tickStep) * tickStep;

        if (max <= min)
        {
            max = min + tickStep * 2;
        }

        if (max - min < tickStep * 2)
        {
            max = min + tickStep * 2;
        }

        return (min, max, tickStep);
    }

    private static void DrawPriceGridAndAxis(
        DrawingContext context,
        Rect plotArea,
        Rect priceAxisArea,
        decimal min,
        decimal max,
        decimal tickStep,
        Pen gridPen,
        Pen axisLinePen,
        IBrush textBrush)
    {
        context.DrawLine(axisLinePen, new Point(plotArea.Right, plotArea.Top), new Point(plotArea.Right, plotArea.Bottom));

        var guard = 0;
        for (var price = min; price <= max + (tickStep / 2m) && guard < 200; price += tickStep, guard++)
        {
            var y = Map(price, min, max, plotArea);
            context.DrawLine(gridPen, new Point(plotArea.Left, y), new Point(plotArea.Right, y));

            var text = new FormattedText(
                FormatNumber(price),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                textBrush);

            context.DrawText(text, new Point(priceAxisArea.X + 4, y - (text.Height / 2)));
        }
    }

    private static void DrawTimeAxis(
        DrawingContext context,
        IReadOnlyList<CandleViewPoint> visible,
        Rect plotArea,
        Rect timeAxisArea,
        double candleStep,
        Pen axisLinePen,
        IBrush textBrush)
    {
        context.DrawLine(axisLinePen, new Point(plotArea.Left, plotArea.Bottom), new Point(plotArea.Right, plotArea.Bottom));

        if (visible.Count == 0)
        {
            return;
        }

        var targetLabels = Math.Min(6, Math.Max(2, visible.Count / 20));
        var stepIndex = Math.Max(1, visible.Count / targetLabels);
        var span = visible[^1].OpenTime - visible[0].OpenTime;
        var fmt = span.TotalDays >= 1 ? "MM-dd HH:mm" : "HH:mm";

        for (var i = 0; i < visible.Count; i += stepIndex)
        {
            var x = GetCandleCenterX(plotArea, candleStep, i);
            context.DrawLine(axisLinePen, new Point(x, plotArea.Bottom), new Point(x, plotArea.Bottom + 4));

            var label = visible[i].OpenTime.ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                textBrush);

            context.DrawText(text, new Point(x - (text.Width / 2), timeAxisArea.Y + 4));
        }
    }

    private void DrawCrosshair(DrawingContext context, RenderState state)
    {
        if (_hoverVisibleIndex < 0 || _hoverVisibleIndex >= state.Visible.Count || _hoverPoint is null)
        {
            return;
        }

        var candle = state.Visible[_hoverVisibleIndex];
        var x = GetCandleCenterX(state.PlotArea, state.CandleStep, _hoverVisibleIndex);
        var y = Math.Clamp(_hoverPoint.Value.Y, state.PlotArea.Top, state.PlotArea.Bottom);

        var crossPen = new Pen(new SolidColorBrush(Color.Parse("#5EAAC7")), 1, dashStyle: new DashStyle([4, 4], 0));
        context.DrawLine(crossPen, new Point(x, state.PlotArea.Top), new Point(x, state.PlotArea.Bottom));
        context.DrawLine(crossPen, new Point(state.PlotArea.Left, y), new Point(state.PlotArea.Right, y));

        var labelBackground = new SolidColorBrush(Color.Parse("#11303E"));
        var labelBorder = new Pen(new SolidColorBrush(Color.Parse("#5EAAC7")), 1);
        var labelTextBrush = new SolidColorBrush(Color.Parse("#CDEBF6"));

        var priceValue = InverseMap(y, state.MinPrice, state.MaxPrice, state.PlotArea);
        var priceText = new FormattedText(
            FormatNumber(priceValue),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            labelTextBrush);

        var priceRect = new Rect(
            state.PriceAxisArea.X + 2,
            y - (priceText.Height / 2) - 2,
            priceText.Width + 8,
            priceText.Height + 4);
        context.DrawRectangle(labelBackground, labelBorder, priceRect, 3);
        context.DrawText(priceText, new Point(priceRect.X + 4, priceRect.Y + 2));

        var span = state.Visible[^1].OpenTime - state.Visible[0].OpenTime;
        var timeFmt = span.TotalDays >= 1 ? "MM-dd HH:mm" : "HH:mm";
        var timeText = new FormattedText(
            candle.OpenTime.ToLocalTime().ToString(timeFmt, CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            labelTextBrush);

        var timeX = Math.Clamp(
            x - (timeText.Width / 2) - 4,
            state.TimeAxisArea.X,
            state.TimeAxisArea.Right - timeText.Width - 8);

        var timeRect = new Rect(
            timeX,
            state.TimeAxisArea.Y + 3,
            timeText.Width + 8,
            timeText.Height + 4);
        context.DrawRectangle(labelBackground, labelBorder, timeRect, 3);
        context.DrawText(timeText, new Point(timeRect.X + 4, timeRect.Y + 2));
    }

    private static void DrawCurrentPriceMarker(DrawingContext context, RenderState state)
    {
        if (state.Visible.Count == 0)
        {
            return;
        }

        var lastCandle = state.Visible[^1];
        var price = lastCandle.Close;
        var y = Map(price, state.MinPrice, state.MaxPrice, state.PlotArea);
        var markerColor = lastCandle.Close >= lastCandle.Open
            ? Color.Parse("#39C7A5")
            : Color.Parse("#E05A73");
        var markerBrush = new SolidColorBrush(markerColor);
        var markerPen = new Pen(markerBrush, 1, dashStyle: new DashStyle([5, 4], 0));
        var labelTextBrush = new SolidColorBrush(Color.Parse("#D9F0F5"));
        var labelBorder = new Pen(markerBrush, 1);

        context.DrawLine(
            markerPen,
            new Point(state.PlotArea.Left, y),
            new Point(state.PlotArea.Right, y));

        var priceText = new FormattedText(
            FormatNumber(price),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            labelTextBrush);

        var labelRect = new Rect(
            state.PriceAxisArea.X + 2,
            y - (priceText.Height / 2) - 2,
            priceText.Width + 8,
            priceText.Height + 4);

        context.DrawRectangle(markerBrush, labelBorder, labelRect, 3);
        context.DrawText(priceText, new Point(labelRect.X + 4, labelRect.Y + 2));
    }

    private static double GetCandleCenterX(Rect plotArea, double candleStep, int index)
    {
        return plotArea.X + candleStep * index + (candleStep / 2.0);
    }

    private static double Map(decimal value, decimal min, decimal max, Rect area)
    {
        var ratio = (double)((value - min) / (max - min));
        return area.Bottom - ratio * area.Height;
    }

    private static decimal InverseMap(double y, decimal min, decimal max, Rect area)
    {
        if (area.Height <= 0)
        {
            return min;
        }

        var ratio = Math.Clamp((area.Bottom - y) / area.Height, 0, 1);
        return min + (decimal)ratio * (max - min);
    }

    private static string FormatNumber(decimal value, int maxDecimals = 8)
    {
        var decimals = Math.Max(0, maxDecimals);
        var rounded = decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
        var format = decimals == 0 ? "0" : $"0.{new string('#', decimals)}";
        var text = rounded.ToString(format, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private readonly record struct RenderState(
        IReadOnlyList<CandleViewPoint> Visible,
        Rect PlotArea,
        Rect PriceAxisArea,
        Rect TimeAxisArea,
        decimal MinPrice,
        decimal MaxPrice,
        decimal PriceTickStep,
        double CandleStep,
        double CandleWidth,
        double RightGap);
}

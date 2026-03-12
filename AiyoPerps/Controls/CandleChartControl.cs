using AiyoPerps.Models;
using AiyoPerps.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;

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

    private static readonly Typeface AxisTypeface = new("Segoe UI");
    private static readonly IBrush AxisTextBrush = new SolidColorBrush(Color.Parse("#84AFC0"));
    private static readonly IBrush HoverLabelTextBrush = new SolidColorBrush(Color.Parse("#CDEBF6"));
    private static readonly IBrush CurrentPriceLabelTextBrush = new SolidColorBrush(Color.Parse("#D9F0F5"));
    private static readonly IBrush HoverLabelBackgroundBrush = new SolidColorBrush(Color.Parse("#11303E"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#1E4B5C")), 1);
    private static readonly Pen AxisLinePen = new(new SolidColorBrush(Color.Parse("#2A5D73")), 1);
    private static readonly Pen UpPen = new(new SolidColorBrush(Color.Parse("#39C7A5")), 1);
    private static readonly Pen DownPen = new(new SolidColorBrush(Color.Parse("#E05A73")), 1);
    private static readonly IBrush UpBrush = new SolidColorBrush(Color.Parse("#39C7A5"));
    private static readonly IBrush DownBrush = new SolidColorBrush(Color.Parse("#E05A73"));
    private static readonly Pen CrosshairPen = new(new SolidColorBrush(Color.Parse("#5EAAC7")), 1, dashStyle: new DashStyle([4, 4], 0));
    private static readonly Pen HoverLabelBorderPen = new(new SolidColorBrush(Color.Parse("#5EAAC7")), 1);
    private static DateTimeOffset _lastRenderDiagnosticAt;
    private static readonly TimeSpan RenderDiagnosticSampleInterval = TimeSpan.FromSeconds(2);
    private const long SlowRenderThresholdMs = 10;

    private int _visibleCandles = 120;
    private int _hoverVisibleIndex = -1;
    private Point? _hoverPoint;
    private bool _renderStateDirty = true;
    private bool _hasRenderState;
    private IReadOnlyList<CandleViewPoint>? _renderStateCandles;
    private Rect _renderStateBounds;
    private int _renderStateVisibleCandles;
    private RenderState _renderState;

    public static readonly StyledProperty<IReadOnlyList<CandleViewPoint>?> CandlesProperty =
        AvaloniaProperty.Register<CandleChartControl, IReadOnlyList<CandleViewPoint>?>(nameof(Candles));

    public static readonly StyledProperty<string?> HoverCandleStatusProperty =
        AvaloniaProperty.Register<CandleChartControl, string?>(
            nameof(HoverCandleStatus),
            defaultBindingMode: BindingMode.TwoWay);

    static CandleChartControl()
    {
        AffectsRender<CandleChartControl>(CandlesProperty);
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

        var diagnosticSample = ShouldSampleRenderDiagnostic();
        if (diagnosticSample)
        {
            App.Logger.Info("KlineDiag", $"render begin bounds={Bounds.Width:0.##}x{Bounds.Height:0.##}, visibleCandles={_visibleCandles}, hasHover={_hoverPoint.HasValue}");
        }

        var stopwatch = Stopwatch.StartNew();
        context.DrawRectangle(Brushes.Transparent, null, Bounds);

        if (!TryGetRenderState(out var state))
        {
            stopwatch.Stop();
            LogRenderDiagnosticIfNeeded(
                "render end",
                stopwatch.ElapsedMilliseconds,
                diagnosticSample,
                "state=empty");
            return;
        }

        DrawPriceGridAndAxis(context, state);

        for (var i = 0; i < state.VisibleCount; i++)
        {
            var candle = state.GetVisibleCandle(i);
            var x = GetCandleCenterX(state.PlotArea, state.CandleStep, i);

            var yHigh = Map(candle.High, state.MinPrice, state.MaxPrice, state.PlotArea);
            var yLow = Map(candle.Low, state.MinPrice, state.MaxPrice, state.PlotArea);
            var yOpen = Map(candle.Open, state.MinPrice, state.MaxPrice, state.PlotArea);
            var yClose = Map(candle.Close, state.MinPrice, state.MaxPrice, state.PlotArea);

            var isUp = candle.Close >= candle.Open;
            var pen = isUp ? UpPen : DownPen;
            var brush = isUp ? UpBrush : DownBrush;

            context.DrawLine(pen, new Point(x, yHigh), new Point(x, yLow));

            var top = Math.Min(yOpen, yClose);
            var bodyHeight = Math.Max(1.0, Math.Abs(yClose - yOpen));
            var body = new Rect(x - (state.CandleWidth / 2.0), top, state.CandleWidth, bodyHeight);
            context.DrawRectangle(brush, null, body);
        }

        DrawTimeAxis(context, state);
        DrawCurrentPriceMarker(context, state);
        DrawCrosshair(context, state);
        stopwatch.Stop();
        LogRenderDiagnosticIfNeeded(
            "render end",
            stopwatch.ElapsedMilliseconds,
            diagnosticSample,
            $"state=ready, visibleCount={state.VisibleCount}, lastOpen={state.LastVisibleCandle.OpenTime:O}, candleStep={state.CandleStep:0.###}");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CandlesProperty || change.Property == BoundsProperty)
        {
            InvalidateRenderState();
        }
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
            InvalidateRenderState();

            if (_hoverPoint.HasValue)
            {
                UpdateHoverState(_hoverPoint.Value);
            }
            else
            {
                InvalidateVisual();
            }

            e.Handled = true;
        }
    }

    private void UpdateHoverState(Point pointer)
    {
        if (!TryGetRenderState(out var state))
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
        index = Math.Clamp(index, 0, state.VisibleCount - 1);

        var nextPoint = new Point(x, y);
        var hoverIndexChanged = index != _hoverVisibleIndex;
        var hoverPointChanged = !_hoverPoint.HasValue || _hoverPoint.Value != nextPoint;

        _hoverPoint = nextPoint;
        _hoverVisibleIndex = index;

        if (hoverIndexChanged)
        {
            var hoverStatus = FormatHoverStatus(state.GetVisibleCandle(index));
            if (!string.Equals(HoverCandleStatus, hoverStatus, StringComparison.Ordinal))
            {
                HoverCandleStatus = hoverStatus;
            }
        }

        if (hoverIndexChanged || hoverPointChanged)
        {
            InvalidateVisual();
        }
    }

    private void ClearHoverState()
    {
        if (_hoverVisibleIndex == -1 && _hoverPoint is null && string.IsNullOrWhiteSpace(HoverCandleStatus))
        {
            return;
        }

        _hoverVisibleIndex = -1;
        _hoverPoint = null;

        if (!string.IsNullOrWhiteSpace(HoverCandleStatus))
        {
            HoverCandleStatus = null;
        }

        InvalidateVisual();
    }

    private bool TryGetRenderState(out RenderState state)
    {
        var candles = Candles;
        var bounds = Bounds;
        if (!_renderStateDirty &&
            _hasRenderState &&
            ReferenceEquals(_renderStateCandles, candles) &&
            _renderStateBounds == bounds &&
            _renderStateVisibleCandles == _visibleCandles)
        {
            state = _renderState;
            return true;
        }

        if (!TryBuildRenderState(candles, bounds, out state))
        {
            _hasRenderState = false;
            _renderState = default;
            _renderStateCandles = candles;
            _renderStateBounds = bounds;
            _renderStateVisibleCandles = _visibleCandles;
            _renderStateDirty = false;
            return false;
        }

        _renderState = state;
        _renderStateCandles = candles;
        _renderStateBounds = bounds;
        _renderStateVisibleCandles = _visibleCandles;
        _renderStateDirty = false;
        _hasRenderState = true;
        return true;
    }

    private void InvalidateRenderState()
    {
        _renderStateDirty = true;
    }

    private static string FormatHoverStatus(CandleViewPoint candle)
    {
        return CandleStatusFormatter.FormatHover(candle);
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
        var visibleStartIndex = candles.Count - visibleCount;

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
        var candleStep = drawableWidth / visibleCount;
        var candleWidth = Math.Clamp(candleStep * 0.72, 2.0, 14.0);

        var (minPrice, maxPrice, priceTickStep) = BuildPriceRange(candles, visibleStartIndex, visibleCount);

        state = new RenderState(
            candles,
            visibleStartIndex,
            visibleCount,
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

    private static (decimal MinPrice, decimal MaxPrice, decimal PriceTickStep) BuildPriceRange(IReadOnlyList<CandleViewPoint> candles, int startIndex, int count)
    {
        var first = candles[startIndex];
        var rawMax = first.High;
        var rawMin = first.Low;

        for (var i = 1; i < count; i++)
        {
            var candle = candles[startIndex + i];
            if (candle.High > rawMax)
            {
                rawMax = candle.High;
            }

            if (candle.Low < rawMin)
            {
                rawMin = candle.Low;
            }
        }

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

    private static void DrawPriceGridAndAxis(DrawingContext context, RenderState state)
    {
        context.DrawLine(AxisLinePen, new Point(state.PlotArea.Right, state.PlotArea.Top), new Point(state.PlotArea.Right, state.PlotArea.Bottom));

        var guard = 0;
        for (var price = state.MinPrice; price <= state.MaxPrice + (state.PriceTickStep / 2m) && guard < 200; price += state.PriceTickStep, guard++)
        {
            var y = Map(price, state.MinPrice, state.MaxPrice, state.PlotArea);
            context.DrawLine(GridPen, new Point(state.PlotArea.Left, y), new Point(state.PlotArea.Right, y));

            var text = new FormattedText(
                FormatNumber(price),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                11,
                AxisTextBrush);

            context.DrawText(text, new Point(state.PriceAxisArea.X + 4, y - (text.Height / 2)));
        }
    }

    private static void DrawTimeAxis(DrawingContext context, RenderState state)
    {
        context.DrawLine(AxisLinePen, new Point(state.PlotArea.Left, state.PlotArea.Bottom), new Point(state.PlotArea.Right, state.PlotArea.Bottom));

        var targetLabels = Math.Min(6, Math.Max(2, state.VisibleCount / 20));
        var stepIndex = Math.Max(1, state.VisibleCount / targetLabels);
        var span = state.LastVisibleCandle.OpenTime - state.FirstVisibleCandle.OpenTime;
        var fmt = span.TotalDays >= 1 ? "MM-dd HH:mm" : "HH:mm";

        for (var i = 0; i < state.VisibleCount; i += stepIndex)
        {
            var x = GetCandleCenterX(state.PlotArea, state.CandleStep, i);
            context.DrawLine(AxisLinePen, new Point(x, state.PlotArea.Bottom), new Point(x, state.PlotArea.Bottom + 4));

            var label = state.GetVisibleCandle(i).OpenTime.ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);
            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface,
                11,
                AxisTextBrush);

            context.DrawText(text, new Point(x - (text.Width / 2), state.TimeAxisArea.Y + 4));
        }
    }

    private void DrawCrosshair(DrawingContext context, RenderState state)
    {
        if (_hoverVisibleIndex < 0 || _hoverVisibleIndex >= state.VisibleCount || _hoverPoint is null)
        {
            return;
        }

        var candle = state.GetVisibleCandle(_hoverVisibleIndex);
        var x = GetCandleCenterX(state.PlotArea, state.CandleStep, _hoverVisibleIndex);
        var y = Math.Clamp(_hoverPoint.Value.Y, state.PlotArea.Top, state.PlotArea.Bottom);

        context.DrawLine(CrosshairPen, new Point(x, state.PlotArea.Top), new Point(x, state.PlotArea.Bottom));
        context.DrawLine(CrosshairPen, new Point(state.PlotArea.Left, y), new Point(state.PlotArea.Right, y));

        var priceValue = InverseMap(y, state.MinPrice, state.MaxPrice, state.PlotArea);
        var priceText = new FormattedText(
            FormatNumber(priceValue),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            AxisTypeface,
            11,
            HoverLabelTextBrush);

        var priceRect = new Rect(
            state.PriceAxisArea.X + 2,
            y - (priceText.Height / 2) - 2,
            priceText.Width + 8,
            priceText.Height + 4);
        context.DrawRectangle(HoverLabelBackgroundBrush, HoverLabelBorderPen, priceRect, 3);
        context.DrawText(priceText, new Point(priceRect.X + 4, priceRect.Y + 2));

        var span = state.LastVisibleCandle.OpenTime - state.FirstVisibleCandle.OpenTime;
        var timeFmt = span.TotalDays >= 1 ? "MM-dd HH:mm" : "HH:mm";
        var timeText = new FormattedText(
            candle.OpenTime.ToLocalTime().ToString(timeFmt, CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            AxisTypeface,
            11,
            HoverLabelTextBrush);

        var timeX = Math.Clamp(
            x - (timeText.Width / 2) - 4,
            state.TimeAxisArea.X,
            state.TimeAxisArea.Right - timeText.Width - 8);

        var timeRect = new Rect(
            timeX,
            state.TimeAxisArea.Y + 3,
            timeText.Width + 8,
            timeText.Height + 4);
        context.DrawRectangle(HoverLabelBackgroundBrush, HoverLabelBorderPen, timeRect, 3);
        context.DrawText(timeText, new Point(timeRect.X + 4, timeRect.Y + 2));
    }

    private static void DrawCurrentPriceMarker(DrawingContext context, RenderState state)
    {
        var lastCandle = state.LastVisibleCandle;
        var price = lastCandle.Close;
        var y = Map(price, state.MinPrice, state.MaxPrice, state.PlotArea);
        var markerColor = lastCandle.Close >= lastCandle.Open
            ? Color.Parse("#39C7A5")
            : Color.Parse("#E05A73");
        var markerBrush = new SolidColorBrush(markerColor);
        var markerPen = new Pen(markerBrush, 1, dashStyle: new DashStyle([5, 4], 0));
        var labelBorder = new Pen(markerBrush, 1);

        context.DrawLine(
            markerPen,
            new Point(state.PlotArea.Left, y),
            new Point(state.PlotArea.Right, y));

        var priceText = new FormattedText(
            FormatNumber(price),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            AxisTypeface,
            11,
            CurrentPriceLabelTextBrush);

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

    private static bool ShouldSampleRenderDiagnostic()
    {
        if (!App.Logger.IsDevelopment)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastRenderDiagnosticAt < RenderDiagnosticSampleInterval)
        {
            return false;
        }

        _lastRenderDiagnosticAt = now;
        return true;
    }

    private static void LogRenderDiagnosticIfNeeded(string phase, long elapsedMs, bool sampled, string details)
    {
        if (!App.Logger.IsDevelopment)
        {
            return;
        }

        if (!sampled && elapsedMs < SlowRenderThresholdMs)
        {
            return;
        }

        if (!sampled)
        {
            _lastRenderDiagnosticAt = DateTimeOffset.UtcNow;
        }

        App.Logger.Info("KlineDiag", $"{phase} elapsedMs={elapsedMs}, {details}");
    }

    private readonly record struct RenderState(
        IReadOnlyList<CandleViewPoint> Candles,
        int VisibleStartIndex,
        int VisibleCount,
        Rect PlotArea,
        Rect PriceAxisArea,
        Rect TimeAxisArea,
        decimal MinPrice,
        decimal MaxPrice,
        decimal PriceTickStep,
        double CandleStep,
        double CandleWidth,
        double RightGap)
    {
        public CandleViewPoint FirstVisibleCandle => Candles[VisibleStartIndex];
        public CandleViewPoint LastVisibleCandle => Candles[VisibleStartIndex + VisibleCount - 1];

        public CandleViewPoint GetVisibleCandle(int visibleIndex)
        {
            return Candles[VisibleStartIndex + visibleIndex];
        }
    }
}

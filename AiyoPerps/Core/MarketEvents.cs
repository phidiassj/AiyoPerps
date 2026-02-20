using System;
using System.Collections.Generic;

namespace AiyoPerps.Core;

public abstract record MarketEvent(DateTimeOffset Timestamp);

public sealed record TradeTick(DateTimeOffset Timestamp, decimal Price, decimal Size) : MarketEvent(Timestamp);
public sealed record VenueHeartbeat(DateTimeOffset Timestamp, string Detail) : MarketEvent(Timestamp);
public sealed record OrderBookSnapshot(DateTimeOffset Timestamp, IReadOnlyList<(decimal Price, decimal Size)> Asks, IReadOnlyList<(decimal Price, decimal Size)> Bids) : MarketEvent(Timestamp);
public sealed record OrderBookDelta(DateTimeOffset Timestamp, IReadOnlyList<(decimal Price, decimal Size)> Asks, IReadOnlyList<(decimal Price, decimal Size)> Bids) : MarketEvent(Timestamp);

public abstract record AccountEvent(DateTimeOffset Timestamp);
public sealed record OrderAck(DateTimeOffset Timestamp, string ClientOrderId, bool Success, string? Message = null) : AccountEvent(Timestamp);

using System;

namespace AiyoPerps.Models;

public sealed record CandleViewPoint(DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close);

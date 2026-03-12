using System;

namespace AiyoPerps.Models;

public readonly record struct CandleViewPoint(DateTimeOffset OpenTime, decimal Open, decimal High, decimal Low, decimal Close);

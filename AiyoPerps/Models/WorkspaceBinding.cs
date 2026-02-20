using System;

namespace AiyoPerps.Models;

public sealed record WorkspaceBinding(string VenueId, Guid AccountId, string Symbol);

namespace AiyoPerps.Services;

public sealed record SymbolCatalogUpsert(
    string RawSymbol,
    string CanonicalKey,
    string DisplaySymbol,
    string? BaseAsset,
    string? QuoteAsset,
    string? SettleAsset,
    string? ContractType)
{
    public static SymbolCatalogUpsert FromVenueSymbol(
        string venueId,
        string rawSymbol,
        string? baseAsset = null,
        string? quoteAsset = null,
        string? settleAsset = null,
        string? contractType = null)
    {
        var descriptor = SymbolCanonicalizer.Describe(venueId, rawSymbol, baseAsset, quoteAsset, settleAsset, contractType);
        return new SymbolCatalogUpsert(
            descriptor.RawSymbol,
            descriptor.CanonicalKey,
            descriptor.DisplaySymbol,
            descriptor.BaseAsset,
            descriptor.QuoteAsset,
            descriptor.SettleAsset,
            descriptor.ContractType);
    }
}

public sealed record SymbolCatalogEntry(
    string RawSymbol,
    string CanonicalKey,
    string DisplaySymbol,
    string? BaseAsset,
    string? QuoteAsset,
    string? SettleAsset,
    string? ContractType);

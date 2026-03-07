using AiyoPerps.Core;
using AiyoPerps.Models;

namespace AiyoPerps.Services;

public interface IVenueFactory
{
    IPerpVenue Create(AccountProfile account, AccountCredentials credentials);
}

public sealed class VenueFactory(AppLogger logger) : IVenueFactory
{
    private readonly AppLogger _logger = logger;

    public IPerpVenue Create(AccountProfile account, AccountCredentials credentials)
    {
        _logger.Info("VenueFactory", $"Create venue for account={account.DisplayName}, venue={account.VenueId}, env={account.Environment}");

        return account.VenueId switch
        {
            "BitMEX" => new BitMexVenueAdapter(account.Environment, credentials, _logger),
            "Hyperliquid" => new HyperliquidVenueAdapter(account.Environment, credentials, _logger),
            "Aster" => new AsterVenueAdapter(account.Environment, credentials, _logger),
            "GRVT" => new GrvtVenueAdapter(account.Environment, credentials, _logger),
            _ => new FakePerpVenue(account.VenueId)
        };
    }
}

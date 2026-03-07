using AiyoPerps.Models;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class McpSurfaceTests
{
    [Fact]
    public async Task Create_AsterAccount_ReturnsAsterVenueAdapter()
    {
        var factory = new VenueFactory(new AppLogger());
        var account = CreateAccount("Aster");

        await using var venue = factory.Create(account, new AccountCredentials());

        Assert.IsType<AsterVenueAdapter>(venue);
    }

    [Fact]
    public async Task Create_GrvtAccount_ReturnsGrvtVenueAdapter()
    {
        var factory = new VenueFactory(new AppLogger());
        var account = CreateAccount("GRVT");

        await using var venue = factory.Create(account, new AccountCredentials());

        Assert.IsType<GrvtVenueAdapter>(venue);
    }

    [Fact]
    public void BuildMcpTools_AccountVenueEnum_ContainsAsterAndGrvt()
    {
        var buildMcpTools = typeof(LocalApiServer).GetMethod(
            "BuildMcpTools",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(buildMcpTools);

        var tools = buildMcpTools!.Invoke(null, null);
        Assert.NotNull(tools);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var root = document.RootElement;

        var accountsCreate = root.EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "accounts_create");
        var accountsUpdate = root.EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "accounts_update");

        Assert.Contains("Aster", ReadVenueEnum(accountsCreate));
        Assert.Contains("GRVT", ReadVenueEnum(accountsCreate));
        Assert.Contains("Aster", ReadVenueEnum(accountsUpdate));
        Assert.Contains("GRVT", ReadVenueEnum(accountsUpdate));
    }

    [Fact]
    public void BuildMcpTools_ContainsMarketAndBalanceTools()
    {
        var buildMcpTools = typeof(LocalApiServer).GetMethod(
            "BuildMcpTools",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(buildMcpTools);

        var tools = buildMcpTools!.Invoke(null, null);
        Assert.NotNull(tools);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var toolNames = document.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        Assert.Contains("symbols_list", toolNames);
        Assert.Contains("market_data_get", toolNames);
        Assert.Contains("balances_list", toolNames);
        Assert.Contains("connections_list", toolNames);
    }

    private static AccountProfile CreateAccount(string venueId)
        => new()
        {
            VenueId = venueId,
            DisplayName = $"{venueId}-test",
            Environment = "testnet",
            Summary = "test"
        };

    private static string[] ReadVenueEnum(JsonElement tool)
    {
        return tool
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("venueId")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
    }
}

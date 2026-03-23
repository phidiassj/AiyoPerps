using AiyoPerps.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class SymbolCatalogRepository
{
    public IReadOnlyList<SymbolCatalogEntry> GetActiveSymbolEntries(string venueId, string environment)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        return db.Symbols
            .AsNoTracking()
            .Where(x => x.VenueId == venueId && x.Environment == environment && x.IsActive)
            .ToList()
            .OrderByDescending(x => x.LastActivatedAt.HasValue)
            .ThenByDescending(x => x.LastActivatedAt)
            .ThenBy(x => x.DisplaySymbol ?? x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(x => new SymbolCatalogEntry(
                x.Symbol,
                string.IsNullOrWhiteSpace(x.CanonicalKey) ? $"RAW:{x.Symbol}" : x.CanonicalKey!,
                string.IsNullOrWhiteSpace(x.DisplaySymbol) ? SymbolCanonicalizer.Format(x.VenueId, x.Symbol) : x.DisplaySymbol!,
                x.BaseAsset,
                x.QuoteAsset,
                x.SettleAsset,
                x.ContractType))
            .ToList();
    }

    public IReadOnlyList<string> GetActiveSymbols(string venueId, string environment)
    {
        return GetActiveSymbolEntries(venueId, environment)
            .Select(x => x.RawSymbol)
            .ToList();
    }

    public (int Added, int Removed, int Total) ReplaceSymbols(string venueId, string environment, IReadOnlyCollection<SymbolCatalogUpsert> symbols)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();

        var now = DateTimeOffset.UtcNow;
        var normalized = symbols
            .Select(x => SymbolCatalogUpsert.FromVenueSymbol(
                venueId,
                x.RawSymbol,
                x.BaseAsset,
                x.QuoteAsset,
                x.SettleAsset,
                x.ContractType))
            .Where(x => !string.IsNullOrWhiteSpace(x.RawSymbol))
            .GroupBy(x => x.RawSymbol, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();

        var existing = db.Symbols
            .Where(x => x.VenueId == venueId && x.Environment == environment)
            .ToList();

        var oldSet = existing.Where(x => x.IsActive).Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSet = normalized.Select(x => x.RawSymbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var upsertsBySymbol = normalized.ToDictionary(x => x.RawSymbol, StringComparer.OrdinalIgnoreCase);

        foreach (var row in existing)
        {
            row.IsActive = newSet.Contains(row.Symbol);
            row.UpdatedAt = now;
            if (upsertsBySymbol.TryGetValue(row.Symbol, out var upsert))
            {
                ApplyMetadata(row, upsert);
            }
        }

        foreach (var upsert in normalized)
        {
            if (existing.Any(x => string.Equals(x.Symbol, upsert.RawSymbol, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var entity = new SymbolCatalogEntity
            {
                VenueId = venueId,
                Environment = environment,
                Symbol = upsert.RawSymbol,
                IsActive = true,
                UpdatedAt = now,
                LastActivatedAt = null
            };
            ApplyMetadata(entity, upsert);
            db.Symbols.Add(entity);
        }

        db.SaveChanges();

        var added = newSet.Except(oldSet, StringComparer.OrdinalIgnoreCase).Count();
        var removed = oldSet.Except(newSet, StringComparer.OrdinalIgnoreCase).Count();
        return (added, removed, newSet.Count);
    }

    public void MarkActivated(string venueId, string environment, string symbol)
    {
        DbSchemaBootstrapper.EnsureSchema();
        var normalized = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        using var db = new AppDbContext();
        var now = DateTimeOffset.UtcNow;
        var metadata = SymbolCatalogUpsert.FromVenueSymbol(venueId, normalized);
        var entity = db.Symbols.SingleOrDefault(x =>
            x.VenueId == venueId &&
            x.Environment == environment &&
            x.Symbol == normalized);

        if (entity is null)
        {
            entity = new SymbolCatalogEntity
            {
                VenueId = venueId,
                Environment = environment,
                Symbol = normalized,
                IsActive = true,
                UpdatedAt = now,
                LastActivatedAt = now
            };
            ApplyMetadata(entity, metadata);
            db.Symbols.Add(entity);
        }
        else
        {
            entity.IsActive = true;
            entity.UpdatedAt = now;
            entity.LastActivatedAt = now;
            ApplyMetadata(entity, metadata);
        }

        db.SaveChanges();
    }

    private static void ApplyMetadata(SymbolCatalogEntity entity, SymbolCatalogUpsert upsert)
    {
        entity.CanonicalKey = upsert.CanonicalKey;
        entity.BaseAsset = upsert.BaseAsset;
        entity.QuoteAsset = upsert.QuoteAsset;
        entity.SettleAsset = upsert.SettleAsset;
        entity.ContractType = upsert.ContractType;
        entity.DisplaySymbol = upsert.DisplaySymbol;
    }
}

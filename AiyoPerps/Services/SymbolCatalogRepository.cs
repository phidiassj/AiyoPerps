using AiyoPerps.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class SymbolCatalogRepository
{
    public IReadOnlyList<string> GetActiveSymbols(string venueId, string environment)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        return db.Symbols
            .AsNoTracking()
            .Where(x => x.VenueId == venueId && x.Environment == environment && x.IsActive)
            .AsEnumerable()
            .OrderByDescending(x => x.LastActivatedAt.HasValue)
            .ThenByDescending(x => x.LastActivatedAt)
            .ThenBy(x => x.Symbol)
            .Select(x => x.Symbol)
            .ToList();
    }

    public (int Added, int Removed, int Total) ReplaceSymbols(string venueId, string environment, IReadOnlyCollection<string> symbols)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();

        var now = DateTimeOffset.UtcNow;
        var normalized = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        var existing = db.Symbols
            .Where(x => x.VenueId == venueId && x.Environment == environment)
            .ToList();

        var oldSet = existing.Where(x => x.IsActive).Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSet = normalized.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var row in existing)
        {
            row.IsActive = newSet.Contains(row.Symbol);
            row.UpdatedAt = now;
        }

        foreach (var sym in normalized)
        {
            if (existing.Any(x => string.Equals(x.Symbol, sym, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            db.Symbols.Add(new SymbolCatalogEntity
            {
                VenueId = venueId,
                Environment = environment,
                Symbol = sym,
                IsActive = true,
                UpdatedAt = now,
                LastActivatedAt = null
            });
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
        var entity = db.Symbols.SingleOrDefault(x =>
            x.VenueId == venueId &&
            x.Environment == environment &&
            x.Symbol == normalized);

        if (entity is null)
        {
            db.Symbols.Add(new SymbolCatalogEntity
            {
                VenueId = venueId,
                Environment = environment,
                Symbol = normalized,
                IsActive = true,
                UpdatedAt = now,
                LastActivatedAt = now
            });
        }
        else
        {
            entity.IsActive = true;
            entity.UpdatedAt = now;
            entity.LastActivatedAt = now;
        }

        db.SaveChanges();
    }
}

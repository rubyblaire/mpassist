using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MakePlaceAssistant;

public sealed class ShoppingItem
{
    public string ItemName { get; set; } = string.Empty;
    public string? DyeName { get; set; }
    public int QuantityNeeded { get; set; }
    public int QuantityOwned { get; set; }
    public bool IsDye { get; set; }
    public uint ItemId { get; set; }
    public bool IsResolved { get; set; }
    public bool ManuallySkipped { get; set; }
    public bool SkippedDueToPriceCap { get; set; }
    public bool HasSeenListings { get; set; }
    public DateTime? LastListingsSeenAtUtc { get; set; }
    public ListingAnalysis? LatestAnalysis { get; set; }

    public int QuantityRemaining => Math.Max(0, this.QuantityNeeded - this.QuantityOwned);
    public bool IsComplete => this.QuantityRemaining <= 0;
    public bool IsSkipped => this.ManuallySkipped || this.SkippedDueToPriceCap;

    public string DisplayName => this.DyeName is { Length: > 0 }
        ? $"{this.ItemName} ({this.DyeName})"
        : this.ItemName;
}

public static class ShoppingListParser
{
    private enum Section
    {
        None,
        Furniture,
        FurnitureWithDye,
        Dyes,
    }

    private sealed class PendingEntry
    {
        public required string ItemName { get; init; }
        public string? DyeName { get; init; }
        public required int Quantity { get; init; }
        public required Section Section { get; init; }
    }

    private static readonly Regex ItemLineRegex = new(
        @"^(.+?)(?:\s*\((.+?)\))?\s*:\s*(\d+)\s*$",
        RegexOptions.Compiled);

    public static List<ShoppingItem> Parse(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var pending = new List<PendingEntry>();
        var section = Section.None;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("="))
                continue;

            if (line.StartsWith("Furniture (With Dye)", StringComparison.OrdinalIgnoreCase))
            {
                section = Section.FurnitureWithDye;
                continue;
            }

            if (line.StartsWith("Furniture", StringComparison.OrdinalIgnoreCase))
            {
                section = Section.Furniture;
                continue;
            }

            if (line.StartsWith("Dyes", StringComparison.OrdinalIgnoreCase))
            {
                section = Section.Dyes;
                continue;
            }

            var match = ItemLineRegex.Match(line);
            if (!match.Success)
                continue;

            pending.Add(new PendingEntry
            {
                ItemName = match.Groups[1].Value.Trim(),
                DyeName = match.Groups[2].Success ? match.Groups[2].Value.Trim() : null,
                Quantity = int.Parse(match.Groups[3].Value),
                Section = section,
            });
        }

        var dyedFurnitureNames = pending
            .Where(x => x.Section == Section.FurnitureWithDye)
            .Select(x => x.ItemName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, ShoppingItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pending)
        {
            if (entry.Section == Section.None)
                continue;

            if (entry.Section == Section.Furniture && dyedFurnitureNames.Contains(entry.ItemName))
                continue;

            var key = entry.Section switch
            {
                Section.Dyes => $"dye::{entry.ItemName}",
                Section.FurnitureWithDye => $"item::{entry.ItemName}::dye::{entry.DyeName ?? string.Empty}",
                _ => $"item::{entry.ItemName}",
            };

            if (!merged.TryGetValue(key, out var item))
            {
                item = new ShoppingItem
                {
                    ItemName = entry.ItemName,
                    DyeName = entry.Section == Section.FurnitureWithDye ? entry.DyeName : null,
                    IsDye = entry.Section == Section.Dyes,
                };
                merged.Add(key, item);
            }

            item.QuantityNeeded += entry.Quantity;

            if (entry.Section == Section.FurnitureWithDye && !string.IsNullOrWhiteSpace(entry.DyeName))
                item.DyeName = entry.DyeName;
        }

        return merged.Values
            .OrderByDescending(x => x.IsDye)
            .ThenBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

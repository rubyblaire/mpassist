using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Network.Structures;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MakePlaceAssistant;

public enum ShoppingState
{
    Idle,
    Active,
    ListingsReady,
    Paused,
    Done,
    Error,
}

public enum ListingSortMode
{
    CheapestPerUnit,
    CheapestTotalStack,
    ExactNeedThenPrice,
}

public sealed class AssistantSettings
{
    public bool PrioritizeDyesFirst { get; set; } = true;
    public bool PreferExactStackMatch { get; set; } = true;
    public bool SkipItemWhenAllListingsOverCap { get; set; }
    public bool NotifyOnNextItem { get; set; } = true;
    public bool NotifyOnListings { get; set; } = true;
    public bool NotifyOnPurchase { get; set; } = true;
    public int MaxPricePerUnit { get; set; }
    public int MaxStackTotal { get; set; }
    public ListingSortMode ListingSortMode { get; set; } = ListingSortMode.ExactNeedThenPrice;
}

public sealed class ListingSuggestion
{
    public required ulong ListingId { get; init; }
    public required uint ItemId { get; init; }
    public required uint Quantity { get; init; }
    public required uint PricePerUnit { get; init; }
    public required uint StackTotal { get; init; }
    public required uint TotalTax { get; init; }
    public required string RetainerName { get; init; }
    public required ulong RetainerId { get; init; }
    public required int RetainerCityId { get; init; }
    public required bool IsHq { get; init; }
    public required int OverbuyAmount { get; init; }
    public required bool IsWithinCaps { get; init; }
    public required bool MatchesNeedExactly { get; init; }
    public required bool HasDyeApplied { get; init; }

    public string Summary =>
        $"{this.Quantity}x @ {this.PricePerUnit:N0} gil ({this.StackTotal:N0} stack{(this.IsWithinCaps ? string.Empty : ", over cap")})";
}

public sealed class ListingAnalysis
{
    public required uint ItemId { get; init; }
    public required int NeededQuantity { get; init; }
    public required IReadOnlyList<ListingSuggestion> SortedListings { get; init; }
    public required IReadOnlyList<ListingSuggestion> AcceptableListings { get; init; }
    public required int OverCapCount { get; init; }
    public required DateTime SeenAtUtc { get; init; }

    public ListingSuggestion? BestOverall => this.SortedListings.FirstOrDefault();
    public ListingSuggestion? BestCheapestPerUnit => this.SortedListings.OrderBy(x => x.PricePerUnit).ThenBy(x => x.StackTotal).FirstOrDefault();
    public ListingSuggestion? BestCheapestStack => this.SortedListings.OrderBy(x => x.StackTotal).ThenBy(x => x.PricePerUnit).FirstOrDefault();
    public ListingSuggestion? BestExactOrClosest => this.SortedListings
        .OrderBy(x => x.OverbuyAmount)
        .ThenBy(x => x.PricePerUnit)
        .ThenBy(x => x.StackTotal)
        .FirstOrDefault();
}

public sealed class ShoppingService : IDisposable
{
    private sealed class OwnedSourceTotals
    {
        public int Inventory;
        public int Retainer;
        public int EstatePlaced;
        public int EstateStoreroom;

        public int Total => this.Inventory + this.Retainer + this.EstatePlaced + this.EstateStoreroom;
    }

    private enum OwnedSourceKind
    {
        Inventory,
        Retainer,
        EstatePlaced,
        EstateStoreroom,
    }

    private static readonly GameInventoryType[] InventoryContainers =
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    };

    private static readonly GameInventoryType[] RetainerContainers =
    {
        GameInventoryType.RetainerPage1,
        GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3,
        GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5,
        GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
        GameInventoryType.RetainerMarket,
    };

    private static readonly GameInventoryType[] EstatePlacedContainers =
    {
        GameInventoryType.HousingInteriorPlacedItems1,
        GameInventoryType.HousingInteriorPlacedItems2,
        GameInventoryType.HousingInteriorPlacedItems3,
        GameInventoryType.HousingInteriorPlacedItems4,
        GameInventoryType.HousingInteriorPlacedItems5,
        GameInventoryType.HousingInteriorPlacedItems6,
        GameInventoryType.HousingInteriorPlacedItems7,
        GameInventoryType.HousingInteriorPlacedItems8,
    };

    private static readonly GameInventoryType[] EstateStoreroomContainers =
    {
        GameInventoryType.HousingInteriorStoreroom1,
        GameInventoryType.HousingInteriorStoreroom2,
        GameInventoryType.HousingInteriorStoreroom3,
        GameInventoryType.HousingInteriorStoreroom4,
        GameInventoryType.HousingInteriorStoreroom5,
        GameInventoryType.HousingInteriorStoreroom6,
        GameInventoryType.HousingInteriorStoreroom7,
        GameInventoryType.HousingInteriorStoreroom8,
    };

    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly IMarketBoard marketBoard;
    private readonly INotificationManager notificationManager;
    private readonly IGameInventory gameInventory;

    private readonly Dictionary<uint, List<ShoppingItem>> itemsByItemId = new();
    private readonly Dictionary<uint, ListingAnalysis> lastAnalysesByItemId = new();

    private IMarketBoardPurchaseHandler? pendingPurchaseRequest;

    public ShoppingService(
        IPluginLog log,
        IDataManager dataManager,
        IMarketBoard marketBoard,
        INotificationManager notificationManager,
        IGameInventory gameInventory)
    {
        this.log = log;
        this.dataManager = dataManager;
        this.marketBoard = marketBoard;
        this.notificationManager = notificationManager;
        this.gameInventory = gameInventory;

        this.Settings = new AssistantSettings();
        this.ShoppingList = new List<ShoppingItem>();
        this.StatusMessage = "Ready. Load a shopping list to begin.";
        this.OwnedScanSummary = "Owned scan has not run yet.";

        this.marketBoard.OfferingsReceived += this.OnOfferingsReceived;
        this.marketBoard.PurchaseRequested += this.OnPurchaseRequested;
        this.marketBoard.ItemPurchased += this.OnItemPurchased;
    }

    public AssistantSettings Settings { get; }
    public List<ShoppingItem> ShoppingList { get; private set; }
    public ShoppingState State { get; private set; } = ShoppingState.Idle;
    public string StatusMessage { get; private set; }
    public string OwnedScanSummary { get; private set; }
    public int CurrentIndex { get; private set; }
    public ShoppingItem? CurrentItem { get; private set; }
    public ListingAnalysis? CurrentAnalysis { get; private set; }
    public int CompletedCount => this.ShoppingList.Count(x => x.IsComplete);
    public int RemainingCount => this.ShoppingList.Count(x => !x.IsComplete && !x.IsSkipped);
    public int SkippedCount => this.ShoppingList.Count(x => x.IsSkipped);
    public int TotalNeeded => this.ShoppingList.Sum(x => x.QuantityNeeded);
    public int TotalRemaining => this.ShoppingList.Sum(x => x.QuantityRemaining);
    public int TotalOwned => this.ShoppingList.Sum(x => x.QuantityOwned);

    public void LoadList(string filePath)
    {
        try
        {
            var parsed = ShoppingListParser.Parse(filePath);
            this.ShoppingList = parsed;
            this.itemsByItemId.Clear();
            this.lastAnalysesByItemId.Clear();
            this.CurrentAnalysis = null;
            this.CurrentItem = null;
            this.pendingPurchaseRequest = null;
            this.CurrentIndex = 0;

            this.ResolveItems();
            this.RefreshOwnedSourcesInternal();
            this.ResetProgressFlags();
            this.RecalculateCurrentItem(announce: false);

            this.State = ShoppingState.Idle;
            this.StatusMessage = $"Loaded {this.ShoppingList.Count} items. {this.RemainingCount} still need shopping.";
            this.log.Info($"[MakePlaceAssistant] Loaded {this.ShoppingList.Count} items.");
        }
        catch (Exception ex)
        {
            this.State = ShoppingState.Error;
            this.StatusMessage = $"Failed to load shopping list: {ex.Message}";
            this.log.Error(ex, "[MakePlaceAssistant] Failed to load shopping list.");
            this.Notify(NotificationType.Error, this.StatusMessage);
        }
    }

    public void Start()
    {
        if (this.ShoppingList.Count == 0)
        {
            this.StatusMessage = "Load a shopping list first.";
            return;
        }

        this.RefreshOwnedSourcesInternal();
        this.RecalculateCurrentItem(announce: true);

        if (this.CurrentItem is null)
        {
            this.State = ShoppingState.Done;
            this.StatusMessage = "Everything on the list is already complete.";
            this.Notify(NotificationType.Success, this.StatusMessage);
            return;
        }

        this.State = ShoppingState.Active;
        this.StatusMessage = $"Search the marketboard for: {this.CurrentItem.DisplayName} x{this.CurrentItem.QuantityRemaining}.";
    }

    public void Pause()
    {
        if (this.State == ShoppingState.Active || this.State == ShoppingState.ListingsReady)
        {
            this.State = ShoppingState.Paused;
            this.StatusMessage = "Assistant paused.";
        }
    }

    public void Resume()
    {
        if (this.State != ShoppingState.Paused)
            return;

        if (this.CurrentItem is null)
            this.RecalculateCurrentItem(announce: true);

        this.State = this.CurrentAnalysis is null ? ShoppingState.Active : ShoppingState.ListingsReady;
        this.StatusMessage = this.CurrentItem is null
            ? "No remaining items."
            : $"Back on track. Search the marketboard for: {this.CurrentItem.DisplayName} x{this.CurrentItem.QuantityRemaining}.";
    }

    public void Stop()
    {
        this.State = ShoppingState.Idle;
        this.CurrentAnalysis = null;
        this.pendingPurchaseRequest = null;
        this.RecalculateCurrentItem(announce: false);
        this.StatusMessage = "Assistant stopped.";
    }

    public void RefreshInventory()
    {
        this.RefreshOwnedSourcesInternal();
        this.RecalculateCurrentItem(announce: false);
        this.StatusMessage = $"Owned sources rescanned. {this.RemainingCount} items still need shopping.";
    }

    public void SkipCurrentItem()
    {
        if (this.CurrentItem is null)
            return;

        this.CurrentItem.ManuallySkipped = true;
        this.CurrentItem.LatestAnalysis = null;
        this.CurrentAnalysis = null;
        this.StatusMessage = $"Skipped {this.CurrentItem.DisplayName} for now.";
        this.RecalculateCurrentItem(announce: true);
    }

    public void ResetSkippedItems()
    {
        foreach (var item in this.ShoppingList)
        {
            item.ManuallySkipped = false;
            item.SkippedDueToPriceCap = false;
        }

        this.RecalculateCurrentItem(announce: false);
        this.StatusMessage = "Skipped items restored to the queue.";
    }

    public void MarkCurrentItemDone()
    {
        if (this.CurrentItem is null)
            return;

        this.SetManualOwnedFloor(this.CurrentItem, this.CurrentItem.QuantityNeeded, announce: true);
        this.StatusMessage = $"Marked {this.CurrentItem.DisplayName} complete.";
    }

    public void SetManualOwnedFloor(ShoppingItem item, int value)
        => this.SetManualOwnedFloor(item, value, announce: false);

    public void AddManualOwned(ShoppingItem item, int amount)
    {
        if (!this.ShoppingList.Contains(item) || amount <= 0)
            return;

        var newFloor = Math.Max(item.ManualOwnedFloor, item.QuantityOwned + amount);
        this.SetManualOwnedFloor(item, newFloor, announce: false);
        this.StatusMessage = $"Confirmed {item.DisplayName}: {item.QuantityOwned}/{item.QuantityNeeded} owned.";
    }

    public void ClearManualOwned(ShoppingItem item)
    {
        if (!this.ShoppingList.Contains(item))
            return;

        item.ManualOwnedFloor = 0;
        this.RecalculateCurrentItem(announce: false);
        this.StatusMessage = $"Cleared manual owned floor for {item.DisplayName}.";
    }

    public void JumpToItem(ShoppingItem item)
    {
        if (!this.ShoppingList.Contains(item))
            return;

        this.CurrentItem = item;
        this.CurrentAnalysis = item.ItemId != 0 && this.lastAnalysesByItemId.TryGetValue(item.ItemId, out var analysis)
            ? analysis
            : null;
        this.CurrentIndex = this.ShoppingList.IndexOf(item);
        if (this.State == ShoppingState.Idle)
            this.State = ShoppingState.Active;

        this.StatusMessage = $"Focused item: {item.DisplayName}. Search for or confirm {item.QuantityRemaining} more.";
        if (this.Settings.NotifyOnNextItem)
            this.Notify(NotificationType.Info, this.StatusMessage);
    }

    private void SetManualOwnedFloor(ShoppingItem item, int value, bool announce)
    {
        if (!this.ShoppingList.Contains(item))
            return;

        item.ManualOwnedFloor = Math.Max(0, value);
        this.CurrentAnalysis = ReferenceEquals(this.CurrentItem, item)
            ? item.ItemId != 0 && this.lastAnalysesByItemId.TryGetValue(item.ItemId, out var analysis)
                ? analysis
                : null
            : this.CurrentAnalysis;

        this.RecalculateCurrentItem(announce: announce);
    }

    private void ResolveItems()
    {
        var itemSheet = this.dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
            return;

        var exactByName = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        var normalizedByName = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in itemSheet)
        {
            var name = row.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            exactByName.TryAdd(name, row);

            var normalized = NormalizeItemLookupName(name);
            if (!string.IsNullOrWhiteSpace(normalized))
                normalizedByName.TryAdd(normalized, row);
        }

        foreach (var item in this.ShoppingList)
        {
            var match = FindItemMatch(item, exactByName, normalizedByName);
            if (match.RowId == 0 && string.IsNullOrWhiteSpace(match.Name.ToString()))
                continue;

            item.ItemId = match.RowId;
            item.IsResolved = item.ItemId != 0;
            if (item.ItemId != 0)
            {
                if (!this.itemsByItemId.TryGetValue(item.ItemId, out var group))
                {
                    group = new List<ShoppingItem>();
                    this.itemsByItemId[item.ItemId] = group;
                }

                group.Add(item);
            }
        }
    }

    private static Item FindItemMatch(ShoppingItem item, IReadOnlyDictionary<string, Item> exactByName, IReadOnlyDictionary<string, Item> normalizedByName)
    {
        if (exactByName.TryGetValue(item.ItemName, out var exact))
            return exact;

        if (item.IsDye && exactByName.TryGetValue($"{item.ItemName} Dye", out exact))
            return exact;

        var normalized = NormalizeItemLookupName(item.ItemName);
        if (!string.IsNullOrWhiteSpace(normalized) && normalizedByName.TryGetValue(normalized, out var normalizedMatch))
            return normalizedMatch;

        if (item.IsDye)
        {
            var normalizedDye = NormalizeItemLookupName($"{item.ItemName} Dye");
            if (!string.IsNullOrWhiteSpace(normalizedDye) && normalizedByName.TryGetValue(normalizedDye, out normalizedMatch))
                return normalizedMatch;
        }

        return default;
    }

    private static string NormalizeItemLookupName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var normalized = value.Trim().ToLowerInvariant();
        normalized = normalized.Replace("’", "'").Replace("‘", "'").Replace("`", "'");
        normalized = normalized.Replace("–", "-").Replace("—", "-");
        normalized = Regex.Replace(normalized, @"\s+", " ");

        if (normalized.EndsWith(" dye", StringComparison.Ordinal))
            normalized = normalized[..^4].TrimEnd();

        return normalized;
    }

    private void RefreshOwnedSourcesInternal()
    {
        foreach (var item in this.ShoppingList)
            item.ResetDetectedOwned();

        var totalsByItemId = new Dictionary<uint, OwnedSourceTotals>();

        var sawInventory = this.ScanSourceGroup(InventoryContainers, totalsByItemId, OwnedSourceKind.Inventory);
        var sawRetainers = this.ScanSourceGroup(RetainerContainers, totalsByItemId, OwnedSourceKind.Retainer);
        var sawEstatePlaced = this.ScanSourceGroup(EstatePlacedContainers, totalsByItemId, OwnedSourceKind.EstatePlaced);
        var sawEstateStoreroom = this.ScanSourceGroup(EstateStoreroomContainers, totalsByItemId, OwnedSourceKind.EstateStoreroom);

        foreach (var pair in this.itemsByItemId)
        {
            if (!totalsByItemId.TryGetValue(pair.Key, out var totals))
                continue;

            this.ApplyDetectedOwnership(pair.Value, totals);
        }

        var loadedSources = new List<string>();
        if (sawInventory)
            loadedSources.Add("inventory");
        if (sawRetainers)
            loadedSources.Add("loaded retainer pages");
        if (sawEstatePlaced || sawEstateStoreroom)
            loadedSources.Add("estate storage");

        this.OwnedScanSummary = loadedSources.Count == 0
            ? "No owned sources were available to scan."
            : $"Scanned {string.Join(", ", loadedSources)}.";
    }

    private bool ScanSourceGroup(IReadOnlyList<GameInventoryType> containers, IDictionary<uint, OwnedSourceTotals> totalsByItemId, OwnedSourceKind sourceKind)
    {
        var sawAnyContainer = false;

        foreach (var container in containers)
        {
            var slots = this.gameInventory.GetInventoryItems(container);
            if (!slots.IsEmpty)
                sawAnyContainer = true;

            foreach (var slot in slots)
            {
                if (slot.IsEmpty || slot.BaseItemId == 0 || slot.Quantity <= 0)
                    continue;

                if (!totalsByItemId.TryGetValue(slot.BaseItemId, out var totals))
                {
                    totals = new OwnedSourceTotals();
                    totalsByItemId[slot.BaseItemId] = totals;
                }

                switch (sourceKind)
                {
                    case OwnedSourceKind.Inventory:
                        totals.Inventory += slot.Quantity;
                        break;
                    case OwnedSourceKind.Retainer:
                        totals.Retainer += slot.Quantity;
                        break;
                    case OwnedSourceKind.EstatePlaced:
                        totals.EstatePlaced += slot.Quantity;
                        break;
                    case OwnedSourceKind.EstateStoreroom:
                        totals.EstateStoreroom += slot.Quantity;
                        break;
                }
            }
        }

        return sawAnyContainer;
    }

    private void ApplyDetectedOwnership(List<ShoppingItem> group, OwnedSourceTotals totals)
    {
        if (group.Count == 0 || totals.Total <= 0)
            return;

        var ambiguousVariants = group.Count > 1 && group.Any(x => !string.IsNullOrWhiteSpace(x.DyeName));
        if (ambiguousVariants)
        {
            foreach (var item in group)
                item.VariantOwnershipNeedsManualHelp = true;
            return;
        }

        this.DistributeSourceCount(group, totals.Inventory, static (item, value) => item.InventoryOwned += value);
        this.DistributeSourceCount(group, totals.Retainer, static (item, value) => item.RetainerOwned += value);
        this.DistributeSourceCount(group, totals.EstatePlaced, static (item, value) => item.EstatePlacedOwned += value);
        this.DistributeSourceCount(group, totals.EstateStoreroom, static (item, value) => item.EstateStoreroomOwned += value);
    }

    private void DistributeSourceCount(List<ShoppingItem> group, int count, Action<ShoppingItem, int> assign)
    {
        if (count <= 0 || group.Count == 0)
            return;

        var remaining = count;
        foreach (var item in group)
        {
            var stillUseful = Math.Max(0, item.QuantityNeeded - item.DetectedOwned);
            var assigned = Math.Min(remaining, stillUseful);
            if (assigned > 0)
            {
                assign(item, assigned);
                remaining -= assigned;
            }

            if (remaining <= 0)
                return;
        }

        if (remaining > 0)
            assign(group[0], remaining);
    }

    private void ResetProgressFlags()
    {
        foreach (var item in this.ShoppingList)
        {
            item.ManuallySkipped = false;
            item.SkippedDueToPriceCap = false;
            item.HasSeenListings = false;
            item.LastListingsSeenAtUtc = null;
            item.LatestAnalysis = null;
        }
    }

    private IEnumerable<ShoppingItem> EnumerateQueue()
    {
        var query = this.ShoppingList.Where(x => !x.IsComplete && !x.IsSkipped);

        query = this.Settings.PrioritizeDyesFirst
            ? query.OrderByDescending(x => x.IsDye).ThenBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            : query.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase);

        return query;
    }

    private void RecalculateCurrentItem(bool announce)
    {
        var previous = this.CurrentItem;
        var next = previous is not null && this.ShoppingList.Contains(previous) && !previous.IsComplete && !previous.IsSkipped
            ? previous
            : this.EnumerateQueue().FirstOrDefault();

        this.CurrentItem = next;
        this.CurrentAnalysis = next is not null && next.ItemId != 0 && this.lastAnalysesByItemId.TryGetValue(next.ItemId, out var analysis)
            ? analysis
            : null;
        this.CurrentIndex = next is null ? this.ShoppingList.Count : Math.Max(0, this.ShoppingList.IndexOf(next));

        if (next is null)
        {
            this.State = this.ShoppingList.Count == 0 ? ShoppingState.Idle : ShoppingState.Done;
            this.StatusMessage = this.ShoppingList.Count == 0
                ? "Ready. Load a shopping list to begin."
                : "Shopping list complete.";

            if (announce && this.ShoppingList.Count > 0)
                this.Notify(NotificationType.Success, "Shopping list complete.");
            return;
        }

        if (announce && this.Settings.NotifyOnNextItem && !ReferenceEquals(previous, next))
        {
            this.Notify(
                NotificationType.Info,
                $"Next up: {next.DisplayName} x{next.QuantityRemaining}.",
                "MakePlace Assistant");
        }
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        if (offerings.ItemListings.Count == 0)
            return;

        var itemId = offerings.ItemListings[0].ItemId;
        var analysis = this.BuildAnalysis(itemId, offerings.ItemListings);
        this.lastAnalysesByItemId[itemId] = analysis;

        if (this.itemsByItemId.TryGetValue(itemId, out var items))
        {
            foreach (var item in items)
            {
                item.HasSeenListings = true;
                item.LastListingsSeenAtUtc = DateTime.UtcNow;
                item.LatestAnalysis = analysis;
            }
        }

        if (this.CurrentItem is null || this.CurrentItem.ItemId != itemId)
            return;

        this.CurrentAnalysis = analysis;
        this.State = ShoppingState.ListingsReady;

        if (analysis.AcceptableListings.Count == 0)
        {
            this.StatusMessage = $"Listings arrived for {this.CurrentItem.DisplayName}, but every visible option is above your gil cap.";
            if (this.Settings.NotifyOnListings)
                this.Notify(NotificationType.Warning, this.StatusMessage);

            if (this.Settings.SkipItemWhenAllListingsOverCap)
            {
                this.CurrentItem.SkippedDueToPriceCap = true;
                this.StatusMessage = $"Skipped {this.CurrentItem.DisplayName} because all visible listings are over cap.";
                this.RecalculateCurrentItem(announce: true);
            }

            return;
        }

        var best = analysis.BestOverall;
        var bestText = best is null ? "No recommendation available." : $"Best pick: {best.Summary}";
        this.StatusMessage = $"Listings ready for {this.CurrentItem.DisplayName}. {bestText}";

        if (this.Settings.NotifyOnListings)
            this.Notify(NotificationType.Info, this.StatusMessage);
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchaseRequested)
    {
        this.pendingPurchaseRequest = purchaseRequested;

        if (this.CurrentItem is null || this.CurrentItem.ItemId != purchaseRequested.CatalogId)
            return;

        if (this.Settings.MaxPricePerUnit > 0 && purchaseRequested.PricePerUnit > this.Settings.MaxPricePerUnit)
            this.Notify(NotificationType.Warning, $"That listing is above your unit cap for {this.CurrentItem.DisplayName}.");

        var stackTotal = (ulong)purchaseRequested.PricePerUnit * purchaseRequested.ItemQuantity + purchaseRequested.TotalTax;
        if (this.Settings.MaxStackTotal > 0 && stackTotal > (ulong)this.Settings.MaxStackTotal)
            this.Notify(NotificationType.Warning, $"That stack total is above your cap for {this.CurrentItem.DisplayName}.");
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (!this.itemsByItemId.TryGetValue(purchase.CatalogId, out var items) || items.Count == 0)
            return;

        var targetItem = this.CurrentItem is not null && this.CurrentItem.ItemId == purchase.CatalogId
            ? this.CurrentItem
            : items[0];

        this.AddManualOwned(targetItem, (int)purchase.ItemQuantity);

        foreach (var item in items)
        {
            item.LatestAnalysis = item.ItemId != 0 && this.lastAnalysesByItemId.TryGetValue(item.ItemId, out var analysis)
                ? analysis
                : item.LatestAnalysis;
        }

        if (this.Settings.NotifyOnPurchase)
        {
            this.Notify(
                NotificationType.Success,
                $"Bought {purchase.ItemQuantity}x {targetItem.ItemName}. {targetItem.QuantityRemaining} remaining.");
        }

        if (this.CurrentItem?.ItemId == purchase.CatalogId)
        {
            if (targetItem.IsComplete)
            {
                this.CurrentAnalysis = null;
                this.StatusMessage = $"Finished {targetItem.DisplayName}.";
                this.RecalculateCurrentItem(announce: true);
                if (this.CurrentItem is not null)
                {
                    this.State = ShoppingState.Active;
                    this.StatusMessage = $"Search the marketboard for: {this.CurrentItem.DisplayName} x{this.CurrentItem.QuantityRemaining}.";
                }
            }
            else
            {
                this.State = ShoppingState.Active;
                this.StatusMessage = $"Still need {targetItem.QuantityRemaining} more of {targetItem.DisplayName}.";
                if (this.Settings.NotifyOnNextItem)
                    this.Notify(NotificationType.Info, this.StatusMessage);
            }
        }
    }

    private ListingAnalysis BuildAnalysis(uint itemId, IReadOnlyList<IMarketBoardItemListing> listings)
    {
        var needed = this.itemsByItemId.TryGetValue(itemId, out var items)
            ? Math.Max(1, this.CurrentItem?.ItemId == itemId ? this.CurrentItem.QuantityRemaining : items.Sum(x => Math.Max(0, x.QuantityRemaining)))
            : 1;

        var suggestions = listings
            .Select(listing => this.ToSuggestion(listing, needed))
            .ToList();

        IOrderedEnumerable<ListingSuggestion> ordered = this.Settings.ListingSortMode switch
        {
            ListingSortMode.CheapestPerUnit => suggestions
                .OrderByDescending(x => x.IsWithinCaps)
                .ThenBy(x => x.PricePerUnit)
                .ThenBy(x => x.OverbuyAmount)
                .ThenBy(x => x.StackTotal),
            ListingSortMode.CheapestTotalStack => suggestions
                .OrderByDescending(x => x.IsWithinCaps)
                .ThenBy(x => x.StackTotal)
                .ThenBy(x => x.PricePerUnit)
                .ThenBy(x => x.OverbuyAmount),
            _ => suggestions
                .OrderByDescending(x => x.IsWithinCaps)
                .ThenBy(x => this.Settings.PreferExactStackMatch ? x.OverbuyAmount : 0)
                .ThenBy(x => x.PricePerUnit)
                .ThenBy(x => x.StackTotal),
        };

        var sorted = ordered.ToList();
        var acceptable = sorted.Where(x => x.IsWithinCaps).ToList();

        return new ListingAnalysis
        {
            ItemId = itemId,
            NeededQuantity = needed,
            SortedListings = sorted,
            AcceptableListings = acceptable,
            OverCapCount = sorted.Count - acceptable.Count,
            SeenAtUtc = DateTime.UtcNow,
        };
    }

    private ListingSuggestion ToSuggestion(IMarketBoardItemListing listing, int needed)
    {
        var stackTotalLong = ((ulong)listing.PricePerUnit * listing.ItemQuantity) + listing.TotalTax;
        var stackTotal = stackTotalLong > uint.MaxValue ? uint.MaxValue : (uint)stackTotalLong;
        var overbuy = Math.Max(0, (int)listing.ItemQuantity - needed);
        var withinUnitCap = this.Settings.MaxPricePerUnit <= 0 || listing.PricePerUnit <= this.Settings.MaxPricePerUnit;
        var withinStackCap = this.Settings.MaxStackTotal <= 0 || stackTotal <= this.Settings.MaxStackTotal;

        return new ListingSuggestion
        {
            ListingId = listing.ListingId,
            ItemId = listing.ItemId,
            Quantity = listing.ItemQuantity,
            PricePerUnit = listing.PricePerUnit,
            StackTotal = stackTotal,
            TotalTax = listing.TotalTax,
            RetainerName = listing.RetainerName,
            RetainerId = listing.RetainerId,
            RetainerCityId = listing.RetainerCityId,
            IsHq = listing.IsHq,
            OverbuyAmount = overbuy,
            IsWithinCaps = withinUnitCap && withinStackCap,
            MatchesNeedExactly = listing.ItemQuantity == needed,
            HasDyeApplied = listing.Stain1Id > 0 || listing.Stain2Id > 0,
        };
    }

    private void Notify(NotificationType type, string content, string? title = null)
    {
        try
        {
            this.notificationManager.AddNotification(new Notification
            {
                Title = title ?? "MakePlace Assistant",
                Content = content,
                Type = type,
            });
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "[MakePlaceAssistant] Failed to display notification.");
        }
    }

    public void Dispose()
    {
        this.marketBoard.OfferingsReceived -= this.OnOfferingsReceived;
        this.marketBoard.PurchaseRequested -= this.OnPurchaseRequested;
        this.marketBoard.ItemPurchased -= this.OnItemPurchased;
    }
}

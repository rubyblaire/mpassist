using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;

namespace MakePlaceAssistant;

public sealed class PluginUi
{
    private readonly ShoppingService shoppingService;
    private readonly FileDialogManager fileDialogManager = new();
    private string filePathInput = string.Empty;
    private ShoppingItem? manualEditTarget;
    private int manualOwnedInput;

    public PluginUi(ShoppingService shoppingService)
    {
        this.shoppingService = shoppingService;
    }

    public bool IsVisible { get; set; }

    public void Draw()
    {
        this.fileDialogManager.Draw();

        if (!this.IsVisible)
            return;

        ImGui.SetNextWindowSize(new Vector2(1080, 760), ImGuiCond.FirstUseEver);

        var isVisible = this.IsVisible;
        if (!ImGui.Begin("MakePlace Assistant", ref isVisible))
        {
            this.IsVisible = isVisible;
            ImGui.End();
            return;
        }

        this.IsVisible = isVisible;

        this.DrawHeader();
        ImGui.Separator();
        this.DrawControls();
        ImGui.Separator();
        this.DrawCurrentItemPanel();
        ImGui.Separator();
        this.DrawShoppingList();

        ImGui.End();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(1f, 0.82f, 0.45f, 1f), "MakePlace Assistant");
        ImGui.TextWrapped("A marketboard shopping companion for MakePlace lists. It tells you what to search next, ranks visible listings, warns on gil caps, tracks purchases, and now helps preserve progress across inventory, loaded retainers, estate storage, and manual confirmations.");
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.65f, 1f, 0.65f, 1f), $"Status: {this.shoppingService.StatusMessage}");
        ImGui.TextDisabled(this.shoppingService.OwnedScanSummary);
    }

    private void DrawControls()
    {
        ImGui.Text("Shopping List (.txt)");
        ImGui.SetNextItemWidth(520f);
        ImGui.InputText("##shoppingListPath", ref this.filePathInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
            this.OpenShoppingListPicker();
        ImGui.SameLine();
        if (ImGui.Button("Load") && !string.IsNullOrWhiteSpace(this.filePathInput))
            this.shoppingService.LoadList(this.filePathInput);

        ImGui.TextDisabled("Tip: Browse will select and load the list automatically.");
        ImGui.Spacing();

        if (ImGui.Button("Start Assistant"))
            this.shoppingService.Start();
        ImGui.SameLine();
        if (ImGui.Button("Pause"))
            this.shoppingService.Pause();
        ImGui.SameLine();
        if (ImGui.Button("Resume"))
            this.shoppingService.Resume();
        ImGui.SameLine();
        if (ImGui.Button("Stop"))
            this.shoppingService.Stop();
        ImGui.SameLine();
        if (ImGui.Button("Rescan Owned Sources"))
            this.shoppingService.RefreshInventory();
        ImGui.SameLine();
        if (ImGui.Button("Reset Skips"))
            this.shoppingService.ResetSkippedItems();

        ImGui.Spacing();
        this.DrawSettings();

        var total = this.shoppingService.ShoppingList.Count;
        var complete = this.shoppingService.CompletedCount;
        if (total > 0)
        {
            var progress = total == 0 ? 0f : (float)complete / total;
            ImGui.ProgressBar(progress, new Vector2(-1f, 0f), $"{complete}/{total} complete | {this.shoppingService.RemainingCount} active | {this.shoppingService.SkippedCount} skipped");
        }
    }

    private void DrawSettings()
    {
        var settings = this.shoppingService.Settings;

        ImGui.Text("Assistant Rules");

        var prioritizeDyes = settings.PrioritizeDyesFirst;
        if (ImGui.Checkbox("Prioritize dyes first", ref prioritizeDyes))
            settings.PrioritizeDyesFirst = prioritizeDyes;
        ImGui.SameLine();
        var exactStacks = settings.PreferExactStackMatch;
        if (ImGui.Checkbox("Prefer exact stack match", ref exactStacks))
            settings.PreferExactStackMatch = exactStacks;
        ImGui.SameLine();
        var skipOverCap = settings.SkipItemWhenAllListingsOverCap;
        if (ImGui.Checkbox("Auto-skip when all listings are over cap", ref skipOverCap))
            settings.SkipItemWhenAllListingsOverCap = skipOverCap;

        var notifyNext = settings.NotifyOnNextItem;
        if (ImGui.Checkbox("Notify on next item", ref notifyNext))
            settings.NotifyOnNextItem = notifyNext;
        ImGui.SameLine();
        var notifyListings = settings.NotifyOnListings;
        if (ImGui.Checkbox("Notify on listings", ref notifyListings))
            settings.NotifyOnListings = notifyListings;
        ImGui.SameLine();
        var notifyPurchase = settings.NotifyOnPurchase;
        if (ImGui.Checkbox("Notify on purchase", ref notifyPurchase))
            settings.NotifyOnPurchase = notifyPurchase;

        var maxPricePerUnit = settings.MaxPricePerUnit;
        ImGui.SetNextItemWidth(140f);
        if (ImGui.InputInt("Max unit price (0 = off)", ref maxPricePerUnit))
            settings.MaxPricePerUnit = Math.Max(0, maxPricePerUnit);
        ImGui.SameLine();
        var maxStackTotal = settings.MaxStackTotal;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt("Max stack total (0 = off)", ref maxStackTotal))
            settings.MaxStackTotal = Math.Max(0, maxStackTotal);

        var sortMode = (int)settings.ListingSortMode;
        var sortOptions = new[]
        {
            "Cheapest per unit",
            "Cheapest total stack",
            "Exact need then price",
        };
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Listing sort mode", ref sortMode, sortOptions, sortOptions.Length))
            settings.ListingSortMode = (ListingSortMode)sortMode;
    }

    private void OpenShoppingListPicker()
    {
        var startPath = this.GetSuggestedStartPath();
        this.fileDialogManager.OpenFileDialog(
            "Select MakePlace shopping list",
            ".txt",
            (ok, paths) =>
            {
                var path = ok ? paths.FirstOrDefault() : null;
                if (string.IsNullOrWhiteSpace(path))
                    return;

                this.filePathInput = path;
                this.shoppingService.LoadList(path);
            },
            1,
            startPath);
    }

    private string GetSuggestedStartPath()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(this.filePathInput))
            {
                if (Directory.Exists(this.filePathInput))
                    return this.filePathInput;

                var directory = Path.GetDirectoryName(this.filePathInput);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    return directory;
            }
        }
        catch
        {
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents) ? "." : documents;
    }

    private void DrawCurrentItemPanel()
    {
        var current = this.shoppingService.CurrentItem;
        if (current is null)
        {
            ImGui.TextWrapped("No active item yet. Load a list, then start the assistant.");
            return;
        }

        this.SyncManualEditor(current);

        ImGui.TextColored(new Vector4(0.95f, 0.9f, 0.55f, 1f), $"Current target: {current.DisplayName}");
        ImGui.Text($"Needed: {current.QuantityNeeded}    Detected: {current.DetectedOwned}    Manual floor: {current.ManualOwnedFloor}    Owned: {current.QuantityOwned}    Remaining: {current.QuantityRemaining}");
        ImGui.Text($"Type: {(current.IsDye ? "Dye" : "Item")}    Resolved item ID: {(current.IsResolved ? current.ItemId.ToString() : "No")}");
        ImGui.TextWrapped($"Owned breakdown: {current.OwnedBreakdownSummary}");

        if (current.VariantOwnershipNeedsManualHelp)
            ImGui.TextColored(new Vector4(1f, 0.78f, 0.45f, 1f), "This item shares a base item ID with another dye variant. Use the manual controls below to confirm the correct variant count.");

        if (current.LastListingsSeenAtUtc.HasValue)
            ImGui.Text($"Last listings seen: {current.LastListingsSeenAtUtc.Value.ToLocalTime():g}");

        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Manual owned floor", ref this.manualOwnedInput))
            this.manualOwnedInput = Math.Max(0, this.manualOwnedInput);
        ImGui.SameLine();
        if (ImGui.Button("Set Confirmed Owned"))
            this.shoppingService.SetManualOwnedFloor(current, this.manualOwnedInput);
        ImGui.SameLine();
        if (ImGui.Button("+1 Confirmed"))
        {
            this.shoppingService.AddManualOwned(current, 1);
            this.SyncManualEditor(current);
        }
        ImGui.SameLine();
        if (ImGui.Button("+Remaining"))
        {
            this.shoppingService.AddManualOwned(current, current.QuantityRemaining);
            this.SyncManualEditor(current);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear Manual"))
        {
            this.shoppingService.ClearManualOwned(current);
            this.SyncManualEditor(current);
        }

        if (ImGui.Button("Skip Current Item"))
            this.shoppingService.SkipCurrentItem();
        ImGui.SameLine();
        if (ImGui.Button("Mark Current Item Done"))
        {
            this.shoppingService.MarkCurrentItemDone();
            this.SyncManualEditor(current);
        }

        var analysis = this.shoppingService.CurrentAnalysis;
        if (analysis is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped("Search this item on the in-game marketboard. When listings arrive, the assistant will rank them here. You can also use the manual owned controls to confirm estate placement or pre-owned items.");
            return;
        }

        ImGui.Spacing();
        ImGui.Text($"Visible listings: {analysis.SortedListings.Count}    Acceptable: {analysis.AcceptableListings.Count}    Over cap: {analysis.OverCapCount}");

        if (analysis.BestOverall is { } bestOverall)
            ImGui.BulletText($"Recommended: {bestOverall.Summary}");
        if (analysis.BestCheapestPerUnit is { } cheapestUnit)
            ImGui.BulletText($"Cheapest per unit: {cheapestUnit.Summary}");
        if (analysis.BestExactOrClosest is { } exactOrClosest)
            ImGui.BulletText($"Best exact/closest: {exactOrClosest.Summary}");

        ImGui.Spacing();
        ImGui.Text("Top visible listings");

        if (ImGui.BeginTable("##listingTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 210f)))
        {
            ImGui.TableSetupColumn("Pick");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Unit");
            ImGui.TableSetupColumn("Stack");
            ImGui.TableSetupColumn("Overbuy");
            ImGui.TableSetupColumn("Retainer");
            ImGui.TableSetupColumn("Notes");
            ImGui.TableHeadersRow();

            foreach (var suggestion in analysis.SortedListings.Take(8))
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text(suggestion.ListingId == analysis.BestOverall?.ListingId ? "★" : string.Empty);

                ImGui.TableSetColumnIndex(1);
                ImGui.Text(suggestion.Quantity.ToString());

                ImGui.TableSetColumnIndex(2);
                ImGui.Text(suggestion.PricePerUnit.ToString("N0"));

                ImGui.TableSetColumnIndex(3);
                ImGui.Text(suggestion.StackTotal.ToString("N0"));

                ImGui.TableSetColumnIndex(4);
                ImGui.Text(suggestion.OverbuyAmount.ToString());

                ImGui.TableSetColumnIndex(5);
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(suggestion.RetainerName) ? "Unknown" : suggestion.RetainerName);

                ImGui.TableSetColumnIndex(6);
                var note = suggestion.IsWithinCaps ? "OK" : "Over cap";
                if (suggestion.MatchesNeedExactly)
                    note += ", exact";
                if (suggestion.IsHq)
                    note += ", HQ";
                ImGui.TextUnformatted(note);
            }

            ImGui.EndTable();
        }
    }

    private void DrawShoppingList()
    {
        ImGui.Text($"Shopping list overview — total needed: {this.shoppingService.TotalNeeded}, owned: {this.shoppingService.TotalOwned}, remaining: {this.shoppingService.TotalRemaining}");

        if (!ImGui.BeginTable("##shoppingItems", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(-1f, 280f)))
            return;

        ImGui.TableSetupColumn("State");
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Need");
        ImGui.TableSetupColumn("Detected");
        ImGui.TableSetupColumn("Manual");
        ImGui.TableSetupColumn("Owned");
        ImGui.TableSetupColumn("Remain");
        ImGui.TableSetupColumn("Sources");
        ImGui.TableSetupColumn("Action");
        ImGui.TableHeadersRow();

        foreach (var item in this.shoppingService.ShoppingList)
        {
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(this.GetStateLabel(item));

            ImGui.TableSetColumnIndex(1);
            var isCurrent = ReferenceEquals(item, this.shoppingService.CurrentItem);
            var color = item.IsComplete
                ? new Vector4(0.55f, 1f, 0.55f, 1f)
                : item.IsSkipped
                    ? new Vector4(1f, 0.75f, 0.4f, 1f)
                    : isCurrent
                        ? new Vector4(1f, 0.92f, 0.5f, 1f)
                        : new Vector4(1f, 1f, 1f, 1f);
            ImGui.TextColored(color, item.DisplayName);
            if (item.VariantOwnershipNeedsManualHelp)
                ImGui.TextDisabled("Variant manual help");

            ImGui.TableSetColumnIndex(2);
            ImGui.Text(item.QuantityNeeded.ToString());

            ImGui.TableSetColumnIndex(3);
            ImGui.Text(item.DetectedOwned.ToString());

            ImGui.TableSetColumnIndex(4);
            ImGui.Text(item.ManualOwnedFloor.ToString());

            ImGui.TableSetColumnIndex(5);
            ImGui.Text(item.QuantityOwned.ToString());

            ImGui.TableSetColumnIndex(6);
            ImGui.Text(item.QuantityRemaining.ToString());

            ImGui.TableSetColumnIndex(7);
            ImGui.TextWrapped(item.OwnedBreakdownSummary);

            ImGui.TableSetColumnIndex(8);
            if (ImGui.SmallButton($"Focus##{item.DisplayName}_{item.ItemId}"))
            {
                this.shoppingService.JumpToItem(item);
                this.SyncManualEditor(item);
            }
        }

        ImGui.EndTable();
    }

    private string GetStateLabel(ShoppingItem item)
    {
        if (item.IsComplete)
            return "Done";
        if (item.ManuallySkipped)
            return "Skipped";
        if (item.SkippedDueToPriceCap)
            return "Cap";
        if (ReferenceEquals(item, this.shoppingService.CurrentItem))
            return "Now";
        return item.IsDye ? "Dye" : "Todo";
    }

    private void SyncManualEditor(ShoppingItem item)
    {
        if (ReferenceEquals(this.manualEditTarget, item))
            return;

        this.manualEditTarget = item;
        this.manualOwnedInput = item.ManualOwnedFloor;
    }
}

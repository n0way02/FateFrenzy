using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace FateFrenzy.Core.Tasks;

internal static class RelicItemResolver
{
    private static readonly Dictionary<uint, uint> TerritoryToItemCache = new();

    public static readonly Dictionary<uint, string> TerritoryToItemName = new()
    {
        { 1187, "Azurite Demiatma" },       // Urqopacha
        { 1188, "Verdigris Demiatma" },     // Kozama'uka
        { 1189, "Malachite Demiatma" },     // Yak T'el
        { 1190, "Realgar Demiatma" },       // Shaaloani
        { 1191, "Caput Mortuum Demiatma" }, // Heritage Found
        { 1192, "Orpiment Demiatma" }       // Living Memory
    };

    public static unsafe bool IsComplete(uint territoryId)
    {
        if (!TerritoryToItemName.TryGetValue(territoryId, out var name)) return false;
        var itemId = ResolveItemId(territoryId, name);
        if (itemId == 0) return false;
        var im = InventoryManager.Instance();
        return im != null && im->GetInventoryItemCount(itemId) >= 3;
    }

    public static unsafe int GetItemCount(uint territoryId)
    {
        if (!TerritoryToItemName.TryGetValue(territoryId, out var name)) return 0;
        var itemId = ResolveItemId(territoryId, name);
        if (itemId == 0) return 0;
        var im = InventoryManager.Instance();
        return im is null ? 0 : im->GetInventoryItemCount(itemId);
    }

    private static uint ResolveItemId(uint territoryId, string name)
    {
        if (TerritoryToItemCache.TryGetValue(territoryId, out var id)) return id;
        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (sheet is null) return 0;
        foreach (var row in sheet)
        {
            if (row.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                TerritoryToItemCache[territoryId] = row.RowId;
                return row.RowId;
            }
        }
        return 0;
    }
}

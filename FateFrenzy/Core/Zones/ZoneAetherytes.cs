using ECommons.DalamudServices;
using System.Numerics;
using EcMap = ECommons.GameHelpers.Map;

namespace FateFrenzy.Core.Zones;

internal readonly record struct ZoneAetheryte(uint Id, string Name, Vector3 Position);

internal static class ZoneAetherytes
{
    private static readonly Dictionary<uint, ZoneAetheryte[]> byTerritory = new();

    public static bool TryFindNearest(uint territoryId, Vector3 target, out ZoneAetheryte nearest)
    {
        var candidates = InTerritory(territoryId);
        nearest = default;
        var bestDistance = float.MaxValue;
        for (var index = 0; index < candidates.Length; index++)
        {
            var distance = Vector3.DistanceSquared(candidates[index].Position, target);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            nearest = candidates[index];
        }
        return bestDistance < float.MaxValue;
    }

    internal static ZoneAetheryte[] InTerritory(uint territoryId)
    {
        if (byTerritory.TryGetValue(territoryId, out var cached)) return cached;
        ZoneAetheryte[] resolved;
        if (territoryId == 1252)
        {
            resolved = new ZoneAetheryte[]
            {
                new(0, "Expedition Base Camp", new Vector3(830.75f, 72.98f, -695.98f)),
                new(0, "The Wanderer's Heaven", new Vector3(-173.02f, 8.19f, -611.14f)),
                new(0, "Crystalized Caverns", new Vector3(-358.14f, 101.98f, -120.96f)),
                new(0, "Eldergrowth", new Vector3(306.94f, 105.18f, 305.65f)),
                new(0, "Stonemarsh", new Vector3(-384.12f, 99.20f, 281.42f))
            };
        }
        else if (territoryId == 1346)
        {
            resolved = new ZoneAetheryte[]
            {
                new(0, "North Horn Base Camp", new Vector3(880.00f, 259.74f, 880.06f)),
                new(0, "The Crown Of Karnat", new Vector3(451.68f, 70.93f, 528.84f)),
                new(0, "Sinking Sanctuary", new Vector3(357.67f, 45.77f, -554.31f)),
                new(0, "Suspended Masonry", new Vector3(-547.25f, 68.00f, 594.40f)),
                new(0, "Moldering Outskirts", new Vector3(-388.57f, 41.22f, -440.52f)),
                new(0, "Unhallowed Hamlet", new Vector3(-13.36f, 3.14f, -40.51f))
            };
        }
        else
        {
            resolved = ResolveTeleportableAetherytes(territoryId);
        }
        byTerritory[territoryId] = resolved;
        return resolved;
    }

    private static ZoneAetheryte[] ResolveTeleportableAetherytes(uint territoryId)
    {
        var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
        if (sheet is null) return [];

        var found = new List<ZoneAetheryte>(4);
        foreach (var row in sheet)
        {
            if (!row.IsAetheryte) continue;
            if (row.Territory.RowId != territoryId) continue;
            if (!TryResolvePosition(row, out var position)) continue;
            found.Add(new ZoneAetheryte(row.RowId, ResolveName(row), position));
        }
        return found.ToArray();
    }

    private static string ResolveName(Lumina.Excel.Sheets.Aetheryte row)
    {
        var name = row.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"aetheryte #{row.RowId}" : name;
    }

    private static bool TryResolvePosition(Lumina.Excel.Sheets.Aetheryte row, out Vector3 position)
    {
        try
        {
            position = EcMap.AetherytePosition(row);
            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[FateFrenzy] Could not resolve a position for aetheryte {row.RowId}; skipping it as a teleport target");
            position = default;
            return false;
        }
    }
}

using FateFrenzy.Core.Zones;
using FateFrenzy.Core.Ipc;
using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.DalamudServices;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed partial class AutoFate
{
    private async Task DoChangeInstance()
    {
        var currentInstance = Svc.ClientState.Instance;
        if (currentInstance == 0)
        {
            successiveInstanceChanges = Plugin.Cfg.NumberOfInstances; // force zone swap next
            return;
        }

        Status = "Changing instance";
        Diag($"No FATEs found. Current instance: {currentInstance}. Changing to next instance...");

        var player = Svc.Objects.LocalPlayer;
        if (player is null) return;

        // Try to find the closest aetheryte in the zone
        var aetherytes = ZoneAetherytes.InTerritory(zone.TerritoryId);
        if (aetherytes.Length == 0)
        {
            Diag("No aetherytes found in this zone to change instance at.");
            successiveInstanceChanges = Plugin.Cfg.NumberOfInstances;
            return;
        }

        var nearestAetheryte = aetherytes[0];
        var bestDist = Vector3.Distance(player.Position, nearestAetheryte.Position);
        for (var i = 1; i < aetherytes.Length; i++)
        {
            var d = Vector3.Distance(player.Position, aetherytes[i].Position);
            if (d < bestDist)
            {
                bestDist = d;
                nearestAetheryte = aetherytes[i];
            }
        }

        // If we are far from the aetheryte (> 15m), teleport to it (or path to it)
        if (bestDist > 15f)
        {
            // If we are really far (> 80m), teleport to it
            if (bestDist > 80f)
            {
                Diag($"Aetheryte {nearestAetheryte.Name} is far ({bestDist:F0}m); teleporting to it.");
                if (zone.TerritoryId == 1252 || zone.TerritoryId == 1346)
                {
                    await TeleportToLocalCrystal(zone.TerritoryId, nearestAetheryte.Name, nearestAetheryte.Position);
                }
                else
                {
                    await PrepareForTeleport("instance-change-tp");
                    if (CancelToken.IsCancellationRequested) return;
                    var tp = new MoveOp(o => o.Teleport(zone.TerritoryId, nearestAetheryte.Position, allowSameZoneTeleport: true));
                    await RunCancellable(tp, TeleportWatchdogMs, "instance-change-tp", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
                }
            }
            else
            {
                // Otherwise, path to it
                Diag($"Aetheryte {nearestAetheryte.Name} is close ({bestDist:F0}m); pathfinding to it.");
                var move = new MoveOp(o => o.Move(zone.TerritoryId, nearestAetheryte.Position, MovementConfig.InteractRange, allowTeleportIfFaster: false, stopCondition: null, allowAethernetWithinTerritory: false));
                await RunCancellable(move, MoveToFateWatchdogMs, "instance-change-walk");
            }
        }

        if (CancelToken.IsCancellationRequested) return;

        // Ensure we are dismounted to interact/change instance
        if (Svc.Condition[ConditionFlag.Mounted])
        {
            await DismountViaOp("instance-change-dismount");
        }

        var nextInstance = (currentInstance % (uint)Plugin.Cfg.NumberOfInstances) + 1;
        Diag($"Swapping to instance {nextInstance} via /li {nextInstance}");
        Chat.ExecuteCommand($"/li {nextInstance}");
        await DelayMs(1000);

        // Wait for Lifestream and loading transition
        var start = Environment.TickCount64;
        while (LifestreamIPC.IsBusy() && Environment.TickCount64 - start < 30000)
        {
            if (CancelToken.IsCancellationRequested) return;
            await DelayMs(500);
        }

        while ((Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) && Environment.TickCount64 - start < 45000)
        {
            if (CancelToken.IsCancellationRequested) return;
            await DelayMs(500);
        }

        await DelayMs(2000); // Wait for actors to load
        successiveInstanceChanges++;
        zoneIdleSinceMs = 0; // Reset idle timer for new instance
    }
}

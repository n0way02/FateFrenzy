using FateFrenzy.Core.External;
using FateFrenzy.Core.Game.Fates;
using FateFrenzy.Core.Game.Player;
using FateFrenzy.Core.Ipc;
using FateFrenzy.Core.Modes;
using FateFrenzy.Core.Trading;
using FateFrenzy.Core.Zones;
using clib.Extensions;
using clib.TaskSystem;
using clib.Utils;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using System.Numerics;
using System.Threading.Tasks;
using CSFateManager = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager;

namespace FateFrenzy.Core.Tasks;

public sealed partial class AutoFate
{
    private async Task<MoveStopReason> MoveToFate(PublicEvent fate)
    {
        await WaitForNavmeshReady(NavmeshReadyWaitMs, 60);
        await GenerateObstacleMap(fate);

        var rnd = RandomPointInsideRadius(fate.Position, fate.Radius * 0.5f);
        var dest = rnd.OnMesh();
        if (dest == rnd)
            Diag($"OnMesh did not project FATE {fate.Id} dest {rnd}; vnav may struggle");

        var config = MovementConfig.Everything.WithTolerance(3f);
        var label = $"Moving to {fate.Name}";
        var targetId = fate.Id;

        await TryTeleportShortcut(fate.Position, targetId, fate.Name);
        if (CancelToken.IsCancellationRequested) return MoveStopReason.None;

        var deadline = Environment.TickCount64 + MoveToFateWatchdogMs;
        var lastRetargetAtMs = Environment.TickCount64;
        var nextProgressLogMs = Environment.TickCount64 + MoveProgressLogMs;
        var stopReason = MoveStopReason.None;

        // Graceful exits clib can observe while it is actively following a path: a deadline backstop,
        // the FATE vanishing/finishing, its prep NPC spawning, or a closer FATE appearing. Returning
        // true here lets clib's MoveTo stop vnav and unwind on its own. Physical "stuck" is handled by
        // the abort tracker below, not here, so the two never race.
        bool StopCondition()
        {
            Status = label;

            if (Environment.TickCount64 >= deadline) { stopReason = MoveStopReason.StuckTeleport; return true; }
            if (stopReason != MoveStopReason.None) return true;

            var refreshed = PublicEvent.GetFateById(targetId);
            if (refreshed is null) { stopReason = MoveStopReason.FateInvalid; return true; }
            if (refreshed.State != FateState.Running)
            {
                if (!FateScanner.AwaitsNpcStart(refreshed))
                {
                    stopReason = MoveStopReason.FateInvalid;
                    return true;
                }
                if (refreshed.MotivationNpc?.IsTargetable == true)
                {
                    stopReason = MoveStopReason.NpcSpawned;
                    return true;
                }
            }

            // Mid-path retargeting (skip when we're heading back to a FATE we died in).
            if (returnToFateId != targetId
             && Environment.TickCount64 - lastRetargetAtMs >= MidPathRetargetIntervalMs)
            {
                lastRetargetAtMs = Environment.TickCount64;
                var player = Svc.Objects.LocalPlayer;
                if (player is not null)
                {
                    var distToCurrent = Vector3.Distance(player.Position, refreshed.Position);
                    // Once we've basically reached the target, finish the trip rather than re-path.
                    if (distToCurrent > RetargetNearArrivalLockMeters)
                    {
                        var better = FateScanner.PickNext(Plugin.Cfg, player.Position, sessionStuckFateIds, null);
                        if (better is not null && better.Id != targetId
                         && Vector3.Distance(player.Position, better.Position) + RetargetDistanceMarginMeters < distToCurrent)
                        {
                            Diag($"Mid-path retarget: {targetId} -> {better.Id} ({better.Name}) (closer by >{RetargetDistanceMarginMeters:F0}m)");
                            stopReason = MoveStopReason.HigherPriority;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // Progress-or-recover: poll every frame across ALL of clib's phases (teleport, aethernet, mount,
        // pathfind, follow) and abort the move the moment forward progress stalls in a way that isn't a
        // legitimate wait. The tracker distinguishes a vnav terrain wedge from a fully-idle pre-pathfind
        // wedge (e.g. a teleport that never started casting) so neither phase is blind.
        var stuck = new TravelStuckTracker();
        bool AbortIfFrozen()
        {
            if (stopReason != MoveStopReason.None) return false;

            if (Environment.TickCount64 >= nextProgressLogMs)
            {
                nextProgressLogMs = Environment.TickCount64 + MoveProgressLogMs;
                var pp = Svc.Objects.LocalPlayer?.Position;
                var pStr = pp is { } v ? $"({v.X:F0},{v.Y:F0},{v.Z:F0})" : "?";
                Diag($"Still moving to FATE {targetId}: pos={pStr} navRun={NavmeshIPC.Instance.IsRunning()} busy={NavmeshIPC.Instance.IsBusy()} inCombat={Svc.Condition[ConditionFlag.InCombat]}");
            }

            var kind = stuck.Check();
            if (kind == StallKind.None) return false;

            stopReason = Svc.Condition[ConditionFlag.InCombat] ? MoveStopReason.StuckInCombat : MoveStopReason.StuckRetry;
            Diag(Svc.Condition[ConditionFlag.InCombat]
                ? $"Move to FATE {targetId} ({fate.Name}) stalled in combat ({kind}); cancelling to clear aggro (teleport is blocked in combat)"
                : kind == StallKind.NavWedge
                    ? $"Move to FATE {targetId} ({fate.Name}) wedged: vnav following but no progress in {HardStuckTimeoutMs/1000}s; cancelling to retry"
                    : $"Move to FATE {targetId} ({fate.Name}) idle: no nav/cast/mount progress in {StuckDetector.IdleStallTimeoutMs/1000}s (clib teleport likely never started); cancelling to retry");
            return true;
        }

        var op = new MoveOp(o => o.MoveInZone(dest, config, StopCondition));

        var completed = await RunCancellable(op, MoveToFateWatchdogMs + MoveOpUnwindSlackMs, label, AbortIfFrozen);
        if (CancelToken.IsCancellationRequested) return MoveStopReason.None;

        if (Svc.ClientState.TerritoryType != zone.TerritoryId)
        {
            // Read only targetId here (a captured uint) — fate.Name would deref a handle that despawned the
            // moment we crossed into the neighbouring territory, and an NRE would skip the LeftZone return.
            Diag($"Move to FATE {targetId} ended in territory {Svc.ClientState.TerritoryType}, not {zone.TerritoryId} ({zone.Name}); its fastest teleport route leaves the zone");
            return MoveStopReason.LeftZone;
        }

        // Cancelled by the hard timeout while wedged in a phase clib wasn't polling (e.g. a mount loop):
        // treat as a teleport-worthy stuck.
        if (!completed && stopReason == MoveStopReason.None)
            stopReason = MoveStopReason.StuckTeleport;

        // A clib fault (pathfind/teleport failure) completes the op without arriving; don't mistake it for
        // a clean arrival. Retry from here — MoveAndArrive escalates to a teleport if it recurs.
        if (stopReason == MoveStopReason.None && op.Fault is { } fault)
        {
            Diag($"Move to FATE {targetId} ({fate.Name}) faulted: {fault.Message}; retrying");
            stopReason = MoveStopReason.StuckRetry;
        }

        if (stopReason != MoveStopReason.None) return stopReason;

        if (PublicEvent.GetFateById(targetId) is { } landed && Svc.Objects.LocalPlayer is { } arrivedPlayer)
        {
            var distanceFromCenter = Vector3.Distance(arrivedPlayer.Position, landed.Position);
            if (distanceFromCenter > landed.Radius)
            {
                Diag($"Move to FATE {targetId} ({landed.Name}) ended {distanceFromCenter:F0}m from center (radius {landed.Radius:F0}); outside the ring, treating as stuck");
                return MoveStopReason.StuckRetry;
            }
        }

        // Clean arrival. clib only dismounts when it lands inside tolerance; a flying mount routinely
        // stops a few metres ABOVE the point (the Y gap), so it would otherwise enter the FATE still
        // mounted. Dismount explicitly — clib's Dismount descends to a reachable point first when in
        // flight — as its own cancellable op so a failed landing can't park the run.
        if (Svc.Condition[ConditionFlag.Mounted])
        {
            var dismount = new MoveOp(o => o.DismountNow());
            await RunCancellable(dismount, DismountWatchdogMs, $"dismount-{targetId}");
        }
        return MoveStopReason.None;
    }

    private async Task TryTeleportShortcut(Vector3 fatePos, uint fateId, string fateName)
    {
        if (FateScanner.PlayerHasTwistOfFate()) return;
        if (Svc.Condition[ConditionFlag.InCombat]) return;
        if (Svc.Objects.LocalPlayer is not { } player) return;
        if (!ZoneAetherytes.TryFindNearest(zone.TerritoryId, fatePos, out var aetheryte)) return;

        var flightFromHere = Vector3.Distance(player.Position, fatePos);
        var flightFromAetheryte = Vector3.Distance(aetheryte.Position, fatePos);
        if (flightFromHere - flightFromAetheryte < TeleportShortcutMinSavingMeters) return;

        Status = $"Teleporting to {aetheryte.Name}";
        Diag($"Teleport shortcut for FATE {fateId} ({fateName}): {aetheryte.Name} leaves {flightFromAetheryte:F0}m to fly vs {flightFromHere:F0}m from here");

        if (zone.TerritoryId == 1252 || zone.TerritoryId == 1346)
        {
            await TeleportToLocalCrystal(zone.TerritoryId, aetheryte.Name, aetheryte.Position);
            return;
        }

        await PrepareForTeleport($"fate-approach-{fateId}");
        if (CancelToken.IsCancellationRequested) return;

        var op = new MoveOp(o => o.Teleport(zone.TerritoryId, aetheryte.Position, allowSameZoneTeleport: true));
        await RunCancellable(op, TeleportWatchdogMs, $"fate-approach-{fateId}", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
        if (op.Fault is { } fault)
            Diag($"Teleport shortcut for FATE {fateId} faulted: {fault.Message}; flying from here instead");
    }

    // Every clib movement primitive — including dismount in combat/engage — goes through a cancellable
    // MoveOp so a wedged op (e.g. clib can't find a landing point in flight) can never park the parent
    // loop. The parent never awaits a raw clib MoveTo/Teleport/Dismount directly.
    private Task DismountViaOp(string label)
        => RunCancellable(new MoveOp(o => o.DismountNow()), DismountWatchdogMs, label);

    private async Task<bool> TryTeleportToFate(PublicEvent fate)
    {
        var fateId = fate.Id;
        if (!ZoneAetherytes.TryFindNearest(zone.TerritoryId, fate.Position, out var aetheryte))
        {
            Diag($"Teleport recovery for FATE {fateId}: {zone.Name} has no aetheryte of its own to teleport to");
            return false;
        }

        Status = $"Teleporting to {fate.Name}";
        Diag($"Teleport recovery to FATE {fateId} via {aetheryte.Name}");

        if (zone.TerritoryId == 1252 || zone.TerritoryId == 1346)
        {
            return await TeleportToLocalCrystal(zone.TerritoryId, aetheryte.Name, aetheryte.Position);
        }

        await PrepareForTeleport($"teleport-recovery-{fateId}");
        if (CancelToken.IsCancellationRequested) return false;

        // Capture position AFTER any dismount so a flight descent isn't mistaken for teleport progress.
        var before = Svc.Objects.LocalPlayer?.Position;
        // Idle-stall guard catches a teleport that never starts casting in ~8s instead of the full watchdog.
        var tp = new MoveOp(o => o.Teleport(zone.TerritoryId, aetheryte.Position, allowSameZoneTeleport: true));
        if (!await RunCancellable(tp, TeleportWatchdogMs, $"teleport-recovery-{fateId}", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs)))
            return false;

        var after = Svc.Objects.LocalPlayer?.Position;
        if (before is null || after is null) return false;

        var moved = Vector3.Distance(before.Value, after.Value);
        if (moved < TeleportRetryProgressMeters)
        {
            Diag($"Teleport moved only {moved:F1}m; treating as failed");
            return false;
        }
        return true;
    }

    // Teleport is blocked in combat, so fight off a mob that aggroed mid-travel (auto-target) to drop
    // combat before the loop re-paths.
    private async Task ClearBlockingCombat()
    {
        if (!Svc.Condition[ConditionFlag.InCombat]) return;

        Status = "Clearing aggro";
        Diag("In combat during travel; enabling rotation to fight free before resuming");

        var preset = Plugin.Cfg.CombatPresetName;
        EnsureCombatPreset(preset);
        if (Svc.Condition[ConditionFlag.Mounted]) await DismountViaOp("dismount-clearcombat");
        AssertPresetActive(preset);

        var deadline = Environment.TickCount64 + CombatClearTimeoutMs;
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                if (CancelToken.IsCancellationRequested) return;
                if (!Svc.Condition[ConditionFlag.InCombat]) break;
                if (IsPlayerKO()) break;
                // A real FATE may have started on top of us; let the state machine take over.
                if (PublicEvent.CurrentFate is { State: FateState.Running }) break;
                if (Svc.Condition[ConditionFlag.Mounted]) { ClearActiveCombatPreset(); await DismountViaOp("dismount-clearcombat"); }
                AssertPresetActive(preset);
                await NextFrame(30);
            }
        }
        finally
        {
            ClearActiveCombatPreset();
        }

        if (Svc.Condition[ConditionFlag.InCombat])
            Diag($"Still in combat after {CombatClearTimeoutMs / 1000}s of fighting; will retry travel");
    }

    internal enum StallKind { None, NavWedge, Idle }

    // Watches a single move for lack of forward progress. It partitions every non-progress case a clib
    // MoveTo can land in, so no phase is blind (the IsRunning-only gate used to miss the teleport phase):
    //   • NavWedge — vnav is actively following a path yet the character hasn't moved (terrain snag).
    //   • Idle     — no movement while NOTHING legitimate is happening: not following, not pathfinding,
    //                not casting/mounting/zone-transitioning. That is a wedged pre-pathfind phase, almost
    //                always a clib teleport that was issued but never started casting.
    // Anything legitimate (movement, vnav busy, or a frozen-legit state like a teleport cast) resets the
    // matching timer, so neither false-fires. Both surface as StuckRetry/StuckInCombat; the caller
    // escalates to a teleport-recovery only if the same FATE stalls again, so a transient block is given
    // a chance to clear on retry before we resort to teleporting.
    private sealed class TravelStuckTracker
    {
        private Vector3? lastPos;
        private long navWedgeSinceMs = Environment.TickCount64;
        private long idleSinceMs = Environment.TickCount64;

        public StallKind Check()
        {
            var player = Svc.Objects.LocalPlayer;
            if (player is null) return StallKind.None;

            var now = Environment.TickCount64;
            var pos = player.Position;

            // lastPos is a PERSISTENT anchor, advanced only once we've actually displaced past the
            // threshold — NOT every poll. (Resetting it each poll made steady travel look stationary,
            // because <1.5m moves between 67ms polls never cleared the threshold, false-firing the wedge
            // timer mid-flight.) Real displacement resets both timers; "stuck" is measured from the
            // anchor.
            if (lastPos is null || Vector3.Distance(lastPos.Value, pos) > StuckDetector.StuckMoveThresholdMeters)
            {
                lastPos = pos;
                navWedgeSinceMs = now;
                idleSinceMs = now;
                return StallKind.None;
            }

            var legitFrozen = StuckDetector.IsPositionFrozenLegit();
            var navRunning = NavmeshIPC.Instance.IsRunning();
            var navBusy = NavmeshIPC.Instance.IsBusy(); // running OR pathfind-in-progress

            // vnav following but no displacement from the anchor → terrain snag.
            if (legitFrozen || !navRunning) navWedgeSinceMs = now;
            else if (now - navWedgeSinceMs >= HardStuckTimeoutMs) return StallKind.NavWedge;

            // No displacement and nothing legitimate in progress → wedged pre-pathfind phase.
            if (legitFrozen || navBusy) idleSinceMs = now;
            else if (now - idleSinceMs >= StuckDetector.IdleStallTimeoutMs) return StallKind.Idle;

            return StallKind.None;
        }
    }

    private void EnsureCombatPreset(string preset)
    {
        var mode = Plugin.Cfg.RotationPlugin;
        if (mode != "BossMod" && mode != "BossModReborn") return;

        if (presetEnsured) return;
        if (preset != DefaultCombatPreset.Name) { presetEnsured = true; return; }

        if (BossModIPC.Instance.GetPreset(preset) is null)
        {
            Diag($"Default preset '{preset}' missing from BossMod, creating it.");
            if (!BossModIPC.Instance.CreatePreset(DefaultCombatPreset.GetSerialized(), overwrite: false))
                Diag($"BossMod.Presets.Create returned false for '{preset}'.");
        }
        presetEnsured = true;
    }

    private void AssertPresetActive(string preset)
    {
        var mode = Plugin.Cfg.RotationPlugin;
        if (mode == "BossMod" || mode == "BossModReborn")
        {
            if (BossModIPC.Instance.GetActive() != preset)
            {
                if (!BossModIPC.Instance.SetActive(preset))
                {
                    Diag($"BossMod.Presets.SetActive('{preset}') returned false — preset may not exist.");
                }
                else
                {
                    BossModIPC.Instance.AddTransientStrategy(preset, "BossMod.Autorotation.MiscAI.AutoTarget", "MaxTargets", PullSize().ToString());
                }
            }
        }
        else if (mode == "RotationSolver")
        {
            if (!rotationSolverArmed)
            {
                Chat.ExecuteCommand("/rotation auto on");
                rotationSolverArmed = true;
            }
        }
        else if (mode == "Wrath")
        {
            if (!wrathArmed)
            {
                Chat.ExecuteCommand("/wrath auto on");
                wrathArmed = true;
            }
        }

        EnableDodgingAI();
    }

    private void ClearActiveCombatPreset()
    {
        var mode = Plugin.Cfg.RotationPlugin;
        if (mode == "BossMod" || mode == "BossModReborn")
        {
            try { BossModIPC.Instance.ClearActive(); } catch { }
        }
        else if (mode == "RotationSolver")
        {
            if (rotationSolverArmed)
            {
                Chat.ExecuteCommand("/rotation off");
                rotationSolverArmed = false;
            }
        }
        else if (mode == "Wrath")
        {
            if (wrathArmed)
            {
                Chat.ExecuteCommand("/wrath auto off");
                wrathArmed = false;
            }
        }

        DisableDodgingAI();
    }

    private void EnableDodgingAI()
    {
        var dodgeMode = Plugin.Cfg.DodgingPlugin;
        var rotationMode = Plugin.Cfg.RotationPlugin;
        
        // Override if RotationPlugin itself is BossMod/BossModReborn
        if (rotationMode == "BossMod" || rotationMode == "BossModReborn")
        {
            dodgeMode = rotationMode;
        }

        if (dodgeMode == "None") return;

        var reach = EngageReachMeters();
        if (dodgeMode != lastDodgeMode || Math.Abs(reach - lastDodgeReach) > 0.01f)
        {
            if (dodgeMode == "BossModReborn")
            {
                Diag($"Enabling BossMod Reborn dodging AI (reach: {reach:F1}m)");
                Chat.ExecuteCommand("/bmrai on");
                Chat.ExecuteCommand("/bmrai followtarget on");
                Chat.ExecuteCommand("/bmrai followcombat on");
                Chat.ExecuteCommand($"/bmrai maxdistancetarget {reach}");
            }
            else if (dodgeMode == "BossMod")
            {
                Diag($"Enabling BossMod dodging AI (reach: {reach:F1}m)");
                Chat.ExecuteCommand("/vbmai on");
                Chat.ExecuteCommand("/vbmai followtarget on");
                Chat.ExecuteCommand("/vbmai followcombat on");
                Chat.ExecuteCommand($"/vbmai maxdistancetarget {reach}");
                if (rotationMode != "BossMod" && rotationMode != "BossModReborn")
                {
                    Chat.ExecuteCommand("/vbmai ForbidActions on");
                }
            }
            lastDodgeMode = dodgeMode;
            lastDodgeReach = reach;
        }
    }

    private void DisableDodgingAI()
    {
        var dodgeMode = Plugin.Cfg.DodgingPlugin;
        var rotationMode = Plugin.Cfg.RotationPlugin;
        
        if (rotationMode == "BossMod" || rotationMode == "BossModReborn")
        {
            dodgeMode = rotationMode;
        }

        if (dodgeMode == "None") return;

        if (lastDodgeMode is not null)
        {
            if (dodgeMode == "BossModReborn")
            {
                Diag("Disabling BossMod Reborn dodging AI");
                Chat.ExecuteCommand("/bmrai off");
                Chat.ExecuteCommand("/bmrai followtarget off");
                Chat.ExecuteCommand("/bmrai followcombat off");
                Chat.ExecuteCommand("/bmrai followoutofcombat off");
            }
            else if (dodgeMode == "BossMod")
            {
                Diag("Disabling BossMod dodging AI");
                Chat.ExecuteCommand("/vbm ar disable");
                Chat.ExecuteCommand("/vbmai off");
                Chat.ExecuteCommand("/vbmai followtarget off");
                Chat.ExecuteCommand("/vbmai followcombat off");
                Chat.ExecuteCommand("/vbmai followoutofcombat off");
                if (rotationMode != "BossMod" && rotationMode != "BossModReborn")
                {
                    Chat.ExecuteCommand("/vbmai ForbidActions off");
                }
            }
            lastDodgeMode = null;
            lastDodgeReach = 0f;
        }
    }

    private static unsafe void SyncToFate(uint fateId)
    {
        var mgr = CSFateManager.Instance();
        if (mgr is null) return;
        if (mgr->CurrentFate is null) return;
        if (mgr->CurrentFate->FateId != fateId) return;
        if (mgr->SyncedFateId == fateId) return;
        mgr->LevelSync();
    }

    // BossMod MaxTargets per role: tanks pull everything (0 = unlimited), healers stay conservative.
    private const byte RoleTank = 1;
    private const byte RoleMelee = 2;
    private const byte RoleHealer = 4;
    private const int  TankMaxTargets = 0;
    private const int  HealerMaxTargets = 5;
    private const int  DefaultMaxTargets = 3;

    private static int PullSize()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player is null) return DefaultMaxTargets;
        var role = player.ClassJob.Value.Role;
        return role switch
        {
            RoleTank   => TankMaxTargets,
            RoleHealer => HealerMaxTargets,
            _          => DefaultMaxTargets,
        };
    }

    private static Vector3 RandomPointInsideRadius(Vector3 center, float radius)
    {
        var angle = rng.NextDouble() * Math.PI * 2;
        var r = (float)(Math.Sqrt(rng.NextDouble()) * radius);
        return new Vector3(
            center.X + (float)Math.Cos(angle) * r,
            center.Y,
            center.Z + (float)Math.Sin(angle) * r);
    }

    private bool StopConditionMet()
        => Plugin.Cfg.ActiveMode.IsComplete(new ModeContext { CompletedCount = session.CompletedCount, Zones = zones, Elapsed = session.Elapsed });

    private bool AdvanceClassQueueIfCapHit()
    {
        var cfg = Plugin.Cfg;
        if (!cfg.ApplyClassOnStart) return false;
        if (cfg.ClassQueue.Count == 0) return false;

        var idx = ClassSwitcher.FindActiveEntryIndex(cfg.ClassQueue);
        if (idx < 0)
        {
            if (cfg.AfterClassQueueDone == AfterClassQueueDone.StopRun)
            {
                Status = "Class queue done";
                Diag("All queued classes hit their level caps, stopping run");
                return true;
            }
            return false;
        }

        var entry = cfg.ClassQueue[idx];
        var jobId = ClassSwitcher.JobIdForUserIndex(entry.GearsetIndex);
        var currentJob = Svc.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (jobId == 0 || jobId == currentJob) return false;

        Diag($"Class cap reached; switching to gearset {entry.GearsetIndex} ({ClassSwitcher.JobNameForUserIndex(entry.GearsetIndex)})");
        ClassSwitcher.TryEquip(entry);
        return false;
    }

    private static bool textAdvanceArmed;
    private const string TextAdvanceScope = AfgConstants.TextAdvanceCallerName;

    private static void EnableTextAdvanceForCollect()
    {
        if (textAdvanceArmed) return;
        if (!ExternalPlugins.IsInstalled(ExternalPlugin.TextAdvance)) return;
        try
        {
            TextAdvanceIPC.EnableExternalControl(TextAdvanceScope, talkSkip: true, requestFill: true, requestHandin: true);
            textAdvanceArmed = true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] TextAdvance enable failed");
        }
    }

    private static void DisableTextAdvance()
    {
        if (!textAdvanceArmed) return;
        try { TextAdvanceIPC.DisableExternalControl(TextAdvanceScope); }
        catch (Exception ex) { Svc.Log.Warning(ex, "[FateFrenzy] TextAdvance disable failed"); }
        textAdvanceArmed = false;
    }

    // Resolves the collection item ID from the PublicEvent or internal FateContext
    private static unsafe uint GetCollectItemId(PublicEvent fate)
    {
        if (fate.EventItem is { IsValid: true } item && item.ItemId > 0)
            return item.ItemId;

        if (fate.Address != nint.Zero)
        {
            var ctx = (FateContext*)fate.Address;
            if (ctx != null)
            {
                if (ctx->TurnInEventItem > 0) return ctx->TurnInEventItem;
                if (ctx->EventItem > 0) return ctx->EventItem;
                if (ctx->ReqEventItem > 0) return ctx->ReqEventItem;
            }
        }
        return 0;
    }

    // Returns how many of the FATE's collect item the player currently has (standard inventory + key items).
    private static unsafe int GetCollectItemCount(uint itemId)
    {
        if (itemId == 0) return 0;
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;
        var count = inv->GetInventoryItemCount(itemId, false, false, false);
        if (count <= 0)
        {
            count = inv->GetItemCountInContainer(itemId, InventoryType.KeyItems);
        }
        return count;
    }

    // Finds the collection hand-in NPC, checking MotivationNpc, EntityId match in Svc.Objects,
    // and proximity to fate.Position.
    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindCollectNpc(PublicEvent fate)
    {
        if (fate.MotivationNpc is { IsTargetable: true } npc)
            return npc;

        var fateNpcId = fate.MotivationNpcId;
        if (fateNpcId != 0 && fateNpcId != FateScanner.NoMotivationNpcId)
        {
            var match = Svc.Objects.FirstOrDefault(o => o.EntityId == fateNpcId && o.IsTargetable);
            if (match != null) return match;
        }

        var fatePos = fate.Position;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? bestNear = null;
        var bestDist = 20f;
        foreach (var obj in Svc.Objects)
        {
            if (!obj.IsTargetable) continue;
            if (obj.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                               or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
            {
                var dist = Vector3.Distance(obj.Position, fatePos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNear = obj;
                }
            }
        }

        return bestNear;
    }

    // Searches for the closest EventNpc/EventObj near the player as a fallback.
    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindClosestEventNpc(float maxRadius)
    {
        var player = Svc.Objects.LocalPlayer;
        if (player is null) return null;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? closest = null;
        var closestDist = maxRadius;

        foreach (var obj in Svc.Objects)
        {
            if (!obj.IsTargetable) continue;
            if (obj.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                               or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)
            {
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = obj;
                }
            }
        }
        return closest;
    }

    // Navigates by coordinate to the collection NPC, targets it, and executes the hand-in.
    private async Task DoCollectHandInByCoordinate(uint fateId)
    {
        var fate = PublicEvent.GetFateById(fateId) ?? PublicEvent.CurrentFate;
        if (fate is null) return;

        var itemId = GetCollectItemId(fate);
        var startCount = GetCollectItemCount(itemId);
        if (startCount == 0 && fate.Progress < 100)
        {
            Diag($"DoCollectHandInByCoordinate: no items to hand in (item {itemId}).");
            return;
        }

        Status = $"Turning in items for {fate.Name}";
        Diag($"Starting hand-in for {fate.Name} ({fateId}), items in bag: {startCount}");

        try
        {
            EnableTextAdvanceForCollect();

            var npc = FindCollectNpc(fate);
            var targetPos = npc?.Position ?? fate.Position;

            Diag($"Moving towards hand-in coordinate {targetPos} (NPC: {(npc != null ? $"{npc.Name} [0x{npc.EntityId:X}]" : "not rendered, heading to fate.Position")})");

            var handInLabel = $"Turning in {fate.Name}";
            var move = new MoveOp(o => o.Move(zone.TerritoryId, targetPos,
                MovementConfig.InteractRange,
                allowTeleportIfFaster: false,
                stopCondition: () =>
                {
                    Status = handInLabel;
                    if (PublicEvent.GetFateById(fateId) is null) return true;
                    if (npc is null || !npc.IsTargetable)
                    {
                        var liveFate = PublicEvent.GetFateById(fateId);
                        if (liveFate != null)
                            npc = FindCollectNpc(liveFate);
                    }
                    if (npc != null && npc.IsTargetable)
                    {
                        var player = Svc.Objects.LocalPlayer;
                        if (player != null && Vector3.Distance(player.Position, npc.Position) <= 3.0f)
                            return true;
                    }
                    return false;
                },
                allowAethernetWithinTerritory: false));

            await RunCancellable(move, ActivateMoveWatchdogMs, $"collect-move-{fateId}");

            if (CancelToken.IsCancellationRequested) return;
            if (PublicEvent.GetFateById(fateId) is not { } live) return;
            fate = live;

            if (Svc.Condition[ConditionFlag.Mounted])
                await DismountViaOp($"dismount-handin-{fateId}");

            // Look for NPC at arrival spot
            var searchDeadline = Environment.TickCount64 + 5_000;
            while (Environment.TickCount64 < searchDeadline && !CancelToken.IsCancellationRequested)
            {
                npc = FindCollectNpc(fate);
                if (npc != null && npc.IsTargetable) break;
                await NextFrame(10);
                fate = PublicEvent.GetFateById(fateId) ?? fate;
            }

            if (npc is null || !npc.IsTargetable)
            {
                Diag($"Collect NPC for {fateId} not found by ID; searching closest EventNpc near player...");
                npc = FindClosestEventNpc(15f);
            }

            if (npc is null || !npc.IsTargetable)
            {
                Diag($"Hand-in NPC not found for {fateId} near {targetPos}.");
                return;
            }

            Diag($"Interacting with hand-in NPC {npc.Name} [0x{npc.EntityId:X}] at {npc.Position}");
            Svc.Targets.Target = npc;

            var interact = new MoveOp(o => o.Interact(npc,
                waitUntil: () =>
                {
                    if (PublicEvent.GetFateById(fateId) is null) return true;
                    return GetCollectItemCount(itemId) == 0;
                },
                skip: UiSkipOptions.Talk | UiSkipOptions.YesNo));

            await RunCancellable(interact, NpcSpawnTimeoutMs, $"collect-interact-{fateId}");

            // Brief delay for inventory and handover window to settle
            var settleDeadline = Environment.TickCount64 + 4_000;
            while (Environment.TickCount64 < settleDeadline && !CancelToken.IsCancellationRequested)
            {
                if (GetCollectItemCount(itemId) == 0) break;
                await NextFrame(10);
            }

            var remaining = GetCollectItemCount(itemId);
            Diag($"Hand-in complete for {fateId}. Items remaining: {remaining}");
        }
        catch (Exception ex)
        {
            Diag($"DoCollectHandInByCoordinate caught: {ex.Message}");
        }
    }
}

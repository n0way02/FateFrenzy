using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FateFrenzy.Core.Game.Ops;
using FateFrenzy.Core.VoucherTrading;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed class AutoVoucherHandoff(string partnerName) : AutoCommon
{
    private readonly string partnerName = partnerName;

    private const uint MeetingTerritoryId = 156; // Mor Dhona
    private static readonly Vector3 MeetingPos = new(22.19f, 4.89f, -8.01f); // Near Edelina in Revenant's Toll

    private const int TeleportWatchdogMs = 60_000;
    private const int MoveWatchdogMs = 120_000;
    private const int PartnerWaitMs = 60_000;
    private const int TradeTimeoutMs = 45_000;

    protected override async Task Execute()
    {
        Diag($"AutoVoucherHandoff started with partner '{partnerName}'");
        Svc.Chat.Print($"[FateFrenzy] Heading to Mor Dhona meeting point to trade vouchers with {partnerName}...");

        // 1. Move to Mor Dhona meeting point
        if (Svc.ClientState.TerritoryType != MeetingTerritoryId)
        {
            var reached = false;
            await RunWithStatusPinned("Teleporting to Mor Dhona meeting point",
                async () => reached = await TeleportToTerritory(MeetingTerritoryId, MeetingPos, "handoff-teleport", TeleportWatchdogMs));

            ErrorIf(!reached, $"Could not reach Mor Dhona (still in {Svc.ClientState.TerritoryType}).");
        }

        // 2. Walk to meeting coordinates
        await RunWithStatusPinned("Walking to meeting point", async () =>
        {
            var move = new MoveOp(o => o.Move(MeetingTerritoryId, MeetingPos,
                MovementConfig.Everything.WithTolerance(3.0f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: true));
            await RunCancellable(move, MoveWatchdogMs, "handoff-walk");
        });

        // 3. Signal heartbeat that we are ready at the meeting spot
        SharedAccountCoordinator.Heartbeat(isReadyForTrade: true);

        // 4. Wait for partner to be visible
        Status = $"Waiting for partner {partnerName}";
        var deadline = Environment.TickCount64 + PartnerWaitMs;
        IPlayerCharacter? partnerObj = null;

        while (Environment.TickCount64 < deadline && !CancelToken.IsCancellationRequested)
        {
            SharedAccountCoordinator.Heartbeat(isReadyForTrade: true);
            partnerObj = Svc.Objects.OfType<IPlayerCharacter>()
                .FirstOrDefault(p => p.Name.ToString().Equals(partnerName, StringComparison.OrdinalIgnoreCase));

            if (partnerObj != null && Vector3.Distance(partnerObj.Position, Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero) < 15.0f)
            {
                break;
            }

            await DelayMs(1000);
        }

        if (partnerObj == null)
        {
            Diag($"Partner '{partnerName}' did not arrive within timeout; aborting handoff.");
            return;
        }

        // 5. Approach partner and initiate trade
        Status = $"Approaching {partnerName}";
        if (Vector3.Distance(partnerObj.Position, Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero) > 3.0f)
        {
            var approach = new MoveOp(o => o.Move(MeetingTerritoryId, partnerObj.Position,
                MovementConfig.Everything.WithTolerance(2.0f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(approach, 15_000, "approach-partner");
        }

        Svc.Targets.Target = partnerObj;
        await DelayMs(500);

        Status = "Opening Trade";
        Chat.SendMessage($"/trade <t>");

        var tradeDeadline = Environment.TickCount64 + TradeTimeoutMs;
        while (Environment.TickCount64 < tradeDeadline && !CancelToken.IsCancellationRequested)
        {
            if (Svc.Condition[ConditionFlag.TradeOpen]) break;
            await DelayMs(500);
        }

        if (!Svc.Condition[ConditionFlag.TradeOpen])
        {
            Diag("Trade window did not open.");
            return;
        }

        Status = "In Trade — awaiting completion";
        // If Trade window is open, wait for trade to complete
        while (Svc.Condition[ConditionFlag.TradeOpen] && !CancelToken.IsCancellationRequested)
        {
            await DelayMs(500);
        }

        SharedAccountCoordinator.Heartbeat(isReadyForTrade: false);
        Svc.Chat.Print($"[FateFrenzy] Trade handoff with {partnerName} ended.");
    }
}

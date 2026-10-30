using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FateFrenzy.Core.Game.Ops;
using FateFrenzy.Core.Ipc;
using FateFrenzy.Core.VoucherTrading;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed class AutoVoucherHandoff(string partnerName) : AutoCommon
{
    private readonly string partnerName = partnerName;

    private const uint MorDhonaTerritoryId = 156;

    // Exact meeting spot from user coordinates: Map X: 22.4, Y: 7.9, Z: 0.5
    // World X = (22.4 - 1) * 51.2 - 1024 = 71.68f
    // World Y (height) = 0.5f
    // World Z = (7.9 - 1) * 51.2 - 1024 = -670.72f
    private static readonly Vector3 MeetingSpotWorldPos = new(71.68f, 0.5f, -670.72f);

    private const int TeleportWatchdogMs = 60_000;
    private const int MoveWatchdogMs = 60_000;
    private const int PartnerWaitMs = 120_000;
    private const int TradeTimeoutMs = 60_000;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void OfferItemTradeDelegate(nint agentTrade, InventoryType type, ushort slot, uint quantity);
    private static OfferItemTradeDelegate? offerItemTrade;
    private static bool sigScanned;

    private static void EnsureSigScan()
    {
        if (sigScanned) return;
        sigScanned = true;
        try
        {
            var addr = Svc.SigScanner.ScanText("48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 83 B9 ?? ?? ?? ?? ?? 41 8B F0");
            if (addr != nint.Zero)
            {
                offerItemTrade = Marshal.GetDelegateForFunctionPointer<OfferItemTradeDelegate>(addr);
                Svc.Log.Info("[FateFrenzy] OfferItemTrade function scanned successfully.");
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] Could not scan OfferItemTrade signature.");
        }
    }

    protected override async Task Execute()
    {
        EnsureSigScan();
        var isSupplier = Plugin.Cfg.MountRole == MountAutomationRole.Supplier;
        var voucherId = Plugin.Cfg.MountTargetVoucherId;
        var transferQty = Plugin.Cfg.MountSupplierThreshold;

        // Configure Dropbox if available
        DropboxHelper.ConfigureForVoucherTrade(isSupplier, voucherId, transferQty);

        Diag($"AutoVoucherHandoff started with partner '{partnerName}' at Mor Dhona meeting coordinates (22.4, 7.9, 0.5)");
        Svc.Chat.Print($"[FateFrenzy] Travelling to Mor Dhona meeting spot (X: 22.4, Y: 7.9) to trade vouchers with {partnerName}...");

        // 1. Move to Mor Dhona
        if (Svc.ClientState.TerritoryType != MorDhonaTerritoryId)
        {
            var reached = false;
            await RunWithStatusPinned("Teleporting to Mor Dhona",
                async () => reached = await TeleportToTerritory(MorDhonaTerritoryId, MeetingSpotWorldPos, "handoff-teleport", TeleportWatchdogMs));

            ErrorIf(!reached, $"Could not reach Mor Dhona (still in {Svc.ClientState.TerritoryType}).");
        }

        // 2. Resolve standable ground position near (71.68f, 0.5f, -670.72f)
        var floorPos = NavmeshIPC.Instance.NearestPointReachable(MeetingSpotWorldPos, 15f, 15f) ?? MeetingSpotWorldPos;

        // Walk to meeting spot on FOOT (GroundMove only, never fly)
        if (Vector3.Distance(Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero, floorPos) > 3.0f)
        {
            await RunWithStatusPinned("Walking to meeting spot (X: 22.4, Y: 7.9)", async () =>
            {
                var move = new MoveOp(o => o.Move(MorDhonaTerritoryId, floorPos,
                    MovementConfig.GroundMove.WithTolerance(2.0f),
                    allowTeleportIfFaster: false,
                    stopCondition: null,
                    allowAethernetWithinTerritory: false));
                await RunCancellable(move, MoveWatchdogMs, "handoff-meeting-walk");
            });
        }

        // 3. Signal heartbeat that we are standing at the meeting spot
        SharedAccountCoordinator.Heartbeat(isReadyForTrade: true);

        // 4. Wait for partner to be near the meeting spot
        Status = $"Waiting for partner {partnerName} at meeting spot";
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
            Diag($"Partner '{partnerName}' did not arrive at Mor Dhona meeting spot within timeout; aborting.");
            return;
        }

        // 5. Approach partner on foot
        Status = $"Approaching {partnerName}";
        if (Vector3.Distance(partnerObj.Position, Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero) > 2.5f)
        {
            var approach = new MoveOp(o => o.Move(MorDhonaTerritoryId, partnerObj.Position,
                MovementConfig.GroundMove.WithTolerance(1.8f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(approach, 15_000, "approach-partner");
        }

        // 6. Initiate Trade
        Svc.Targets.Target = partnerObj;
        await DelayMs(500);

        Status = "Opening Trade window";
        Chat.SendMessage("/trade <t>");

        var tradeDeadline = Environment.TickCount64 + TradeTimeoutMs;
        while (Environment.TickCount64 < tradeDeadline && !CancelToken.IsCancellationRequested)
        {
            if (Svc.Condition[ConditionFlag.TradeOpen]) break;

            if (Svc.Targets.Target?.Address != partnerObj.Address)
                Svc.Targets.Target = partnerObj;

            Chat.SendMessage("/trade <t>");
            await DelayMs(1500);
        }

        if (!Svc.Condition[ConditionFlag.TradeOpen])
        {
            Diag("Trade window did not open within timeout.");
            return;
        }

        Status = "Trade window open — executing exchange";
        await DelayMs(1500);

        // 7. Trade flow logic
        if (isSupplier)
        {
            Status = "Offering vouchers into trade...";
            await OfferVouchersIntoTrade();
            await DelayMs(1500);

            // Supplier locks in offer once items are placed
            ClickTradeAddonButton();
            await DelayMs(1500);

            // Wait for partner to lock in and then confirm
            var waitConfirmDeadline = Environment.TickCount64 + 30_000;
            while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < waitConfirmDeadline && !CancelToken.IsCancellationRequested)
            {
                ClickTradeAddonButton();
                await DelayMs(1000);
            }
        }
        else
        {
            // MasterSeller side: MUST NOT click Trade until partner has offered items!
            Status = "Awaiting vouchers from supplier...";
            var waitOfferDeadline = Environment.TickCount64 + 40_000;
            while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < waitOfferDeadline && !CancelToken.IsCancellationRequested)
            {
                // Check if partner locked in or offered items
                if (TradeDetectionManager.PartnerReadyForTrade)
                {
                    Diag("Partner is ready with trade offer. Locking in and confirming.");
                    ClickTradeAddonButton();
                    await DelayMs(1000);
                    ClickTradeAddonButton();
                    break;
                }
                await DelayMs(500);
            }

            var finalizeDeadline = Environment.TickCount64 + 15_000;
            while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < finalizeDeadline && !CancelToken.IsCancellationRequested)
            {
                ClickTradeAddonButton();
                await DelayMs(1000);
            }
        }

        SharedAccountCoordinator.Heartbeat(isReadyForTrade: false);
        Svc.Chat.Print($"[FateFrenzy] Voucher trade with {partnerName} completed!");
        await DelayMs(1500);
    }

    private unsafe Task OfferVouchersIntoTrade()
    {
        var voucherId = Plugin.Cfg.MountTargetVoucherId;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Trade);
        if (agent == null) return Task.CompletedTask;

        var im = InventoryManager.Instance();
        if (im == null) return Task.CompletedTask;

        InventoryType[] bags = [
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4
        ];

        foreach (var bag in bags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null) continue;

            for (ushort slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->ItemId == voucherId && item->Quantity > 0)
                {
                    Diag($"Offering {item->Quantity}x vouchers from bag {bag} slot {slot} into trade.");
                    if (offerItemTrade != null)
                    {
                        offerItemTrade((nint)agent, bag, slot, (uint)item->Quantity);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private static unsafe void ClickTradeAddonButton()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, 0);
        }
    }
}

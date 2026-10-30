using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FateFrenzy.Core.Game.Ops;
using FateFrenzy.Core.VoucherTrading;
using FateFrenzy.Core.Zones;
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

    private const int TeleportWatchdogMs = 60_000;
    private const int MoveWatchdogMs = 60_000;
    private const int PartnerWaitMs = 90_000;
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
        Diag($"AutoVoucherHandoff started with partner '{partnerName}' at Mor Dhona Aetheryte");
        Svc.Chat.Print($"[FateFrenzy] Heading to Mor Dhona Aetheryte crystal to trade vouchers with {partnerName}...");

        // 1. Move to Mor Dhona Aetheryte
        if (Svc.ClientState.TerritoryType != MorDhonaTerritoryId)
        {
            var reached = false;
            await RunWithStatusPinned("Teleporting to Mor Dhona",
                async () => reached = await TeleportToTerritory(MorDhonaTerritoryId, Vector3.Zero, "handoff-teleport", TeleportWatchdogMs));

            ErrorIf(!reached, $"Could not reach Mor Dhona (still in {Svc.ClientState.TerritoryType}).");
        }

        // 2. Locate Mor Dhona crystal
        var aetheryte = Svc.Objects.FirstOrDefault(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte);
        var crystalPos = aetheryte?.Position ?? Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        // Walk to Aetheryte crystal on foot (GroundMove only, never fly)
        if (Vector3.Distance(Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero, crystalPos) > 4.0f)
        {
            await RunWithStatusPinned("Walking to Aetheryte crystal", async () =>
            {
                var move = new MoveOp(o => o.Move(MorDhonaTerritoryId, crystalPos,
                    MovementConfig.GroundMove.WithTolerance(3.0f),
                    allowTeleportIfFaster: false,
                    stopCondition: null,
                    allowAethernetWithinTerritory: false));
                await RunCancellable(move, MoveWatchdogMs, "handoff-crystal-walk");
            });
        }

        // 3. Signal heartbeat that we are ready at the crystal
        SharedAccountCoordinator.Heartbeat(isReadyForTrade: true);

        // 4. Wait for partner to be near the crystal
        Status = $"Waiting for partner {partnerName} at crystal";
        var deadline = Environment.TickCount64 + PartnerWaitMs;
        IPlayerCharacter? partnerObj = null;

        while (Environment.TickCount64 < deadline && !CancelToken.IsCancellationRequested)
        {
            SharedAccountCoordinator.Heartbeat(isReadyForTrade: true);
            partnerObj = Svc.Objects.OfType<IPlayerCharacter>()
                .FirstOrDefault(p => p.Name.ToString().Equals(partnerName, StringComparison.OrdinalIgnoreCase));

            if (partnerObj != null && Vector3.Distance(partnerObj.Position, Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero) < 20.0f)
            {
                break;
            }

            await DelayMs(1000);
        }

        if (partnerObj == null)
        {
            Diag($"Partner '{partnerName}' did not arrive at Mor Dhona crystal within timeout; aborting.");
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
        var isSupplier = Plugin.Cfg.MountRole == MountAutomationRole.Supplier;

        // Trade handshake
        Chat.SendMessage("/trade <t>");

        var tradeDeadline = Environment.TickCount64 + TradeTimeoutMs;
        while (Environment.TickCount64 < tradeDeadline && !CancelToken.IsCancellationRequested)
        {
            if (Svc.Condition[ConditionFlag.TradeOpen]) break;

            // Retarget and retry trade if window hasn't opened yet
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
        await DelayMs(1000);

        // 7. If Supplier, offer vouchers into Trade
        if (isSupplier)
        {
            await OfferVouchersIntoTrade();
            await DelayMs(1000);
        }

        // 8. Confirm Trade
        var confirmDeadline = Environment.TickCount64 + 30_000;
        while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < confirmDeadline && !CancelToken.IsCancellationRequested)
        {
            ClickTradeAddonButton();
            await DelayMs(1000);
        }

        SharedAccountCoordinator.Heartbeat(isReadyForTrade: false);
        Svc.Chat.Print($"[FateFrenzy] Voucher trade with {partnerName} completed!");
        await DelayMs(1000);
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
            // Button 0 in AddonTrade is Trade (Lock / Confirm)
            Callback.Fire(addon, true, 0);
        }
    }
}

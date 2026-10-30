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
using FFXIVClientStructs.FFXIV.Client.UI;
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

    // Exact meeting spot from user screenshot coordinates: Map X: 22.4, Y: 7.9, Z: 0.5
    // World X = (22.4 - 1) * 51.2 - 1024 = 71.68f
    // World Y (height) = 0.5f
    // World Z = (7.9 - 1) * 51.2 - 1024 = -670.72f
    private static readonly Vector3 MeetingSpotWorldPos = new(71.68f, 0.5f, -670.72f);

    private const int TeleportWatchdogMs = 60_000;
    private const int MoveWatchdogMs = 60_000;
    private const int PartnerWaitMs = 120_000;
    private const int TradeTimeoutMs = 60_000;

    // Signature matches Dropbox's working OfferItemTrade hook:
    // SafeOfferItemTrade(tradeAddress, slot, type)
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void OfferItemTradeDelegate(nint agentTrade, ushort slot, InventoryType type);
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
        var initialVouchers = CurrentVoucherCount(voucherId);
        var transferQty = Math.Max(Plugin.Cfg.MountSupplierThreshold, initialVouchers);

        Diag($"AutoVoucherHandoff started with partner '{partnerName}' at Mor Dhona meeting coordinates (22.4, 7.9, 0.5). Role: {Plugin.Cfg.MountRole}, Current vouchers: {initialVouchers}");
        Svc.Chat.Print($"[FateFrenzy] Travelling to Mor Dhona meeting spot (X: 22.4, Y: 7.9) to trade vouchers with {partnerName}...");

        // 1. Travel to Mor Dhona
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

        // 6. Initiate Trade window
        // Let Supplier initiate trade first to avoid both accounts spamming /trade simultaneously
        if (isSupplier)
        {
            Svc.Targets.Target = partnerObj;
            await DelayMs(500);

            Status = "Initiating trade with partner";
            var tradeDeadline = Environment.TickCount64 + TradeTimeoutMs;
            while (Environment.TickCount64 < tradeDeadline && !CancelToken.IsCancellationRequested)
            {
                if (Svc.Condition[ConditionFlag.TradeOpen]) break;

                if (Svc.Targets.Target?.Address != partnerObj.Address)
                    Svc.Targets.Target = partnerObj;

                Chat.SendMessage("/trade <t>");
                await DelayMs(2000);
            }
        }
        else
        {
            Status = "Waiting for supplier to initiate trade...";
            var tradeDeadline = Environment.TickCount64 + TradeTimeoutMs;
            while (Environment.TickCount64 < tradeDeadline && !CancelToken.IsCancellationRequested)
            {
                if (Svc.Condition[ConditionFlag.TradeOpen]) break;

                // Fallback: If 15 seconds pass and trade has not opened, initiate ourselves
                if (Environment.TickCount64 > tradeDeadline - (TradeTimeoutMs - 15_000))
                {
                    if (Svc.Targets.Target?.Address != partnerObj.Address)
                        Svc.Targets.Target = partnerObj;
                    Chat.SendMessage("/trade <t>");
                }
                await DelayMs(2000);
            }
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
            var offered = await OfferVouchersIntoTrade(voucherId, transferQty);
            if (!offered)
            {
                Diag("No vouchers could be offered into trade; cancelling trade.");
                CancelTrade();
                return;
            }

            await DelayMs(1000);

            // Supplier locks in offer once items are placed in trade slots
            Status = "Locking in trade offer...";
            ClickTradeAddonButton();
            await DelayMs(1000);

            var selfLockDeadline = Environment.TickCount64 + 10_000;
            while (!TradeDetectionManager.SelfReadyForTrade && Environment.TickCount64 < selfLockDeadline && Svc.Condition[ConditionFlag.TradeOpen])
            {
                ClickTradeAddonButton();
                await DelayMs(1000);
            }

            // Wait for partner (MasterSeller) to lock in
            Status = "Waiting for partner to lock trade...";
            var waitPartnerDeadline = Environment.TickCount64 + 40_000;
            while (!TradeDetectionManager.PartnerReadyForTrade && Environment.TickCount64 < waitPartnerDeadline && Svc.Condition[ConditionFlag.TradeOpen] && !CancelToken.IsCancellationRequested)
            {
                await DelayMs(500);
            }

            if (!TradeDetectionManager.PartnerReadyForTrade)
            {
                Diag("Partner did not lock in trade within timeout; cancelling.");
                CancelTrade();
                return;
            }

            // Both have locked in! Confirm trade
            Status = "Confirming trade...";
            var waitConfirmDeadline = Environment.TickCount64 + 20_000;
            while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < waitConfirmDeadline && !CancelToken.IsCancellationRequested)
            {
                if (ShopInteraction.SelectYesnoOpen())
                {
                    Diag("Clicking Yes on trade confirmation dialog.");
                    ShopInteraction.ClickSelectYesno();
                    await DelayMs(1000);
                }
                else
                {
                    ClickTradeAddonButton();
                    await DelayMs(1000);
                }
            }
        }
        else
        {
            // MasterSeller side: MUST NOT click Trade until partner has offered items and locked in!
            Status = "Awaiting vouchers from supplier...";
            var waitOfferDeadline = Environment.TickCount64 + 60_000;
            while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < waitOfferDeadline && !CancelToken.IsCancellationRequested)
            {
                if (TradeDetectionManager.PartnerReadyForTrade)
                {
                    Diag("Partner is ready with trade offer (PartnerReadyForTrade == true).");
                    break;
                }
                await DelayMs(500);
            }

            if (!TradeDetectionManager.PartnerReadyForTrade)
            {
                Diag("Partner never locked in trade offer within timeout; cancelling.");
                CancelTrade();
                return;
            }

            // MasterSeller locks in offer
            Status = "Locking in trade offer...";
            ClickTradeAddonButton();
            await DelayMs(1000);

            var selfLockDeadline = Environment.TickCount64 + 10_000;
            while (!TradeDetectionManager.SelfReadyForTrade && Environment.TickCount64 < selfLockDeadline && Svc.Condition[ConditionFlag.TradeOpen])
            {
                ClickTradeAddonButton();
                await DelayMs(1000);
            }

            // Both have locked in! Confirm trade
            Status = "Confirming trade...";
            var finalizeDeadline = Environment.TickCount64 + 20_000;
            while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < finalizeDeadline && !CancelToken.IsCancellationRequested)
            {
                if (ShopInteraction.SelectYesnoOpen())
                {
                    Diag("Clicking Yes on trade confirmation dialog.");
                    ShopInteraction.ClickSelectYesno();
                    await DelayMs(1000);
                }
                else
                {
                    ClickTradeAddonButton();
                    await DelayMs(1000);
                }
            }
        }

        // Wait for trade window to close
        var closeDeadline = Environment.TickCount64 + 5000;
        while (Svc.Condition[ConditionFlag.TradeOpen] && Environment.TickCount64 < closeDeadline)
        {
            await DelayMs(200);
        }

        SharedAccountCoordinator.Heartbeat(isReadyForTrade: false);

        var finalVouchers = CurrentVoucherCount(voucherId);
        Svc.Chat.Print($"[FateFrenzy] Voucher trade with {partnerName} finished! Vouchers: before={initialVouchers}, after={finalVouchers}.");
        await DelayMs(1500);
    }

    private readonly struct VoucherSlotInfo(InventoryType bag, ushort slot, int quantity)
    {
        public readonly InventoryType Bag = bag;
        public readonly ushort Slot = slot;
        public readonly int Quantity = quantity;
    }

    private static unsafe List<VoucherSlotInfo> FindVoucherSlots(uint voucherId)
    {
        var list = new List<VoucherSlotInfo>();
        var im = InventoryManager.Instance();
        if (im == null) return list;

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
                    list.Add(new VoucherSlotInfo(bag, slot, item->Quantity));
                }
            }
        }
        return list;
    }

    private static unsafe nint GetTradeAgent()
    {
        var agentModule = UIModule.Instance()->GetAgentModule();
        if (agentModule == null) return nint.Zero;

        var tradeAgent = agentModule->GetAgentByInternalId((AgentId)36);
        if (tradeAgent == null)
            tradeAgent = agentModule->GetAgentByInternalId(AgentId.Trade);

        return (nint)tradeAgent;
    }

    private static unsafe bool TrySubmitInputNumeric(int quantity)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var numAddon) && GenericHelpers.IsAddonReady(numAddon))
        {
            Callback.Fire(numAddon, true, quantity);
            return true;
        }
        return false;
    }

    private async Task<bool> OfferVouchersIntoTrade(uint voucherId, int transferQty)
    {
        EnsureSigScan();
        if (offerItemTrade == null)
        {
            Svc.Log.Error("[FateFrenzy] Could not offer vouchers: offerItemTrade function pointer is null.");
            return false;
        }

        var tradeAgent = GetTradeAgent();
        if (tradeAgent == nint.Zero)
        {
            Svc.Log.Error("[FateFrenzy] Could not obtain trade agent pointer.");
            return false;
        }

        var slots = FindVoucherSlots(voucherId);
        if (slots.Count == 0)
        {
            Diag($"No vouchers (ID {voucherId}) found in bags.");
            return false;
        }

        var remainingToOffer = transferQty;
        var anyOffered = false;

        foreach (var slotInfo in slots)
        {
            if (remainingToOffer <= 0) break;

            var offerQty = Math.Min(slotInfo.Quantity, remainingToOffer);
            Diag($"Offering {offerQty}x vouchers from bag {slotInfo.Bag} slot {slotInfo.Slot} into trade.");
            offerItemTrade(tradeAgent, slotInfo.Slot, slotInfo.Bag);
            await DelayMs(300);

            // If the item stack in this slot is > 1, FFXIV opens the InputNumeric addon
            if (slotInfo.Quantity > 1)
            {
                var numericDeadline = Environment.TickCount64 + 4000;
                var submitted = false;
                while (Environment.TickCount64 < numericDeadline && !CancelToken.IsCancellationRequested)
                {
                    if (TrySubmitInputNumeric(offerQty))
                    {
                        Diag($"InputNumeric open: submitted quantity {offerQty} via Callback.");
                        submitted = true;
                        await DelayMs(600);
                        break;
                    }
                    await DelayMs(100);
                }

                if (!submitted)
                {
                    Diag("InputNumeric dialog was not detected or ready within timeout.");
                }
            }

            remainingToOffer -= offerQty;
            anyOffered = true;
            await DelayMs(500);
        }

        return anyOffered;
    }

    private static unsafe void ClickTradeAddonButton()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, 0);
        }
    }

    private static unsafe void CancelTrade()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Trade", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, 1);
        }
    }

    private static unsafe int CurrentVoucherCount(uint voucherItemId)
    {
        var im = InventoryManager.Instance();
        return im != null ? im->GetInventoryItemCount(voucherItemId, false, false, false) : 0;
    }
}

using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FateFrenzy.Core.Game.Ops;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed class AutoMountExchange : AutoCommon
{
    private const uint MorDhonaTerritoryId = 156;
    private const uint EdelinaEnpcBaseId = 1036913;

    // Converted world coordinates for Mor Dhona Map X: 22.2, Y: 4.8, Z: 0.5
    // X_world = (22.2 - 1) * 51.2 - 1024 = 61.44f
    // Y_world (height) = 0.5f
    // Z_world = (4.8 - 1) * 51.2 - 1024 = -829.44f
    private static readonly Vector3 EdelinaFallbackWorldPos = new(61.44f, 0.5f, -829.44f);

    private const int TeleportWatchdogMs = 60_000;
    private const int MoveWatchdogMs = 120_000;
    private const int InteractWaitMs = 15_000;
    private const int ShopOpenWaitMs = 15_000;
    private const int ConfirmWaitMs = 10_000;
    private const int ExchangeWaitMs = 10_000;
    private const int SubmenuWaitMs = 10_000;
    private const int SettleDelayMs = 500;

    public bool ExchangeSuccess { get; private set; }

    protected override async Task Execute()
    {
        var targetItemId = Plugin.Cfg.MountTargetItemId;       // 43590 (Ty'aitya Whistle)
        var targetVoucherId = Plugin.Cfg.MountTargetVoucherId; // 43961 (Turali Bicolor Gemstone Voucher)
        const int RequiredVouchers = 500; // Whistle exchange is strictly 500 vouchers

        var currentVouchers = CurrentVoucherCount(targetVoucherId);
        if (currentVouchers < RequiredVouchers)
        {
            Diag($"AutoMountExchange aborted: has {currentVouchers} vouchers, strictly needs {RequiredVouchers}.");
            return;
        }

        Diag($"AutoMountExchange starting: target whistle {targetItemId}, vouchers={currentVouchers}");
        Svc.Chat.Print($"[FateFrenzy] Exchanging {RequiredVouchers} vouchers for Ty'aitya Whistle at Edelina in Mor Dhona...");

        await ReviveIfDead();

        // 1. Travel to Mor Dhona
        if (Svc.ClientState.TerritoryType != MorDhonaTerritoryId)
        {
            var reached = false;
            await RunWithStatusPinned("Teleporting to Mor Dhona",
                async () => reached = await TeleportToTerritory(MorDhonaTerritoryId, EdelinaFallbackWorldPos, "mount-teleport", TeleportWatchdogMs));

            if (!reached && Svc.Condition[ConditionFlag.Unconscious])
            {
                await ReviveIfDead();
                await RunWithStatusPinned("Teleporting to Mor Dhona (retry)",
                    async () => reached = await TeleportToTerritory(MorDhonaTerritoryId, EdelinaFallbackWorldPos, "mount-teleport-retry", TeleportWatchdogMs));
            }

            ErrorIf(!reached, $"Could not reach Mor Dhona (still in {Svc.ClientState.TerritoryType}); aborting mount exchange.");
        }

        // 2. Resolve Edelina location
        var npc = RepairOps.FindNearestObjectByBaseId(EdelinaEnpcBaseId)
               ?? Svc.Objects.FirstOrDefault(o => o.Name.ToString().Equals("Edelina", StringComparison.OrdinalIgnoreCase));
        var targetPos = npc?.Position ?? EdelinaFallbackWorldPos;

        // 3. Walk on FOOT (GroundMove only, never fly into sky) to Edelina
        await RunWithStatusPinned("Walking to Edelina", async () =>
        {
            var move = new MoveOp(o => o.Move(MorDhonaTerritoryId, targetPos,
                MovementConfig.GroundMove.WithTolerance(2.5f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(move, MoveWatchdogMs, "mount-walk");
        });

        // 4. Find and interact with Edelina
        npc ??= RepairOps.FindNearestObjectByBaseId(EdelinaEnpcBaseId)
             ?? Svc.Objects.FirstOrDefault(o => o.Name.ToString().Equals("Edelina", StringComparison.OrdinalIgnoreCase));
        ErrorIf(npc is null, "Could not find Edelina in Revenant's Toll.");

        Status = "Interacting with Edelina";
        var interact = new MoveOp(o => o.Interact(npc!,
            waitUntil: () => ShopInteraction.ShopExchangeCurrencyOpen() || ShopInteraction.SelectIconStringOpen(),
            skip: UiSkipOptions.YesNo));
        await RunCancellable(interact, InteractWaitMs, "mount-interact");

        ErrorIf(!await WaitUntilTimed(
                () => ShopInteraction.ShopExchangeCurrencyOpen() || ShopInteraction.SelectIconStringOpen(),
                ShopOpenWaitMs, "wait-edelina-menu"),
            "Edelina did not open a menu within timeout; aborting.");

        // 5. If submenu open, pick "Bicolor Gemstone Vouchers"
        if (ShopInteraction.SelectIconStringOpen())
        {
            await NavigateEdelinaMenu();
        }

        ErrorIf(!ShopInteraction.ShopExchangeCurrencyOpen(),
            "Could not open Edelina's voucher exchange shop.");

        // 6. Buy Whistle
        var vouchersBefore = CurrentVoucherCount(targetVoucherId);
        var whistleBefore = CurrentWhistleCount(targetItemId);

        Status = "Purchasing Mount Whistle";
        ErrorIf(!ShopInteraction.BuyFromCurrencyShop(targetItemId, 1),
            $"Mount Whistle (ID {targetItemId}) not visible in Edelina's shop.");

        if (!await WaitUntilTimed(
                () => ShopInteraction.SelectYesnoOpen() || CurrentVoucherCount(targetVoucherId) < vouchersBefore,
                ConfirmWaitMs, "wait-confirm-whistle"))
            Diag("No confirm dialog detected; continuing.");

        if (ShopInteraction.SelectYesnoOpen())
            ShopInteraction.ClickSelectYesno();

        await WaitUntilTimed(
            () => CurrentWhistleCount(targetItemId) > whistleBefore,
            ExchangeWaitMs, "wait-whistle-received");

        Status = "Closing shop";
        ShopInteraction.CloseShop();
        await DelayMs(SettleDelayMs);

        var whistleAfter = CurrentWhistleCount(targetItemId);
        if (whistleAfter > whistleBefore)
        {
            ExchangeSuccess = true;
            Svc.Chat.Print("[FateFrenzy] Mount Whistle acquired successfully! Preparing Retainer listing...");
        }
        else
        {
            Diag("Mount Whistle not detected in inventory after exchange attempt.");
        }
    }

    private unsafe Task NavigateEdelinaMenu()
    {
        var count = ShopInteraction.SelectIconStringEntryCount();
        Diag($"Edelina menu open with {count} options.");

        for (var i = 0; i < count; i++)
        {
            if (!ShopInteraction.SelectIconStringOpen()) break;

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectIconString, out var addon)
                && GenericHelpers.IsAddonReady(addon))
            {
                var master = new AddonMaster.SelectIconString(addon);
                var text = master.Entries[i].Text;
                if (!string.IsNullOrEmpty(text) && text.Contains("Bicolor", StringComparison.OrdinalIgnoreCase))
                {
                    Diag($"Found Bicolor menu entry at index {i}: '{text}'");
                    master.Entries[i].Select();
                    return WaitUntilTimed(ShopInteraction.ShopExchangeCurrencyOpen, SubmenuWaitMs, "wait-bicolor-shop");
                }
            }
        }

        if (ShopInteraction.SelectIconStringOpen())
        {
            ShopInteraction.ClickSelectIconString(0);
            return WaitUntilTimed(ShopInteraction.ShopExchangeCurrencyOpen, SubmenuWaitMs, "wait-bicolor-shop-default");
        }

        return Task.CompletedTask;
    }

    private static unsafe int CurrentVoucherCount(uint voucherItemId)
    {
        var im = InventoryManager.Instance();
        return im != null ? im->GetInventoryItemCount(voucherItemId, false, false, false) : 0;
    }

    private static unsafe int CurrentWhistleCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im != null ? im->GetInventoryItemCount(itemId, false, false, false) : 0;
    }

    private async Task ReviveIfDead()
    {
        if (Svc.Condition[ConditionFlag.Unconscious])
        {
            try
            {
                GameMain.ExecuteCommand(200, 8, 0, 0, 0);
            }
            catch { }

            var deadline = Environment.TickCount64 + 30000;
            while (Environment.TickCount64 < deadline)
            {
                if (CancelToken.IsCancellationRequested) return;
                var stillKO = Svc.Condition[ConditionFlag.Unconscious];
                var transitioning = Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51];
                if (!stillKO && !transitioning)
                {
                    await DelayMs(1000);
                    break;
                }
                await DelayMs(250);
            }
        }
    }
}

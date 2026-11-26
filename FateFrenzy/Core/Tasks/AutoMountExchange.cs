using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FateFrenzy.Core.Game.Ops;
using FateFrenzy.Core.Ipc;
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

    // Mor Dhona (Territory 156, SizeFactor 100) Revenant's Toll — Edelina at Map (X: 22.1, Y: 4.8)
    // World X = (22.1 - 1.0) * 50.0 - 1024 = 31.0f
    // World Y = 31.0f (Revenant's Toll ground elevation)
    // World Z = (4.8 - 1.0) * 50.0 - 1024 = -834.0f
    private static readonly Vector3 EdelinaFallbackWorldPos = new(31.0f, 31.0f, -834.0f);

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

        await WaitForNavmeshReady(60_000);
        if (CancelToken.IsCancellationRequested) return;

        // 2. Resolve Edelina location (snap fallback to walkable floor via navmesh with generous vertical range)
        var npc = RepairOps.FindNearestObjectByBaseId(EdelinaEnpcBaseId)
               ?? Svc.Objects.FirstOrDefault(o => o.Name.ToString().Equals("Edelina", StringComparison.OrdinalIgnoreCase));
        var rawPos = npc?.Position ?? EdelinaFallbackWorldPos;
        var targetPos = NavmeshIPC.Instance.NearestPointReachable(rawPos, 20f, 40f) ?? rawPos;

        if (Svc.Condition[ConditionFlag.Mounted])
            await RunCancellable(new MoveOp(o => o.DismountNow()), 15_000, "mount-dismount");

        // 3. Walk on FOOT (GroundMove only, never fly into sky) to Edelina
        await RunWithStatusPinned("Walking to Edelina", async () =>
        {
            var move = new MoveOp(o => o.Move(MorDhonaTerritoryId, targetPos,
                MovementConfig.GroundMove.WithTolerance(2.5f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(move, MoveWatchdogMs, "mount-walk", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
        });

        // 4. Find Edelina now that we're in render range and approach closer if needed
        npc ??= RepairOps.FindNearestObjectByBaseId(EdelinaEnpcBaseId)
             ?? Svc.Objects.FirstOrDefault(o => o.Name.ToString().Equals("Edelina", StringComparison.OrdinalIgnoreCase));
        ErrorIf(npc is null, "Could not find Edelina in Revenant's Toll.");

        if (Vector3.Distance(Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero, npc!.Position) > 3.0f)
        {
            var step = new MoveOp(o => o.Move(MorDhonaTerritoryId, npc.Position,
                MovementConfig.GroundMove.WithTolerance(2.0f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(step, 15_000, "mount-approach");
        }

        Status = "Interacting with Edelina";
        var interact = new MoveOp(o => o.Interact(npc!,
            waitUntil: () => AnyEdelinaShopOpen() || ShopInteraction.SelectIconStringOpen() || IsSelectStringOpen(),
            skip: UiSkipOptions.Talk));
        await RunCancellable(interact, InteractWaitMs, "mount-interact");

        ErrorIf(!await WaitUntilTimed(
                () => AnyEdelinaShopOpen() || ShopInteraction.SelectIconStringOpen() || IsSelectStringOpen(),
                ShopOpenWaitMs, "wait-edelina-menu"),
            "Edelina did not open a menu within timeout; aborting.");

        // 5. If submenu open, pick "Turali Bicolor Gemstone" FIRST (then fallback to "Bicolor" / "Voucher")
        if (ShopInteraction.SelectIconStringOpen() || IsSelectStringOpen())
        {
            await NavigateEdelinaMenu(targetVoucherId);
        }

        ErrorIf(!AnyEdelinaShopOpen(),
            "Could not open Edelina's voucher exchange shop.");

        // 6. Buy Whistle (supports both ShopExchangeItem and ShopExchangeCurrency)
        var vouchersBefore = CurrentVoucherCount(targetVoucherId);
        var whistleBefore = CurrentWhistleCount(targetItemId);

        Status = "Purchasing Mount Whistle";
        bool bought = false;
        if (ShopInteraction.ShopExchangeCurrencyOpen())
            bought = ShopInteraction.BuyFromCurrencyShop(targetItemId, 1);
        else if (IsShopExchangeItemOpen())
            bought = BuyFromExchangeItemShop(targetItemId);

        ErrorIf(!bought,
            $"Mount Whistle (ID {targetItemId}) not found in Edelina's shop.");

        if (!await WaitUntilTimed(
                () => AnyConfirmDialogOpen() || CurrentVoucherCount(targetVoucherId) < vouchersBefore,
                ConfirmWaitMs, "wait-confirm-whistle"))
            Diag("No confirm dialog detected; continuing.");

        ConfirmPurchaseDialog();

        await WaitUntilTimed(
            () => CurrentWhistleCount(targetItemId) > whistleBefore,
            ExchangeWaitMs, "wait-whistle-received");

        Status = "Closing shop";
        CloseAllEdelinaMenus();
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

    private static bool AnyEdelinaShopOpen()
        => ShopInteraction.ShopExchangeCurrencyOpen() || IsShopExchangeItemOpen();

    private static unsafe bool IsShopExchangeItemOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItem", out var addon) && GenericHelpers.IsAddonReady(addon);

    private static unsafe bool AnyConfirmDialogOpen()
        => ShopInteraction.SelectYesnoOpen()
        || (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItemDialog", out var d1) && GenericHelpers.IsAddonReady(d1))
        || (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrencyDialog", out var d2) && GenericHelpers.IsAddonReady(d2));

    private static unsafe void ConfirmPurchaseDialog()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItemDialog", out var itemDlg) && GenericHelpers.IsAddonReady(itemDlg))
        {
            // FireCallback(0) is crash-safe and confirms the exchange directly
            Callback.Fire(itemDlg, true, 0);
            return;
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrencyDialog", out var curDlg) && GenericHelpers.IsAddonReady(curDlg))
        {
            Callback.Fire(curDlg, true, 0);
            return;
        }
        if (ShopInteraction.SelectYesnoOpen())
        {
            ShopInteraction.ClickSelectYesno();
        }
    }

    private static string NormalizeShopName(string s)
        => s.Replace('\u2019', '\'').Replace('\u2018', '\'').Trim();

    private static unsafe bool BuyFromExchangeItemShop(uint targetItemId)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItem", out var addon) || !GenericHelpers.IsAddonReady(addon))
            return false;

        var rawTargetName = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(targetItemId)?.Name.ExtractText() ?? "Ty'aitya Whistle";
        var targetItemName = NormalizeShopName(rawTargetName);

        // Scan AtkComponentList in ShopExchangeItem for matching item name
        for (var n = 0; n < addon->UldManager.NodeListCount; n++)
        {
            var node = addon->UldManager.NodeList[n];
            if (node == null || node->Type != NodeType.Component) continue;
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component == null || compNode->Component->GetComponentType() != ComponentType.List) continue;

            var list = (AtkComponentList*)compNode->Component;
            for (var row = 0; row < list->ListLength; row++)
            {
                var renderer = list->ItemRendererList[row].AtkComponentListItemRenderer;
                if (renderer == null) continue;
                var uld = &renderer->AtkComponentButton.AtkComponentBase.UldManager;
                for (var j = 0; j < uld->NodeListCount; j++)
                {
                    var child = uld->NodeList[j];
                    if (child == null || child->Type != NodeType.Text) continue;
                    var text = NormalizeShopName(((AtkTextNode*)child)->NodeText.ToString());
                    if (!string.IsNullOrEmpty(text) && text.Contains(targetItemName, StringComparison.OrdinalIgnoreCase))
                    {
                        // ShopExchangeItem requires 3 args: (0 = exchange, row = item index, 1 = quantity)!
                        Svc.Log.Info($"[FateFrenzy] ShopExchangeItem: found '{text}' at row {row}, firing Callback(0, {row}, 1).");
                        Callback.Fire(addon, true, 0, row, 1);
                        return true;
                    }
                }
            }
        }

        // Fallback: also scan ShopExchangeItem AtkValues for item names (handles scrolled/virtualized list items)
        var itemCount = addon->AtkValuesCount > 1 ? addon->AtkValues[1].Int : 0;
        if (itemCount > 0 && itemCount < 200)
        {
            for (var i = 0; i < itemCount; i++)
            {
                var nameIdx = 3 + i;
                if (nameIdx >= addon->AtkValuesCount) break;
                var val = addon->AtkValues[nameIdx];
                if (val.Type is FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String
                    or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ConstString
                    or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString
                    && val.String.Value != null)
                {
                    var str = NormalizeShopName(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)val.String.Value).TextValue);
                    if (!string.IsNullOrEmpty(str) && str.Contains(targetItemName, StringComparison.OrdinalIgnoreCase))
                    {
                        Svc.Log.Info($"[FateFrenzy] ShopExchangeItem (AtkValues): found '{str}' at row {i}, firing Callback(0, {i}, 1).");
                        Callback.Fire(addon, true, 0, i, 1);
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static unsafe void CloseAllEdelinaMenus()
    {
        ShopInteraction.CloseShop();
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeItem", out var itemShop) && GenericHelpers.IsAddonReady(itemShop))
            Callback.Fire(itemShop, true, -1);
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectIconString, out var iconStr) && GenericHelpers.IsAddonReady(iconStr))
            Callback.Fire(iconStr, true, -1);
    }

    private static unsafe bool IsSelectStringOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var addon) && GenericHelpers.IsAddonReady(addon);

    private unsafe Task NavigateEdelinaMenu(uint targetVoucherId)
    {
        // Turali Bicolor Gemstone Voucher = 43961 (Dawntrail — Ty'aitya Whistle).
        // Edelina lists the Endwalker "Bicolor Gemstone Voucher" entry BEFORE "Turali Bicolor Gemstone Voucher",
        // so we MUST search specifically for "Turali" in pass 1 before falling back to "Bicolor"/"Voucher"!
        var preferTurali = targetVoucherId == 43961;

        if (ShopInteraction.SelectIconStringOpen()
            && GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectIconString, out var iconAddon)
            && GenericHelpers.IsAddonReady(iconAddon))
        {
            var master = new AddonMaster.SelectIconString(iconAddon);
            Diag($"Edelina SelectIconString open with {master.Entries.Length} options.");

            if (preferTurali)
            {
                for (var i = 0; i < master.Entries.Length; i++)
                {
                    var text = master.Entries[i].Text;
                    if (!string.IsNullOrEmpty(text) && text.Contains("Turali", StringComparison.OrdinalIgnoreCase))
                    {
                        Diag($"Found Turali menu entry at index {i}: '{text}'");
                        master.Entries[i].Select();
                        return WaitUntilTimed(AnyEdelinaShopOpen, SubmenuWaitMs, "wait-turali-shop");
                    }
                }
            }

            for (var i = 0; i < master.Entries.Length; i++)
            {
                var text = master.Entries[i].Text;
                if (!string.IsNullOrEmpty(text) &&
                    (text.Contains("Bicolor", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Voucher", StringComparison.OrdinalIgnoreCase)))
                {
                    Diag($"Found Bicolor menu entry at index {i}: '{text}'");
                    master.Entries[i].Select();
                    return WaitUntilTimed(AnyEdelinaShopOpen, SubmenuWaitMs, "wait-bicolor-shop");
                }
            }

            if (master.Entries.Length > 0)
            {
                master.Entries[0].Select();
                return WaitUntilTimed(AnyEdelinaShopOpen, SubmenuWaitMs, "wait-bicolor-shop-default");
            }
        }
        else if (IsSelectStringOpen()
            && GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var strAddon)
            && GenericHelpers.IsAddonReady(strAddon))
        {
            var master = new AddonMaster.SelectString(strAddon);
            Diag($"Edelina SelectString open with {master.Entries.Length} options.");

            if (preferTurali)
            {
                for (var i = 0; i < master.Entries.Length; i++)
                {
                    var text = master.Entries[i].Text;
                    if (!string.IsNullOrEmpty(text) && text.Contains("Turali", StringComparison.OrdinalIgnoreCase))
                    {
                        Diag($"Found Turali SelectString entry at index {i}: '{text}'");
                        master.Entries[i].Select();
                        return WaitUntilTimed(AnyEdelinaShopOpen, SubmenuWaitMs, "wait-turali-shop");
                    }
                }
            }

            for (var i = 0; i < master.Entries.Length; i++)
            {
                var text = master.Entries[i].Text;
                if (!string.IsNullOrEmpty(text) &&
                    (text.Contains("Bicolor", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("Voucher", StringComparison.OrdinalIgnoreCase)))
                {
                    Diag($"Found Bicolor SelectString entry at index {i}: '{text}'");
                    master.Entries[i].Select();
                    return WaitUntilTimed(AnyEdelinaShopOpen, SubmenuWaitMs, "wait-bicolor-shop");
                }
            }
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

using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FateFrenzy.Core.Game.Ops;
using FateFrenzy.Core.Ipc;
using FateFrenzy.Core.VoucherTrading;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed class AutoRetainerSell(bool isUndercutOnly = false) : AutoCommon
{
    private readonly bool isUndercutOnly = isUndercutOnly;

    private const int WorldTravelWatchdogMs = 120_000;
    private const int MoveWatchdogMs = 60_000;
    private const int InteractWaitMs = 15_000;
    private const int AddonWaitMs = 12_000;
    private const int ActionDelayMs = 600;

    protected override async Task Execute()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player is null) return;

        var homeWorld = player.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
        var currentWorld = player.CurrentWorld.ValueNullable?.Name.ToString() ?? string.Empty;

        // 1. Ensure we are on Home World
        if (!string.IsNullOrEmpty(homeWorld) && !currentWorld.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))
        {
            Status = $"Returning to Home World ({homeWorld})";
            Diag($"Player is currently on {currentWorld}, moving to Home World {homeWorld}");

            Chat.SendMessage($"/li {homeWorld}");
            var travelDeadline = Environment.TickCount64 + WorldTravelWatchdogMs;
            while (Environment.TickCount64 < travelDeadline && !CancelToken.IsCancellationRequested)
            {
                await DelayMs(1000);
                if (LifestreamIPC.IsBusy()) continue;

                var cur = Svc.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString();
                if (cur != null && cur.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))
                {
                    Diag("Arrived on Home World.");
                    break;
                }
            }

            if (!IsOnHomeWorld())
            {
                ErrorIf(true, $"Could not return to Home World {homeWorld} within timeout.");
                return;
            }
        }

        // 2. Go to Inn if configured
        if (Plugin.Cfg.MountUsePrivateInn && !IsInsideInn())
        {
            Status = "Retiring to Inn room for private retainer access";
            var inn = new AutoReturnToInn();
            var tcs = new TaskCompletionSource();
            inn.Run(() => tcs.TrySetResult());
            await tcs.Task;
        }

        // 3. Locate Summoning Bell
        Status = "Searching for Summoning Bell";
        var bell = FindSummoningBell();
        if (bell == null)
        {
            ErrorIf(true, "No Summoning Bell found nearby.");
            return;
        }

        // 4. Approach and Interact with Bell
        var bellPos = bell.Position;
        if (Vector3.Distance(player.Position, bellPos) > 3.0f)
        {
            Status = "Approaching Summoning Bell";
            var walk = new MoveOp(o => o.Move(Svc.ClientState.TerritoryType, bellPos,
                MovementConfig.Everything.WithTolerance(2.0f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(walk, MoveWatchdogMs, "bell-approach");
        }

        Status = "Interacting with Summoning Bell";
        var interact = new MoveOp(o => o.Interact(bell, waitUntil: IsRetainerListOpen, skip: UiSkipOptions.None));
        await RunCancellable(interact, InteractWaitMs, "bell-interact");

        ErrorIf(!await WaitUntilTimed(IsRetainerListOpen, AddonWaitMs, "wait-retainer-list"),
            "Retainer list did not open.");

        // 5. Select Retainer
        Status = $"Selecting Retainer #{Plugin.Cfg.MountRetainerIndex + 1}";
        await DelayMs(ActionDelayMs);
        if (!SelectRetainerIndex(Plugin.Cfg.MountRetainerIndex))
        {
            ErrorIf(true, $"Failed to select Retainer index {Plugin.Cfg.MountRetainerIndex}.");
            return;
        }

        ErrorIf(!await WaitUntilTimed(IsSelectStringOpen, AddonWaitMs, "wait-retainer-menu"),
            "Retainer menu did not open.");

        // 6. Fetch Universalis Prices & Compute Anti-Bait Price
        Status = "Querying Universalis market prices";
        var itemId = Plugin.Cfg.MountTargetItemId; // 43590
        var market = await UniversalisPriceService.GetListingsAsync(homeWorld, itemId, forceRefresh: true);

        var calculatedPrice = UniversalisPriceService.CalculateUndercutPrice(
            market,
            Plugin.Cfg.MountInitialPrice,
            Plugin.Cfg.MountFloorPrice,
            Plugin.Cfg.MountUndercutAmount);

        Diag($"Calculated target price for item {itemId}: {calculatedPrice:N0} gil (Floor: {Plugin.Cfg.MountFloorPrice:N0})");

        // 7. Handle Listing or Adjusting
        var whistleCount = CurrentWhistleCount(itemId);
        if (!isUndercutOnly && whistleCount > 0)
        {
            Status = $"Listing Mount Whistle for {calculatedPrice:N0} gil";
            await ProcessSellNewItem(itemId, (int)calculatedPrice);
        }
        else
        {
            Status = $"Checking and adjusting listing price to {calculatedPrice:N0} gil";
            await ProcessAdjustPrice(itemId, (int)calculatedPrice);
        }

        // 8. Quit Retainer Menu
        Status = "Closing Retainer";
        await ExitRetainerMenu();
        await DelayMs(ActionDelayMs);

        Svc.Chat.Print($"[FateFrenzy] Retainer market update complete (Price: {calculatedPrice:N0} gil).");
    }

    private static bool IsOnHomeWorld()
    {
        var p = Svc.Objects.LocalPlayer;
        if (p == null) return false;
        var home = p.HomeWorld.ValueNullable?.Name.ToString();
        var cur = p.CurrentWorld.ValueNullable?.Name.ToString();
        return home != null && home.Equals(cur, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideInn()
    {
        var t = Svc.ClientState.TerritoryType;
        return t is 177 or 178 or 179; // Limsa, Ul'dah, Gridania Inns
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FindSummoningBell()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null) return null;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDist = float.MaxValue;

        foreach (var obj in Svc.Objects)
        {
            var name = obj.Name.ToString();
            if (name.Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase) || obj.BaseId is 2000001 or 2000401)
            {
                var d = Vector3.Distance(player.Position, obj.Position);
                if (d < bestDist)
                {
                    best = obj;
                    bestDist = d;
                }
            }
        }
        return best;
    }

    private static unsafe bool IsRetainerListOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var a) && GenericHelpers.IsAddonReady(a);

    private static unsafe bool IsSelectStringOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var a) && GenericHelpers.IsAddonReady(a);

    private static unsafe bool SelectRetainerIndex(int index)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
            return false;

        var master = new AddonMaster.RetainerList(addon);
        if (index < 0 || index >= master.Retainers.Length) return false;
        return master.Retainers[index].Select();
    }

    private static unsafe bool IsRetainerSellListOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var a) && GenericHelpers.IsAddonReady(a);

    private static unsafe uint GetOpenInventoryAddonId()
    {
        var names = new[] { "InventoryRetainerLarge", "InventoryExpansion", "InventoryLarge", "Inventory" };
        foreach (var name in names)
        {
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon) && GenericHelpers.IsAddonReady(addon))
                return addon->Id;
        }
        return 0;
    }

    private static unsafe (InventoryType bag, int slot)? FindItemSlot(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return null;

        var bags = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4
        };

        foreach (var bag in bags)
        {
            var container = im->GetInventoryContainer(bag);
            if (container == null) continue;

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->ItemId == itemId && item->Quantity > 0)
                {
                    return (bag, slot);
                }
            }
        }
        return null;
    }

    private static unsafe bool TryOpenInventorySlotContextMenu(InventoryType bag, int slot, uint addonId)
    {
        var aic = AgentInventoryContext.Instance();
        if (aic == null) return false;
        aic->OpenForItemSlot(bag, slot, 0, addonId);
        return true;
    }

    private static unsafe bool IsRetainerSellOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var a) && GenericHelpers.IsAddonReady(a);

    private static unsafe bool IsContextMenuOpen()
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var a) && GenericHelpers.IsAddonReady(a);

    private static unsafe bool TrySelectSellFromContextMenu()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) || !GenericHelpers.IsAddonReady(addon))
            return false;

        var menu = new AddonMaster.ContextMenu(addon);
        for (var i = 0; i < menu.Entries.Length; i++)
        {
            var text = menu.Entries[i].Text;
            if (text.Contains("Sell", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Put Up", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Market", StringComparison.OrdinalIgnoreCase))
            {
                if (!menu.Entries[i].Select())
                {
                    Callback.Fire(addon, true, 0, i, 0);
                }
                return true;
            }
        }
        return false;
    }

    private static unsafe bool TrySetRetainerSellPrice(int price)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            var sell = new AddonMaster.RetainerSell(addon);
            sell.AskingPrice = price;
            return true;
        }
        return false;
    }

    private static unsafe bool ConfirmRetainerSell()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            var sell = new AddonMaster.RetainerSell(addon);
            sell.Confirm();
            return true;
        }
        return false;
    }

    private static unsafe bool CancelRetainerSell()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            var sell = new AddonMaster.RetainerSell(addon);
            sell.Cancel();
            return true;
        }
        return false;
    }

    private static unsafe bool TryGetRetainerSellPrice(out int currentPrice)
    {
        currentPrice = 0;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            var sell = new AddonMaster.RetainerSell(addon);
            currentPrice = sell.AskingPrice;
            return true;
        }
        return false;
    }

    private static unsafe bool ClickRetainerSellListSlot(int index)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, 0, index);
            return true;
        }
        return false;
    }

    private static unsafe void CloseRetainerSellList()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, -1);
        }
    }

    private async Task ProcessSellNewItem(uint itemId, int price)
    {
        Diag("Selecting 'Sell items in your inventory on the market.' from Retainer menu...");
        ClickMenuEntryContaining("Sell items in your inventory");
        await DelayMs(ActionDelayMs * 2);

        // Wait for RetainerSellList and Inventory to open
        var waitWindowDeadline = Environment.TickCount64 + AddonWaitMs;
        while (Environment.TickCount64 < waitWindowDeadline && !CancelToken.IsCancellationRequested)
        {
            if (IsRetainerSellListOpen() && GetOpenInventoryAddonId() != 0)
                break;
            await DelayMs(200);
        }

        var slotInfo = FindItemSlot(itemId);
        if (slotInfo == null)
        {
            Diag($"Could not find Mount Whistle (ID {itemId}) in player inventory.");
            CloseRetainerSellList();
            return;
        }

        var invAddonId = GetOpenInventoryAddonId();
        Diag($"Found Whistle in bag {slotInfo.Value.bag} slot {slotInfo.Value.slot}. Triggering inventory context menu (addonId={invAddonId})...");
        TryOpenInventorySlotContextMenu(slotInfo.Value.bag, slotInfo.Value.slot, invAddonId);

        // Wait for either ContextMenu or RetainerSell addon
        var waitDialogDeadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < waitDialogDeadline && !CancelToken.IsCancellationRequested)
        {
            if (IsRetainerSellOpen())
            {
                break;
            }

            if (IsContextMenuOpen())
            {
                Diag("ContextMenu detected; selecting sell entry...");
                if (TrySelectSellFromContextMenu())
                {
                    await DelayMs(600);
                }
            }

            await DelayMs(200);
        }

        // Set asking price and confirm sale
        if (IsRetainerSellOpen())
        {
            Diag($"Setting asking price to {price:N0} gil...");
            TrySetRetainerSellPrice(price);
            await DelayMs(ActionDelayMs);
            ConfirmRetainerSell();
            await DelayMs(ActionDelayMs * 2);
        }
        else
        {
            Diag("RetainerSell dialog did not appear for setting price.");
        }

        // Close RetainerSellList and return to SelectString
        CloseRetainerSellList();
        await DelayMs(ActionDelayMs);
        await WaitUntilTimed(IsSelectStringOpen, AddonWaitMs, "wait-return-to-retainer-menu");
    }

    private async Task ProcessAdjustPrice(uint itemId, int price)
    {
        Diag("Selecting 'Have retainer sell items on the market.' from Retainer menu...");
        ClickMenuEntryContaining("Have retainer sell");
        await DelayMs(ActionDelayMs * 2);

        if (!await WaitUntilTimed(IsRetainerSellListOpen, AddonWaitMs, "wait-retainer-sell-list"))
        {
            Diag("RetainerSellList did not open for price adjustment.");
            return;
        }

        // Click slot 0 in RetainerSellList
        Diag("Clicking item slot 0 in RetainerSellList...");
        ClickRetainerSellListSlot(0);
        await DelayMs(ActionDelayMs);

        if (await WaitUntilTimed(IsRetainerSellOpen, AddonWaitMs, "wait-retainer-sell-price"))
        {
            if (TryGetRetainerSellPrice(out var curPrice))
            {
                Diag($"RetainerSell open. Current asking price: {curPrice:N0}, target price: {price:N0}");
                if (curPrice != price)
                {
                    TrySetRetainerSellPrice(price);
                    await DelayMs(ActionDelayMs);
                    ConfirmRetainerSell();
                    await DelayMs(ActionDelayMs);
                }
                else
                {
                    Diag("Asking price is already equal to target price; cancelling dialog.");
                    CancelRetainerSell();
                    await DelayMs(ActionDelayMs);
                }
            }
        }

        CloseRetainerSellList();
        await DelayMs(ActionDelayMs);
        await WaitUntilTimed(IsSelectStringOpen, AddonWaitMs, "wait-return-to-retainer-menu");
    }

    private static unsafe void ClickMenuEntryContaining(string text)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var addon) || !GenericHelpers.IsAddonReady(addon))
            return;

        var master = new AddonMaster.SelectString(addon);
        for (var i = 0; i < master.Entries.Length; i++)
        {
            if (master.Entries[i].Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                master.Entries[i].Select();
                return;
            }
        }
    }

    private async Task ExitRetainerMenu()
    {
        ClickMenuEntryContaining("Quit");
        await DelayMs(ActionDelayMs);

        CloseRetainerList();
    }

    private static unsafe void CloseRetainerList()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, -1);
        }
    }

    private static unsafe int CurrentWhistleCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        return im != null ? im->GetInventoryItemCount(itemId, false, false, false) : 0;
    }
}

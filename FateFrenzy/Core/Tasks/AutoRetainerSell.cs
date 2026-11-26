using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FateFrenzy.Core.Game.Ops;
using FateFrenzy.Core.Game.Player;
using FateFrenzy.Core.Ipc;
using FateFrenzy.Core.VoucherTrading;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

public sealed class AutoRetainerSell(bool isUndercutOnly = false) : AutoCommon
{
    private readonly bool isUndercutOnly = isUndercutOnly;

    private const int TeleportWatchdogMs = 60_000;
    private const int MoveWatchdogMs = 120_000;
    private const int DismountWatchdogMs = 30_000;
    private const int NavmeshReadyWaitMs = 60_000;
    private const int EnterInnTimeoutMs = 60_000;
    private const int InteractWaitMs = 15_000;
    private const int AddonWaitMs = 15_000;
    private const int ActionDelayMs = 600;

    // Limsa Lominsa Lower Decks (128) Hawkers' Alley Summoning Bell fallback when Private Inn is disabled
    private const uint LimsaLowerDecksTerritoryId = 128;
    private static readonly Vector3 LimsaBellFallbackPos = new(-123.5f, 18.0f, 19.8f);

    public bool ListingSuccess { get; private set; }

    private readonly record struct InnInfo(uint CityTerritory, uint InnTerritory, uint InnkeeperDataId, Vector3 InnkeeperPos, string City);

    private static unsafe InnInfo ResolveGrandCompanyInn()
    {
        var ps = PlayerState.Instance();
        var gc = ps is null ? 0 : ps->GrandCompany;
        return gc switch
        {
            GrandCompanyId.TwinAdder => new InnInfo(132, 179, 1000102, new Vector3(25.6627f, -8f, 99.74237f), "Gridania"),
            GrandCompanyId.ImmortalFlames => new InnInfo(130, 178, 1001976, new Vector3(28.85994f, 6.999999f, -80.12716f), "Ul'dah"),
            _ => new InnInfo(128, 177, 1000974, new Vector3(15.42688f, 39.99999f, 12.466553f), "Limsa Lominsa"),
        };
    }

    protected override async Task Execute()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player is null) return;

        var homeWorld = player.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty;
        var currentWorld = player.CurrentWorld.ValueNullable?.Name.ToString() ?? string.Empty;

        // 1. Ensure we are on Home World (Retainers can only be accessed on Home World)
        if (!string.IsNullOrEmpty(homeWorld) && !currentWorld.Equals(homeWorld, StringComparison.OrdinalIgnoreCase))
        {
            Svc.Chat.Print($"[FateFrenzy] Returning to Home World '{homeWorld}' to access Retainer...");
            var arrived = false;
            await RunWithStatusPinned($"Returning to Home World ({homeWorld})",
                async () => arrived = await TravelToWorld(homeWorld, "retainer-home-world"));

            ErrorIf(!arrived, $"Could not return to Home World {homeWorld} within timeout.");
        }

        // 2. Travel to a Summoning Bell (Private Inn room if enabled, otherwise Limsa Lominsa Hawkers' Alley)
        await EnsureAtSummoningBell();
        if (CancelToken.IsCancellationRequested) return;

        // 3. Locate Summoning Bell
        Status = "Searching for Summoning Bell";
        var bell = FindSummoningBell();
        ErrorIf(bell is null, "No Summoning Bell found nearby.");

        // 4. Approach and Interact with Bell (use fresh LocalPlayer position after zoning!)
        var bellPos = bell!.Position;
        var currentPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        if (Vector3.Distance(currentPos, bellPos) > 3.0f)
        {
            await WaitForNavmeshReady(NavmeshReadyWaitMs);
            if (CancelToken.IsCancellationRequested) return;

            Status = "Approaching Summoning Bell";
            var walk = new MoveOp(o => o.Move(Svc.ClientState.TerritoryType, bellPos,
                MovementConfig.GroundMove.WithTolerance(2.0f),
                allowTeleportIfFaster: false,
                stopCondition: null,
                allowAethernetWithinTerritory: false));
            await RunCancellable(walk, MoveWatchdogMs, "bell-approach", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
        }

        Status = "Interacting with Summoning Bell";
        var interact = new MoveOp(o => o.Interact(bell, waitUntil: IsRetainerListOpen, skip: UiSkipOptions.Talk));
        await RunCancellable(interact, InteractWaitMs, "bell-interact");

        ErrorIf(!await WaitUntilTimed(IsRetainerListOpen, AddonWaitMs, "wait-retainer-list"),
            "Retainer list did not open.");

        // 5. Select Retainer and click through greeting Talk dialog until SelectString opens
        Status = $"Selecting Retainer #{Plugin.Cfg.MountRetainerIndex + 1}";
        await DelayMs(ActionDelayMs);
        ErrorIf(!SelectRetainerIndex(Plugin.Cfg.MountRetainerIndex),
            $"Failed to select Retainer index {Plugin.Cfg.MountRetainerIndex}.");

        var menuOpened = await WaitForRetainerMainMenu(AddonWaitMs);
        ErrorIf(!menuOpened, "Retainer menu (SelectString) did not open.");

        // 6. Fetch Universalis Prices & Compute Anti-Bait Price
        Status = "Querying Universalis market prices";
        var itemId = Plugin.Cfg.MountTargetItemId; // 43590 (Ty'aitya Whistle)
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
            ListingSuccess = await ProcessSellNewItem(itemId, (int)calculatedPrice);
        }
        else
        {
            Status = $"Checking and adjusting listing price to {calculatedPrice:N0} gil";
            ListingSuccess = await ProcessAdjustPrice(itemId, (int)calculatedPrice);
        }

        // 8. Quit Retainer Menu cleanly
        Status = "Closing Retainer";
        await ExitRetainerMenu();
        await DelayMs(ActionDelayMs);

        if (ListingSuccess)
        {
            Svc.Chat.Print($"[FateFrenzy] Retainer market update complete! Listed at {calculatedPrice:N0} gil.");
        }
        else
        {
            Svc.Chat.PrintError("[FateFrenzy] Could not complete Retainer listing — check if Retainer has free market slots.");
        }
    }

    private async Task EnsureAtSummoningBell()
    {
        if (Plugin.Cfg.MountUsePrivateInn)
        {
            if (IsInsideInn())
            {
                Diag("Already inside an Inn room.");
                return;
            }

            var inn = ResolveGrandCompanyInn();
            Svc.Chat.Print($"[FateFrenzy] Entering Private Inn room in {inn.City} for Retainer access...");

            if (Svc.ClientState.TerritoryType != inn.CityTerritory)
            {
                var reached = false;
                await RunWithStatusPinned($"Teleporting to {inn.City}",
                    async () => reached = await TeleportToTerritory(inn.CityTerritory, Vector3.Zero, "retainer-inn-tp", TeleportWatchdogMs));
                ErrorIf(!reached, $"Could not teleport to {inn.City} for Inn room.");
            }

            await WaitForNavmeshReady(NavmeshReadyWaitMs);
            if (CancelToken.IsCancellationRequested) return;

            if (Svc.Condition[ConditionFlag.Mounted])
                await RunCancellable(new MoveOp(o => o.DismountNow()), DismountWatchdogMs, "retainer-inn-dismount");

            Status = $"Walking to Innkeeper in {inn.City}";
            var walk = new MoveOp(o => o.Move(inn.CityTerritory, inn.InnkeeperPos,
                MovementConfig.Everything.WithTolerance(3.5f),
                allowTeleportIfFaster: false,
                stopCondition: () => CancelToken.IsCancellationRequested,
                allowAethernetWithinTerritory: true));
            await RunCancellable(walk, MoveWatchdogMs, "retainer-inn-walk", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
            if (CancelToken.IsCancellationRequested) return;

            await EnterInnRoom(inn);
            await DelayMs(1000);
            return;
        }

        // Non-Inn mode: if no Summoning Bell is already nearby, teleport to Limsa Lominsa Lower Decks (Hawkers' Alley)
        if (FindSummoningBell() is not null) return;

        if (Svc.ClientState.TerritoryType != LimsaLowerDecksTerritoryId)
        {
            var reached = false;
            await RunWithStatusPinned("Teleporting to Limsa Lominsa",
                async () => reached = await TeleportToTerritory(LimsaLowerDecksTerritoryId, LimsaBellFallbackPos, "retainer-limsa-tp", TeleportWatchdogMs));
            ErrorIf(!reached, "Could not teleport to Limsa Lominsa for Summoning Bell.");
        }

        await WaitForNavmeshReady(NavmeshReadyWaitMs);
        if (CancelToken.IsCancellationRequested) return;

        if (FindSummoningBell() is null)
        {
            Status = "Walking to Hawkers' Alley Summoning Bell";
            var walk = new MoveOp(o => o.Move(LimsaLowerDecksTerritoryId, LimsaBellFallbackPos,
                MovementConfig.GroundMove.WithTolerance(3.0f),
                allowTeleportIfFaster: false,
                stopCondition: () => FindSummoningBell() is not null,
                allowAethernetWithinTerritory: true));
            await RunCancellable(walk, MoveWatchdogMs, "retainer-limsa-bell-walk", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
        }
    }

    private async Task EnterInnRoom(InnInfo inn)
    {
        Status = $"Entering Private Inn in {inn.City}";
        var deadline = Environment.TickCount64 + EnterInnTimeoutMs;
        while (Environment.TickCount64 < deadline && !CancelToken.IsCancellationRequested)
        {
            if (Svc.ClientState.TerritoryType == inn.InnTerritory && !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51])
            {
                Diag($"Entered Inn room ({inn.InnTerritory}).");
                return;
            }

            if (DriveInnDialogs())
            {
                await DelayMs(250);
                continue;
            }

            var npc = RepairOps.FindObjectByBaseId(inn.InnkeeperDataId);
            if (npc is null)
            {
                await DelayMs(250);
                continue;
            }

            var player = Svc.Objects.LocalPlayer;
            if (player is not null && Vector3.Distance(player.Position, npc.Position) > 5.0f)
            {
                var step = new MoveOp(o => o.Move(inn.CityTerritory, npc.Position,
                    MovementConfig.GroundMove.WithTolerance(2.5f),
                    allowTeleportIfFaster: false,
                    stopCondition: () => CancelToken.IsCancellationRequested,
                    allowAethernetWithinTerritory: false));
                await RunCancellable(step, 25_000, "retainer-inn-approach", StuckDetector.IdleStallAbort(StuckDetector.IdleStallTimeoutMs));
                continue;
            }

            var interact = new MoveOp(o => o.Interact(npc, waitUntil: AnyInnDialogOpen, skip: UiSkipOptions.Talk));
            await RunCancellable(interact, 8_000, "retainer-inn-interact");
            await DelayMs(500);
        }

        ErrorIf(!IsInsideInn(), $"Timed out entering Inn room in {inn.City}.");
    }

    private static unsafe bool AnyInnDialogOpen()
        => (GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var selectString) && GenericHelpers.IsAddonReady(selectString))
        || (GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectYesno, out var yesno) && GenericHelpers.IsAddonReady(yesno))
        || (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && GenericHelpers.IsAddonReady(talk));

    private static unsafe bool DriveInnDialogs()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var ss) && GenericHelpers.IsAddonReady(ss))
        {
            if (EzThrottler.Throttle("AFG.RetainerInnSelectString", 600))
            {
                var m = new AddonMaster.SelectString((nint)ss);
                if (m.EntryCount > 0) m.Entries[0].Select();
            }
            return true;
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectYesno, out var yn) && GenericHelpers.IsAddonReady(yn))
        {
            if (EzThrottler.Throttle("AFG.RetainerInnSelectYesno", 600))
                new AddonMaster.SelectYesno((nint)yn).Yes();
            return true;
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var tk) && GenericHelpers.IsAddonReady(tk))
        {
            if (EzThrottler.Throttle("AFG.RetainerInnTalk", 350))
                new AddonMaster.Talk((nint)tk).Click();
            return true;
        }
        return false;
    }

    private async Task<bool> WaitForRetainerMainMenu(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !CancelToken.IsCancellationRequested)
        {
            if (IsSelectStringOpen()) return true;
            ClickTalkIfOpen();
            await DelayMs(200);
        }
        return IsSelectStringOpen();
    }

    private static unsafe bool ClickTalkIfOpen()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var talk) && GenericHelpers.IsAddonReady(talk))
        {
            if (EzThrottler.Throttle("AFG.RetainerTalk", 300))
                new AddonMaster.Talk((nint)talk).Click();
            return true;
        }
        return false;
    }

    private static bool IsInsideInn()
    {
        var t = Svc.ClientState.TerritoryType;
        return t is 177 or 178 or 179 or 429 or 629 or 843 or 990 or 1205;
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
            if (text.Contains("Put Up", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Sell", StringComparison.OrdinalIgnoreCase) ||
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
            Callback.Fire(addon, true, 2, price);
            return true;
        }
        return false;
    }

    private static unsafe bool ConfirmRetainerSell()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            // Use native AtkUnitBase FireCallback(0) instead of AddonMaster.Confirm() (ClickButtonIfEnabled),
            // which dereferences a null AtkEventData pointer in AddonRetainerSell.ReceiveEvent+0x1B5 on Patch 7.x!
            Callback.Fire(addon, true, 0);
            return true;
        }
        return false;
    }

    private static unsafe bool CancelRetainerSell()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            Callback.Fire(addon, true, 1);
            return true;
        }
        return false;
    }

    private static unsafe bool TryGetRetainerSellPrice(out int currentPrice)
    {
        currentPrice = 0;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            if (addon->AtkValuesCount > 5)
            {
                currentPrice = addon->AtkValues[5].Int;
                return true;
            }
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

    private async Task<bool> ProcessSellNewItem(uint itemId, int price)
    {
        var whistleBefore = CurrentWhistleCount(itemId);
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
            return false;
        }

        var invAddonId = GetOpenInventoryAddonId();
        Diag($"Found Whistle in bag {slotInfo.Value.bag} slot {slotInfo.Value.slot}. Opening inventory context menu (addonId={invAddonId})...");
        await Svc.Framework.RunOnTick(() => TryOpenInventorySlotContextMenu(slotInfo.Value.bag, slotInfo.Value.slot, invAddonId));

        // Wait for ContextMenu -> select Put Up for Sale -> wait for RetainerSell
        var waitDialogDeadline = Environment.TickCount64 + 10_000;
        var contextClicked = false;
        while (Environment.TickCount64 < waitDialogDeadline && !CancelToken.IsCancellationRequested)
        {
            if (IsRetainerSellOpen())
                break;

            if (IsContextMenuOpen() && !contextClicked)
            {
                Diag("ContextMenu detected; selecting 'Put Up for Sale'...");
                var selected = false;
                await Svc.Framework.RunOnTick(() => selected = TrySelectSellFromContextMenu());
                if (selected)
                {
                    contextClicked = true;
                    await DelayMs(500);
                }
            }

            await DelayMs(200);
        }

        var listed = false;
        if (IsRetainerSellOpen())
        {
            Diag($"Setting asking price to {price:N0} gil...");
            await Svc.Framework.RunOnTick(() => TrySetRetainerSellPrice(price));
            await DelayMs(ActionDelayMs);
            await Svc.Framework.RunOnTick(() => ConfirmRetainerSell());

            listed = await WaitUntilTimed(
                () => CurrentWhistleCount(itemId) < whistleBefore || !IsRetainerSellOpen(),
                8_000, "wait-whistle-listed");
            await DelayMs(ActionDelayMs);
        }
        else
        {
            Diag("RetainerSell dialog did not appear for setting price.");
        }

        // Close RetainerSellList and return to SelectString
        CloseRetainerSellList();
        await DelayMs(ActionDelayMs);
        await WaitUntilTimed(IsSelectStringOpen, AddonWaitMs, "wait-return-to-retainer-menu");
        return listed && CurrentWhistleCount(itemId) < whistleBefore;
    }

    private async Task<bool> ProcessAdjustPrice(uint itemId, int price)
    {
        Diag("Selecting 'Sell items in your retainer's inventory on the market.' / 'Have retainer sell' from Retainer menu...");
        if (!ClickMenuEntryContaining("Have retainer sell"))
            ClickMenuEntryContaining("Sell items in your inventory");
        await DelayMs(ActionDelayMs * 2);

        if (!await WaitUntilTimed(IsRetainerSellListOpen, AddonWaitMs, "wait-retainer-sell-list"))
        {
            Diag("RetainerSellList did not open for price adjustment.");
            return false;
        }

        Diag("Clicking item slot 0 in RetainerSellList...");
        ClickRetainerSellListSlot(0);
        await DelayMs(ActionDelayMs);

        var adjusted = false;
        if (await WaitUntilTimed(IsRetainerSellOpen, AddonWaitMs, "wait-retainer-sell-price"))
        {
            if (TryGetRetainerSellPrice(out var curPrice))
            {
                Diag($"RetainerSell open. Current asking price: {curPrice:N0}, target price: {price:N0}");
                if (curPrice != price)
                {
                    await Svc.Framework.RunOnTick(() => TrySetRetainerSellPrice(price));
                    await DelayMs(ActionDelayMs);
                    await Svc.Framework.RunOnTick(() => ConfirmRetainerSell());
                    await DelayMs(ActionDelayMs);
                }
                else
                {
                    Diag("Asking price is already equal to target price; cancelling dialog.");
                    await Svc.Framework.RunOnTick(() => CancelRetainerSell());
                    await DelayMs(ActionDelayMs);
                }
                adjusted = true;
            }
        }

        CloseRetainerSellList();
        await DelayMs(ActionDelayMs);
        await WaitUntilTimed(IsSelectStringOpen, AddonWaitMs, "wait-return-to-retainer-menu");
        return adjusted;
    }

    private static unsafe bool ClickMenuEntryContaining(string text)
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>(AfgConstants.AddonNames.SelectString, out var addon) || !GenericHelpers.IsAddonReady(addon))
            return false;

        var master = new AddonMaster.SelectString(addon);
        for (var i = 0; i < master.Entries.Length; i++)
        {
            if (master.Entries[i].Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                master.Entries[i].Select();
                return true;
            }
        }
        return false;
    }

    private async Task ExitRetainerMenu()
    {
        ClickMenuEntryContaining("Quit");

        var deadline = Environment.TickCount64 + 8_000;
        while (Environment.TickCount64 < deadline && !CancelToken.IsCancellationRequested)
        {
            if (IsRetainerListOpen()) break;
            ClickTalkIfOpen();
            await DelayMs(200);
        }

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

using FateFrenzy.Core.Game.Ops;
using clib.TaskSystem;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Linq;

namespace FateFrenzy.Core.Tasks;

public sealed partial class AutoFate
{
    private const int ResummonChocoboTimeLeft = 180; // 3 minutes

    private static unsafe float GetBuddyTimeRemaining()
    {
        var ui = UIState.Instance();
        if (ui == null) return 0f;
        return ui->Buddy.CompanionInfo.TimeLeft;
    }

    private static unsafe int GetGysahlGreensCount()
    {
        var im = InventoryManager.Instance();
        return im is null ? 0 : im->GetInventoryItemCount(4868);
    }

    private static unsafe bool IsMerchantWindowOpen()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Shop", out var s) && GenericHelpers.IsAddonReady(s)) return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrency", out var sec) && GenericHelpers.IsAddonReady(sec)) return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var ss) && GenericHelpers.IsAddonReady(ss)) return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectIconString", out var sis) && GenericHelpers.IsAddonReady(sis)) return true;
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var t) && GenericHelpers.IsAddonReady(t)) return true;
        return false;
    }

    private static unsafe bool ClickTalk()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Talk", out var addon)) return false;
        if (!GenericHelpers.IsAddonReady(addon)) return false;
        new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk(addon).Click();
        return true;
    }

    private static unsafe bool ClickSelectString(int index)
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            var master = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectString(addon);
            if (index >= 0 && index < master.Entries.Length)
            {
                master.Entries[index].Select();
                return true;
            }
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectIconString", out var iconAddon) && GenericHelpers.IsAddonReady(iconAddon))
        {
            var master = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(iconAddon);
            if (index >= 0 && index < master.Entries.Length)
            {
                master.Entries[index].Select();
                return true;
            }
        }
        return false;
    }

    private static unsafe bool ClickShopAndBuy()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("Shop", out var addon)) return false;
        if (!GenericHelpers.IsAddonReady(addon)) return false;
        var master = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Shop(addon);
        var items = master.ShopItems;
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].ItemId == 4868)
            {
                items[i].Select(99);
                return true;
            }
        }
        return false;
    }

    private async Task EnsureChocobo()
    {
        var stance = Plugin.Cfg.ChocoboStance;
        if (stance == "None") return;

        // Skip chocobo in special field exploration zones (South Horn / North Horn)
        var territory = Svc.ClientState.TerritoryType;
        if (territory == 1252 || territory == 1346) return;

        if (GetBuddyTimeRemaining() <= ResummonChocoboTimeLeft)
        {
            if (GetGysahlGreensCount() > 0)
            {
                if (Svc.Condition[ConditionFlag.Mounted])
                {
                    await DismountViaOp("chocobo-summon-dismount");
                }

                Diag("Summoning chocobo companion...");
                unsafe
                {
                    var am = ActionManager.Instance();
                    if (am != null)
                    {
                        am->UseAction(ActionType.Item, 4868, extraParam: 65535);
                    }
                }
                await DelayMs(3000); // Wait for summon cast/spawn

                Diag($"Setting chocobo stance to {stance}");
                Chat.ExecuteCommand($"/cac \"{stance} stance\"");
                await DelayMs(1000);
            }
        }
    }

    private async Task DoBuyGysahlGreens()
    {
        if (GetGysahlGreensCount() > 0)
        {
            if (ShopInteraction.CloseShop())
            {
                await DelayMs(500);
            }
            return;
        }

        var territory = Svc.ClientState.TerritoryType;
        if (territory != 129) // Limsa Lower Decks
        {
            Status = "Teleporting to Limsa to buy greens";
            Diag("No Gysahl Greens left. Teleporting to Limsa Lominsa Lower Decks...");
            var dest = new Vector3(-13.25f, 18.0f, 15.0f);
            await TeleportToTerritory(129, dest, "limsa-greens", perAttemptTimeoutMs: 30000);
            return;
        }

        var vendorPos = new Vector3(-62.1f, 18.0f, 9.4f);
        var player = Svc.Objects.LocalPlayer;
        if (player is null) return;

        var dist = Vector3.Distance(player.Position, vendorPos);
        if (dist > 5f)
        {
            Status = "Moving to Bango Zango";
            Diag($"Moving to Bango Zango ({dist:F0}m)");
            var move = new MoveOp(o => o.Move(129, vendorPos, MovementConfig.InteractRange, allowTeleportIfFaster: false, stopCondition: null, allowAethernetWithinTerritory: false));
            await RunCancellable(move, 45000, "move-to-greens-vendor");
            return;
        }

        if (Svc.Condition[ConditionFlag.Mounted])
        {
            await DismountViaOp("greens-vendor-dismount");
            return;
        }

        Status = "Interacting with Bango Zango";
        var vendor = Svc.Objects.FirstOrDefault(o => o.Name.TextValue == "Bango Zango" && o.IsTargetable);
        if (vendor is null)
        {
            Diag("Could not find Bango Zango targetable nearby.");
            await DelayMs(1000);
            return;
        }

        if (Svc.Targets.Target?.Address != vendor.Address)
        {
            Svc.Targets.Target = vendor;
            await DelayMs(500);
        }

        if (ClickTalk())
        {
            await DelayMs(300);
            return;
        }

        if (ClickSelectString(0))
        {
            await DelayMs(500);
            return;
        }

        if (ShopInteraction.SelectYesnoOpen())
        {
            Diag("Confirming Gysahl Greens purchase...");
            ShopInteraction.ClickSelectYesno();
            await DelayMs(500);
            return;
        }

        if (ClickShopAndBuy())
        {
            Diag("Greens purchased successfully.");
            await DelayMs(1000);
            return;
        }

        if (!Svc.Condition[ConditionFlag.Occupied])
        {
            Diag("Interacting with Bango Zango...");
            InteractWith(vendor);
            await DelayMs(1000);
        }
    }

    private static unsafe void InteractWith(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (ts != null)
        {
            ts->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address);
        }
    }
}

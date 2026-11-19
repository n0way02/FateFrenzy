using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FateFrenzy.Core.Game.Watchers;

// Handles automatic login on game launch and automatic relogin after a disconnect.
// Modeled after Lifestream's TaskChangeCharacter flow for TitleMenu, TitleDCWorldMap,
// _CharaSelectListMenu, and _CharaSelectWorldServer.
internal sealed unsafe class AutoLoginWatcher : IDisposable
{
    private const long ActionCooldownMs = 1200;

    private long nextActionTick = 0;
    private long nextWorldSwitchTick = 0;
    private bool wasLoggedIn = false;
    private bool hasLoggedInOnceThisLaunch = false;
    private bool pendingReloginFromDisconnect = false;
    private bool dcSelectedThisAttempt = false;

    private string lastCharName = string.Empty;
    private string lastCharWorld = string.Empty;

    public AutoLoginWatcher()
    {
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework _)
    {
        var cfg = Plugin.Cfg;
        var isLoggedIn = Svc.ClientState.IsLoggedIn;

        if (isLoggedIn)
        {
            hasLoggedInOnceThisLaunch = true;
            pendingReloginFromDisconnect = false;
            dcSelectedThisAttempt = false;

            if (!wasLoggedIn)
                wasLoggedIn = true;

            var player = Svc.Objects.LocalPlayer;
            if (player is not null)
            {
                lastCharName = player.Name.TextValue;
                lastCharWorld = player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
            }
            return;
        }

        // Transition: Logged in -> Logged out / Disconnected
        if (wasLoggedIn)
        {
            wasLoggedIn = false;
            dcSelectedThisAttempt = false;
            if (cfg.WasRunningBeforeDisconnect || Plugin.Instance.Controller.SessionSnapshot is not null)
                pendingReloginFromDisconnect = true;
        }

        var wantLaunchLogin = cfg.AutoLoginOnLaunch && !hasLoggedInOnceThisLaunch;
        var hasErrorPopup = IsDisconnectPopupOpen();
        var wantRelogin = cfg.AutoReloginOnDisconnect && (pendingReloginFromDisconnect || hasErrorPopup);

        if (!wantLaunchLogin && !wantRelogin)
            return;

        var now = Environment.TickCount64;
        if (now < nextActionTick)
            return;

        TryAdvanceLogin(cfg, now);
    }

    private static bool IsDisconnectPopupOpen()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Dialogue", out var dlg) && GenericHelpers.IsAddonReady(dlg))
            return true;

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectOk", out var ok) && GenericHelpers.IsAddonReady(ok))
        {
            var text = new AddonMaster.SelectOk(ok).Text;
            if (!IsQueuePrompt(text))
                return true;
        }

        return false;
    }

    private static bool IsQueuePrompt(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("queue", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fila", StringComparison.OrdinalIgnoreCase)
            || text.Contains("players remaining", StringComparison.OrdinalIgnoreCase);
    }

    private (string name, string world) ResolveTargetCharacter(Configuration cfg)
    {
        if (pendingReloginFromDisconnect && !string.IsNullOrEmpty(lastCharName))
            return (lastCharName, lastCharWorld);

        return (cfg.AutoLoginCharacterName?.Trim() ?? string.Empty, cfg.AutoLoginWorldName?.Trim() ?? string.Empty);
    }

    private void TryAdvanceLogin(Configuration cfg, long now)
    {
        try
        {
            // 1. Dismiss disconnect error popups (Dialogue / SelectOk), ignoring queue popups
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Dialogue", out var dlg) && GenericHelpers.IsAddonReady(dlg))
            {
                Svc.Log.Info("[FateFrenzy] AutoLogin: dismissing Dialogue error popup.");
                new AddonMaster.Dialogue(dlg).Ok();
                nextActionTick = now + ActionCooldownMs;
                return;
            }

            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectOk", out var ok) && GenericHelpers.IsAddonReady(ok))
            {
                var okMaster = new AddonMaster.SelectOk(ok);
                if (!IsQueuePrompt(okMaster.Text))
                {
                    Svc.Log.Info("[FateFrenzy] AutoLogin: dismissing SelectOk popup.");
                    okMaster.Ok();
                    nextActionTick = now + ActionCooldownMs;
                }
                return;
            }

            // Wait quietly while lobby connection is in progress
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleConnect", out _))
                return;

            var (targetName, targetWorld) = ResolveTargetCharacter(cfg);
            int targetDcId = 0;
            string targetDcName = string.Empty;
            if (!string.IsNullOrEmpty(targetWorld) && ExcelWorldHelper.TryGet(targetWorld, out var wInfo))
            {
                targetDcId = (int)wInfo.DataCenter.RowId;
                targetDcName = wInfo.DataCenter.ValueNullable?.Name.ToString() ?? targetDcId.ToString();
            }

            // 2. Data Center selection screen (TitleDCWorldMap)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var dcAddon) && GenericHelpers.IsAddonReady(dcAddon))
            {
                if (!dcSelectedThisAttempt && targetDcId > 0)
                {
                    Svc.Log.Info($"[FateFrenzy] AutoLogin: selecting Data Center {targetDcName} (id={targetDcId}) for world {targetWorld}.");
                    var dcMaster = new AddonMaster.TitleDCWorldMap(dcAddon);
                    if (dcMaster.Select(targetDcId))
                    {
                        dcSelectedThisAttempt = true;
                        nextActionTick = now + 500;
                    }
                    else
                    {
                        Svc.Log.Warning($"[FateFrenzy] AutoLogin: TitleDCWorldMap.Select({targetDcId}) returned false.");
                        nextActionTick = now + ActionCooldownMs;
                    }
                }
                else if (dcSelectedThisAttempt)
                {
                    // Close TitleDCWorldMap after selection just like Lifestream's SelectServiceAccount
                    Svc.Log.Debug("[FateFrenzy] AutoLogin: closing TitleDCWorldMap after DC selection.");
                    dcAddon->Close(true);
                    nextActionTick = now + 1500;
                }
                return;
            }

            // 3. Title Screen (_TitleMenu): open Data Center menu if needed, otherwise click Start
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var titleAddon))
            {
                var title = new AddonMaster._TitleMenu(titleAddon);
                if (title.IsReady)
                {
                    if (!dcSelectedThisAttempt && targetDcId > 0)
                    {
                        Svc.Log.Info($"[FateFrenzy] AutoLogin: opening Data Center menu for world {targetWorld} (DC={targetDcName}).");
                        title.DataCenter();
                        // Note: do NOT set dcSelectedThisAttempt = true here; wait until TitleDCWorldMap actually opens and selects!
                        nextActionTick = now + 1200;
                        return;
                    }

                    Svc.Log.Info("[FateFrenzy] AutoLogin: clicking Start on Title Menu.");
                    title.Start();
                    nextActionTick = now + 2000;
                }
                return;
            }

            // 4. Character Select confirmation ("Log in with <Name>?")
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var ynAddon) && GenericHelpers.IsAddonReady(ynAddon))
            {
                Svc.Log.Info("[FateFrenzy] AutoLogin: confirming character login (SelectYesno Yes).");
                new AddonMaster.SelectYesno(ynAddon).Yes();
                nextActionTick = now + 2500;
                return;
            }

            // 5. Character Select List (_CharaSelectListMenu) + World List (_CharaSelectWorldServer)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var charaAddon) && GenericHelpers.IsAddonReady(charaAddon))
            {
                var menu = new AddonMaster._CharaSelectListMenu(charaAddon);
                if (menu.TemporarilyLocked) return;

                var chars = menu.Characters;
                var target = FindCharacter(chars, targetName, targetWorld);

                if (target is not null)
                {
                    if (target.CanLoginNormally)
                    {
                        Svc.Log.Info($"[FateFrenzy] AutoLogin: logging in as {target.Name}@{ExcelWorldHelper.GetName(target.HomeWorld)}.");
                        target.Login();
                        nextActionTick = now + 2000;
                    }
                    return;
                }

                // Target character is not in the currently displayed world's character list.
                // Check the _CharaSelectWorldServer sidebar (which is always open on Character Select).
                if (!string.IsNullOrEmpty(targetWorld) &&
                    GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectWorldServer", out var worldAddon) &&
                    GenericHelpers.IsAddonReady(worldAddon))
                {
                    var worldMenu = new AddonMaster._CharaSelectWorldServer(worldAddon);
                    if (worldMenu.Worlds.Length == 0) return;

                    var worldMatch = worldMenu.Worlds.FirstOrDefault(w => w.Name.Equals(targetWorld, StringComparison.OrdinalIgnoreCase));
                    if (worldMatch is not null)
                    {
                        if (now >= nextWorldSwitchTick)
                        {
                            Svc.Log.Info($"[FateFrenzy] AutoLogin: switching world sidebar to {worldMatch.Name}.");
                            worldMatch.Select();
                            nextWorldSwitchTick = now + 2000;
                            nextActionTick = now + 1500;
                        }
                        return;
                    }

                    // Target world is NOT in this Data Center! Return to Title Screen to pick the right DC.
                    if (targetDcId > 0 &&
                        GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectReturn", out var retAddon) &&
                        GenericHelpers.IsAddonReady(retAddon))
                    {
                        Svc.Log.Info($"[FateFrenzy] AutoLogin: world '{targetWorld}' is not on this Data Center; returning to Title Screen to switch DC.");
                        dcSelectedThisAttempt = false;
                        var retBtn = retAddon->GetComponentButtonById(4);
                        if (retBtn != null && retBtn->IsEnabled)
                        {
                            retBtn->ClickAddonButton(retAddon);
                        }
                        nextActionTick = now + 2500;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] AutoLogin: error while advancing login.");
            nextActionTick = now + 2500;
        }
    }

    private static AddonMaster._CharaSelectListMenu.Character? FindCharacter(
        AddonMaster._CharaSelectListMenu.Character[] chars,
        string targetName,
        string targetWorld)
    {
        if (chars.Length == 0) return null;

        if (!string.IsNullOrEmpty(targetName))
        {
            foreach (var c in chars)
            {
                if (!c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(targetWorld))
                {
                    var homeName = ExcelWorldHelper.GetName(c.HomeWorld);
                    if (!homeName.Equals(targetWorld, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                return c;
            }
            return null;
        }

        if (!string.IsNullOrEmpty(targetWorld))
        {
            return chars.FirstOrDefault(c =>
                ExcelWorldHelper.GetName(c.HomeWorld).Equals(targetWorld, StringComparison.OrdinalIgnoreCase));
        }

        return chars[0];
    }
}

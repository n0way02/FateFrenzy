using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FateFrenzy.Core.Game.Watchers;

// Handles automatic login on game launch and automatic relogin after a disconnect.
// Both features are disabled by default and operate strictly on the Title Screen / Lobby
// when Svc.ClientState.IsLoggedIn is false.
internal sealed unsafe class AutoLoginWatcher : IDisposable
{
    private const long ActionCooldownMs = 1500;

    private long nextActionTick = 0;
    private bool wasLoggedIn = false;
    private bool hasLoggedInOnceThisLaunch = false;
    private bool pendingReloginFromDisconnect = false;

    private string lastCharName = string.Empty;
    private uint lastCharHomeWorld = 0;

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

            if (!wasLoggedIn)
                wasLoggedIn = true;

            var player = Svc.Objects.LocalPlayer;
            if (player is not null)
            {
                lastCharName = player.Name.TextValue;
                lastCharHomeWorld = player.HomeWorld.RowId;
            }
            return;
        }

        // Transition: Logged in -> Logged out / Disconnected
        if (wasLoggedIn)
        {
            wasLoggedIn = false;
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
            || text.Contains("fila", StringComparison.OrdinalIgnoreCase);
    }

    private void TryAdvanceLogin(Configuration cfg, long now)
    {
        try
        {
            // 1. Dismiss disconnect error popups (Dialogue / SelectOk)
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

            // Wait quietly if lobby connection is currently in progress
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleConnect", out _))
                return;

            // 2. Title Screen: click Start
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var titleAddon))
            {
                var title = new AddonMaster._TitleMenu(titleAddon);
                if (title.IsReady)
                {
                    Svc.Log.Info("[FateFrenzy] AutoLogin: clicking Start on Title Menu.");
                    title.Start();
                    nextActionTick = now + 2000;
                }
                return;
            }

            // 3. Character Select confirmation ("Log in with <Name>?")
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var ynAddon) && GenericHelpers.IsAddonReady(ynAddon))
            {
                Svc.Log.Info("[FateFrenzy] AutoLogin: confirming character login (SelectYesno Yes).");
                new AddonMaster.SelectYesno(ynAddon).Yes();
                nextActionTick = now + 2500;
                return;
            }

            // 4. Character Select List: select and log in
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var charaAddon) && GenericHelpers.IsAddonReady(charaAddon))
            {
                var menu = new AddonMaster._CharaSelectListMenu(charaAddon);
                if (menu.TemporarilyLocked) return;

                var chars = menu.Characters;
                if (chars.Length == 0) return;

                var target = PickTargetCharacter(chars, cfg);
                if (target is null) return;

                if (!target.IsSelected)
                {
                    Svc.Log.Info($"[FateFrenzy] AutoLogin: selecting character {target.Name}.");
                    target.Select();
                    nextActionTick = now + 1000;
                    return;
                }

                if (target.CanLoginNormally)
                {
                    Svc.Log.Info($"[FateFrenzy] AutoLogin: logging in as {target.Name}.");
                    target.Login();
                    nextActionTick = now + 2000;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] AutoLogin: error while advancing login.");
            nextActionTick = now + 2500;
        }
    }

    private AddonMaster._CharaSelectListMenu.Character? PickTargetCharacter(AddonMaster._CharaSelectListMenu.Character[] chars, Configuration cfg)
    {
        // On disconnect relogin, prefer the exact character that was logged in
        if (pendingReloginFromDisconnect && !string.IsNullOrEmpty(lastCharName))
        {
            var match = chars.FirstOrDefault(c =>
                c.Name.Equals(lastCharName, StringComparison.OrdinalIgnoreCase) &&
                (lastCharHomeWorld == 0 || c.HomeWorld == lastCharHomeWorld));

            if (match is not null)
                return match;
        }

        var idx = Math.Clamp(cfg.AutoLoginCharacterSlot, 0, chars.Length - 1);
        return chars[idx];
    }
}

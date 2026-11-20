using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace FateFrenzy.Core.Game.Watchers;

// Handles automatic login on game launch and automatic relogin after a disconnect.
// Uses an explicit state machine modeled directly after Lifestream's TaskChangeCharacter
// and AutoLogin's TitleDCWorldMap / _CharaSelectListMenu flow.
internal sealed unsafe class AutoLoginWatcher : IDisposable
{
    private enum LoginStep
    {
        Idle,
        OpeningDataCenter,
        SelectingDataCenter,
        ConnectingToLobby,
        SelectingCharacter,
        ConfirmingLogin,
    }

    private const long ActionCooldownMs = 1200;

    private LoginStep step = LoginStep.Idle;
    private long nextActionTick = 0;
    private long nextWorldSwitchTick = 0;
    private long stepEnteredTick = 0;

    private bool wasLoggedIn = false;
    private bool hasLoggedInOnceThisLaunch = false;
    private bool pendingReloginFromDisconnect = false;

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

    private void SetStep(LoginStep newStep, long now)
    {
        if (step != newStep)
        {
            Svc.Log.Info($"[FateFrenzy] AutoLogin step: {step} -> {newStep}");
            step = newStep;
            stepEnteredTick = now;
        }
    }

    private void OnUpdate(IFramework _)
    {
        var cfg = Plugin.Cfg;
        var isLoggedIn = Svc.ClientState.IsLoggedIn;

        if (isLoggedIn)
        {
            hasLoggedInOnceThisLaunch = true;
            pendingReloginFromDisconnect = false;
            step = LoginStep.Idle;

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
            step = LoginStep.Idle;
            if (cfg.WasRunningBeforeDisconnect || Plugin.Instance.Controller.SessionSnapshot is not null)
                pendingReloginFromDisconnect = true;
        }

        var wantLaunchLogin = cfg.AutoLoginOnLaunch && !hasLoggedInOnceThisLaunch;
        var hasErrorPopup = IsDisconnectPopupOpen();
        var wantRelogin = cfg.AutoReloginOnDisconnect && (pendingReloginFromDisconnect || hasErrorPopup);

        if (!wantLaunchLogin && !wantRelogin)
        {
            step = LoginStep.Idle;
            return;
        }

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
            // Always dismiss disconnect error popups first (ignoring queue prompts)
            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Dialogue", out var dlg) && GenericHelpers.IsAddonReady(dlg))
            {
                Svc.Log.Info("[FateFrenzy] AutoLogin: dismissing Dialogue error popup.");
                new AddonMaster.Dialogue(dlg).Ok();
                SetStep(LoginStep.Idle, now);
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
                    SetStep(LoginStep.Idle, now);
                    nextActionTick = now + ActionCooldownMs;
                }
                return;
            }

            var (targetName, targetWorld) = ResolveTargetCharacter(cfg);
            int targetDcId = 0;
            string targetDcName = string.Empty;
            if (!string.IsNullOrEmpty(targetWorld) && ExcelWorldHelper.TryGet(targetWorld, out var wInfo))
            {
                targetDcId = (int)wInfo.Unknown0;
                if (targetDcId == 0) targetDcId = (int)wInfo.DataCenter.RowId;
                targetDcName = Svc.Data.GetExcelSheet<WorldDCGroupType>()?.GetRowOrDefault((uint)targetDcId)?.Name.ToString() ?? targetDcId.ToString();
            }

            // Initialize step based on whatever screen is currently visible
            if (step == LoginStep.Idle)
            {
                if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var cs) && cs->IsVisible)
                    SetStep(LoginStep.SelectingCharacter, now);
                else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var dc) && dc->IsVisible)
                    SetStep(LoginStep.SelectingDataCenter, now);
                else if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_TitleMenu", out _))
                    SetStep(LoginStep.OpeningDataCenter, now);
                else
                    return;
            }

            switch (step)
            {
                case LoginStep.OpeningDataCenter:
                {
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var dcAddon) && dcAddon->IsVisible)
                    {
                        SetStep(LoginStep.SelectingDataCenter, now);
                        nextActionTick = now + 400;
                        return;
                    }

                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var csAddon) && csAddon->IsVisible)
                    {
                        SetStep(LoginStep.SelectingCharacter, now);
                        return;
                    }

                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var titleAddon))
                    {
                        var title = new AddonMaster._TitleMenu(titleAddon);
                        if (title.IsReady)
                        {
                            if (targetDcId > 0)
                            {
                                Svc.Log.Info($"[FateFrenzy] AutoLogin: opening Data Center menu for {targetWorld} (DC={targetDcName}, id={targetDcId}).");
                                title.DataCenter();
                                nextActionTick = now + 1000;
                            }
                            else
                            {
                                Svc.Log.Info("[FateFrenzy] AutoLogin: no target world/DC configured; clicking Start.");
                                title.Start();
                                SetStep(LoginStep.ConnectingToLobby, now);
                                nextActionTick = now + 1500;
                            }
                        }
                    }
                    break;
                }

                case LoginStep.SelectingDataCenter:
                {
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var csAddon) && csAddon->IsVisible)
                    {
                        SetStep(LoginStep.SelectingCharacter, now);
                        return;
                    }

                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var dcAddon) && GenericHelpers.IsAddonReady(dcAddon))
                    {
                        if (targetDcId > 0)
                        {
                            Svc.Log.Info($"[FateFrenzy] AutoLogin: firing TitleDCWorldMap.Select({targetDcId}) for {targetDcName}.");
                            if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMapBg", out var dcBg) && dcBg != null)
                                dcBg->Hide(true, true, 0);
                            dcAddon->Hide(true, true, 0);

                            var dcMaster = new AddonMaster.TitleDCWorldMap(dcAddon);
                            dcMaster.Select(targetDcId);
                            dcAddon->Close(true);

                            SetStep(LoginStep.ConnectingToLobby, now);
                            nextActionTick = now + 1500;
                        }
                        else
                        {
                            SetStep(LoginStep.OpeningDataCenter, now);
                        }
                    }
                    break;
                }

                case LoginStep.ConnectingToLobby:
                {
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var dcAddon) && dcAddon != null)
                    {
                        dcAddon->Close(true);
                    }

                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var csAddon) && GenericHelpers.IsAddonReady(csAddon))
                    {
                        SetStep(LoginStep.SelectingCharacter, now);
                        return;
                    }

                    // Handle Service Account selection popup if present
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var ssAddon) && GenericHelpers.IsAddonReady(ssAddon))
                    {
                        var ss = new AddonMaster.SelectString(ssAddon);
                        var compareTo = Svc.Data.GetExcelSheet<Lobby>()?.GetRowOrDefault(11)?.Text.ExtractText() ?? string.Empty;
                        if (!string.IsNullOrEmpty(compareTo) && ss.Text == compareTo && ss.Entries.Length > 0)
                        {
                            Svc.Log.Info("[FateFrenzy] AutoLogin: selecting first Service Account.");
                            ss.Entries[0].Select();
                            nextActionTick = now + 1500;
                            return;
                        }
                    }

                    // Safety timeout if stuck back on _TitleMenu for >10s
                    if (now - stepEnteredTick > 10000 &&
                        GenericHelpers.TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var titleAddon) &&
                        !GenericHelpers.TryGetAddonByName<AtkUnitBase>("TitleConnect", out _))
                    {
                        var title = new AddonMaster._TitleMenu(titleAddon);
                        if (title.IsReady)
                        {
                            Svc.Log.Warning("[FateFrenzy] AutoLogin: timed out waiting for lobby; retrying Data Center selection.");
                            SetStep(LoginStep.OpeningDataCenter, now);
                        }
                    }
                    break;
                }

                case LoginStep.SelectingCharacter:
                {
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var ynAddon) && GenericHelpers.IsAddonReady(ynAddon))
                    {
                        SetStep(LoginStep.ConfirmingLogin, now);
                        return;
                    }

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
                                SetStep(LoginStep.ConfirmingLogin, now);
                                nextActionTick = now + 1500;
                            }
                            return;
                        }

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
                                    nextActionTick = now + 1200;
                                }
                                return;
                            }

                            // Wrong Data Center! Return to Title Screen to pick the right DC.
                            if (targetDcId > 0 &&
                                GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectReturn", out var retAddon) &&
                                GenericHelpers.IsAddonReady(retAddon))
                            {
                                Svc.Log.Info($"[FateFrenzy] AutoLogin: '{targetWorld}' is not in this Data Center; returning to Title Screen.");
                                var retBtn = retAddon->GetComponentButtonById(4);
                                if (retBtn != null && retBtn->IsEnabled)
                                {
                                    retBtn->ClickAddonButton(retAddon);
                                    SetStep(LoginStep.OpeningDataCenter, now);
                                    nextActionTick = now + 2500;
                                }
                            }
                        }
                    }
                    break;
                }

                case LoginStep.ConfirmingLogin:
                {
                    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var ynAddon) && GenericHelpers.IsAddonReady(ynAddon))
                    {
                        Svc.Log.Info("[FateFrenzy] AutoLogin: confirming character login (SelectYesno Yes).");
                        new AddonMaster.SelectYesno(ynAddon).Yes();
                        nextActionTick = now + 2500;
                    }
                    else if (now - stepEnteredTick > 8000 &&
                             GenericHelpers.TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var csAddon) &&
                             csAddon->IsVisible &&
                             !GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectOk", out _))
                    {
                        SetStep(LoginStep.SelectingCharacter, now);
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[FateFrenzy] AutoLogin: error while advancing login.");
            nextActionTick = now + 2000;
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

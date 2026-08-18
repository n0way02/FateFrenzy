using FateFrenzy.Core;
using FateFrenzy.Core.Debug;
using FateFrenzy.Core.Game.Watchers;
using FateFrenzy.Core.Stats;
using FateFrenzy.Core.Tasks;
using FateFrenzy.Core.Zones;
using FateFrenzy.Windows;
using clib;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FateFrenzy;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    internal Configuration Configuration { get; }
    internal static Configuration Cfg { get; private set; } = null!;
    internal WindowSystem WindowSystem { get; } = new("FateFrenzy");
    internal RunHistory History { get; }
    internal AutoFateController Controller { get; }
    private readonly GmAlertWatcher gmAlertWatcher;
    private readonly PartyInviteWatcher partyInviteWatcher;
    private readonly DutyWatcher dutyWatcher;

    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly AboutWindow aboutWindow;
    private readonly DependenciesWindow dependenciesWindow;
    private readonly RunHistoryWindow runHistoryWindow;
    internal LiveFateWindow LiveFateWindow { get; }

    private readonly EventHandler<UnobservedTaskExceptionEventArgs> unobservedTaskHandler;

    public Plugin()
    {
        Instance = this;

        ECommonsMain.Init(PluginInterface, this);
        CLibMain.Init(PluginInterface, this, CLibModule.Automation);

        unobservedTaskHandler = OnUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += unobservedTaskHandler;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Cfg = Configuration;
        History = new RunHistory();
        Controller = new AutoFateController();
        gmAlertWatcher = new GmAlertWatcher();
        partyInviteWatcher = new PartyInviteWatcher();
        dutyWatcher = new DutyWatcher();

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        aboutWindow = new AboutWindow();
        dependenciesWindow = new DependenciesWindow();
        runHistoryWindow = new RunHistoryWindow();
        LiveFateWindow = new LiveFateWindow(this) { IsOpen = Configuration.ShowLivePopout };

        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(aboutWindow);
        WindowSystem.AddWindow(dependenciesWindow);
        WindowSystem.AddWindow(runHistoryWindow);
        WindowSystem.AddWindow(LiveFateWindow);

        CommandManager.AddHandler(AfgConstants.PrimaryCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the FateFrenzy window. /fatefrenzy config | stats | deps | about | pause (pause or resume the run) | target (dump current target's BaseId)."
        });
        CommandManager.AddHandler(AfgConstants.AliasCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /fatefrenzy."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Svc.ClientState.Login += OnLogin;
        Svc.Framework.Update += OnFrameworkUpdate;

        if (Svc.ClientState.IsLoggedIn)
        {
            OnLogin();
        }
    }

    // vnavmesh/BossMod run their obstacle-map and pathfind IPC on fire-and-forget Tasks we never get a
    // handle to (we only see a TaskStatus), so we can't ObserveLeak them. When one faults — e.g. a bitmap
    // build issued while the zone navmesh is still creating — its exception reaches the finalizer as
    // unobserved and gets rethrown as log noise. Mark only those (matched by the vnavmesh stack) observed.
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (e.Observed) return;
        if (e.Exception.ToString().Contains("Navmesh.IPCProvider"))
        {
            e.SetObserved();
            Log.Debug($"[FateFrenzy] Observed vnavmesh IPC task fault: {e.Exception.GetBaseException().Message}");
        }
    }

    public void Dispose()
    {
        Controller.StopTelemetry();

        TaskScheduler.UnobservedTaskException -= unobservedTaskHandler;

        Svc.ClientState.Login -= OnLogin;
        Svc.Framework.Update -= OnFrameworkUpdate;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();
        aboutWindow.Dispose();
        dependenciesWindow.Dispose();
        runHistoryWindow.Dispose();
        LiveFateWindow.Dispose();

        CommandManager.RemoveHandler(AfgConstants.PrimaryCommand);
        CommandManager.RemoveHandler(AfgConstants.AliasCommand);

        gmAlertWatcher.Dispose();
        partyInviteWatcher.Dispose();
        dutyWatcher.Dispose();

        CLibMain.Dispose();
        ECommonsMain.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("config", StringComparison.OrdinalIgnoreCase))
            ToggleConfigUi();
        else if (trimmed.Equals("about", StringComparison.OrdinalIgnoreCase))
            ToggleAboutUi();
        else if (trimmed.Equals("deps", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("dependencies", StringComparison.OrdinalIgnoreCase))
            ToggleDependenciesUi();
        else if (trimmed.Equals("stats", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("history", StringComparison.OrdinalIgnoreCase))
            ToggleHistoryUi();
        else if (trimmed.Equals("pause", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("resume", StringComparison.OrdinalIgnoreCase))
            Controller.TogglePause();
        else if (trimmed.Equals("target", StringComparison.OrdinalIgnoreCase))
            TargetDumper.Dump();
        else
            ToggleMainUi();
    }

    public void ToggleMainUi() => mainWindow.Toggle();
    public void ToggleConfigUi() => configWindow.Toggle();
    public void ToggleAboutUi() => aboutWindow.Toggle();
    public void ToggleDependenciesUi() => dependenciesWindow.Toggle();
    public void ToggleHistoryUi() => runHistoryWindow.Toggle();

    private void OnLogin()
    {
        if (Configuration.AutoResumeAfterDisconnect && Configuration.WasRunningBeforeDisconnect)
        {
            Task.Run(async () =>
            {
                Log.Info("[FateFrenzy] Login detected. Waiting for player character to load...");
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);
                    if (Svc.ClientState.IsLoggedIn && Svc.Objects.LocalPlayer is not null)
                    {
                        // Give it one more second to make sure UI and zones are ready
                        await Task.Delay(1000);

                        _ = Svc.Framework.RunOnFrameworkThread(() =>
                        {
                            if (Configuration.WasRunningBeforeDisconnect)
                            {
                                var zonesToRun = ZoneSelection.ResolveStartList(Configuration);
                                if (zonesToRun.Count > 0)
                                {
                                    Log.Info("[FateFrenzy] Auto-resuming after disconnect/startup...");
                                    Controller.RunAll(zonesToRun);
                                }
                            }
                        });
                        return;
                    }
                }
                Log.Warning("[FateFrenzy] Player character failed to load within 30 seconds. Auto-resume aborted.");
            });
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Svc.ClientState.IsLoggedIn)
        {
            if (Controller.SessionSnapshot is not null)
            {
                if (!Configuration.WasRunningBeforeDisconnect)
                {
                    Log.Info("[FateFrenzy] Disconnect detected while grinding! Setting WasRunningBeforeDisconnect = true.");
                    Configuration.WasRunningBeforeDisconnect = true;
                    Configuration.Save();
                }

                clib.Services.Svc.Automation.Stop();
                Controller.AbortOnDisconnect();
            }

            ClickSelectOkIfOpen();
        }
    }

    private unsafe void ClickSelectOkIfOpen()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectOk", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
            new AddonMaster.SelectOk(addon).Ok();
            Log.Info("[FateFrenzy] Clicked OK on disconnect/error dialog.");
        }
    }
}

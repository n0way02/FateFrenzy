using FateFrenzy.Core.External;
using FateFrenzy.Core.Game.Player;
using FateFrenzy.Core.Trading;
using FateFrenzy.Core.Zones;
using FateFrenzy.Core.Ipc;
using clib.Services;
using ECommons.Automation;
using System.Threading.Tasks;

namespace FateFrenzy.Core.Tasks;

internal sealed partial class AutoFateController
{
    public bool Running => Svc.Automation.Running || Paused;

    public string Status => PauseReason switch
    {
        PauseReason.InContent => "Paused while you are in content",
        PauseReason.Manual    => "Paused",
        _                     => Svc.Automation.CurrentTask?.Status ?? "Idle",
    };

    public AutoPhase Phase { get; private set; } = AutoPhase.Idle;

    private AutoFateSession? session;
    private IReadOnlyList<ZoneInfo> activeZones = [];
    public AutoFateSession? SessionSnapshot => session;

    private static readonly Random rng = new();

    private static void Diag(string message)
        => ECommons.DalamudServices.Svc.Log.Info($"{AfgConstants.LogPrefix} {message}");

    // First active-zone index whose territory matches origin (first match wins), or fallback when origin is
    // null / not in the current selection.
    private int ResumeIndexFor(ZoneInfo? origin, int fallback = 0)
    {
        if (origin is null) return fallback;
        for (var i = 0; i < activeZones.Count; i++)
            if (activeZones[i].TerritoryId == origin.TerritoryId) return i;
        return fallback;
    }

    public void RunAll(IEnumerable<ZoneInfo> zones)
    {
        var finalZones = zones.ToList();
        if (Plugin.Cfg.EnableMultiZone)
        {
            var currentTerritory = Svc.ClientState.TerritoryType;
            var currentZone = ZoneRegistry.Zones.FirstOrDefault(z => z.TerritoryId == currentTerritory);
            if (currentZone is not null)
            {
                var expansion = currentZone.Expansion;
                var expZones = ZoneRegistry.ByExpansion(expansion);
                var blacklist = new System.Collections.Generic.HashSet<string>(
                    Plugin.Cfg.BlacklistedZones.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().ToLowerInvariant())
                );
                finalZones = expZones
                    .Where(z => !blacklist.Contains(z.Name.ToLowerInvariant()))
                    .ToList();
            }
        }

        activeZones = finalZones;
        if (activeZones.Count == 0)
        {
            Diag("Start aborted: no zones selected.");
            return;
        }

        if (!ExternalPlugins.AllRequiredInstalled())
        {
            var missing = string.Join(", ", ExternalPlugins.All
                .Where(p => ExternalPlugins.Catalog[p].Required && !ExternalPlugins.IsInstalled(p))
                .Select(p => ExternalPlugins.Catalog[p].DisplayName));
            Diag($"Start aborted: required plugins missing ({missing}).");
            ECommons.DalamudServices.Svc.Chat.PrintError($"[FateFrenzy] Cannot start — install all required plugins first: {missing}.");
            return;
        }

        PauseReason = PauseReason.None;

        Plugin.Cfg.WasRunningBeforeDisconnect = true;
        Plugin.Cfg.Save();

        var startWallet = GemstoneCatalog.CurrentWalletCount();
        var s = new AutoFateSession
        {
            GemstoneCurrent = startWallet,
        };
        s.CaptureStartExp();
        session = s;
        Diag($"Run starting: {activeZones.Count} zone(s), mode {Plugin.Cfg.ActiveMode.DisplayName}, wallet {startWallet}g, threshold {Plugin.Cfg.TradeThreshold}g, trade-on-cap {(Plugin.Cfg.TradeOnCap ? "on" : "off")}.");

        ApplyStartingClass();
        StartFateGrind(0, s);

        if (ExternalPlugins.IsInstalled(ExternalPlugin.TextAdvance) && !TextAdvanceIPC.IsPluginEnabled())
        {
            Chat.ExecuteCommand("/at");
        }

        StartTelemetry(s);
    }

    private static void ApplyStartingClass()
    {
        var cfg = Plugin.Cfg;
        if (!cfg.ApplyClassOnStart) return;
        if (cfg.ClassQueue.Count == 0) return;

        var idx = ClassSwitcher.FindActiveEntryIndex(cfg.ClassQueue);
        if (idx < 0)
        {
            ECommons.DalamudServices.Svc.Chat.Print("[FateFrenzy] Class queue: every entry is at its level cap, staying on current class.");
            return;
        }
        var entry = cfg.ClassQueue[idx];
        var label = $"gearset {entry.GearsetIndex} ({ClassSwitcher.JobNameForUserIndex(entry.GearsetIndex)})";
        if (ClassSwitcher.TryEquip(entry))
            ECommons.DalamudServices.Svc.Chat.Print($"[FateFrenzy] Switching to {label}.");
        else
            ECommons.DalamudServices.Svc.Chat.PrintError($"[FateFrenzy] Could not equip {label} (game refused — combat, mount, or transient lock?). See /xllog for details.");
    }

    public void Stop()
    {
        Plugin.Cfg.WasRunningBeforeDisconnect = false;
        Plugin.Cfg.Save();

        StopTelemetry();

        var ending = session;
        currentTask = null;
        grindTask = null;
        PauseReason = PauseReason.None;
        Svc.Automation.Stop();

        if (ExternalPlugins.IsInstalled(ExternalPlugin.TextAdvance) && TextAdvanceIPC.IsPluginEnabled())
        {
            Chat.ExecuteCommand("/at");
        }

        FinalizeRun(ending);
        session = null;
        activeZones = [];
        Phase = AutoPhase.Idle;
        if (ending is not null) Diag("Stop requested; session cleared.");
    }

    public void AbortOnDisconnect()
    {
        currentTask = null;
        grindTask = null;
        Phase = AutoPhase.Idle;
    }

    private System.Threading.CancellationTokenSource? telemetryCts;

    private void StartTelemetry(AutoFateSession s)
    {
        telemetryCts?.Cancel();
        telemetryCts = new System.Threading.CancellationTokenSource();
        var token = telemetryCts.Token;

        Task.Run(async () =>
        {
            var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (clib.Services.Svc.Automation.Running && !Paused && session is not null)
                    {
                        string? playerName = null;
                        string? serverName = null;
                        string? currentServer = null;
                        string? currentMap = null;

                        var taskCompletion = new TaskCompletionSource<bool>();
                        _ = Svc.Framework.RunOnFrameworkThread(() =>
                        {
                            try
                            {
                                var pc = Svc.Objects.LocalPlayer;
                                if (pc is not null)
                                {
                                    playerName = pc.Name.ToString();
                                    serverName = pc.HomeWorld.Value.Name.ToString();
                                    currentServer = pc.CurrentWorld.Value.Name.ToString();

                                    var currentTerritory = Svc.ClientState.TerritoryType;
                                    var currentZone = ZoneRegistry.Zones.FirstOrDefault(z => z.TerritoryId == currentTerritory);
                                    currentMap = currentZone?.Name ?? (activeZones.Count > 0 ? activeZones[0].Name : "Unknown");
                                }
                                taskCompletion.SetResult(true);
                            }
                            catch (Exception ex)
                            {
                                taskCompletion.SetException(ex);
                            }
                        });

                        await taskCompletion.Task;

                        if (playerName is not null)
                        {
                            var annotatedName = playerName + " (plugin)";
                            var gemsFarmed = s.GemstonesEarned;

                            var json = $"{{\"playerName\": \"{annotatedName}\", \"serverName\": \"{serverName}\", \"currentServer\": \"{currentServer}\", \"currentMap\": \"{currentMap}\", \"gemstonesFarmed\": {gemsFarmed}}}";
                            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                            
                            var response = await client.PostAsync("https://y-kohl-omega.vercel.app/api/track", content, token);
                            Diag($"Telemetry ping sent: status={response.StatusCode}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Diag($"Telemetry tracking request failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(60000, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    internal void StopTelemetry()
    {
        telemetryCts?.Cancel();
        telemetryCts = null;
    }

}

internal enum AutoPhase { Idle, Grinding, Trading, Repairing, Humanizing, Finishing, Paused }

using FateFrenzy.Core.External;
using FateFrenzy.Core.Game.Player;
using FateFrenzy.Core.Trading;
using FateFrenzy.Core.Zones;
using FateFrenzy.Core.Ipc;
using clib.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Text.Json;
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
    public bool WasStoppedManually { get; set; } = false;

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
        WasStoppedManually = false;
        var list = zones.ToList();

        if (Plugin.Cfg.RelicModeEnabled)
        {
            var incomplete = list.Where(z => !RelicItemResolver.TerritoryToItemName.ContainsKey(z.TerritoryId) || !RelicItemResolver.IsComplete(z.TerritoryId)).ToList();
            if (incomplete.Count > 0)
            {
                list = incomplete;
            }
        }

        activeZones = list;
        if (activeZones.Count == 0)
        {
            Diag("Start aborted: no zones selected (or all selected relic zones are already completed).");
            ECommons.DalamudServices.Svc.Chat.PrintError("[FateFrenzy] Start aborted: all selected relic zones are already completed.");
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
        WasStoppedManually = true;
        Plugin.Cfg.WasRunningBeforeDisconnect = false;
        Plugin.Cfg.Save();

        StopTelemetry();

        var ending = session;
        currentTask = null;
        grindTask = null;
        PauseReason = PauseReason.None;
        Svc.Automation.Stop();

        if (!Plugin.Cfg.KeepTextAdvanceOnStop
            && ExternalPlugins.IsInstalled(ExternalPlugin.TextAdvance) && TextAdvanceIPC.IsPluginEnabled())
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
        WasStoppedManually = false;
        currentTask = null;
        grindTask = null;
        session = null;
        activeZones = [];
        Phase = AutoPhase.Idle;
    }

    private System.Threading.CancellationTokenSource? telemetryCts;

    private static readonly string[] TelemetryEndpoints =
    [
        "https://fatetracker.discloud.app/api/track",
        "https://y-kohl-omega.vercel.app/api/track"
    ];

    private void StartTelemetry(AutoFateSession s)
    {
        telemetryCts?.Cancel();
        telemetryCts = new System.Threading.CancellationTokenSource();
        var token = telemetryCts.Token;

        Task.Run(async () =>
        {
            var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
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
                        string classJob = "";
                        int level = 0;
                        int walletGemstones = 0;
                        string selectedTradeItem = "";
                        List<object> tradeItems = [];

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
                                    classJob = pc.ClassJob.Value.Abbreviation.ToString();
                                    level = pc.Level;

                                    var currentTerritory = Svc.ClientState.TerritoryType;
                                    var currentZone = ZoneRegistry.Zones.FirstOrDefault(z => z.TerritoryId == currentTerritory);
                                    if (currentZone is not null)
                                    {
                                        currentMap = currentZone.Name;
                                    }
                                    else
                                    {
                                        var terrRow = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(currentTerritory);
                                        var placeName = terrRow?.PlaceName.ValueNullable?.Name.ExtractText();
                                        currentMap = !string.IsNullOrWhiteSpace(placeName)
                                            ? placeName
                                            : (activeZones.Count > 0 ? activeZones[0].Name : "Unknown");
                                    }

                                    s.UpdateGemstones();
                                    s.UpdateExp();
                                    walletGemstones = GemstoneCatalog.CurrentWalletCount();

                                    var targetItem = GemstoneCatalog.FindById(Plugin.Cfg.TargetTradeItemId);
                                    selectedTradeItem = targetItem?.ItemName ?? "";

                                    unsafe
                                    {
                                        var im = InventoryManager.Instance();
                                        if (im is not null)
                                        {
                                            foreach (var item in GemstoneCatalog.All)
                                            {
                                                var count = im->GetInventoryItemCount(item.ItemId) + im->GetInventoryItemCount(item.ItemId, true);
                                                var isStandardMatOrVoucher = item.CostPerOne <= 3 || item.ItemId is 35833 or 43961;
                                                if (count > 0 || isStandardMatOrVoucher || item.ItemId == Plugin.Cfg.TargetTradeItemId)
                                                {
                                                    tradeItems.Add(new
                                                    {
                                                        id = item.ItemId,
                                                        name = item.ItemName,
                                                        count,
                                                        cost = item.CostPerOne
                                                    });
                                                }
                                            }
                                        }
                                    }
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
                            var payload = new
                            {
                                playerName,
                                source = "plugin",
                                version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1.8.6.0",
                                serverName,
                                currentServer,
                                currentMap,
                                classJob,
                                level,
                                phase = Phase.ToString(),
                                state = Status,
                                walletGemstones,
                                gemstonesFarmed = s.GemstonesEarned,
                                fatesCompleted = s.CompletedCount,
                                fatesPerHour = Math.Round(s.FatesPerHour, 1),
                                expEarned = s.ExpEarned,
                                expPerHour = (long)Math.Round(s.ExpPerHour),
                                levelsGained = s.LevelsGained,
                                sessionDurationMs = (long)s.Elapsed.TotalMilliseconds,
                                selectedTradeItem,
                                tradeItems
                            };

                            var json = JsonSerializer.Serialize(payload);
                            foreach (var endpoint in TelemetryEndpoints)
                            {
                                try
                                {
                                    using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
                                    var response = await client.PostAsync(endpoint, content, token);
                                    if (response.IsSuccessStatusCode)
                                    {
                                        Diag($"Telemetry ping sent ({endpoint}): status={response.StatusCode}");
                                        break;
                                    }
                                }
                                catch
                                {
                                    // Tenta o próximo endpoint (fallback Discloud -> Vercel)
                                }
                            }
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

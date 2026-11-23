using Dalamud.Plugin;
using ECommons.DalamudServices;
using ECommons.Reflection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace FateFrenzy.Core.External;

public static class PluginInstaller
{
    private static readonly HashSet<ExternalPlugin> InFlight = [];

    public static bool IsInstalling(ExternalPlugin plugin) => InFlight.Contains(plugin);

    public static async Task<bool> Install(ExternalPlugin plugin)
    {
        if (!InFlight.Add(plugin)) return false;
        try
        {
            var info = ExternalPlugins.Catalog[plugin];
            Svc.Log.Info($"[ExternalPlugin] Installing/enabling {info.DisplayName} ({info.InternalName}) from {info.RepoUrl}");

            var pm = DalamudReflector.GetPluginManager();
            if (pm is null)
            {
                Svc.Log.Warning("[ExternalPlugin] Could not resolve Dalamud PluginManager.");
                return false;
            }

            // 1. If already installed in Dalamud (just disabled/unloaded), enable & load it directly!
            var existing = FindLocalPlugin(pm, info);
            if (existing is not null)
            {
                Svc.Log.Info($"[ExternalPlugin] {info.DisplayName} is already installed; loading/enabling...");
                return await EnsureLoadedAsync(existing, info.DisplayName);
            }

            // 2. Ensure custom repository is registered and plugin masters are refreshed
            if (!DalamudReflector.HasRepo(info.RepoUrl))
                DalamudReflector.AddRepo(info.RepoUrl, true);
            DalamudReflector.ReloadPluginMasters();

            // 3. Fetch remote manifest for the plugin (matching InternalName or any alias)
            var masterPlugins = await DalamudReflector.GetPluginMaster(info.RepoUrl);
            if (masterPlugins is null || masterPlugins.Count == 0)
            {
                Svc.Log.Warning($"[ExternalPlugin] Could not fetch plugin master from {info.RepoUrl}");
                return false;
            }

            var manifest = masterPlugins.FirstOrDefault(x =>
            {
                var name = x.GetFoP("InternalName") as string;
                if (string.IsNullOrEmpty(name)) return false;
                if (name.Equals(info.InternalName, StringComparison.OrdinalIgnoreCase)) return true;
                return info.Aliases is not null && info.Aliases.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase));
            });

            if (manifest is null)
            {
                Svc.Log.Warning($"[ExternalPlugin] Plugin '{info.InternalName}' not found in {info.RepoUrl}");
                return false;
            }

            // 4. Dynamically invoke PluginManager.InstallPluginAsync matching Dalamud's exact parameter list (API 14/15+)
            var installMethod = pm.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "InstallPluginAsync")
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length >= 1 && ps[0].ParameterType.IsInstanceOfType(manifest);
                });

            if (installMethod is null)
            {
                Svc.Log.Warning("[ExternalPlugin] Could not find compatible InstallPluginAsync overload on PluginManager.");
                return false;
            }

            var parameters = installMethod.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (i == 0)
                {
                    args[i] = manifest;
                }
                else if (p.ParameterType == typeof(bool))
                {
                    args[i] = false; // useTesting = false
                }
                else if (p.ParameterType == typeof(PluginLoadReason))
                {
                    args[i] = PluginLoadReason.Installer;
                }
                else if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                }
                else
                {
                    args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                }
            }

            var taskObj = installMethod.Invoke(pm, args) as Task;
            if (taskObj is null)
            {
                Svc.Log.Warning("[ExternalPlugin] InstallPluginAsync did not return a Task.");
                return false;
            }

            await taskObj;

            var localPlugin = taskObj.GetFoP("Result") ?? FindLocalPlugin(pm, info);
            if (localPlugin is null)
            {
                Svc.Log.Warning($"[ExternalPlugin] {info.DisplayName} install finished but LocalPlugin was null.");
                return false;
            }

            var loaded = await EnsureLoadedAsync(localPlugin, info.DisplayName);
            Svc.Log.Info(loaded
                ? $"[ExternalPlugin] {info.DisplayName} installed and enabled!"
                : $"[ExternalPlugin] {info.DisplayName} installed, but could not verify loaded state.");
            return loaded;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[ExternalPlugin] install threw");
            return false;
        }
        finally
        {
            InFlight.Remove(plugin);
        }
    }

    private static object? FindLocalPlugin(object pm, ExternalPluginInfo info)
    {
        try
        {
            if (pm.GetFoP("InstalledPlugins") is not IEnumerable list) return null;
            foreach (var item in list)
            {
                if (item is null) continue;
                var name = item.GetFoP("InternalName") as string;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.Equals(info.InternalName, StringComparison.OrdinalIgnoreCase)
                    || (info.Aliases is not null && info.Aliases.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase))))
                {
                    return item;
                }
            }
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, "[ExternalPlugin] FindLocalPlugin failed");
        }
        return null;
    }

    private static async Task<bool> EnsureLoadedAsync(object localPlugin, string displayName)
    {
        try
        {
            if (localPlugin.GetFoP("IsLoaded") is true) return true;

            var loadMethod = localPlugin.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "LoadAsync")
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();

            if (loadMethod is not null)
            {
                var ps = loadMethod.GetParameters();
                var args = new object?[ps.Length];
                for (var i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    if (p.ParameterType == typeof(PluginLoadReason))
                        args[i] = PluginLoadReason.Installer;
                    else if (p.ParameterType == typeof(bool))
                        args[i] = true;
                    else if (p.HasDefaultValue)
                        args[i] = p.DefaultValue;
                    else
                        args[i] = p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
                }

                if (loadMethod.Invoke(localPlugin, args) is Task loadTask)
                    await loadTask;
            }

            return localPlugin.GetFoP("IsLoaded") is true;
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[ExternalPlugin] EnsureLoadedAsync failed for {displayName}");
            return localPlugin.GetFoP("IsLoaded") is true;
        }
    }
}

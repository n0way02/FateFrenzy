using Dalamud.Plugin.Ipc;
using ECommons.DalamudServices;
using System;

namespace FateFrenzy.Core.Ipc;

internal static class LifestreamIPC
{
    private static ICallGateSubscriber<bool>? isBusy;
    private static bool initialized;

    private static void EnsureInit()
    {
        if (initialized) return;
        initialized = true;
        try
        {
            isBusy = Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[LifestreamIPC] subscribe failed");
        }
    }

    public static bool IsBusy()
    {
        EnsureInit();
        try
        {
            return isBusy?.HasFunction == true && isBusy.InvokeFunc();
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[LifestreamIPC] IsBusy failed");
            return false;
        }
    }
}

using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System.Numerics;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace FateFrenzy.Core.Game.Fates;

internal static unsafe class FateMobScanner
{
    public static bool TryFindNearestMob(uint fateId, Vector3 from, out Vector3 position, out float distance)
        => TryFindNearestMob(fateId, from, out _, out position, out distance);

    public static bool TryFindNearestMob(uint fateId, Vector3 from, out IBattleNpc? nearestNpc, out Vector3 position, out float distance)
    {
        nearestNpc = null;
        position = default;
        distance = float.MaxValue;

        var objects = Svc.Objects;
        for (var index = 0; index < objects.Length; index++)
        {
            if (objects[index] is not IBattleNpc npc) continue;
            if (!npc.IsTargetable) continue;
            if (npc.CurrentHp == 0) continue;

            var native = (CSGameObject*)npc.Address;
            if (native->FateId != fateId) continue;
            if (native->BattleNpcSubKind != BattleNpcSubKind.Combatant) continue;

            var candidate = Vector3.Distance(from, npc.Position);
            if (candidate >= distance) continue;

            distance = candidate;
            position = npc.Position;
            nearestNpc = npc;
        }

        return nearestNpc is not null;
    }
}

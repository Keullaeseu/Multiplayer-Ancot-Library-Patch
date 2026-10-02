using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

public partial class AncotLibrary
{
    private static void PatchMisc()
    {
        // CompApparelReloadable_DeployPawn sortie toggle writes sortie + spawned terminals inline.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompApparelReloadable_DeployPawn",
            "CompGetWornGizmosExtra",
            ["sortie", "sortie_Terminal"],
            []);

        // CompSpawnerCustom dev spawn only, ignore. CompSelfDetonation is verb-tick, no sync.
        // CompSustainedShoot tick, no sync.

        // LibraryVersionCheck popup must not open in MP (different mod versions across clients would desync/pause)
        var versionCheckType = SafeType("AncotLibrary.LibraryVersionCheck");
        if (versionCheckType != null)
        {
            var versionCheck = AccessTools.DeclaredMethod(versionCheckType, "VersionCheck");
            SafePatch(versionCheck, new HarmonyMethod(typeof(AncotLibrary), nameof(PreVersionCheckSuppress)));
            var lowWarning = AccessTools.DeclaredMethod(versionCheckType, "LowVersionWarning");
            SafePatch(lowWarning, new HarmonyMethod(typeof(AncotLibrary), nameof(PreVersionCheckSuppress)));
        }

        // GenStep SeedPart using Rand.Range breaks determinism (Assist/Attack/Defend). Patch to constant.
        foreach (var genStepName in new[]
                 {
                     "AncotLibrary.GenStep_GenPawnAroundMapCenter_Assist",
                     "AncotLibrary.GenStep_GenPawnAroundMapCenter_Attack",
                     "AncotLibrary.GenStep_GenPawnAroundMapCenter_Defend"
                 })
        {
            var genStepType = SafeType(genStepName);
            if (genStepType != null)
                try
                {
                    var seedGetter = AccessTools.PropertyGetter(genStepType, "SeedPart");
                    if (seedGetter != null)
                        SafePatch(seedGetter,
                            postfix: new HarmonyMethod(typeof(AncotLibrary), nameof(PostGenStepSeedPart)));
                }
                catch (Exception exception)
                {
                    Log.Warning($"{LogPrefix} GenStep SeedPart patch failed for {genStepName}: {exception.Message}");
                }
        }

        // Projectile_Repulsive.TryToKnockBack uses UnityEngine.Random.onUnitSphere when vector is zero (unsynced).
        // Replace with Rand.InsideUnitCircle (synced) via prefix that handles zero-vector case deterministically.
        var repulsiveType = SafeType("AncotLibrary.Projectile_Repulsive");
        if (repulsiveType != null)
        {
            var knockBack = AccessTools.DeclaredMethod(repulsiveType, "TryToKnockBack");
            SafePatch(knockBack, new HarmonyMethod(typeof(AncotLibrary), nameof(PreRepulsiveKnockBack)));
        }

        // IncidentWorker trader caravan is tick-synced, but ensure parms resolve is deterministic
        SafeSyncMethod("AncotLibrary.IncidentWorker_TraderCaravanArrival_Custom", "TryResolveParmsGeneral");

        // Royal permit targeting is player input -> ensure OrderForceTarget is synced (it opens targeting, then CallResources)
        SafeSyncMethod("AncotLibrary.RoyalTitlePermitWorker_DropPawn_join", "OrderForceTarget");
        SafeSyncMethod("AncotLibrary.RoyalTitlePermitWorker_DropSkyFaller", "OrderForceTarget");

        // ScenPart randomize is pre-game, no sync. StartDrones droneAmount Rand is pre-game, no sync.
        // JobGiver/Verb Rand is tick-synced, no patch needed (Verse.Rand is already synced by MP tick).

        // JobDriver_EnterContainer runs in tick (toils) - no sync needed (Verse.Rand/tick is synced by MP).
        // ScenPart randomize is pre-game, no sync. StartDrones droneAmount Rand is pre-game, no sync.
        // JobGiver/Verb Rand is tick-synced, no patch needed (Verse.Rand is already synced by MP tick).
    }

    private static bool PreVersionCheckSuppress()
    {
        // Return false to skip original when in MP (avoid popup + pause). Always allow in singleplayer.
        if (MP.IsInMultiplayer) return false;
        return true;
    }

    private static void PostGenStepSeedPart(ref int __result, object __instance)
    {
        try
        {
            // Original: 341125487 + Rand.Range(0, 99999) - consumes global Rand in getter, breaks determinism.
            // Replace with stable per-type constant. Map gen already provides seed via GenStepParams.
            var typeName = __instance.GetType().FullName;
            __result = typeName switch
            {
                "AncotLibrary.GenStep_GenPawnAroundMapCenter_Assist" => 341125487,
                "AncotLibrary.GenStep_GenPawnAroundMapCenter_Attack" => 341125488,
                "AncotLibrary.GenStep_GenPawnAroundMapCenter_Defend" => 341125489,
                _ => 341125487
            };
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PostGenStepSeedPart failed: {exception.Message}");
        }
    }

    private static bool PreRepulsiveKnockBack(object __instance, IntVec3 original, Thing thing, float knockBackDistance)
    {
        try
        {
            if (!MP.IsInMultiplayer) return true;
            // Only intercept the zero-vector fallback path (UnityEngine.Random.onUnitSphere is unsynced).
            // Non-zero vectors use deterministic math - let original run.
            if (thing == null || thing.Position != original) return true;
            // Deterministic fallback: use synced Rand.Range for angle instead of UnityEngine.Random.onUnitSphere.
            var angle = Rand.Range(0f, 360f);
            var fallback = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f, Mathf.Sin(angle * Mathf.Deg2Rad));
            DoDeterministicKnockBack(original, thing, knockBackDistance, fallback);
            return false;
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreRepulsiveKnockBack failed: {exception.Message}");
            return true;
        }
    }

    private static void DoDeterministicKnockBack(IntVec3 original, Thing thing, float knockBackDistance,
        Vector3 direction)
    {
        try
        {
            direction.Normalize();
            var best = thing.Position;
            for (var index = 0; index < knockBackDistance; index++)
            {
                var offset = index * direction;
                var candidate = thing.Position + offset.ToIntVec3();
                if (candidate.InBounds(thing.Map) && candidate.Walkable(thing.Map) &&
                    GenSight.LineOfSight(original, candidate, thing.Map))
                    best = candidate;
                else
                    break;
            }

            if (best.IsValid)
            {
                thing.Position = best;
                if (thing is Pawn pawn)
                {
                    pawn.pather?.StopDead();
                    pawn.jobs?.StopAll();
                }
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} DoDeterministicKnockBack failed: {exception.Message}");
        }
    }
}
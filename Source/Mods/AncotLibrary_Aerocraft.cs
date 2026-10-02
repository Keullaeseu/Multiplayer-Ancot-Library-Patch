using HarmonyLib;
using Multiplayer.API;
using UnityEngine;
using Verse;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

public partial class AncotLibrary
{
    private static void PatchAerocraft()
    {
        // Building_Aerocraft.SetTargetDestination is called from land lambda + right-click lambda.
        // There are no TakeOff/Land named methods (takeoff/landing are inline lambdas in GetGizmos).
        SafeSyncMethod("AncotLibrary.Building_Aerocraft", "SetTargetDestination");

        // Building_Aerocraft gizmo actions: land (FlightState + rotation + destination),
        // right-click destination, takeoff (state + fuel + effecter). Discovered by IL scan.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.Building_Aerocraft",
            "GetGizmos",
            ["FlightState", "landRotation"],
            ["SetTargetDestination"]);

        // Designator_Land.DesignateSingleCell -> action(c, placingRot), must be synced.
        // Command_Land.ProcessInput (Event arg, selection-only) is intentionally not synced:
        // vanilla leaves designator selection local and syncs the designation itself.
        SafeSyncMethod("AncotLibrary.Designator_Land", "DesignateSingleCell");
        MP.RegisterSyncMethod(typeof(AncotLibrary), nameof(SyncedAerocraftLand));
        var designatorType = SafeType("AncotLibrary.Designator_Land");
        if (designatorType != null)
            try
            {
                MP.RegisterSyncWorker<Designator>(SyncLandDesignator, designatorType);
            }
            catch (Exception exception)
            {
                Log.Warning($"{LogPrefix} Register Designator_Land worker failed: {exception.Message}");
            }

        // CompTransporterCustom gizmo actions: haul job order + cancel load.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompTransporterCustom",
            "CompGetGizmosExtra",
            [],
            ["TryTakeOrderedJob", "CancelLoad"]);

        // CompLaunchableAerocraft / CompTransporterAerocraft / CompRefuelableAerocraft are filter/display-only,
        // no lambdas in CompGetGizmosExtra - no sync needed.
        // ITab_ContentsAerocraft.OnDropThing mutates container - watch it
        var itabType = SafeType("AncotLibrary.ITab_ContentsAerocraft");
        if (itabType != null)
        {
            var onDrop = AccessTools.DeclaredMethod(itabType, "OnDropThing");
            SafePatch(onDrop,
                new HarmonyMethod(typeof(AncotLibrary), nameof(PreAerocraftTabWatch)),
                new HarmonyMethod(typeof(AncotLibrary), nameof(PostAerocraftTabWatch)));
        }
    }

    private static void SyncLandDesignator(SyncWorker sync, ref Designator designator)
    {
        try
        {
            var type = SafeType("AncotLibrary.Designator_Land");
            if (type == null) return;
            if (sync.isWriting)
            {
                var thing = AccessTools.Field(type, "thing")?.GetValue(designator) as Thing;
                var thingDef = AccessTools.Field(type, "thingDef")?.GetValue(designator) as ThingDef;
                var placingRotObj = AccessTools.Field(type, "placingRot")?.GetValue(designator);
                var placingRot = placingRotObj is Rot4 rot ? rot : Rot4.North;
                sync.Write(thing);
                sync.Write(thingDef);
                sync.Write(placingRot);
            }
            else
            {
                var thing = sync.Read<Thing>();
                var thingDef = sync.Read<ThingDef>();
                var placingRot = sync.Read<Rot4>();
                // Reconstruct with wrapper action that replicates the original land lambda.
                // DesignateSingleCell itself is synced, so this runs identically on all clients.
                var instance = Activator.CreateInstance(type,
                    ResolveLandDesignatorIcon(thingDef),
                    thing,
                    thingDef,
                    (Action<IntVec3, Rot4>)((cell, rot) => SyncedAerocraftLand(thing, cell, rot)));
                designator = (Designator)instance;
                if (designator != null)
                    AccessTools.Field(type, "placingRot")?.SetValue(designator, placingRot);
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncLandDesignator failed: {exception.Message}");
        }
    }

    private static Texture ResolveLandDesignatorIcon(ThingDef thingDef)
    {
        try
        {
            if (thingDef?.uiIcon != null) return thingDef.uiIcon;
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} ResolveLandDesignatorIcon failed: {exception.Message}");
        }

        return null;
    }

    // Invoked by designators reconstructed in SyncLandDesignator on all clients.
    private static void SyncedAerocraftLand(Thing thing, IntVec3 cell, Rot4 rot)
    {
        try
        {
            if (thing == null) return;
            // Replicates Building_Aerocraft land lambda: FlightState=Landing + SetTargetDestination
            var aerocraftType = SafeType("AncotLibrary.Building_Aerocraft");
            var flightStateValue = Enum.Parse(SafeType("AncotLibrary.AerocraftState"), "Landing");
            AccessTools.Field(aerocraftType, "FlightState")?.SetValue(thing, flightStateValue);
            AccessTools.Field(aerocraftType, "landRotation")?.SetValue(thing, rot);
            AccessTools.Method(aerocraftType, "SetTargetDestination")?.Invoke(thing, new object[] { cell });
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncedAerocraftLand failed: {exception.Message}");
        }
    }

    private static void PreAerocraftTabWatch()
    {
        if (MP.IsInMultiplayer) MP.WatchBegin();
    }

    private static void PostAerocraftTabWatch()
    {
        if (MP.IsInMultiplayer) MP.WatchEnd();
    }
}
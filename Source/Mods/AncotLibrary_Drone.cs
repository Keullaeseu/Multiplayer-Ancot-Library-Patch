using System.Collections;
using HarmonyLib;
using Multiplayer.API;
using RimWorld;
using Verse;
using Verse.AI;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

public partial class AncotLibrary
{
    // Sync fields for DroneGizmo drag/toggle (watched in GizmoOnGUI)
    private static ISyncField droneAutoRepairField;
    private static ISyncField dronePercentRechargeField;
    private static ISyncField droneWorkModeField;

    private static void PatchDrone()
    {
        // CompDrone.SelfShutdown spawns shutdown job (must be synced). ChargeTick is tick - no sync.
        SafeSyncMethod("AncotLibrary.CompDrone", "SelfShutdown");

        // DroneGizmo work-mode menu action sets workMode + StopAll (captures are comp/def/list only).
        // The sort-key lambda never matches the filters and stays local.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.DroneGizmo",
            "GetWorkModeOptions",
            ["workMode"],
            ["StopAll"]);

        // Drafted flag is covered by vanilla MP (Pawn_DraftController.Drafted sync).

        // PawnColumnWorker_DroneAutoRepair is PawnColumnWorker_Checkbox subclass: SetValue mutates autoRepair.
        SafeSyncMethod("AncotLibrary.PawnColumnWorker_DroneAutoRepair", "SetValue");

        // FloatMenuOptionProvider_Drone disassemble action captures the UI-only FloatMenuContext,
        // which has no sync worker - so it is rerouted to a named synced wrapper with explicit args
        // instead of a delegate sync (the confirm-dialog lambda stays local).
        foreach (var disassembleLambda in AncotLambdaSync.FindStateLambdas(
                     "AncotLibrary.FloatMenuOptionProvider_Drone",
                     "GetOptionsFor",
                     [],
                     ["TryTakeOrderedJob"]))
            SafePatch(disassembleLambda, new HarmonyMethod(typeof(AncotLibrary), nameof(PreDisassembleOrder)));
        MP.RegisterSyncMethod(typeof(AncotLibrary), nameof(SyncedDisassembleMech));

        // Register sync fields + Watch for DroneGizmo direct writes
        droneAutoRepairField = SafeSyncField("AncotLibrary.CompDrone", "autoRepair");
        dronePercentRechargeField = SafeSyncField("AncotLibrary.CompDrone", "PercentRecharge");
        droneWorkModeField = SafeSyncField("AncotLibrary.CompDrone", "workMode");

        var droneGizmoType = SafeType("AncotLibrary.DroneGizmo");
        if (droneGizmoType != null && droneAutoRepairField != null)
        {
            var gizmoOnGui = AccessTools.DeclaredMethod(droneGizmoType, "GizmoOnGUI");
            SafePatch(gizmoOnGui,
                new HarmonyMethod(typeof(AncotLibrary), nameof(PreDroneGizmoOnGUI)),
                new HarmonyMethod(typeof(AncotLibrary), nameof(PostDroneGizmoOnGUI)));
        }
    }

    private static bool PreDisassembleOrder(object __instance)
    {
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return true;

        try
        {
            var instanceType = __instance.GetType();
            var context = AccessTools.Field(instanceType, "context")?.GetValue(__instance);
            var clicked = AccessTools.Field(instanceType, "clickedPawn")?.GetValue(__instance) as Pawn;
            var worker = context == null
                ? null
                : AccessTools.Property(context.GetType(), "FirstSelectedPawn")?.GetValue(context, null) as Pawn;

            if (worker?.jobs == null || clicked == null)
                return true;

            SyncedDisassembleMech(worker, clicked);
            return false;
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreDisassembleOrder failed, running locally: {exception.Message}");
            return true;
        }
    }

    private static void PreDroneGizmoOnGUI(object __instance)
    {
        if (!MP.IsInMultiplayer) return;
        try
        {
            MP.WatchBegin();
            // Watch all CompDrone instances reachable from this gizmo (powerCell + groupedComps via reflection)
            var gizmoType = __instance.GetType();
            var powerCell = AccessTools.Field(gizmoType, "powerCell")?.GetValue(__instance);
            if (powerCell != null && droneAutoRepairField != null)
            {
                droneAutoRepairField.Watch(powerCell);
                dronePercentRechargeField?.Watch(powerCell);
                droneWorkModeField?.Watch(powerCell);
            }

            var grouped = AccessTools.Field(gizmoType, "groupedComps")?.GetValue(__instance);
            if (grouped is IEnumerable enumerable)
                foreach (var comp in enumerable)
                    if (comp != null)
                    {
                        droneAutoRepairField?.Watch(comp);
                        dronePercentRechargeField?.Watch(comp);
                        droneWorkModeField?.Watch(comp);
                    }
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreDroneGizmoOnGUI watch failed: {exception.Message}");
        }
    }

    private static void PostDroneGizmoOnGUI()
    {
        if (MP.IsInMultiplayer) MP.WatchEnd();
    }

    private static void SyncedDisassembleMech(Pawn clickedPawn, Pawn worker)
    {
        try
        {
            // Replicates FloatMenuOptionProvider_Drone disassemble job order (uses vanilla DisassembleMech).
            if (worker?.jobs == null || clickedPawn == null) return;
            worker.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DisassembleMech, (LocalTargetInfo)clickedPawn),
                JobTag.Misc);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncedDisassembleMech failed: {exception.Message}");
        }
    }
}
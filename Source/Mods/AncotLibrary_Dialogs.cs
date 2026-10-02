using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Compat;
using RimWorld.Planet;
using Verse;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

public partial class AncotLibrary
{
    private static void PatchDialogsAndWorld()
    {
        // Dialog_AssignWorldObjectOwner_Caravan rows call TryAssign/TryUnassign directly (no syncable lambdas:
        // DrawAssignedRow has none, DrawUnassignedRow has only ideo-tooltip lambda). Sync the named methods.
        SafeSyncMethod("AncotLibrary.WorldObjectComp_AssignableToPawn_Caravan", "TryAssignPawn");
        SafeSyncMethod("AncotLibrary.WorldObjectComp_AssignableToPawn_Caravan", "TryUnassignPawn");
        var assignDialogType = SafeType("AncotLibrary.Dialog_AssignWorldObjectOwner_Caravan");
        if (assignDialogType != null)
            try
            {
                MP.RegisterSyncWorker<Window>(SyncAssignCaravanDialog, assignDialogType);
            }
            catch (Exception exception)
            {
                Log.Warning($"{LogPrefix} Register AssignCaravan dialog worker failed: {exception.Message}");
            }

        // Dialog_NameWeapon.DoWindowContents has no lambdas (Accept directly calls SetUniqueName extension).
        // The extension call is swapped for the synced wrapper via transpiler; dialog worker + close sync
        // handle reconstruction.
        MP.RegisterSyncMethod(typeof(AncotLibrary), nameof(SyncedSetUniqueWeaponName));
        var nameWeaponType = SafeType("AncotLibrary.Dialog_NameWeapon");
        if (nameWeaponType != null)
        {
            try
            {
                MP.RegisterSyncWorker<Window>(SyncNameWeaponDialog, nameWeaponType);
            }
            catch (Exception exception)
            {
                Log.Warning($"{LogPrefix} Register NameWeapon dialog worker failed: {exception.Message}");
            }

            var doContents = AccessTools.DeclaredMethod(nameWeaponType, "DoWindowContents");
            SafePatch(doContents, transpiler: new HarmonyMethod(typeof(AncotLibrary), nameof(TranspileSetUniqueName)));
        }

        // WorldObjectComp_RoyalTitleUpdate caravan gizmo: outer menu builder stays local,
        // the inner option applies the royal title - discovered by IL scan.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.WorldObjectComp_RoyalTitleUpdate",
            "GetCaravanGizmos",
            [],
            ["TryUpdateTitle"]);

        // Royal permit workers targeting -> CallResources / CallSkyFaller (spawn + pods, must be synced).
        // GetRoyalAidOptions lambdas only start vanilla targeting (no sim mutation) - intentionally not synced.
        SafeSyncMethod("AncotLibrary.RoyalTitlePermitWorker_DropPawn_join", "CallResources");
        SafeSyncMethod("AncotLibrary.RoyalTitlePermitWorker_DropSkyFaller", "CallSkyFaller");

        // DialogUtility.OpenAssembledDialog creates Dialog_NodeTree from AssembleDialog result.
        // AssembleDialog has a single action lambda (Messages/ReceiveLetter/SendSignal drive quests + letters).
        SafeSyncMethod("AncotLibrary.DialogUtility", "OpenAssembledDialog");
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.DialogUtility",
            "AssembleDialog",
            [],
            ["ReceiveLetter", "SendSignal"]);

        // CompReadDiaLog.UsedBy: letter delivery mutates sim (synced), dialog close stays local.
        // OpenNestedDialog lambdas only navigate/close dialogs - intentionally not synced.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompReadDiaLog",
            "UsedBy",
            [],
            ["ReceiveLetter"]);

        // Dialog_ColorPicker Accept is caller-provided Action<Color>, dialog itself is local (no sync).
        // Ensure color dialogs don't pause-lock incorrectly; register close sync only.
        foreach (var dialogName in new[]
                 {
                     "AncotLibrary.Dialog_AssignWorldObjectOwner_Caravan",
                     "AncotLibrary.Dialog_NameWeapon"
                 })
        {
            var dialogType = SafeType(dialogName);
            if (dialogType != null)
                try
                {
                    DialogUtilities.RegisterDialogCloseSync(dialogType, true);
                }
                catch (Exception exception)
                {
                    Log.Warning($"{LogPrefix} RegisterDialogCloseSync failed for {dialogName}: {exception.Message}");
                }
        }
    }

    private static void SyncAssignCaravanDialog(SyncWorker sync, ref Window dialog)
    {
        try
        {
            var type = SafeType("AncotLibrary.Dialog_AssignWorldObjectOwner_Caravan");
            if (type == null) return;
            if (sync.isWriting)
            {
                var assignable = AccessTools.Field(type, "assignable")?.GetValue(dialog);
                var caravan = AccessTools.Field(type, "caravan")?.GetValue(dialog);
                WorldObject parent = null;
                if (assignable is WorldObjectComp comp) parent = comp.parent;
                sync.Write(parent);
                sync.Write((Caravan)caravan);
            }
            else
            {
                var parent = sync.Read<WorldObject>();
                var caravan = sync.Read<Caravan>();
                WorldObjectComp comp = null;
                if (parent != null)
                    foreach (var candidate in parent.AllComps)
                        if (candidate.GetType().FullName == "AncotLibrary.WorldObjectComp_AssignableToPawn_Caravan")
                        {
                            comp = candidate;
                            break;
                        }

                if (comp != null) dialog = (Window)Activator.CreateInstance(type, comp, caravan);
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncAssignCaravanDialog failed: {exception.Message}");
        }
    }

    private static void SyncNameWeaponDialog(SyncWorker sync, ref Window dialog)
    {
        try
        {
            var type = SafeType("AncotLibrary.Dialog_NameWeapon");
            if (type == null) return;
            if (sync.isWriting)
            {
                var weapon = AccessTools.Field(type, "weapon")?.GetValue(dialog);
                sync.Write((Thing)weapon);
            }
            else
            {
                var weapon = sync.Read<Thing>();
                dialog = (Window)Activator.CreateInstance(type, weapon);
            }
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncNameWeaponDialog failed: {exception.Message}");
        }
    }

    private static IEnumerable<CodeInstruction> TranspileSetUniqueName(
        IEnumerable<CodeInstruction> instructions,
        MethodBase baseMethod)
    {
        var utilityType = SafeType("AncotLibrary.WeaponTraitsUtility");
        var from = utilityType == null
            ? null
            : AccessTools.DeclaredMethod(utilityType, "SetUniqueName", [typeof(Thing), typeof(string)]);
        var to = AccessTools.DeclaredMethod(typeof(AncotLibrary), nameof(SyncedSetUniqueWeaponName));

        if (from == null || to == null)
        {
            Log.Warning($"{LogPrefix} SetUniqueName swap unavailable, weapon naming stays local.");
            return instructions;
        }

        return instructions.ReplaceMethod(from, to, baseMethod, expectedReplacements: -2);
    }

    private static void SyncedSetUniqueWeaponName(Thing weapon, string newName)
    {
        try
        {
            if (weapon == null || string.IsNullOrEmpty(newName)) return;
            var utilityType = SafeType("AncotLibrary.WeaponTraitsUtility");
            if (utilityType != null)
            {
                var setName = AccessTools.DeclaredMethod(utilityType, "SetUniqueName",
                    new[] { typeof(Thing), typeof(string) });
                if (setName != null)
                {
                    setName.Invoke(null, new object[] { weapon, newName });
                    return;
                }
            }

            // Fallback: try direct extension via reflection on Thing (in case mod merges it)
            AccessTools.Method(typeof(Thing), "SetUniqueName")?.Invoke(weapon, new object[] { newName });
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncedSetUniqueWeaponName failed: {exception.Message}");
        }
    }
}
using System.Collections;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

public partial class AncotLibrary
{
    private static ISyncField customFireAtWillField;
    private static ISyncField customForcedTargetField;
    private static ISyncField customForceTargetDownedField;
    private static ISyncField buildingFireAtWillField;
    private static ISyncField buildingForcedTargetField;
    private static ISyncField buildingForceTargetDownedField;
    private static ISyncField apparelTargetChargesField;
    private static ISyncField carrierMaxToFillField;

    private static void PatchTurret()
    {
        // CompTurretGun_Custom / CompTurretGun_Building named logic
        SafeSyncMethod("AncotLibrary.CompTurretGun_Custom", "ResetCurrentTarget");
        SafeSyncMethod("AncotLibrary.CompTurretGun_Building", "ResetCurrentTarget");
        SafeSyncMethod("AncotLibrary.CompTurretGun_Custom", "ShotOnce");
        SafeSyncMethod("AncotLibrary.CompTurretGun_Building", "ShotOnce");

        // Turret targeting confirm callbacks capture the gizmo itself (not syncable),
        // so they are rerouted to a named synced method with explicit comp args.
        foreach (var gizmoName in new[] { "AncotLibrary.Gizmo_TurretGun", "AncotLibrary.Gizmo_TurretGunBuilding" })
        foreach (var confirmLambda in AncotLambdaSync.FindStateLambdas(
                     gizmoName,
                     "ProcessTargetingInput",
                     ["currentTarget", "forcedTarget"],
                     []))
            SafePatch(confirmLambda, new HarmonyMethod(typeof(AncotLibrary), nameof(PreTurretForceTarget)));
        MP.RegisterSyncMethod(typeof(AncotLibrary), nameof(SyncedTurretSetTarget));

        // Building_SpinTurretGun gizmos: ExtractShell, stop-force reset, holdFire toggle.
        // The holdFire getter never matches the write-only field filter and stays local.
        SafeSyncMethod("AncotLibrary.Building_SpinTurretGun", "ExtractShell");
        SafeSyncMethod("AncotLibrary.Building_SpinTurretGun", "OrderAttack");
        SafeSyncMethod("AncotLibrary.Building_SpinTurretGun", "ResetForcedTarget");
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.Building_SpinTurretGun",
            "GetGizmos",
            ["holdFire"],
            ["ExtractShell", "ResetForcedTarget"]);

        // CompTurretGun_Custom.GetGizmos and CompTurretGun_Building.CompGetGizmosExtra only construct
        // Gizmo_TurretGun/Gizmo_TurretGunBuilding (no lambdas inside) - sync via targeting reroute above.
        // CompPhysicalShield toggle (holdShield flip, no ResetStamina named method - actual Reset is private).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompPhysicalShield",
            "GetGizmos",
            ["holdShield"],
            []);

        // CompPointDefense toggle (the isActive getter reads only and stays local).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompPointDefense",
            "GetGizmos",
            ["switchOn"],
            []);

        // CompChargeWeaponImmediately action (the cooldown getter stays local).
        SafeSyncMethod("AncotLibrary.CompChargeWeaponImmediately", "ChargeWeaponAtOnce");
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompChargeWeaponImmediately",
            "GetGizmos",
            [],
            ["ChargeWeaponAtOnce"]);

        // CompRangeWeaponVerbSwitch weapon shoot mode toggle (verbProps swap + VerbRefresh + jobs.StopAll).
        SafeSyncMethod("AncotLibrary.CompRangeWeaponVerbSwitch", "Notify_VerbSwitch");
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompRangeWeaponVerbSwitch",
            "GetWeaponGizmos",
            ["switched"],
            ["VerbRefresh", "StopAll", "Reset"]);

        // HediffComp_AlternateWeapon weapon swap (no SwitchWeapon named method;
        // Gizmo_SwitchWeapon_Hediff is plain Command_Action with no GizmoOnGUI).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.HediffComp_AlternateWeapon",
            "CompGetGizmos",
            [],
            ["TryDrop", "EquipeFromStorage"]);

        // Apparel reloadable targetCharges slider (DraggableBar in GizmoOnGUI writes targetCharges directly)
        apparelTargetChargesField = SafeSyncField("AncotLibrary.CompApparelReloadable_Custom", "targetCharges");
        var apparelGizmo = SafeType("AncotLibrary.Gizmo_ApparelReloadable_Custom");
        if (apparelGizmo != null)
        {
            var gizmoOnGui = AccessTools.DeclaredMethod(apparelGizmo, "GizmoOnGUI");
            SafePatch(gizmoOnGui,
                new HarmonyMethod(typeof(AncotLibrary), nameof(PreApparelReloadableGizmo)),
                new HarmonyMethod(typeof(AncotLibrary), nameof(PostApparelReloadableGizmo)));
        }

        // ThingCarrier maxToFill slider (DraggableBar in GizmoOnGUI writes maxToFill directly,
        // covered by the field Watch below)
        carrierMaxToFillField = SafeSyncField("AncotLibrary.CompThingCarrier_Custom", "maxToFill");
        var carrierGizmo = SafeType("AncotLibrary.ThingCarrierGizmo");
        if (carrierGizmo != null)
        {
            var gizmoOnGui = AccessTools.DeclaredMethod(carrierGizmo, "GizmoOnGUI");
            SafePatch(gizmoOnGui,
                new HarmonyMethod(typeof(AncotLibrary), nameof(PreThingCarrierGizmo)),
                new HarmonyMethod(typeof(AncotLibrary), nameof(PostThingCarrierGizmo)));
        }

        // Turret fireAtWill / forcedTarget / downed flag via GizmoOnGUI direct writes.
        // Each comp type gets its own field handles (a Custom handle cannot watch a Building comp).
        customFireAtWillField = SafeSyncField("AncotLibrary.CompTurretGun_Custom", "fireAtWill");
        customForcedTargetField = SafeSyncField("AncotLibrary.CompTurretGun_Custom", "forcedTarget");
        customForceTargetDownedField = SafeSyncField("AncotLibrary.CompTurretGun_Custom", "isForcetargetDowned");
        buildingFireAtWillField = SafeSyncField("AncotLibrary.CompTurretGun_Building", "fireAtWill");
        buildingForcedTargetField = SafeSyncField("AncotLibrary.CompTurretGun_Building", "forcedTarget");
        buildingForceTargetDownedField = SafeSyncField("AncotLibrary.CompTurretGun_Building", "isForcetargetDowned");

        foreach (var gizmoName in new[] { "AncotLibrary.Gizmo_TurretGun", "AncotLibrary.Gizmo_TurretGunBuilding" })
        {
            var gizmoType = SafeType(gizmoName);
            if (gizmoType != null)
            {
                var gizmoOnGui = AccessTools.DeclaredMethod(gizmoType, "GizmoOnGUI");
                SafePatch(gizmoOnGui,
                    new HarmonyMethod(typeof(AncotLibrary), nameof(PreTurretGizmoOnGUI)),
                    new HarmonyMethod(typeof(AncotLibrary), nameof(PostTurretGizmoOnGUI)));
            }
        }

        MP.RegisterSyncMethod(typeof(AncotLibrary), nameof(SyncedTurretSetTarget));
    }

    private static bool PreTurretForceTarget(object __instance, object[] __args)
    {
        // The confirm callback captures the gizmo (not syncable), so reroute through the
        // named synced method with explicit comp args instead of a delegate sync.
        if (!MP.IsInMultiplayer || MP.IsExecutingSyncCommand)
            return true;

        try
        {
            if (__args == null || __args.Length < 1 || __args[0] is not LocalTargetInfo target)
                return true;

            var gizmo = AccessTools.Field(__instance.GetType(), "<>4__this")?.GetValue(__instance);
            var comps = AccessTools.Field(gizmo?.GetType(), "comps")?.GetValue(gizmo) as IEnumerable;

            if (comps == null)
                return true;

            foreach (var comp in comps)
                if (comp is ThingComp thingComp)
                    SyncedTurretSetTarget(thingComp, target);

            return false;
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreTurretForceTarget failed, running locally: {exception.Message}");
            return true;
        }
    }

    private static void PreApparelReloadableGizmo(object __instance)
    {
        if (!MP.IsInMultiplayer) return;
        try
        {
            MP.WatchBegin();
            var comp = AccessTools.Field(__instance.GetType(), "compReloadable")?.GetValue(__instance);
            if (comp != null) apparelTargetChargesField?.Watch(comp);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreApparelReloadableGizmo failed: {exception.Message}");
        }
    }

    private static void PostApparelReloadableGizmo()
    {
        if (MP.IsInMultiplayer) MP.WatchEnd();
    }

    private static void PreThingCarrierGizmo(object __instance)
    {
        if (!MP.IsInMultiplayer) return;
        try
        {
            MP.WatchBegin();
            var carrier = AccessTools.Field(__instance.GetType(), "carrier")?.GetValue(__instance);
            if (carrier != null) carrierMaxToFillField?.Watch(carrier);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreThingCarrierGizmo failed: {exception.Message}");
        }
    }

    private static void PostThingCarrierGizmo()
    {
        if (MP.IsInMultiplayer) MP.WatchEnd();
    }

    private static void PreTurretGizmoOnGUI(object __instance)
    {
        if (!MP.IsInMultiplayer) return;
        try
        {
            MP.WatchBegin();
            // Watch all comps merged into this gizmo, each with its own type's field handles
            var comps = AccessTools.Field(__instance.GetType(), "comps")?.GetValue(__instance);
            if (comps is IEnumerable enumerable)
                foreach (var comp in enumerable)
                {
                    if (comp == null) continue;
                    var fullName = comp.GetType().FullName;
                    if (fullName == "AncotLibrary.CompTurretGun_Custom")
                    {
                        customFireAtWillField?.Watch(comp);
                        customForcedTargetField?.Watch(comp);
                        customForceTargetDownedField?.Watch(comp);
                    }
                    else if (fullName == "AncotLibrary.CompTurretGun_Building")
                    {
                        buildingFireAtWillField?.Watch(comp);
                        buildingForcedTargetField?.Watch(comp);
                        buildingForceTargetDownedField?.Watch(comp);
                    }
                }
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} PreTurretGizmoOnGUI failed: {exception.Message}");
        }
    }

    private static void PostTurretGizmoOnGUI()
    {
        if (MP.IsInMultiplayer) MP.WatchEnd();
    }

    private static void SyncedTurretSetTarget(ThingComp comp, LocalTargetInfo target)
    {
        try
        {
            AccessTools.Field(comp.GetType(), "currentTarget")?.SetValue(comp, target);
            AccessTools.Field(comp.GetType(), "forcedTarget")?.SetValue(comp, target);
            if (target.Pawn != null && target.Pawn.Downed)
                AccessTools.Field(comp.GetType(), "isForcetargetDowned")?.SetValue(comp, true);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SyncedTurretSetTarget failed: {exception.Message}");
        }
    }
}
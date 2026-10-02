namespace MultiplayerAncotLibraryPatch.Source.Mods;

public partial class AncotLibrary
{
    private static void PatchCarrier()
    {
        // CompMechCarrier_Custom.TrySpawnPawns is named and contains GeneratePawn+Spawn+lord logic.
        // There is no Recover named method (recover is inline lambda in CompGetGizmosExtra).
        SafeSyncMethod("AncotLibrary.CompMechCarrier_Custom", "TrySpawnPawns");
        SafeSyncMethod("AncotLibrary.CompThingCarrier_Custom", "TryRemoveThingInCarrier");

        // MechCarrier gizmo actions: spawn (TrySpawnPawns) + inline recover (MakeThing/TryAdd).
        // Cooldown getters and the dev reset never match the filters and stay local.
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompMechCarrier_Custom",
            "CompGetGizmosExtra",
            [],
            ["TrySpawnPawns", "TryAdd"]);

        // CompCommandPivot has no SwitchSortie named method (sortie toggle is inline lambda).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompCommandPivot",
            "CompGetGizmosExtra",
            ["sortie"],
            []);

        // CompMechAutoFight has no SwitchAutoFight named method (toggle + hediff add/remove inline).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompMechAutoFight",
            "GetGizmos",
            [],
            ["set_AutoFight", "AddHediff", "RemoveHediff"]);

        // CompJobGizmo has no MakeJob named method (job creation is inline in GetGizmos lambda).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompJobGizmo",
            "GetGizmos",
            [],
            ["TryTakeOrderedJob"]);

        // CompAdditionalGraphicSwitch.SwitchGraphicTo is private (int); menu selection lambda
        // calls it while the menu builder itself stays local.
        SafeSyncMethod("AncotLibrary.CompAdditionalGraphicSwitch", "SwitchGraphicTo");
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompAdditionalGraphicSwitch",
            "GetGizmos",
            [],
            ["SwitchGraphicTo"]);

        // CompEmptyUniqueWeapon has no SwitchStarWeapon named method (GameComponent list toggle inline).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.CompEmptyUniqueWeapon",
            "GetWeaponGizmos",
            [],
            ["Remove"]);

        // HediffComp_AISwitchCombat UI toggle only (tick flips it back, do NOT sync tick).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.HediffComp_AISwitchCombat",
            "CompGetGizmos",
            ["switchOn"],
            []);

        // Mech work priorities go through vanilla Pawn_WorkSettings.SetPriority, which
        // Multiplayer itself already syncs - nothing to do here.

        // FloatMenuOptionProvider_ReloadApparel reload job order (captures are pawn/gear/ammo only).
        AncotLambdaSync.SyncStateChangingLambdas(
            "AncotLibrary.FloatMenuOptionProvider_ReloadApparel",
            "GetOptions",
            [],
            ["TryTakeOrderedJob", "MakeReloadJob"]);

        // Deploy pawn/thing: Deploy is named and spawns (must be synced). Verb TryCastShot is protected
        // and runs in synced tick context via MP verb syncing, so only sync Deploy.
        SafeSyncMethod("AncotLibrary.CompApparelReloadable_DeployPawn", "Deploy");
        SafeSyncMethod("AncotLibrary.CompApparelReloadable_DeployThing", "Deploy");
    }
}
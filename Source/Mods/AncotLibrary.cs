using System.Reflection;
using HarmonyLib;
using Multiplayer.API;
using Multiplayer.Compat;
using Verse;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

/// <summary>
///     Multiplayer Patch for Ancot Library by Ancot,
///     Last Update: 9 Dec, 2025 @ 2:52pm
///     Framework of Ancot's Races (drones, aerocraft, turrets, carriers, dialogs, royal, quests).
///     <see href="https://steamcommunity.com/sharedfiles/filedetails/?id=2988801276" />
/// </summary>
[MpCompatFor("Ancot.AncotLibrary")]
public partial class AncotLibrary
{
    private const string LogPrefix = "[Multiplayer Ancot Library Patch]";

    public AncotLibrary(ModContentPack content)
    {
        LongEventHandler.ExecuteWhenFinished(LatePatch);
    }

    private static void LatePatch()
    {
        try
        {
            Log.Message($"{LogPrefix} Initializing...");
            PatchDrone();
            PatchTurret();
            PatchAerocraft();
            PatchCarrier();
            PatchDialogsAndWorld();
            PatchMisc();
            Log.Message($"{LogPrefix} Initialized.");
        }
        catch (Exception exception)
        {
            Log.Error($"{LogPrefix} LatePatch failed: {exception}");
        }
    }

    #region Safe registration helpers

    private static Type SafeType(string typeName)
    {
        try
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null) Log.Warning($"{LogPrefix} Type not found (skipped): {typeName}");
            return type;
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SafeType failed for {typeName}: {exception.Message}");
            return null;
        }
    }

    private static void SafeSyncMethod(Type type, string methodName, Type[] argTypes = null)
    {
        try
        {
            if (type == null) return;
            var method = argTypes == null
                ? AccessTools.DeclaredMethod(type, methodName)
                : AccessTools.DeclaredMethod(type, methodName, argTypes);
            if (method == null)
            {
                Log.Warning($"{LogPrefix} Method not found (skipped): {type.FullName}:{methodName}");
                return;
            }

            MP.RegisterSyncMethod(method);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SafeSyncMethod failed for {type?.FullName}:{methodName}: {exception.Message}");
        }
    }

    private static void SafeSyncMethod(string typeName, string methodName)
    {
        SafeSyncMethod(SafeType(typeName), methodName);
    }

    private static void SafeLambdaMethod(string typeName, string parentMethod, params int[] ordinals)
    {
        try
        {
            var type = SafeType(typeName);
            if (type == null) return;
            foreach (var sync in MpCompat.RegisterLambdaMethod(type, parentMethod, ordinals))
            {
                // Keep reference to avoid compiler warning, sync is already registered
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                $"{LogPrefix} SafeLambdaMethod failed for {typeName}:{parentMethod} [{string.Join(",", ordinals)}]: {exception.Message}");
        }
    }

    private static void SafeLambdaDelegate(string typeName, string parentMethod, params int[] ordinals)
    {
        try
        {
            var type = SafeType(typeName);
            if (type == null) return;
            foreach (var sync in MpCompat.RegisterLambdaDelegate(type, parentMethod, ordinals))
            {
                // Keep reference to avoid compiler warning, sync is already registered
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                $"{LogPrefix} SafeLambdaDelegate failed for {typeName}:{parentMethod} [{string.Join(",", ordinals)}]: {exception.Message}");
        }
    }

    private static ISyncField SafeSyncField(string typeName, string fieldName)
    {
        try
        {
            var type = SafeType(typeName);
            if (type == null) return null;
            return MP.RegisterSyncField(type, fieldName);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} SafeSyncField failed for {typeName}:{fieldName}: {exception.Message}");
            return null;
        }
    }

    private static void SafePatch(MethodBase target, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
        HarmonyMethod transpiler = null)
    {
        try
        {
            if (target == null) return;
            MpCompat.harmony.Patch(target, prefix, postfix, transpiler);
        }
        catch (Exception exception)
        {
            Log.Warning(
                $"{LogPrefix} SafePatch failed for {target?.DeclaringType?.FullName}:{target?.Name}: {exception.Message}");
        }
    }

    #endregion
}
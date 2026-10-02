using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Multiplayer.API;
using Verse;

namespace MultiplayerAncotLibraryPatch.Source.Mods;

/// <summary>
///     Finds gizmo/FloatMenu lambdas by scanning IL instead of guessing ordinal numbers.
///     <para />
///     Ordinal lookup only searches compiler display classes and misses lambdas living
///     directly on the parent type (common in AncotLibrary), and ordinals shift between
///     builds. This scanner checks the parent type itself plus every nested type, picks
///     methods named <c>&lt;ParentMethod&gt;b__*</c> whose IL touches known state-changing
///     fields (writes only — getters read, so they never match) or methods, and registers
///     each: nested display-class lambdas via <c>MP.RegisterSyncDelegate</c> (captured
///     fields auto-sync when they have workers), parent-type instance lambdas via
///     <c>MP.RegisterSyncMethod</c> (their instance is the syncable comp). Lambdas owned
///     by UI types (gizmos holding the callback, windows) are skipped with a log line —
///     those need an explicit reroute to a named synced method instead, because the
///     display instance itself is not syncable.
/// </summary>
internal static class AncotLambdaSync
{
    private const string LogPrefix = "[Multiplayer Ancot Library Patch]";

    public static int SyncStateChangingLambdas(
        string typeName,
        string parentMethodName,
        string[] stateFieldNames,
        string[] stateMethodNames,
        SyncContext? context = null)
    {
        var found = FindStateLambdas(typeName, parentMethodName, stateFieldNames, stateMethodNames);
        var parentType = AccessTools.TypeByName(typeName);
        var synced = 0;

        foreach (var method in found)
            try
            {
                if (method.DeclaringType == parentType)
                {
                    if (IsUiOwned(parentType))
                    {
                        Log.Message(
                            $"{LogPrefix} Skipped UI-owned lambda {parentType.FullName}.{method.Name} (needs manual reroute).");
                        continue;
                    }

                    var sync = MP.RegisterSyncMethod(method);

                    if (context.HasValue)
                        sync.SetContext(context.Value);
                }
                else
                {
                    var sync = MP.RegisterSyncDelegate(parentType, method.DeclaringType.Name, method.Name, null);

                    if (context.HasValue)
                        sync.SetContext(context.Value);
                }

                synced++;
                Log.Message(
                    $"{LogPrefix} Synced {typeName}.{parentMethodName} lambda {method.DeclaringType.Name}.{method.Name}.");
            }
            catch (Exception exception)
            {
                Log.Warning(
                    $"{LogPrefix} Could not sync {typeName}.{parentMethodName} lambda {method.DeclaringType.Name}.{method.Name}: {exception.Message}");
            }

        if (synced == 0)
            Log.Warning($"{LogPrefix} No state-changing lambdas found in {typeName}.{parentMethodName}.");

        return synced;
    }

    public static List<MethodInfo> FindStateLambdas(
        string typeName,
        string parentMethodName,
        string[] stateFieldNames,
        string[] stateMethodNames)
    {
        var found = new List<MethodInfo>();
        var parentType = AccessTools.TypeByName(typeName);

        if (parentType == null)
        {
            Log.Warning($"{LogPrefix} Type not found: {typeName}.");
            return found;
        }

        var prefix = $"<{parentMethodName}>b__";

        CollectMatching(parentType, parentType, prefix, stateFieldNames, stateMethodNames, found);

        foreach (var nested in AllNestedTypes(parentType))
            CollectMatching(parentType, nested, prefix, stateFieldNames, stateMethodNames, found);

        return found;
    }

    private static void CollectMatching(
        Type parentType,
        Type declaringType,
        string prefix,
        string[] stateFieldNames,
        string[] stateMethodNames,
        List<MethodInfo> found)
    {
        List<MethodInfo> methods;

        try
        {
            methods = AccessTools.GetDeclaredMethods(declaringType);
        }
        catch (Exception exception)
        {
            Log.Warning($"{LogPrefix} Could not list methods of {declaringType.FullName}: {exception.Message}");
            return;
        }

        foreach (var method in methods)
        {
            if (!method.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (TouchesState(method, stateFieldNames, stateMethodNames))
                found.Add(method);
        }
    }

    private static bool TouchesState(MethodInfo method, string[] stateFieldNames, string[] stateMethodNames)
    {
        List<CodeInstruction> instructions;

        try
        {
            instructions = PatchProcessor.GetOriginalInstructions(method);
        }
        catch
        {
            return false;
        }

        foreach (var code in instructions)
        {
            // Writes only: getters read the same fields and must stay local.
            if ((code.opcode == OpCodes.Stfld || code.opcode == OpCodes.Stsfld)
                && code.operand is FieldInfo field
                && Array.IndexOf(stateFieldNames, field.Name) >= 0)
                return true;

            if (code.operand is MethodInfo called && Array.IndexOf(stateMethodNames, called.Name) >= 0)
                return true;
        }

        return false;
    }

    private static bool IsUiOwned(Type type)
    {
        foreach (var uiTypeName in new[] { "Verse.Gizmo", "Verse.Window", "Verse.Command" })
        {
            var uiType = AccessTools.TypeByName(uiTypeName);

            if (uiType != null && uiType.IsAssignableFrom(type))
                return true;
        }

        return false;
    }

    private static IEnumerable<Type> AllNestedTypes(Type type)
    {
        foreach (var nested in type.GetNestedTypes(AccessTools.all))
        {
            yield return nested;

            foreach (var deeper in AllNestedTypes(nested))
                yield return deeper;
        }
    }
}
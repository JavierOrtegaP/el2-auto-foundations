using System;
using System.Collections.Generic;
using HarmonyLib;

namespace AutoFoundations
{
    // Every patch catches its own exceptions and reports them here, so a game update that breaks one part of the mod
    // switches that part off instead of throwing inside the game. The failed patch is removed on the main thread.
    internal static class Guard
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<Type> Failed = new HashSet<Type>();
        private static readonly List<Type> ToUnpatch = new List<Type>();

        public static bool HasFailed(Type patch)
        {
            lock (Lock)
            {
                return Failed.Contains(patch);
            }
        }

        public static bool AnyFailed
        {
            get
            {
                lock (Lock)
                {
                    return Failed.Count > 0;
                }
            }
        }

        // A patch that could not be applied at all.
        public static void MarkFailed(Type patch)
        {
            lock (Lock)
            {
                Failed.Add(patch);
            }
        }

        // Patches run on both the main and the sandbox thread.
        public static void Fail(Type patch, Exception e)
        {
            lock (Lock)
            {
                if (!Failed.Add(patch))
                {
                    return;
                }
                ToUnpatch.Add(patch);
            }
            Plugin.Log.LogError($"{patch.Name} failed and is now off (game update?): {e}");
        }

        // Main thread only.
        public static void UnpatchFailed(Harmony harmony)
        {
            Type[] failed;
            lock (Lock)
            {
                if (ToUnpatch.Count == 0)
                {
                    return;
                }
                failed = ToUnpatch.ToArray();
                ToUnpatch.Clear();
            }
            foreach (Type type in failed)
            {
                try
                {
                    foreach (System.Reflection.MethodBase method in new List<System.Reflection.MethodBase>(harmony.GetPatchedMethods()))
                    {
                        Patches info = Harmony.GetPatchInfo(method);
                        foreach (Patch patch in info.Prefixes)
                        {
                            Remove(harmony, method, patch, type);
                        }
                        foreach (Patch patch in info.Postfixes)
                        {
                            Remove(harmony, method, patch, type);
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Could not unpatch {type.Name}: {e}");
                }
            }
        }

        private static void Remove(Harmony harmony, System.Reflection.MethodBase method, Patch patch, Type type)
        {
            if (patch.owner == harmony.Id && patch.PatchMethod.DeclaringType == type)
            {
                harmony.Unpatch(method, patch.PatchMethod);
            }
        }
    }
}

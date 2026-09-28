using System;
using System.Reflection;

namespace FixMemoryLeaks;

public static class WarpContainerRelease
{
    private static bool resolved;
    private static FieldInfo warpContainer;

    public static void Hook()
    {
        On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
    }

    private static void RainWorldGame_ShutDownProcess(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
    {
        orig(self);

        if (!resolved)
        {
            resolved = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == "Warp")
                    warpContainer = assembly.GetType("WarpModMenu")?.GetField("warpContainer", BindingFlags.Static | BindingFlags.Public);
        }

        warpContainer?.SetValue(null, null);
    }
}

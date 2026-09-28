using BepInEx;
using BepInEx.Logging;

namespace FixMemoryLeaks;

[BepInPlugin(Janitor.Guid, "Fixed Memory Leaks", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;

    public void Awake()
    {
        Log = Logger;
        ComboBoxUnassign.Hook();
        ThreadRelease.Hook();
        WarpContainerRelease.Hook();
        PushToMeowRelease.Hook();
        Janitor.Hook();
    }
}

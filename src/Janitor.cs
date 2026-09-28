using System;
using System.Collections.Generic;
using System.Reflection;

namespace FixMemoryLeaks;

public static class Janitor
{
    public const string Guid = "lwteam.fixmemoryleaks";

    private static readonly FieldInfo oldProcess = typeof(ProcessManager).GetField("oldProcess", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static MainLoopProcess released;
    private static bool switching;
    private static readonly Dictionary<Type, bool> inGameWorld = [];
    private static readonly List<WeakReference> games = [];

    public static void Hook()
    {
        if (oldProcess == null)
        {
            Plugin.Log.LogError("ProcessManager.oldProcess not found, janitor disabled");
            return;
        }

        On.ProcessManager.PreSwitchMainProcess += ProcessManager_PreSwitchMainProcess;
        On.AssetManager.HardCleanFutileAssets += AssetManager_HardCleanFutileAssets;
        On.RainWorldGame.ctor += RainWorldGame_ctor;
    }

    private static void ProcessManager_PreSwitchMainProcess(On.ProcessManager.orig_PreSwitchMainProcess orig, ProcessManager self, ProcessManager.ProcessID ID)
    {
        MainLoopProcess previous = oldProcess.GetValue(self) as MainLoopProcess;
        released = previous != null && previous != self.currentMainLoop && previous != self.pendingProcess ? previous : null;

        switching = true;
        try
        {
            orig(self, ID);
        }
        finally
        {
            switching = false;
        }

        Sweep();
    }

    private static void AssetManager_HardCleanFutileAssets(On.AssetManager.orig_HardCleanFutileAssets orig)
    {
        Sweep();

        if (!switching)
        {
            orig();
            return;
        }

        WeakTableSweeper.Detached detached = null;
        try
        {
            detached = WeakTableSweeper.Detach(GameScoped);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }

        try
        {
            orig();
        }
        finally
        {
            if (detached != null)
            {
                WeakTableSweeper.Reattached result = WeakTableSweeper.Reattach(detached);
                Plugin.Log.LogInfo($"released {result.Released} entries whose keys nothing else holds, kept {result.Restored}, conflicts {result.Conflicts}, tables with shared values {result.SharedTables}, {detached.Milliseconds + result.Milliseconds} ms");
            }
        }
    }

    private static void RainWorldGame_ctor(On.RainWorldGame.orig_ctor orig, RainWorldGame self, ProcessManager manager)
    {
        games.RemoveAll(game => !game.IsAlive);
        int survivors = games.Count;
        games.Add(new WeakReference(self));

        orig(self, manager);

        Plugin.Log.LogInfo($"previous games still in memory: {survivors}");
    }

    private static void Sweep()
    {
        if (released == null)
            return;

        MainLoopProcess dead = released;
        released = null;

        try
        {
            WeakTableSweeper.Result result = WeakTableSweeper.Sweep(dead, Barrier, GameScoped, dead is RainWorldGame ? null : InGameWorld);
            Plugin.Log.LogInfo($"{dead.GetType().Name}: removed {result.Removed} of {result.Entries} entries in {result.Tables} tables, {result.Milliseconds} ms");
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private static bool Barrier(object obj) =>
        obj is MainLoopProcess or RainWorld or ProcessManager or PlayerProgression or Options or UnityEngine.Object
            or FStage or FAtlas or FAtlasElement or FShader or ExtEnumBase or CreatureTemplate
            or SaveState or RegionState or DeathPersistentSaveData or MiscWorldSaveData or WinState
            or PlayerProgression.MiscProgressionData or Menu.SlugcatSelectMenu.SaveGameData;

    private static bool InGameWorld(object obj)
    {
        Type type = obj.GetType();
        if (!inGameWorld.TryGetValue(type, out bool scoped))
            inGameWorld[type] = scoped = GameScoped(type);
        return scoped;
    }

    private static bool GameScoped(Type type) =>
        typeof(UpdatableAndDeletable).IsAssignableFrom(type)
        || typeof(AbstractWorldEntity).IsAssignableFrom(type)
        || typeof(Room).IsAssignableFrom(type)
        || typeof(AbstractRoom).IsAssignableFrom(type)
        || typeof(World).IsAssignableFrom(type)
        || typeof(RainWorldGame).IsAssignableFrom(type)
        || typeof(RoomCamera).IsAssignableFrom(type)
        || typeof(GraphicsModule).IsAssignableFrom(type)
        || typeof(CreatureState).IsAssignableFrom(type);
}

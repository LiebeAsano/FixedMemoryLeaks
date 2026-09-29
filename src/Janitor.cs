using System;
using System.Collections.Generic;
using System.Linq;
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
        On.RainWorldGame.Update += RainWorldGame_Update;
    }

    private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        orig(self);

        try
        {
            WeakTableSweeper.Prune(MortalKey, Gone);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError(e);
        }
    }

    private static void ProcessManager_PreSwitchMainProcess(On.ProcessManager.orig_PreSwitchMainProcess orig, ProcessManager self, ProcessManager.ProcessID ID)
    {
        released = oldProcess.GetValue(self) is MainLoopProcess previous && previous != self.currentMainLoop && previous != self.pendingProcess ? previous : null;

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
            detached = WeakTableSweeper.Detach(OwnedKey);
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
            WeakTableSweeper.Result result = WeakTableSweeper.Sweep(dead, Barrier, OwnedKey, dead is RainWorldGame ? null : InGameWorld);
            Plugin.Log.LogInfo($"{dead.GetType().Name}: removed {result.Removed} of {result.Entries} entries in {result.Tables} tables, {result.Milliseconds} ms");
            if (WeakTableSweeper.Pruned > 0)
            {
                Plugin.Log.LogInfo($"removed {WeakTableSweeper.Pruned} entries of destroyed objects during play");
                WeakTableSweeper.Pruned = 0;
            }
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

    private static readonly HashSet<string> modWorldTypes =
    [
        "LetMeSetMyNeedlesDown.LetMeSetMyNeedlesDown+NeedleAndCord",
        "RandomBuffUtils.ParticleSystem.ParticleEmitter"
    ];

    private static readonly Type[] gameScoped =
    [
        typeof(UpdatableAndDeletable), typeof(AbstractWorldEntity), typeof(Room), typeof(AbstractRoom), typeof(World),
        typeof(RainWorldGame), typeof(RoomCamera), typeof(GraphicsModule), typeof(CreatureState)
    ];

    private static readonly Type[] owned =
    [
        .. gameScoped,
        typeof(RoomCamera.SpriteLeaser), typeof(ArtificialIntelligence), typeof(AIModule), typeof(BodyPart), typeof(Room.Tile),
        typeof(RoomSettings), typeof(Region), typeof(Region.RegionParams), typeof(PlacedObject), typeof(PlacedObject.Data),
        typeof(WorldLoader), typeof(BodyChunk), typeof(Creature.Grasp), typeof(AbstractPhysicalObject.AbstractObjectStick),
        typeof(AbstractCreatureAI), typeof(FlyAI), typeof(OracleBehavior), typeof(Conversation), typeof(Tentacle),
        typeof(Player.SpearOnBack), typeof(KingTusks.Tusk), typeof(DaddyGraphics.HunterDummy),
        typeof(MoreSlugcats.VultureMaskGraphics), typeof(MoreSlugcats.ConsoleVisualizer),
        typeof(JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyPointer), typeof(Menu.Remix.MixedUI.UIelement),
        typeof(Water), typeof(OverWorld), typeof(GlobalRain), typeof(RainCycle), typeof(GhostWorldPresence), typeof(GameSession),
        typeof(HUD.HUD), typeof(HUD.HudPart), typeof(HUD.FoodMeter.MeterCircle),
        typeof(Menu.MenuObject), typeof(DevInterface.DevUI), typeof(DevInterface.DevUINode)
    ];

    private static readonly Type[] mortal =
    [
        typeof(UpdatableAndDeletable), typeof(AbstractWorldEntity), typeof(RoomCamera.SpriteLeaser), typeof(BodyChunk)
    ];

    private static bool OwnedKey(Type type) =>
        owned.Any(scope => scope.IsAssignableFrom(type)) || modWorldTypes.Contains(type.FullName);

    private static bool GameScoped(Type type) =>
        gameScoped.Any(scope => scope.IsAssignableFrom(type));

    private static bool MortalKey(Type key) =>
        mortal.Any(kind => kind.IsAssignableFrom(key) || key.IsAssignableFrom(kind));

    private static bool Gone(object key) => key switch
    {
        UpdatableAndDeletable obj => obj.slatedForDeletetion,
        AbstractWorldEntity entity => entity.slatedForDeletion,
        RoomCamera.SpriteLeaser leaser => leaser.deleteMeNextFrame,
        BodyChunk chunk => chunk.owner is { slatedForDeletetion: true },
        _ => false
    };
}

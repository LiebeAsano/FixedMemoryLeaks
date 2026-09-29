using System;
using System.Collections.Generic;
using System.Reflection;

namespace FixMemoryLeaks;

public static class OrphanedPreparers
{
    private const int Finished = 5;
    private const int Grace = 60;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo status = typeof(RoomPreparer).GetField("status", Members);
    private static readonly FieldInfo requestShortcutsReady = typeof(RoomPreparer).GetField("requestShortcutsReady", Members);
    private static readonly FieldInfo requestReadyForAI = typeof(RoomPreparer).GetField("requestReadyForAI", Members);
    private static readonly FieldInfo threadFinished = typeof(RoomPreparer).GetField("threadFinished", Members);
    private static readonly FieldInfo thread = typeof(RoomPreparer).GetField("thread", Members);

    private static readonly List<RoomPreparer> orphans = [];
    private static readonly Dictionary<RoomPreparer, int> running = [];
    private static readonly List<RoomPreparer> gone = [];
    private static int frame;
    private static Action<string> log;

    public static void Hook(Action<string> logger)
    {
        log = logger;
        On.RoomPreparer.Update += RoomPreparer_Update;
        On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
        On.OverWorld.WorldLoaded += OverWorld_WorldLoaded;
        On.ProcessManager.Update += ProcessManager_Update;
    }

    private static void RoomPreparer_Update(On.RoomPreparer.orig_Update orig, RoomPreparer self)
    {
        orig(self);

        if (!Stopped(self))
            running[self] = frame;
    }

    private static void RainWorldGame_ShutDownProcess(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
    {
        orig(self);
        Adopt(self.world, null);
    }

    private static void OverWorld_WorldLoaded(On.OverWorld.orig_WorldLoaded orig, OverWorld self, bool warpUsed)
    {
        World previous = self.activeWorld;

        orig(self, warpUsed);

        if (previous != null && previous != self.activeWorld)
            Adopt(previous, self.activeWorld);
    }

    private static void Adopt(World world, World successor)
    {
        if (world?.loadingRooms == null)
            return;

        int adopted = 0;
        for (int i = world.loadingRooms.Count - 1; i >= 0; i--)
        {
            RoomPreparer preparer = world.loadingRooms[i];
            if (preparer == null || preparer.done || (successor != null && preparer.room?.world == successor))
                continue;

            world.loadingRooms.RemoveAt(i);
            if (Orphan(preparer))
                adopted++;
        }

        if (adopted > 0)
            log?.Invoke($"stopping {adopted} room preparation threads left in {world.name}");
    }

    private static bool Orphan(RoomPreparer preparer)
    {
        if (orphans.Contains(preparer))
            return false;
        orphans.Add(preparer);
        return true;
    }

    private static bool Stopped(RoomPreparer preparer) =>
        thread.GetValue(preparer) == null || (bool)threadFinished.GetValue(preparer);

    private static bool Abandoned(RoomPreparer preparer)
    {
        Room room = preparer.room;
        return room == null || (room.abstractRoom?.realizedRoom != room && room.world?.loadingRooms?.Contains(preparer) != true);
    }

    private static void ProcessManager_Update(On.ProcessManager.orig_Update orig, ProcessManager self, float deltaTime)
    {
        orig(self, deltaTime);
        frame++;

        foreach (KeyValuePair<RoomPreparer, int> entry in running)
        {
            RoomPreparer preparer = entry.Key;
            if (Stopped(preparer))
                gone.Add(preparer);
            else if (frame - entry.Value > Grace && Abandoned(preparer))
            {
                gone.Add(preparer);
                if (Orphan(preparer))
                    log?.Invoke($"stopping the room preparation thread abandoned in {preparer.room?.abstractRoom?.name ?? "an unloaded room"}");
            }
        }

        foreach (RoomPreparer preparer in gone)
            running.Remove(preparer);
        gone.Clear();

        for (int i = orphans.Count - 1; i >= 0; i--)
        {
            RoomPreparer preparer = orphans[i];
            lock (preparer)
            {
                if (Stopped(preparer))
                {
                    orphans.RemoveAt(i);
                    continue;
                }

                requestShortcutsReady.SetValue(preparer, false);
                requestReadyForAI.SetValue(preparer, false);
                status.SetValue(preparer, Finished);
            }
        }
    }
}

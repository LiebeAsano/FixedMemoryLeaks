using System;
using System.Reflection;
using System.Threading;

namespace FixMemoryLeaks;

public static class ThreadRelease
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo startDelegate = typeof(Thread).GetField("m_Delegate", Instance);
    private static readonly FieldInfo helperStart = typeof(Thread).Assembly.GetType("System.Threading.ThreadHelper")?.GetField("_start", Instance);

    public static void Hook()
    {
        On.RoomPreparer.UpdateThread += RoomPreparer_UpdateThread;
        On.AImapper.AIMappingThread += AImapper_AIMappingThread;
        On.WorldLoader.UpdateThread += WorldLoader_UpdateThread;
        On.WorldLoader.CreatingAbstractRoomsThread += WorldLoader_CreatingAbstractRoomsThread;
        On.WorldLoader.FindingCreaturesThread += WorldLoader_FindingCreaturesThread;
    }

    public static void Release(object owner)
    {
        Thread thread = Thread.CurrentThread;
        if (startDelegate?.GetValue(thread) is Delegate start && start.Target != null
            && helperStart?.GetValue(start.Target) is Delegate work && ReferenceEquals(work.Target, owner))
            startDelegate.SetValue(thread, null);
    }

    private static void RoomPreparer_UpdateThread(On.RoomPreparer.orig_UpdateThread orig, RoomPreparer self)
    {
        try
        {
            orig(self);
        }
        finally
        {
            Release(self);
        }
    }

    private static void AImapper_AIMappingThread(On.AImapper.orig_AIMappingThread orig, AImapper self)
    {
        try
        {
            orig(self);
        }
        finally
        {
            Release(self);
        }
    }

    private static void WorldLoader_UpdateThread(On.WorldLoader.orig_UpdateThread orig, WorldLoader self)
    {
        try
        {
            orig(self);
        }
        finally
        {
            Release(self);
        }
    }

    private static void WorldLoader_CreatingAbstractRoomsThread(On.WorldLoader.orig_CreatingAbstractRoomsThread orig, WorldLoader self)
    {
        try
        {
            orig(self);
        }
        finally
        {
            Release(self);
        }
    }

    private static void WorldLoader_FindingCreaturesThread(On.WorldLoader.orig_FindingCreaturesThread orig, WorldLoader self)
    {
        try
        {
            orig(self);
        }
        finally
        {
            Release(self);
        }
    }
}

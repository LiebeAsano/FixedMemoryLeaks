using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;

namespace FixMemoryLeaks;

public static class PushToMeowRelease
{
    private const string Guid = "pushtomeow";

    private static readonly string[] playerStateFields = ["PlayersMeowButtonLastState", "PlayersMeowingState", "PlayersLastMeowTime", "PlayersLookup"];

    private static bool resolved;
    private static IDictionary npcLastMeow;
    private static readonly List<IDictionary> playerStates = [];

    public static void Hook()
    {
        On.AbstractCreature.Abstractize += AbstractCreature_Abstractize;
        On.Player.Destroy += Player_Destroy;
        On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
    }

    private static void AbstractCreature_Abstractize(On.AbstractCreature.orig_Abstractize orig, AbstractCreature self, WorldCoordinate coord)
    {
        Creature realized = self.realizedCreature;
        orig(self, coord);

        if (realized is Player player && self.realizedCreature != player && Resolve())
            npcLastMeow.Remove(player);
    }

    private static void Player_Destroy(On.Player.orig_Destroy orig, Player self)
    {
        orig(self);

        if (Resolve())
            npcLastMeow.Remove(self);
    }

    private static void RainWorldGame_ShutDownProcess(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
    {
        orig(self);

        if (!Resolve())
            return;

        npcLastMeow.Clear();
        foreach (IDictionary state in playerStates)
            state.Clear();
    }

    private static bool Resolve()
    {
        if (!resolved)
        {
            resolved = true;
            if (Chainloader.PluginInfos.TryGetValue(Guid, out PluginInfo info) && info.Instance is { } plugin)
            {
                npcLastMeow = plugin.GetType().Assembly.GetType("PushToMeowMod.MeowUtils")?.GetField("SlugNPCLastMeow", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) as IDictionary;
                foreach (string name in playerStateFields)
                    if (plugin.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(plugin) is IDictionary state)
                        playerStates.Add(state);
            }
        }

        return npcLastMeow != null;
    }
}

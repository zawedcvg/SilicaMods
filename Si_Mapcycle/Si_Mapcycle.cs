﻿/*
Silica Map Cycle
Copyright (C) 2023 by databomb

* Description *
Provides map management and cycles to a server.

* License *
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

#if NET6_0
using Il2Cpp;
# endif

using HarmonyLib;
using MelonLoader;
using Si_Mapcycle;
using System.Timers;
using MelonLoader.Utils;
using System;
using System.IO;
using System.Collections.Generic;
using SilicaAdminMod;

[assembly: MelonInfo(typeof(MapCycleMod), "Mapcycle", "1.2.1", "databomb", "https://github.com/data-bomb/Silica")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod")]

namespace Si_Mapcycle
{
    public class MapCycleMod : MelonMod
    {
        static String mapName = "";
        static GameMode gameModeInstance;
        static bool bEndRound;
        static bool bTimerExpired;
        static int iMapLoadCount;
        static string[] sMapCycle;

        private static System.Timers.Timer DelayTimer;

        public override void OnInitializeMelon()
        {
            String mapCycleFile = MelonEnvironment.UserDataDirectory + "\\mapcycle.txt";

            try
            {
                if (File.Exists(mapCycleFile))
                {
                    // Open the stream and read it back.
                    using (StreamReader mapFileStream = File.OpenText(mapCycleFile))
                    {
                        List<string> sMapList = new List<string>();
                        string sMap = "";
                        while ((sMap = mapFileStream.ReadLine()) != null)
                        {
                            sMapList.Add(sMap);
                        }
                        sMapCycle = sMapList.ToArray();
                    }
                }
                else
                {
                    // Create simple mapcycle.txt file
                    using (FileStream fs = File.Create(mapCycleFile))
                    {
                        fs.Close();
                        System.IO.File.WriteAllText(mapCycleFile, "RiftBasin\nGreatErg\nBadlands\nNarakaCity\n");
                    }
                }

            }
            catch (Exception exception)
            {
                MelonLogger.Msg(exception.ToString());
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            mapName = sceneName;
        }

        //TODO change to use the admin helper methods instead
        #if NET6_0
        [HarmonyPatch(typeof(Il2CppSilica.UI.Chat), nameof(Il2CppSilica.UI.Chat.MessageReceived))]
        #else
        [HarmonyPatch(typeof(Silica.UI.Chat), "MessageReceived")]
        #endif
        private static class ApplyChatReceiveCurrentMatchInfo
        {
            #if NET6_0
            public static void Postfix(Il2CppSilica.UI.Chat __instance, Player __0, string __1, bool __2)
            #else
            public static void Postfix(Silica.UI.Chat __instance, Player __0, string __1, bool __2)
            #endif
            {
                try
                {
                    if (!__instance.ToString().Contains("alien") || __2 == true)
                    {
                        return;
                    }

                    bool isCurrMapCommand = String.Equals(__1, "currentmap", StringComparison.OrdinalIgnoreCase);
                    if (isCurrMapCommand)
                    {
                        HelperMethods.ReplyToCommand("Current map is " + mapName);
                        return;
                    }

                    bool isNextMapCommand = String.Equals(__1, "nextmap", StringComparison.OrdinalIgnoreCase);
                    if (isNextMapCommand)
                    {
                        HelperMethods.ReplyToCommand("Next map is " + sMapCycle[(iMapLoadCount+1) % (sMapCycle.Length-1)]);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    HelperMethods.PrintError(exception);
                }
            }
        }

        private static void HandleTimerChangeLevel(object source, ElapsedEventArgs e)
        {
            MapCycleMod.bTimerExpired = true;
        }

        #if NET6_0
        [HarmonyPatch(typeof(MusicJukeboxHandler), nameof(MusicJukeboxHandler.Update))]
        #else
        [HarmonyPatch(typeof(MusicJukeboxHandler), "Update")]
        #endif
        private static class ApplyPatch_MusicJukeboxHandlerUpdate
        {
            private static void Postfix(MusicJukeboxHandler __instance)
            {
                // check if timer expired
                if (MapCycleMod.bEndRound == true && MapCycleMod.bTimerExpired == true)
                {
                    MapCycleMod.bEndRound = false;

                    MapCycleMod.iMapLoadCount++;

                    String sNextMap = sMapCycle[iMapLoadCount % (sMapCycle.Length-1)];

                    String sCurrentMap = NetworkGameServer.GetServerMap();
                    MelonLogger.Msg("Changing map to " + sNextMap + "...");

                    NetworkGameServer.LoadLevel(sNextMap, MapCycleMod.gameModeInstance.GameModeInfo);
                }
            }
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), nameof(MusicJukeboxHandler.OnGameEnded))]
        private static class ApplyPatch_OnGameEnded
        {
            public static void Postfix(MusicJukeboxHandler __instance, GameMode __0, Team __1)
            {
                HelperMethods.ReplyToCommand("Preparing to change map to " + sMapCycle[(iMapLoadCount + 1) % (sMapCycle.Length-1)] + "..." );
                MapCycleMod.gameModeInstance = __0;
                MapCycleMod.bEndRound = true;
                MapCycleMod.bTimerExpired = false;

                double interval = 12500.0;
                MapCycleMod.DelayTimer = new System.Timers.Timer(interval);
                MapCycleMod.DelayTimer.Elapsed += new ElapsedEventHandler(MapCycleMod.HandleTimerChangeLevel);
                MapCycleMod.DelayTimer.AutoReset = false;
                MapCycleMod.DelayTimer.Enabled = true;
            }
        }
    }
}
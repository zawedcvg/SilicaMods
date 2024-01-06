﻿/*
 Silica Commander Management Mod
 Copyright (C) 2024 by databomb
 
 * Description *
 For Silica servers, establishes a random selection for commander at the 
 start of each round and provides for admin commands to !demote a team's
 commander as well as !cmdrban a player from being commander in the
 future.

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
#else
using System.Reflection;
#endif

using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using Si_CommanderManagement;
using UnityEngine;
using System;
using System.Collections.Generic;
using SilicaAdminMod;
using System.Linq;

[assembly: MelonInfo(typeof(CommanderManager), "Commander Management", "1.3.4", "databomb", "https://github.com/data-bomb/Silica")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod")]

namespace Si_CommanderManagement
{
    public class CommanderManager : MelonMod
    {
        static MelonPreferences_Category? _modCategory;
        static MelonPreferences_Entry<bool>? _BlockRoundStartUntilEnoughApplicants;

        const int MaxTeams = 3;
        const int AlienTeam = 0;
        const int CentauriTeam = 1;
        const int SolTeam = 2;

        public class BanEntry
        {
            public long OffenderSteamId
            {
                get;
                set;
            }
            public String? OffenderName
            {
                get;
                set;
            }
            public int UnixBanTime
            {
                get;
                set;
            }
            public String? Comments
            {
                get;
                set;
            }
        }

        static List<Player>[]? commanderApplicants;
        static List<Player>? previousCommanders;
        static bool bOnGameInitFired;
        static bool AdminModAvailable = false;

        static Player?[]? teamswapCommanderChecks;
        static Player[]? promotedCommanders;
        static List<BanEntry>? MasterBanList;
        static readonly String banListFile = System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "commander_bans.json");

        public static void UpdateCommanderBanFile()
        {
            // convert back to json string
            String JsonRaw = JsonConvert.SerializeObject(MasterBanList, Newtonsoft.Json.Formatting.Indented);
            System.IO.File.WriteAllText(banListFile, JsonRaw);
        }

        public override void OnInitializeMelon()
        {
            _modCategory ??= MelonPreferences.CreateCategory("Silica");
            _BlockRoundStartUntilEnoughApplicants ??= _modCategory.CreateEntry<bool>("BlockRoundStartUntilCommandersApplied", true);

            try
            {
                if (System.IO.File.Exists(CommanderManager.banListFile))
                {
                    // Open the stream and read it back.
                    System.IO.StreamReader banFileStream = System.IO.File.OpenText(CommanderManager.banListFile);
                    using (banFileStream)
                    {
                        String JsonRaw = banFileStream.ReadToEnd();
                        if (JsonRaw == null)
                        {
                            MelonLogger.Warning("The commander_bans.json read as empty. No commander ban entries loaded.");
                        }
                        else
                        {
                            MasterBanList = JsonConvert.DeserializeObject<List<BanEntry>>(JsonRaw);
                            if (MasterBanList == null)
                            {
                                MelonLogger.Warning("Encountered deserialization error in commander_bans.json file. Ensure file is in valid format (e.g. https://jsonlint.com/)");
                            }
                            else
                            {
                                MelonLogger.Msg("Loaded Silica commander banlist with " + MasterBanList.Count + " entries.");
                            }
                        }
                    }
                }
                else
                {
                    MelonLogger.Warning("Did not find commander_bans.json file. No commander ban entries loaded.");
                    MasterBanList = new List<BanEntry>();
                }

                CommanderManager.commanderApplicants = new List<Player>[MaxTeams];
                CommanderManager.commanderApplicants[AlienTeam] = new List<Player>();
                CommanderManager.commanderApplicants[CentauriTeam] = new List<Player>();
                CommanderManager.commanderApplicants[SolTeam] = new List<Player>();

                CommanderManager.previousCommanders = new List<Player>();

                CommanderManager.teamswapCommanderChecks = new Player[MaxTeams];
                CommanderManager.promotedCommanders = new Player[MaxTeams];

                bOnGameInitFired = false;
            }
            catch (Exception error)
            {
                HelperMethods.PrintError(error, "Failed to load Silica commander banlist (OnInitializeMelon)");
            }
        }

        public override void OnLateInitializeMelon()
        {
            AdminModAvailable = RegisteredMelons.Any(m => m.Info.Name == "Admin Mod");

            if (AdminModAvailable)
            {
                HelperMethods.CommandCallback commanderBanCallback = Command_CommanderBan;
                HelperMethods.RegisterAdminCommand("!cmdrban", commanderBanCallback, Power.Commander);
                HelperMethods.RegisterAdminCommand("!commanderban", commanderBanCallback, Power.Commander);
                HelperMethods.RegisterAdminCommand("!cban", commanderBanCallback, Power.Commander);

                HelperMethods.CommandCallback commanderUnbanCallback = Command_CommanderUnban;
                HelperMethods.RegisterAdminCommand("!removecommanderban", commanderUnbanCallback, Power.Commander);
                HelperMethods.RegisterAdminCommand("!uncban", commanderUnbanCallback, Power.Commander);

                HelperMethods.CommandCallback commanderDemoteCallback = Command_CommanderDemote;
                HelperMethods.RegisterAdminCommand("!demote", commanderDemoteCallback, Power.Commander);
            }
            else
            {
                MelonLogger.Warning("Dependency missing: Admin Mod");
            }
        }

        public static void SendToRole(Player FormerCommander, MP_Strategy.ETeamRole role)
        {
            GameByteStreamWriter theRoleStream;
            theRoleStream = GameMode.CurrentGameMode.CreateRPCPacket(2);
            if (theRoleStream == null)
            {
                return;
            }

            theRoleStream.WriteUInt64(FormerCommander.PlayerID.m_SteamID);
            theRoleStream.WriteByte((byte)FormerCommander.PlayerChannel);
            theRoleStream.WriteByte((byte)role);
            GameMode.CurrentGameMode.SendRPCPacket(theRoleStream);
        }

        // may need to re-think this approach on preventing commander promotion
        [HarmonyPatch(typeof(MP_Strategy), nameof(MP_Strategy.GetStrategyCommanderTeamSetup))]
        private static class ApplyPatchCommanderTeamSetup
        {
            public static bool Prefix(MP_Strategy __instance, StrategyTeamSetup? __result, Player? __0)
            {
                try
                {
                    if (__0 == null)
                    {
                        return true;
                    }

                    if (MasterBanList == null)
                    {
                        return true;
                    }

                    // check if player is allowed to be commander
                    long joiningPlayerSteamId = long.Parse(__0.ToString().Split('_')[1]);
                    BanEntry? banEntry = MasterBanList.Find(i => i.OffenderSteamId == joiningPlayerSteamId);
                    if (banEntry != null)
                    {
                        MelonLogger.Msg("Preventing " + banEntry.OffenderName + " from selecting commander.");

                        __0 = null;
                        __result = null;
                        return false;
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run MP_Strategy::GetStrategyCommanderTeamSetup");
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), nameof(MusicJukeboxHandler.OnGameInit))]
        private static class ApplyPatchOnGameInit
        {
            public static void Postfix(MusicJukeboxHandler __instance, GameMode __0)
            {
                try
                {
                    if (commanderApplicants == null || teamswapCommanderChecks == null || previousCommanders == null)
                    {
                        return;
                    }

                    // prevent from running twice in one round switch cycle
                    if (!bOnGameInitFired)
                    {
                        bOnGameInitFired = true;

                        int NumCommandersPastRound = 0;
                        for (int i = 0; i < MaxTeams; i++)
                        {
                            if (commanderApplicants[i].Count > 0)
                            {
                                MelonLogger.Msg("Clearing applicants from team index " + i.ToString());
                                commanderApplicants[i].Clear();

                                NumCommandersPastRound++;

                                // clear previous commander tracking status, if any
                                teamswapCommanderChecks[i] = null;
                            }
                        }


                        // we want to remove the oldest commanders from the list
                        int NumCommandersToRemove = previousCommanders.Count - NumCommandersPastRound;
                        if (NumCommandersToRemove < 0)
                        {
                            MelonLogger.Warning("Logic error. NumCommandersToRemove is " + NumCommandersToRemove.ToString());
                            NumCommandersPastRound = 0;
                        }

                        if (CommanderManager.previousCommanders.Count > NumCommandersToRemove)
                        {
                            // remove the commanders from 2 rounds ago. first entry is the oldest.
                            for (int i = 0; i < NumCommandersToRemove; i++)
                            {
                                CommanderManager.previousCommanders.RemoveAt(i);
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run MusicJukeboxHandler::OnGameInit");
                }
            }
        }

        [HarmonyPatch(typeof(MusicJukeboxHandler), nameof(MusicJukeboxHandler.OnGameStarted))]
        private static class ApplyPatchOnGameStarted
        {
            public static void Postfix(MusicJukeboxHandler __instance, GameMode __0)
            {
                try
                {
                    bOnGameInitFired = false;

                    if (commanderApplicants == null || previousCommanders == null || promotedCommanders == null)
                    {
                        return;
                    }

                    // *** TODO: need to account for if a player leaves the game within the 30 second window
                    System.Random randomIndex = new System.Random();
                    Player? RemovePlayer = null;

                    for (int i = 0; i < MaxTeams; i++)
                    {
                        if (commanderApplicants[i].Count == 0)
                        {
                            continue;
                        }

                        // remove previous commanders from applicant list
                        for (int j = 0; j < CommanderManager.previousCommanders.Count; j++)
                        {
                            RemovePlayer = commanderApplicants[i].Find(k => k == CommanderManager.previousCommanders[j]);
                            if (RemovePlayer != null)
                            {
                                MelonLogger.Msg("Removing applicant from 2 rounds ago from random selection: " + RemovePlayer.PlayerName);
                                commanderApplicants[i].Remove(RemovePlayer);
                            }
                        }

                        if (commanderApplicants[i].Count == 0)
                        {
                            continue;
                        }

                        int iCommanderIndex = randomIndex.Next(0, commanderApplicants[i].Count - 1);
                        Player CommanderPlayer = commanderApplicants[i][iCommanderIndex];

                        if (CommanderPlayer != null && CommanderPlayer.Team.Index == i)
                        {
                            HelperMethods.ReplyToCommand("Promoted " + HelperMethods.GetTeamColor(CommanderPlayer) + CommanderPlayer.PlayerName + HelperMethods.defaultColor + " to commander for " + HelperMethods.GetTeamColor(CommanderPlayer) + CommanderPlayer.Team.TeamName);
                            promotedCommanders[CommanderPlayer.Team.Index] = CommanderPlayer;
                            PromoteToCommander(CommanderPlayer);
                            previousCommanders.Add(CommanderPlayer);
                        }
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run MusicJukeboxHandler::OnGameStarted");
                }
            }
        }

        #if NET6_0
        [HarmonyPatch(typeof(MP_Strategy), nameof(MP_Strategy.SetCommander))]
        #else
        [HarmonyPatch(typeof(MP_Strategy), "SetCommander")]
        #endif
        private static class ApplyPatchSetCommander
        {
            public static bool Prefix(MP_Strategy __instance, Team __0, Player? __1)
            {
                try
                {
                    if (__instance == null || __0 == null || MasterBanList == null || commanderApplicants == null || teamswapCommanderChecks == null || promotedCommanders == null)
                    {
                        return true;
                    }

                    if (__1 != null)
                    {
                        // check if player is allowed to be commander
                        long JoiningPlayerSteamId = long.Parse(__1.ToString().Split('_')[1]);
                        BanEntry? banEntry = MasterBanList.Find(i => i.OffenderSteamId == JoiningPlayerSteamId);
                        if (banEntry != null)
                        {
                            MelonLogger.Msg("Preventing " + banEntry.OffenderName + " from playing as commander.");

                            // need to get the player back to Infantry and not stuck in no-clip
                            SendToRole(__1, MP_Strategy.ETeamRole.INFANTRY);
                            // respawn
                            GameMode.CurrentGameMode.SpawnUnitForPlayer(__1, __0);

                            __1 = null;
                            return false;
                        }

                        // check if they're trying to join before the 30 second countdown expires and the game begins
                        if (GameMode.CurrentGameMode.Started && !GameMode.CurrentGameMode.GameBegun)
                        {
                            // check if player is already an applicant
                            if (!commanderApplicants[__1.Team.Index].Contains(__1))
                            {
                                HelperMethods.ReplyToCommand_Player(__1, "has applied for commander");

                                // need to get the player back to Infantry and not stuck in no-clip
                                SendToRole(__1, MP_Strategy.ETeamRole.INFANTRY);
                                // respawn
                                GameMode.CurrentGameMode.SpawnUnitForPlayer(__1, __0);

                                commanderApplicants[__1.Team.Index].Add(__1);
                            }
                            Player? stratComm = __instance.GetStrategyTeamSetup(__1.Team).Commander;

                            MelonLogger.Msg("Denied SetCommander with player " + __1.PlayerName + " Team's Commander is: " + stratComm.PlayerName);

                            __1 = null;
                            return false;
                        }

                        // when the game is in full swing
                        if (GameMode.CurrentGameMode.Started && GameMode.CurrentGameMode.GameBegun)
                        {
                            // determine if promoted commander was previously commanding another team
                            int commanderSwappedTeamIndex = -1;
                            for (int i = 0; i < MaxTeams; i++)
                            {
                                if (i == __0.Index)
                                {
                                    continue;
                                }

                                if (teamswapCommanderChecks[i] == __1)
                                {
                                    commanderSwappedTeamIndex = i;
                                    break;
                                }
                            }

                            // announce a commander swapped to command another team
                            if (commanderSwappedTeamIndex != -1)
                            {
                                Team departingTeam = Team.Teams[commanderSwappedTeamIndex];
                                HelperMethods.ReplyToCommand_Player(__1, "has left command of " + HelperMethods.GetTeamColor(departingTeam) + departingTeam.TeamName + HelperMethods.defaultColor + " and taken command of " + HelperMethods.GetTeamColor(__0) + __0.TeamName);
                                teamswapCommanderChecks[commanderSwappedTeamIndex] = null;
                            }
                            else
                            {
                                // announce a new commander, if needed
                                if (teamswapCommanderChecks[__0.Index] != __1 && promotedCommanders[__0.Index] != __1)
                                {
                                    promotedCommanders[__0.Index] = __1;
                                    HelperMethods.ReplyToCommand_Player(__1, "has taken command of " + HelperMethods.GetTeamColor(__0) + __0.TeamName);
                                }
                            }
                        }
                    }
                    // player is null
                    else
                    {
                        // check if there is a current commander
                        Player? teamCommander = __instance.GetCommanderForTeam(__0);
                        if (teamCommander != null)
                        {
                            teamswapCommanderChecks[__0.Index] = teamCommander;
                        }
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run MP_Strategy::SetCommander");
                }

                return true;
            }
        }

        public static bool RemoveCommanderBan(Player playerToCmdrUnban)
        {
            if (MasterBanList == null)
            {
                return false;
            }

            BanEntry? matchingCmdrBan;
            matchingCmdrBan = MasterBanList.Find(i => i.OffenderSteamId == (long)playerToCmdrUnban.PlayerID.m_SteamID);

            if (matchingCmdrBan == null)
            {
                return false;
            }

            MelonLogger.Msg("Removed player name (" + matchingCmdrBan.OffenderName + ") SteamID (" + matchingCmdrBan.OffenderSteamId.ToString() + ") from the commander banlist.");
            MasterBanList.Remove(matchingCmdrBan);
            UpdateCommanderBanFile();

            return true;
        }

        public static void AddCommanderBan(Player playerToCmdrBan)
        {
            if (MasterBanList == null)
            {
                return;
            }

            // gather information to log in the banlist
            Player serverPlayer = NetworkGameServer.GetServerPlayer();
            BanEntry thisBan = new BanEntry()
            {
                OffenderSteamId = long.Parse(playerToCmdrBan.ToString().Split('_')[1]),
                OffenderName = playerToCmdrBan.PlayerName,
                UnixBanTime = (int)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds,
                Comments = "banned from playing commander by " + serverPlayer.PlayerName
            };

            // are we currently a commander?
            if (playerToCmdrBan.IsCommander)
            {
                Team playerTeam = playerToCmdrBan.Team;
                MP_Strategy strategyInstance = GameObject.FindObjectOfType<MP_Strategy>();

                DemoteTeamsCommander(strategyInstance, playerTeam);
                HelperMethods.ReplyToCommand_Player(playerToCmdrBan, "was demoted");
            }

            // are we already banned?
            if (MasterBanList.Find(i => i.OffenderSteamId == thisBan.OffenderSteamId) != null)
            {
                MelonLogger.Warning("Player name (" + thisBan.OffenderName + ") SteamID (" + thisBan.OffenderSteamId.ToString() + ") already on commander banlist.");
            }
            else
            {
                MelonLogger.Msg("Added player name (" + thisBan.OffenderName + ") SteamID (" + thisBan.OffenderSteamId.ToString() + ") to the commander banlist.");
                MasterBanList.Add(thisBan);
                UpdateCommanderBanFile();
            }
        }

        public static void PromoteToCommander(Player CommanderPlayer)
        {
            MP_Strategy strategyInstance = GameObject.FindObjectOfType<MP_Strategy>();

            // lock in commanders on the server side
            StrategyTeamSetup strategyTeamInstance = strategyInstance.GetStrategyTeamSetup(CommanderPlayer.Team);

#if NET6_0
            GameMode.CurrentGameMode.DestroyAllUnitsForPlayer(CommanderPlayer);
            if (strategyInstance.PlayerRespawnTracker.ContainsKey(CommanderPlayer))
            {
                strategyInstance.PlayerRespawnTracker.Remove(CommanderPlayer);
            }
            strategyInstance.SetCommander(CommanderPlayer.Team, CommanderPlayer);
            strategyInstance.RPC_SynchCommander(CommanderPlayer.Team);
#else
            GameMode.CurrentGameMode.DestroyAllUnitsForPlayer(CommanderPlayer);
            FieldInfo respawnTrackerField = typeof(MP_Strategy).GetField("PlayerRespawnTracker", BindingFlags.NonPublic | BindingFlags.Instance);
            Dictionary<Player, float> respawnCopy = (Dictionary<Player, float>)respawnTrackerField.GetValue(strategyInstance);
            if (respawnCopy.ContainsKey(CommanderPlayer))
            {
                respawnCopy.Remove(CommanderPlayer);
                respawnTrackerField.SetValue(strategyInstance, respawnCopy);
            }

            MethodInfo setCommanderMethod = typeof(MP_Strategy).GetMethod("SetCommander", BindingFlags.Instance | BindingFlags.NonPublic);
            setCommanderMethod.Invoke(strategyInstance, new object[] { CommanderPlayer.Team, CommanderPlayer });
            MethodInfo synchCommanderMethod = typeof(MP_Strategy).GetMethod("RPC_SynchCommander", BindingFlags.Instance | BindingFlags.NonPublic);
            synchCommanderMethod.Invoke(strategyInstance, new object[] { CommanderPlayer.Team });
            #endif

            MelonLogger.Msg("Trying to promote " + CommanderPlayer.PlayerName + " on team " + CommanderPlayer.Team.TeamName + " and team instance " + strategyTeamInstance.ToString());

            // replicate to client to get them to re-select commander
            /*
            Il2Cpp.GameByteStreamWriter theTeamStream;
            theTeamStream = Il2Cpp.GameMode.CurrentGameMode.CreateRPCPacket((byte)MP_Strategy.ERPCs.REQUEST_ROLE);
            if (theTeamStream == null)
            {
                MelonLogger.Warning("Could not create GameByteStreamWriter for PromoteToCommander");
                return;
            }

            theTeamStream.WriteUInt64(CommanderPlayer.PlayerID.m_SteamID);
            theTeamStream.WriteByte((byte)CommanderPlayer.PlayerChannel);
            theTeamStream.WriteByte((byte)MP_Strategy.ETeamRole.COMMANDER);
            theTeamStream.WriteTeam(CommanderPlayer.Team);
            Il2Cpp.GameMode.CurrentGameMode.SendRPCPacket(theTeamStream);
            */
            // TODO: Investigate what more to do so commanders don't need to switch back to commander using 'T'
        }

        public static void DemoteTeamsCommander(MP_Strategy strategyInstance, Team TargetTeam)
        {
            Player DemotedCommander = strategyInstance.GetCommanderForTeam(TargetTeam);

            #if NET6_0
            strategyInstance.SetCommander(TargetTeam, null);
            strategyInstance.RPC_SynchCommander(TargetTeam);
            #else
            Type strategyType = typeof(MP_Strategy);
            MethodInfo setCommanderMethod = strategyType.GetMethod("SetCommander", BindingFlags.Instance | BindingFlags.NonPublic);
            setCommanderMethod.Invoke(strategyInstance, parameters: new object?[] { TargetTeam, null });

            MethodInfo synchCommanderMethod = strategyType.GetMethod("RPC_SynchCommander", BindingFlags.Instance | BindingFlags.NonPublic);
            synchCommanderMethod.Invoke(strategyInstance, new object[] { TargetTeam });
            #endif

            // need to get the player back to Infantry and not stuck in no-clip
            SendToRole(DemotedCommander, MP_Strategy.ETeamRole.INFANTRY);
            // respawn
            GameMode.CurrentGameMode.SpawnUnitForPlayer(DemotedCommander, TargetTeam);
        }

        public static void Command_CommanderDemote(Player callerPlayer, String args)
        {
            // count number of arguments
            int argumentCount = args.Split(' ').Length - 1;
            if (argumentCount > 1)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Too many arguments");
                return;
            }

            int targetTeamIndex = -1;
            MP_Strategy strategyInstance = GameObject.FindObjectOfType<MP_Strategy>();

            // if no team was specified then try and use current team of the admin
            if (argumentCount == 0)
            {
                Team? callerTeam = callerPlayer.Team;
                if (callerTeam != null)
                {
                    targetTeamIndex = callerPlayer.Team.Index;
                }
                else
                {
                    HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Too few arguments");
                    return;
                }
            }
            // argument is present and targets team where current commander needs to get demoted
            else
            {
                String targetTeamText = args.Split(' ')[1];

                if (String.Equals(targetTeamText, "Human", StringComparison.OrdinalIgnoreCase))
                {
                    // check gamemode - if Humans vs Aliens or the other ones
                    if (strategyInstance.TeamsVersus == MP_Strategy.ETeamsVersus.HUMANS_VS_ALIENS)
                    {
                        // if it's human vs aliens then human translates to the Human (Sol) team index
                        targetTeamIndex = 2;
                    }
                    // otherwise, it's ambigious and we can't make a decision
                }
                else if (String.Equals(targetTeamText, "Alien", StringComparison.OrdinalIgnoreCase))
                {
                    targetTeamIndex = 0;
                }
                else if (targetTeamText.Contains("Cent", StringComparison.OrdinalIgnoreCase))
                {
                    targetTeamIndex = 1;
                }
                else if (String.Equals(targetTeamText, "Sol", StringComparison.OrdinalIgnoreCase))
                {
                    targetTeamIndex = 2;
                }

                // check if we still don't have a valid target
                if (targetTeamIndex < 0)
                {
                    HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Valid targets are Alien, Centauri, or Sol");
                    return;
                }
            }

            Team targetTeam = Team.GetTeamByIndex(targetTeamIndex);
            if (targetTeam != null)
            {
                // check if they have a commander to demote
                Player? targetPlayer = strategyInstance.GetCommanderForTeam(targetTeam);

                // team has a commander if targetPlayer isn't null
                if (targetPlayer != null)
                {
                    if (callerPlayer.CanAdminTarget(targetPlayer))
                    {
                        DemoteTeamsCommander(strategyInstance, targetTeam);
                        HelperMethods.AlertAdminActivity(callerPlayer, targetPlayer, "demoted");
                    }
                    else
                    {
                        HelperMethods.ReplyToCommand_Player(targetPlayer, "is immune due to level");
                    }
                }
                else
                {
                    HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": No commander found on specified team");
                }
            }
            else
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": No valid team found");
            }
        }

        public static void Command_CommanderUnban(Player callerPlayer, String args)
        {
            if (MasterBanList == null)
            {
                return;
            }

            // count number of arguments
            int argumentCount = args.Split(' ').Length - 1;
            if (argumentCount > 1)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Too many arguments");
                return;
            }
            else if (argumentCount < 1)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Too few arguments");
                return;
            }

            String sTarget = args.Split(' ')[1];
            Player? playerToUnCmdrBan = HelperMethods.FindTargetPlayer(sTarget);

            if (playerToUnCmdrBan == null)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Ambiguous or invalid target");
                return;
            }

            if (callerPlayer.CanAdminTarget(playerToUnCmdrBan))
            {
                bool removed = RemoveCommanderBan(playerToUnCmdrBan);
                if (removed)
                {
                    HelperMethods.AlertAdminAction(callerPlayer, "permitted " + HelperMethods.GetTeamColor(playerToUnCmdrBan) + playerToUnCmdrBan.PlayerName + HelperMethods.defaultColor + " to play as commander");
                }
                else
                {
                    HelperMethods.ReplyToCommand_Player(playerToUnCmdrBan, "not commander banned");
                }
            }
            else
            {
                HelperMethods.ReplyToCommand_Player(playerToUnCmdrBan, "is immune due to level");
            }
        }

        public static void Command_CommanderBan(Player callerPlayer, String args)
        {
            if (MasterBanList == null)
            {
                return;
            }    

            // count number of arguments
            int argumentCount = args.Split(' ').Length - 1;
            if (argumentCount > 1)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Too many arguments");
                return;
            }
            else if (argumentCount < 1)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Too few arguments");
                return;
            }

            String sTarget = args.Split(' ')[1];
            Player? playerToCmdrBan = HelperMethods.FindTargetPlayer(sTarget);

            if (playerToCmdrBan == null)
            {
                HelperMethods.ReplyToCommand(args.Split(' ')[0] + ": Ambiguous or invalid target");
                return;
            }

            if (callerPlayer.CanAdminTarget(playerToCmdrBan))
            {
                AddCommanderBan(playerToCmdrBan);
                HelperMethods.AlertAdminAction(callerPlayer, "restricted " + playerToCmdrBan.PlayerName + " to play as infantry only");
            }
            else
            {
                HelperMethods.ReplyToCommand_Player(playerToCmdrBan, "is immune due to level");
            }
        }

        [HarmonyPatch(typeof(GameMode), nameof(GameMode.CreateRPCPacket))]
        private static class CommanderManager_Patch_GameMode_GameByteStreamWriter
        {
            static void Postfix(GameMode __instance, GameByteStreamWriter __result, byte __0)
            {
                if (_BlockRoundStartUntilEnoughApplicants != null && _BlockRoundStartUntilEnoughApplicants.Value)
                {
                    MP_Strategy strategyInstance = GameObject.FindObjectOfType<MP_Strategy>();

                    // is this the countdown timer for the round to start?
                    if (__0 == (byte)MP_Strategy.ERPCs.TIMER_UPDATE && !strategyInstance.GameOver)
                    {
#if NET6_0
                        if (!AllTeamsHaveCommanderApplicants() && strategyInstance.Timer < 5f && strategyInstance.Timer > 4f)
                        {
                            // reset timer value and keep counting down
                            strategyInstance.Timer = 25f;
                            // TODO: Fix repeating message 
                            HelperMethods.ReplyToCommand("Round cannot start because all teams don't have a commander. Chat !commander to apply.");
                        }

#else
                        Type strategyType = typeof(MP_Strategy);
                        FieldInfo timerField = strategyType.GetField("Timer", BindingFlags.NonPublic | BindingFlags.Instance);

                        float timerValue = (float)timerField.GetValue(strategyInstance);
                        if (!AllTeamsHaveCommanderApplicants() && timerValue < 5f && timerValue > 4f)
                        {
                            // reset timer value and keep counting down
                            timerField.SetValue(strategyInstance, 25f);
                            // TODO: Fix repeating message 
                            HelperMethods.ReplyToCommand("Round cannot start because all teams don't have a commander. Chat !commander to apply.");
                        }
#endif
                    }
                }
            }
        }

        [HarmonyPatch(typeof(GameMode), nameof(GameMode.OnPlayerLeftBase))]
        private static class CommanderManager_Patch_GameMode_OnPlayerLeftBase
        {
            public static void Prefix(GameMode __instance, Player __0)
            {
                try
                {
                    if (commanderApplicants == null || __0 == null || __0.Team == null)
                    {
                        return;
                    }

                    if (GameMode.CurrentGameMode.Started && !GameMode.CurrentGameMode.GameBegun)
                    {
                        bool hasApplied = commanderApplicants[__0.Team.Index].Any(k => k == __0);
                        if (hasApplied)
                        {
                            commanderApplicants[__0.Team.Index].Remove(__0);
                            HelperMethods.ReplyToCommand_Player(__0, "was removed from consideration due to disconnect");
                        }
                    }

                    if (GameMode.CurrentGameMode.Started && GameMode.CurrentGameMode.GameBegun)
                    {
                        if (__0.IsCommander)
                        {
                            HelperMethods.ReplyToCommand_Player(__0, "left commander position vacant by disconnecting");
                        }
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run GameMode::OnPlayerLeftBase");
                }
            }
        }

        [HarmonyPatch(typeof(GameMode), nameof(GameMode.SpawnUnitForPlayer), new Type[] { typeof(Player), typeof(GameObject), typeof(Vector3), typeof(Quaternion) })]
        private static class CommanderManager_Patch_GameMode_SpawnUnitForPlayer
        {
            public static void Postfix(GameMode __instance, Unit __result, Player __0, UnityEngine.GameObject __1, UnityEngine.Vector3 __2, UnityEngine.Quaternion __3)
            {
                try
                {
                    if (teamswapCommanderChecks == null || __0 == null)
                    {
                        return;
                    }

                    if (GameMode.CurrentGameMode.Started && GameMode.CurrentGameMode.GameBegun)
                    {
                        // determine if player was a commander from any team
                        int commanderSwappedTeamIndex = -1;
                        for (int i = 0; i < MaxTeams; i++)
                        {
                            if (teamswapCommanderChecks[i] == __0)
                            {
                                commanderSwappedTeamIndex = i;
                                break;
                            }
                        }
                            
                        // announce if player swapped from commander to infantry
                        if (commanderSwappedTeamIndex != -1)
                        {
                            Team departingTeam = Team.Teams[commanderSwappedTeamIndex];
                            teamswapCommanderChecks[commanderSwappedTeamIndex] = null;
                            HelperMethods.ReplyToCommand_Player(__0, "left commander position vacant for " + HelperMethods.GetTeamColor(departingTeam) + departingTeam.TeamName + HelperMethods.defaultColor + " by switching to infantry");
                        }
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run GameMode::SpawnUnitForPlayer");
                }
            }
        }

#if NET6_0
        [HarmonyPatch(typeof(Il2CppSilica.UI.Chat), nameof(Il2CppSilica.UI.Chat.MessageReceived))]
#else
        [HarmonyPatch(typeof(Silica.UI.Chat), "MessageReceived")]
#endif
        private static class CommanderManager_Patch_Chat_MessageReceived
        {
#if NET6_0
            public static void Postfix(Il2CppSilica.UI.Chat __instance, Player __0, string __1, bool __2)
#else
            public static void Postfix(Silica.UI.Chat __instance, Player __0, string __1, bool __2)
#endif
            {
                try
                {
                    if (commanderApplicants == null)
                    {
                        return;
                    }

                    // each faction has its own chat manager but by looking at alien and only global messages this catches commands only once
                    if (__instance.ToString().Contains("alien") && __2 == false)
                    {
                        bool isCommanderCommand = String.Equals(__1, "!commander", StringComparison.OrdinalIgnoreCase);
                        if (isCommanderCommand)
                        {
                            if (__0.Team == null)
                            {
                                HelperMethods.ReplyToCommand_Player(__0, "is not on a valid team");
                                return;
                            }

                            // check if they're trying to apply for commander before the 30 second countdown expires and the game begins
                            if (GameMode.CurrentGameMode.Started && !GameMode.CurrentGameMode.GameBegun)
                            {

                                // check if we are already on the commander applicant list
                                bool hasApplied = commanderApplicants[__0.Team.Index].Any(k => k == __0);

                                if (!hasApplied)
                                {
                                    commanderApplicants[__0.Team.Index].Add(__0);
                                    HelperMethods.ReplyToCommand_Player(__0, "applied for commander");
                                }
                                else
                                {
                                    commanderApplicants[__0.Team.Index].Remove(__0);
                                    HelperMethods.ReplyToCommand_Player(__0, "removed themselves from commander lottery");
                                }
                            }
                            else
                            {
                                HelperMethods.ReplyToCommand_Player(__0, "cannot apply for commander during the game");
                            }
                        }
                    }
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run Chat::MessageReceived");
                }
            }
        }

        public static bool AllTeamsHaveCommanderApplicants()
        {
            if (commanderApplicants == null)
            {
                return true;
            }

            for (int i = 0; i < Team.Teams.Count; i++)
            {
                if (Team.Teams[i] == null)
                {
                    continue;
                }

                // does the team have at least 1 player?
                if (Team.Teams[i].GetNumPlayers() >= 1)
                {
                    if (commanderApplicants[i].Count == 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
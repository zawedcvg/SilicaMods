﻿/*
Silica Worm Bounty
Copyright (C) 2024 by databomb

* Description *
Allows players who kill a Great Worm to receive a bounty, and allows
server operators to adjust the probabilities and number of Great Worms.

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
using Il2CppSilica.AI;
#else
using System.Reflection;
using Silica.AI;
#endif

using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using UnityEngine;
using Si_WormBounty;


[assembly: MelonInfo(typeof(WormBounty), "Worm Bounty", "0.9.1", "databomb", "https://github.com/data-bomb/Silica")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonOptionalDependencies("Admin Mod")]

namespace Si_WormBounty
{
    public class WormBounty : MelonMod
    {
        static MelonPreferences_Category _modCategory = null!;
        static MelonPreferences_Entry<int> _Pref_GreatWorms_MaxNumber = null!;
        static MelonPreferences_Entry<float> _Pref_GreatWorms_SpawnChance = null!;

        public override void OnInitializeMelon()
        {
            _modCategory ??= MelonPreferences.CreateCategory("Silica");
            _Pref_GreatWorms_MaxNumber ??= _modCategory.CreateEntry<int>("GreatWorms_MaxNumber", 2);
            _Pref_GreatWorms_SpawnChance ??= _modCategory.CreateEntry<float>("GreatWorms_SpawnChance", 0.25f);
        }

        public override void OnLateInitializeMelon()
        {
            HelperMethods.CommandCallback devourCallback = Command_Devour;
            HelperMethods.RegisterAdminCommand("devour", devourCallback, Power.Slay, "Spawns a Great Worm at the location of the player. Usage: !devour <player>");
        }

        public void Command_Devour(Player? callerPlayer, String args)
        {
            string commandName = args.Split(' ')[0];

            // validate argument count
            int argumentCount = args.Split(' ').Length - 1;
            if (argumentCount > 1)
            {
                HelperMethods.SendChatMessageToPlayer(callerPlayer, HelperMethods.chatPrefix, commandName, ": Too many arguments");
                return;
            }
            else if (argumentCount < 1)
            {
                HelperMethods.SendChatMessageToPlayer(callerPlayer, HelperMethods.chatPrefix, commandName, ": Too few arguments");
                return;
            }

            // validate argument contents
            string targetText = args.Split(' ')[1];
            Player? targetPlayer = HelperMethods.FindTargetPlayer(targetText);

            if (targetPlayer == null)
            {
                HelperMethods.SendChatMessageToPlayer(callerPlayer, HelperMethods.chatPrefix, commandName, ": Ambiguous or invalid target");
                return;
            }

            if (callerPlayer != null && !callerPlayer.CanAdminTarget(targetPlayer))
            {
                HelperMethods.ReplyToCommand_Player(targetPlayer, "is immune due to level");
                return;
            }

            // do the devouring
            AmbientLife wildLifeInstance = GameObject.FindObjectOfType<AmbientLife>();
            Vector3 targetPosition = targetPlayer.ControlledUnit.WorldPhysicalCenter;
            Quaternion rotatedQuaternion = GameMath.GetRotatedQuaternion(Quaternion.identity, Vector3.up * UnityEngine.Random.Range(-180f, 180f));
            Target target = Target.GetTargetByNetID(targetPlayer.ControlledUnit.NetworkComponent.NetID);

            Vector3 spawnVector = targetPosition + rotatedQuaternion * Vector3.forward * UnityEngine.Random.Range(10f, 25f);
            GameObject greatWormObject = Game.SpawnPrefab(wildLifeInstance.Boss.Prefab, null, wildLifeInstance.Team, targetPosition, rotatedQuaternion, true, true);
            Unit? greatWormUnit = greatWormObject.GetBaseGameObject() as Unit;
            if (greatWormUnit == null)
            {
                return;
            }

            greatWormUnit.OnAttackOrder(target, target.transform.position, AgentMoveSpeed.Fast, true);
            greatWormUnit.OnMoveOrder(targetPosition, AgentMoveSpeed.Fast, true);

            MelonLogger.Msg("Spawning Great Worm to devour player (" + targetPlayer.PlayerName + ")");
            HelperMethods.AlertAdminActivity(callerPlayer, targetPlayer, "devoured");
        }

        #if NET6_0
        [HarmonyPatch(typeof(AmbientLife), nameof(AmbientLife.OnEnable))]
        #else
        [HarmonyPatch(typeof(AmbientLife), "OnEnable")]
        #endif
        private static class WormBounty_Patch_AmbientLife_OnEnable
        {
            public static void Postfix(AmbientLife __instance)
            {
                try
                {
                    __instance.NumBossMax = _Pref_GreatWorms_MaxNumber.Value;
                    __instance.SpawnBossChance = _Pref_GreatWorms_SpawnChance.Value;
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run AmbientLife::OnEnable");
                }
            }
        }

        #if NET6_0
        [HarmonyPatch(typeof(AmbientLife), nameof(AmbientLife.OnUnitDestroyed))]
        #else
        [HarmonyPatch(typeof(AmbientLife), "OnUnitDestroyed")]
        #endif
        private static class WormBounty_Patch_AmbientLife_OnUnitDestroyed
        {
            public static void Prefix(AmbientLife __instance, Unit __0, EDamageType __1, GameObject __2)
            {
                try
                {
                    if (__2 == null)
                    {
                        return;
                    }

                    // a Basic wildlife was destroyed, ignore that
                    if (__instance.Boss != __0.ObjectInfo)
                    {
                        return;
                    }

                    // check if this is an actual player
                    BaseGameObject attackerBase = GameFuncs.GetBaseGameObject(__2);
                    if (attackerBase == null)
                    {
                        return;
                    }

                    NetworkComponent attackerNetComp = attackerBase.NetworkComponent;
                    if (attackerNetComp == null)
                    {
                        return;
                    }

                    Player attackerPlayer = attackerNetComp.OwnerPlayer;
                    if (attackerPlayer == null)
                    {
                        return;
                    }

                    if (attackerPlayer.Team == null)
                    {
                        return;
                    }

                    // determine bounty amount
                    int bountyAmount = FindBounty(__1);

                    // award bounty
                    AwardBounty(attackerPlayer, bountyAmount);
                }
                catch (Exception error)
                {
                    HelperMethods.PrintError(error, "Failed to run AmbientLife::OnUnitDestroyed");
                }
            }
        }

        private static int FindBounty(EDamageType damageType)
        {
            // explosion kills earn a bit less
            int baseAmount = (damageType == EDamageType.Explosion) ? 500 : 1000;

            // give a little more, perhaps
            System.Random randomIndex = new System.Random();
            int varyingAmount = randomIndex.Next(0, 750);

            // round down to nearest ten
            varyingAmount = (int)Math.Round(varyingAmount / 10.0) * 10;

            return baseAmount + varyingAmount;
        }

        private static void AwardBounty(Player player, int bounty)
        {
            Team team = player.Team;
            team.StoreResource(bounty);

            HelperMethods.ReplyToCommand_Player(player, "defeated a Great Worm. ", bounty.ToString(), " awarded to ", team.TeamShortName);
        }
    }
}
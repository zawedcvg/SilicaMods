﻿/*
Silica Admin Mod
Copyright (C) 2024 by databomb

* Description *
Provides basic admin mod system to allow additional admins beyond
the host.

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
using System.Collections.Generic;
#endif

using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;

namespace SilicaAdminMod
{
    public class SiAdminMod : MelonMod
    {
        static MelonPreferences_Category _modCategory = null!;
        public static MelonPreferences_Entry<bool> Pref_Admin_AcceptTeamChatCommands = null!;

        public static List<Admin> AdminList = null!;
        public static List<AdminCommand> AdminCommands = null!;

        public override void OnInitializeMelon()
        {
            try
            {
                AdminCommands = new List<AdminCommand>();
                AdminList = AdminFile.Initialize();

                _modCategory ??= MelonPreferences.CreateCategory("Silica");
                Pref_Admin_AcceptTeamChatCommands ??= _modCategory.CreateEntry<bool>("Admin_AllowTeamChatCommands", false);
            }
            catch (Exception error)
            {
                HelperMethods.PrintError(error, "Failed to load admins");
            }
        }

        public static void RegisterAdminCommand(String adminCommand, HelperMethods.CommandCallback adminCallback, Power adminPower)
        {
            AdminCommand thisCommand = new AdminCommand();
            thisCommand.AdminCommandText = adminCommand;
            thisCommand.AdminCallback = adminCallback;
            thisCommand.AdminPower = adminPower;

            AdminCommands.Add(thisCommand);
        }
    }
}
﻿using System;
using System.IO;
using Intersect.Config;
using IntersectClientExtras.Database;
using Microsoft.Win32;

namespace Intersect.Client.Classes.MonoGame.Database
{
    public class MonoDatabase : GameDatabase
    {
        public override void SavePreference(string key, object value)
        {
            var regkey = Registry.CurrentUser?.OpenSubKey("Software", true);

            regkey?.CreateSubKey("IntersectClient");
            regkey = regkey?.OpenSubKey("IntersectClient", true);
            regkey?.CreateSubKey(ClientOptions.ServerHost + ":" + ClientOptions.ServerPort);
            regkey = regkey?.OpenSubKey(ClientOptions.ServerHost + ":" + ClientOptions.ServerPort, true);
            regkey?.SetValue(key, Convert.ToString(value));
        }

        public override string LoadPreference(string key)
        {
            var regkey = Registry.CurrentUser?.OpenSubKey("Software", false);
            regkey = regkey?.OpenSubKey("IntersectClient", false);
            regkey = regkey?.OpenSubKey(ClientOptions.ServerHost + ":" + ClientOptions.ServerPort);
            return regkey?.GetValue(key) as string ?? "";
        }

        public override bool LoadConfig()
        {
            if (!File.Exists(Path.Combine("resources", "config.json")))
            {
                ClientOptions.Load(null);
                File.WriteAllText(Path.Combine("resources", "config.json"), ClientOptions.GetJson());
                return true;
            }

            var jsonData = File.ReadAllText(Path.Combine("resources", "config.json"));
            ClientOptions.Load(jsonData);
            return true;
        }
    }
}
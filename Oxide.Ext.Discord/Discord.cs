﻿using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core.Plugins;
using Oxide.Ext.Discord.Exceptions;
using Oxide.Ext.Discord.Logging;

namespace Oxide.Ext.Discord
{
    public class Discord
    {
        public static List<DiscordClient> Clients { get; private set; } = new List<DiscordClient>();

        //public static List<string> PendingTokens = new List<string>(); // Not efficient, will re-do later

        public static void CreateClient(Plugin plugin, string apiKey)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentNullException(nameof(apiKey));
            }

            var settings = new DiscordSettings()
            {
                ApiToken = apiKey,
                LogLevel = LogLevel.Info
            };

            CreateClient(plugin, settings);
        }

        public static void CreateClient(Plugin plugin, DiscordSettings settings)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (string.IsNullOrEmpty(settings.ApiToken))
            {
                throw new ArgumentNullException(nameof(settings.ApiToken));
            }

            // Find an existing DiscordClient and update it 
            var client = Clients.FirstOrDefault(x => x.Plugins.Any(p => p.Name == plugin.Name));
            if (client != null)
            {
                if (client.Settings.ApiToken != settings.ApiToken)
                {
                    throw new LimitedClientException();
                }

                var existingPlugins = client.Plugins.Where(x => x.Title == plugin.Title).ToList();
                existingPlugins.ForEach(x => client.Plugins.Remove(x));

                client.RegisterPlugin(plugin);
                client.UpdatePluginReference(plugin);
                client.Settings = settings;
                plugin.CallHook("DiscordSocket_Initialized");
                return;
            }

            // Create a new DiscordClient
            var newClient = new DiscordClient();
            Clients.Add(newClient);
            newClient.Initialize(plugin, settings);
        }

        public static void CloseClient(DiscordClient client)
        {
            if (client == null) return;
            client.Disconnect();
            Clients.Remove(client);
        }
    }
}

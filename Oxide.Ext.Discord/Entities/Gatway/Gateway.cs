﻿using System;
using Newtonsoft.Json;
using Oxide.Ext.Discord.Entities.Api;

namespace Oxide.Ext.Discord.Entities.Gatway
{
    /// <summary>
    /// Represents Discord Gatway Connection Url
    /// See <a href="https://discord.com/developers/docs/topics/gateway#get-gateway">Get Gateway</a>
    /// </summary>
    [JsonObject(MemberSerialization = MemberSerialization.OptIn)]
    class Gateway
    {
        /// <summary>
        /// Gatway URL to use
        /// </summary>
        [JsonProperty("url")]
        public string Url { get; private set; }
        
        /// <summary>
        /// Saved Gateway URL
        /// Example: wss://gateway.discord.gg/?v=8&amp;encoding=json
        /// </summary>
        public static string WebsocketUrl { get; private set; }

        /// <summary>
        /// Returns an object with a single valid WSS URL, which the client can use for Connecting.
        /// Clients should cache this value and only call this endpoint to retrieve a new URL if they are unable to properly establish a connection using the cached version of the URL.
        /// See <a href="https://discord.com/developers/docs/topics/gateway#get-gateway">Get Gateway</a>
        /// </summary>
        /// <param name="client">Client to use</param>
        /// <param name="callback">Callback with the Gateway response</param>
        public static void GetGateway(BotClient client, Action<Gateway> callback)
        {
            client.Rest.DoRequest("/gateway", RequestMethod.GET, null, callback, null);
        }

        public static void UpdateGatewayUrl(BotClient client, Action callback)
        {
            GetGateway(client, gateway =>
            {
                WebsocketUrl = $"{gateway.Url}/?{GatewayConnect.ConnectionArgs}";
                client.Logger.Debug($"{nameof(Gateway)}.{nameof(UpdateGatewayUrl)} Updated Gateway Url: {WebsocketUrl}");
                callback.Invoke();
            });
        }
    }
}

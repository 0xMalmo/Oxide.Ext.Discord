﻿using System;
using System.Collections.Generic;
using Oxide.Ext.Discord.Logging;
using Oxide.Plugins;

namespace Oxide.Ext.Discord.REST
{
    public class RestHandler
    {
        private readonly Dictionary<string, string> _headers;
        private readonly LogLevel _logLevel;
        private readonly BotRestHandler _handler;
        private readonly DiscordClient _client;
        private readonly string _apiKey;
        
        internal static readonly Hash<string, BotRestHandler> BotHandlers = new Hash<string, BotRestHandler>();

        public RestHandler(DiscordClient client, string apiKey, LogLevel logLevel)
        {
            _client = client;
            _logLevel = logLevel;
            _apiKey = apiKey;

            _headers = new Dictionary<string, string>
            {
                { "Authorization", $"Bot {apiKey}" },
                { "Content-Type", "application/json" },
                { "User-Agent", $"DiscordBot (https://github.com/Trickyyy/Oxide.Ext.Discord, {DiscordExtension.GetExtensionVersion})" }
            };

            lock (BotHandlers)
            {
                _handler = BotHandlers[apiKey];
                if (_handler == null)
                {
                    _handler = new BotRestHandler();
                    BotHandlers[apiKey] = _handler;
                }
                
                _handler.AddClient(_client);
            }
        }

        public void Disconnect()
        {
            _handler.RemoveClient(_client);
            if (_handler.IsEmpty)
            {
                _handler.Shutdown();
                lock (BotHandlers)
                {
                    BotHandlers.Remove(_apiKey);
                }
            }
        }

        public void DoRequest(string url, RequestMethod method, object data, Action callback)
        {
            CreateRequest(method, url, _headers, data, response => callback?.Invoke());
        }

        public void DoRequest<T>(string url, RequestMethod method, object data, Action<T> callback)
        {
            CreateRequest(method, url, _headers, data, response =>
            {
                callback?.Invoke(response.ParseData<T>());
            });
        }

        private void CreateRequest(RequestMethod method, string url, Dictionary<string, string> headers, object data, Action<RestResponse> callback)
        {
            Request request = new Request(method, url, headers, data, callback, _logLevel);
            _handler.CleanupExpired();
            _handler.QueueRequest(request, _logLevel);
        }
    }
}

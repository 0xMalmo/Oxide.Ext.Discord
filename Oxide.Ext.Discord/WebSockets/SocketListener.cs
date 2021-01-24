using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Ext.Discord.Entities.Channels;
using Oxide.Ext.Discord.Entities.Gatway;
using Oxide.Ext.Discord.Entities.Gatway.Events;
using Oxide.Ext.Discord.Entities.Guilds;
using Oxide.Ext.Discord.Entities.Interactions;
using Oxide.Ext.Discord.Entities.Messages;
using Oxide.Ext.Discord.Entities.Roles;
using Oxide.Ext.Discord.Entities.Users;
using Oxide.Ext.Discord.Entities.Voice;
using Oxide.Ext.Discord.Extensions;
using Oxide.Ext.Discord.Logging;
using WebSocketSharp;

namespace Oxide.Ext.Discord.WebSockets
{
    public class SocketListener
    {
        internal int Retries;
        
        private readonly BotClient _client;

        private readonly Socket _webSocket;

        private readonly ILogger _logger;

        public SocketListener(BotClient client, Socket socket, ILogger logger)
        {
            _client = client;
            _webSocket = socket;
            _logger = logger;
        }

        public void SocketOpened(object sender, EventArgs e)
        {
            _logger.Warning("Discord socket opened!");
            _client.CallHook("DiscordSocket_WebSocketOpened");
            Retries = 0;
        }

        public void SocketClosed(object sender, CloseEventArgs e)
        {
            _logger.Debug($"Discord WebSocket closed. Code: {e.Code}, reason: {e.Reason}");
            
            _webSocket.DisposeSocket();
            
            if (_webSocket.RequestReconnect)
            {
                _webSocket.RequestReconnect = false;
                _client.ConnectWebSocket();
                return;
            }
            
            _client.CallHook("DiscordSocket_WebSocketClosed", e.Reason, e.Code, e.WasClean);
            
            if (HandleDiscordClosedSocket(e.Code, e.Reason))
            {
                return;
            }

            if (!e.WasClean)
            {
                _logger.Warning($"Discord connection closed uncleanly: code {e.Code}, Reason: {e.Reason}");
            }

            if (_client.Initialized)
            {
                _webSocket.StartReconnectTimer(Retries < 3 ? 1f : 15f, () =>
                {
                    Retries++;
                    _logger.Warning($"Attempting to reconnect to Discord... [Retry={Retries}]");
                    if (Retries <= 8)
                    {
                        _client.ConnectWebSocket();
                    }
                    else
                    {
                        //If more than 8 tries something could be wrong on discords end. Try and fetch the websocket url
                        _client.UpdateGatewayUrl(_client.ConnectWebSocket);
                    }
                });
            }
        }

        private bool HandleDiscordClosedSocket(int code, string reason)
        {
            if (!code.ToString().TryParse(out SocketCloseCode closeCode))
            {
                if(code >= 4000 && code < 5000)
                {
                    closeCode = SocketCloseCode.UnknownCloseCode;
                }
                else
                {
                    return false;
                }
            }

            bool reconnect = false;

            switch (closeCode)
            {
                case SocketCloseCode.UnknownError: 
                    _logger.Error("Discord had an unknown error. Reconnecting.");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.UnknownOpcode: 
                    _logger.Error($"Unknown gateway opcode sent: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.DecodeError: 
                    _logger.Error($"Invalid gateway payload sent: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.NotAuthenticated: 
                    _logger.Error($"Tried to send a payload before identifying: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.AuthenticationFailed: 
                    _logger.Error($"The given bot token is invalid. Please enter a valid token: {reason}");
                    break;
                
                case SocketCloseCode.AlreadyAuthenticated: 
                    _logger.Error($"The bot has already authenticated. Please don't identify more than once.: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.InvalidSequence: 
                    _logger.Error($"Invalid resume sequence. Doing full reconnect.: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.RateLimited: 
                    _logger.Error($"You're being rate limited. Please slow down how quickly you're sending requests: {reason}");
                    break;
                
                case SocketCloseCode.SessionTimedOut: 
                    _logger.Error($"Session has timed out. Starting a new one: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.InvalidShard: 
                    _logger.Error($"Invalid shared has been specified: {reason}");
                    reconnect = true;
                    break;
                
                case SocketCloseCode.ShardingRequired: 
                    _logger.Error($"Bot is in too many guilds. You must shard your bot: {reason}");
                    break;
                
                case SocketCloseCode.InvalidApiVersion: 
                    _logger.Error("Gateway is using invalid API version. Please contact Discord Extension Devs immediately!");
                    break;
                
                case SocketCloseCode.InvalidIntents: 
                    _logger.Error("Invalid intent(s) specified for the gateway. Please check that you're using valid intents in the connect.");
                    break;
                
                case SocketCloseCode.DisallowedIntent:
                    _logger.Error("The plugin is asking for an intent you have not granted your bot. Please go to your bot and enable the privileged gateway intents: https://support.discord.com/hc/en-us/articles/360040720412-Bot-Verification-and-Data-Whitelisting#privileged-intent-whitelisting");
                    break;
                
                case SocketCloseCode.UnknownCloseCode:
                    _logger.Error($"Discord has closed the gateway with a code we do not recognize. Code: {code}. Please Contact Discord Extension Authors.");
                    break;
                
                default:
                    return false;
            }

            if (reconnect)
            {
                _webSocket.ShouldAttemptResume = false;
                _client.ConnectWebSocket();
            }
            
            return true;
        }

        public void SocketErrored(object sender, ErrorEventArgs e)
        {
            _logger.Exception("An error has occured in the websocket", e.Exception);
            _client.CallHook("DiscordSocket_WebSocketErrored", e.Exception, e.Message);

            _logger.Warning("Attempting to reconnect to Discord...");
            _webSocket.Disconnect(true, false);
        }

        public void SocketMessage(object sender, MessageEventArgs e)
        {
            RPayload payload = JsonConvert.DeserializeObject<RPayload>(e.Data);
            if (payload.Sequence.HasValue)
            {
                _client.Sequence = payload.Sequence.Value;
            }

            _logger.Debug($"Received socket message, OpCode: {payload.OpCode}");

            try
            {
                switch (payload.OpCode)
                {
                    // Dispatch (dispatches an event)
                    case ReceiveOpCode.Dispatch:
                        HandleDispatch(payload);
                        break;

                    // Heartbeat
                    // https://discordapp.com/developers/docs/topics/gateway#gateway-heartbeat
                    case ReceiveOpCode.Heartbeat:
                        HandleHeartbeat(payload);
                        break;

                    // Reconnect (used to tell clients to reconnect to the gateway)
                    // we should immediately reconnect here
                    case ReceiveOpCode.Reconnect:
                        HandleReconnect(payload);
                        break;

                    // Invalid Session (used to notify client they have an invalid session ID)
                    case ReceiveOpCode.InvalidSession:
                        HandleInvalidSession(payload);
                        break;

                    // Hello (sent immediately after connecting, contains heartbeat and server debug information)
                    case ReceiveOpCode.Hello:
                        HandleHello(payload);
                        break;

                    // Heartbeat ACK (sent immediately following a client heartbeat
                    // that was received)
                    // (See 'zombied or failed connections')
                    case ReceiveOpCode.HeartbeatAcknowledge:
                        HandleHeartbeatAcknowledge(payload);
                        break;

                    default:
                        UnhandledOpCode(payload);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Exception($"{nameof(SocketListener)}.{nameof(SocketMessage)} Exception Occured. Please give error message below to Discord Extension Authors:\n", ex);
            }
        }

        private void HandleDispatch(RPayload payload)
        {
            _logger.Debug($"Received OpCode: Dispatch, event: {payload.EventName}");

            // Listed here: https://discordapp.com/developers/docs/topics/gateway#commands-and-events-gateway-events
            switch (payload.EventName)
            {
                case "READY":
                    HandleDispatchReady(payload);
                    break;

                case "RESUMED":
                    HandleDispatchResumed(payload);
                    break;

                case "CHANNEL_CREATE":
                    HandleDispatchChannelCreate(payload);
                    break;

                case "CHANNEL_UPDATE":
                    HandleDispatchChannelUpdate(payload);
                    break;

                case "CHANNEL_DELETE":
                    HandleDispatchChannelDelete(payload);
                    break;

                case "CHANNEL_PINS_UPDATE":
                    HandleDispatchChannelPinUpdate(payload);
                    break;

                case "GUILD_CREATE":
                    HandleDispatchGuildCreate(payload);
                    break;

                case "GUILD_UPDATE":
                    HandleDispatchGuildUpdate(payload);
                    break;

                case "GUILD_DELETE":
                    HandleDispatchGuildDelete(payload);
                    break;

                case "GUILD_BAN_ADD":
                    HandleDispatchGuildBanAdd(payload);
                    break;

                case "GUILD_BAN_REMOVE":
                    HandleDispatchGuildBanRemove(payload);
                    break;

                case "GUILD_EMOJIS_UPDATE":
                    HandleDispatchGuildEmojisUpdate(payload);
                    break;

                case "GUILD_INTEGRATIONS_UPDATE":
                    HandleDispatchGuildIntegrationsUpdate(payload);
                    break;

                case "GUILD_MEMBER_ADD":
                    HandleDispatchGuildMemberAdd(payload);
                    break;

                case "GUILD_MEMBER_REMOVE":
                    HandleDispatchGuildMemberRemove(payload);
                    break;

                case "GUILD_MEMBER_UPDATE":
                    HandleDispatchGuildMemberUpdate(payload);
                    break;

                case "GUILD_MEMBERS_CHUNK":
                    HandleDispatchGuildMembersChunk(payload);
                    break;

                case "GUILD_ROLE_CREATE":
                    HandleDispatchGuildRoleCreate(payload);
                    break;

                case "GUILD_ROLE_UPDATE":
                    HandleDispatchGuildRoleUpdate(payload);
                    break;

                case "GUILD_ROLE_DELETE":
                    HandleDispatchGuildRoleDelete(payload);
                    break;

                case "MESSAGE_CREATE":
                    HandleDispatchMessageCreate(payload);
                    break;

                case "MESSAGE_UPDATE":
                    HandleDispatchMessageUpdate(payload);
                    break;

                case "MESSAGE_DELETE":
                    HandleDispatchMessageDelete(payload);
                    break;

                case "MESSAGE_DELETE_BULK":
                    HandleDispatchMessageDeleteBulk(payload);
                    break;

                case "MESSAGE_REACTION_ADD":
                    HandleDispatchMessageReactionAdd(payload);
                    break;

                case "MESSAGE_REACTION_REMOVE":
                    HandleDispatchMessageReactionRemove(payload);
                    break;

                case "MESSAGE_REACTION_REMOVE_ALL":
                    HandleDispatchMessageReactionRemoveAll(payload);
                    break;

                case "PRESENCE_UPDATE":
                    HandleDispatchPresenceUpdate(payload);
                    break;

                // Bots should ignore this
                case "PRESENCES_REPLACE":
                    break;

                case "TYPING_START":
                    HandleDispatchTypingStart(payload);
                    break;

                case "USER_UPDATE":
                    HandleDispatchUserUpdate(payload);
                    break;

                case "VOICE_STATE_UPDATE":
                    HandleDispatchVoiceStateUpdate(payload);
                    break;

                case "VOICE_SERVER_UPDATE":
                    HandleDispatchVoiceServerUpdate(payload);
                    break;

                case "WEBHOOKS_UPDATE":
                    HandleDispatchWebhooksUpdate(payload);
                    break;

                case "INVITE_CREATE":
                    HandleDispatchInviteCreate(payload);
                    break;

                case "INVITE_DELETE":
                    HandleDispatchInviteDelete(payload);
                    break;
                
                case "INTERACTION_CREATE":
                    HandleDispatchInteractionCreate(payload);
                    break;

                //Currently sent by discord but are not yet documented in the Discord API docs..
                case "INTEGRATION_CREATE":
                case "INTEGRATION_UPDATE":
                case "INTEGRATION_DELETE":
                    break;
                
                default:
                    HandleDispatchUnhandledEvent(payload);
                    break;
            }
        }

        //https://discord.com/developers/docs/topics/gateway#ready
        private void HandleDispatchReady(RPayload payload)
        {
            Ready ready = payload.EventData.ToObject<Ready>();
            _client.Servers = ready.Guilds.ToHash(g => g.Id);
            _client.SessionId = ready.SessionId;
            _client.ReadyData = ready;
            _client.Application = ready.Application;
            _logger.Info($"Your bot was found in {ready.Guilds.Count} Guilds!");
            _client.CallHook("Discord_Ready", ready, false);
        }

        //https://discord.com/developers/docs/topics/gateway#resumed
        private void HandleDispatchResumed(RPayload payload)
        {
            Resumed resumed = payload.EventData.ToObject<Resumed>();
            _logger.Info("Session resumed successfully!");
            _client.CallHook("Discord_Resumed", resumed);
        }

        //https://discord.com/developers/docs/topics/gateway#channel-create
        private void HandleDispatchChannelCreate(RPayload payload)
        {
            Channel channel = payload.EventData.ToObject<Channel>();
            if (channel.Type == ChannelType.DM || channel.Type == ChannelType.GROUP_DM)
            {
                _client.DirectMessages[channel.Id] = channel;
            }
            else
            {
                Guild guild = _client.GetGuild(channel.GuildId);
                if (guild != null && guild.IsAvailable)
                {
                    guild.Channels[channel.Id] = channel;
                }
            }

            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchChannelCreate)} CHANNEL_CREATE: ID: {channel.Id} Type: {channel.Type}.");
            _client.CallHook("Discord_ChannelCreate", channel);
        }

        //https://discord.com/developers/docs/topics/gateway#channel-update
        private void HandleDispatchChannelUpdate(RPayload payload)
        {
            Channel update = payload.EventData.ToObject<Channel>();
            Channel previous = null;
            if (update.Type == ChannelType.DM || update.Type == ChannelType.GROUP_DM)
            {
                previous = _client.DirectMessages[update.Id];
                if (previous != null)
                {
                    _client.DirectMessages.Remove(update.Id);
                }

                _client.DirectMessages[update.Id] = update;
            }
            else
            {
                Guild guild = _client.GetGuild(update.GuildId);
                if (guild != null && guild.IsAvailable)
                {
                    guild.Channels[update.Id] = update;
                }
            }

            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchChannelUpdate)} CHANNEL_UPDATE: ID: {update.Id} Type: {update.Type}.");
            _client.CallHook("Discord_ChannelUpdate", update, previous);
        }

        //https://discord.com/developers/docs/topics/gateway#channel-delete
        private void HandleDispatchChannelDelete(RPayload payload)
        {
            Channel channel = payload.EventData.ToObject<Channel>();
            _client.GetGuild(channel.GuildId)?.Channels.RemoveAll(c => c.Id == channel.Id);
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchChannelDelete)} CHANNEL_DELETE: ID: {channel.Id} Type: {channel.Type}.");
            _client.CallHook("Discord_ChannelDelete", channel);
        }

        //https://discord.com/developers/docs/topics/gateway#channel-pins-update
        private void HandleDispatchChannelPinUpdate(RPayload payload)
        {
            ChannelPinsUpdate pins = payload.EventData.ToObject<ChannelPinsUpdate>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchChannelPinUpdate)} CHANNEL_PINS_UPDATE: Channel ID: {pins.GuildId}");
            _client.CallHook("Discord_ChannelPinsUpdate", pins);
        }

        // NOTE: Some elements of Guild object is only sent with GUILD_CREATE
        //https://discord.com/developers/docs/topics/gateway#guild-create
        private void HandleDispatchGuildCreate(RPayload payload)
        {
            Guild guild = payload.EventData.ToObject<Guild>();
            Guild existing = _client.GetGuild(guild.Id);
            if (existing == null)
            {
                _client.AddGuild(guild);
                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildCreate)} GUILD_CREATE: Guild not found adding to list.");
            }
            else
            {
                existing.Unavailable = guild.Unavailable;
                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildCreate)} GUILD_CREATE: Guild updating to unavailable.");
            }

            _client.CallHook("Discord_GuildCreate", guild);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-update
        private void HandleDispatchGuildUpdate(RPayload payload)
        {
            Guild guild = payload.EventData.ToObject<Guild>();
            _client.GetGuild(guild.Id)?.Update(guild);
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildUpdate)} GUILD_UPDATE: Guild was Updated.");
            _client.CallHook("Discord_GuildUpdate", guild);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-delete
        private void HandleDispatchGuildDelete(RPayload payload)
        {
            Guild guild = payload.EventData.ToObject<Guild>();
            if (guild.Unavailable ?? false) // There is an outage with Discord
            {
                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildDelete)} GUILD_DELETE: There is an outage with the guild.");
                Guild existing = _client.GetGuild(guild.Id);
                if (existing != null)
                {
                    existing.Unavailable = guild.Unavailable;
                }
                else
                {
                    _client.AddGuild(guild);
                }
            }
            else
            {
                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildDelete)} GUILD_DELETE: User was removed from the guild.");
                _client.RemoveGuild(guild.Id);
            }

            _client.CallHook("Discord_GuildDelete", guild);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-ban-add
        private void HandleDispatchGuildBanAdd(RPayload payload)
        {
            GuildBanEvent ban = payload.EventData.ToObject<GuildBanEvent>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildBanAdd)} GUILD_BAN_ADD: User was banned from the guild. Guild ID: {ban.GuildId} User ID: {ban.User.Id}.");
            _client.CallHook("Discord_GuildBanAdd", ban.User, ban.GuildId);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-ban-remove
        private void HandleDispatchGuildBanRemove(RPayload payload)
        {
            GuildBanEvent ban = payload.EventData.ToObject<GuildBanEvent>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildBanRemove)} GUILD_BAN_REMOVE: User was unbanned from the guild. Guild ID: {ban.GuildId} User ID: {ban.User.Id}.");
            _client.CallHook("Discord_GuildBanRemove", ban.User, ban.GuildId);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-emojis-update
        private void HandleDispatchGuildEmojisUpdate(RPayload payload)
        {
            GuildEmojisUpdate emojis = payload.EventData.ToObject<GuildEmojisUpdate>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildEmojisUpdate)} GUILD_EMOJIS_UPDATE: Guild ID: {emojis.GuildId}");
            _client.CallHook("Discord_GuildEmojisUpdate", emojis);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-integrations-update
        private void HandleDispatchGuildIntegrationsUpdate(RPayload payload)
        {
            GuildIntergrationsUpdate integration = payload.EventData.ToObject<GuildIntergrationsUpdate>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildIntegrationsUpdate)} GUILD_INTEGRATIONS_UPDATE: Guild ID: {integration.GuildId}");
            _client.CallHook("Discord_GuildIntegrationsUpdate", integration);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-member-add
        private void HandleDispatchGuildMemberAdd(RPayload payload)
        {
            GuildMemberAdd member = payload.EventData.ToObject<GuildMemberAdd>();
            Guild guild = _client.GetGuild(member.GuildId);
            if (guild != null && guild.IsAvailable)
            {
                guild.Members[member.User.Id] = member;
                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildMemberAdd)} GUILD_MEMBER_ADD: Guild ID: {member.GuildId} User ID: {member.User.Id}");
                _client.CallHook("Discord_MemberAdded", member);
            }
        }

        //https://discord.com/developers/docs/topics/gateway#guild-member-remove
        private void HandleDispatchGuildMemberRemove(RPayload payload)
        {
            GuildMemberRemove remove = payload.EventData.ToObject<GuildMemberRemove>();
            Guild guild = _client.GetGuild(remove.GuildId);
            if (guild != null && guild.IsAvailable)
            {
                GuildMember member = guild.Members[remove.User.Id];
                if (member != null)
                {
                    guild.Members.Remove(remove.User.Id);
                }

                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildMemberRemove)} GUILD_MEMBER_REMOVE: Guild ID: {remove.GuildId} User ID: {remove.User.Id}");
                _client.CallHook("Discord_MemberRemoved", member);
            }
        }

        //https://discord.com/developers/docs/topics/gateway#guild-member-update
        private void HandleDispatchGuildMemberUpdate(RPayload payload)
        {
            GuildMemberUpdate update = payload.EventData.ToObject<GuildMemberUpdate>();
            Guild guild = _client.GetGuild(update.GuildId);
            if (guild != null && guild.IsAvailable)
            {
                GuildMember member = guild.Members[update.User.Id];
                if (member != null)
                {
                    _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildMemberUpdate)} GUILD_MEMBER_UPDATE: Guild ID: {update.GuildId} User ID: {update.User.Id}");
                    GuildMember oldMember = JObject.FromObject(member).ToObject<GuildMember>(); // lazy way to copy the object
                    if (update.User != null)
                        member.User = update.User;
                    if (update.Nick != null)
                        member.Nick = update.Nick;
                    if (update.Roles != null)
                        member.Roles = update.Roles;
                    _client.CallHook("Discord_GuildMemberUpdate", update, oldMember);
                }
            }
        }

        //https://discord.com/developers/docs/topics/gateway#guild-members-chunk
        private void HandleDispatchGuildMembersChunk(RPayload payload)
        {
            GuildMembersChunk chunk = payload.EventData.ToObject<GuildMembersChunk>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildMembersChunk)} GUILD_MEMBER_UPDATE: Guild ID: {chunk.GuildId} Nonce: {chunk.Nonce}");
            _client.CallHook("Discord_GuildMembersChunk", chunk);
        }

        //https://discord.com/developers/docs/topics/gateway#guild-role-create
        private void HandleDispatchGuildRoleCreate(RPayload payload)
        {
            GuildRoleCreate role = payload.EventData.ToObject<GuildRoleCreate>();
            Guild guild = _client.GetGuild(role.GuildId);
            if (guild != null && guild.IsAvailable)
            {
                guild.Roles.Add(role.Role);
                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildRoleCreate)} GUILD_ROLE_CREATE: Guild ID: {role.GuildId} Role ID: {role.Role.Id} Role Name: {role.Role.Name}");
                _client.CallHook("Discord_GuildRoleCreate", role.Role);
            }
        }

        //https://discord.com/developers/docs/topics/gateway#guild-role-update
        private void HandleDispatchGuildRoleUpdate(RPayload payload)
        {
            GuildRoleUpdate update = payload.EventData.ToObject<GuildRoleUpdate>();
            Role updatedRole = update.Role;
            Guild guild = _client.GetGuild(update.GuildId);
            if (guild != null && guild.IsAvailable)
            {
                Role oldRole = guild.Roles.FirstOrDefault(x => x.Id == updatedRole.Id);
                if (oldRole != null)
                {
                    guild.Roles.Remove(oldRole);
                }

                guild.Roles.Add(updatedRole);

                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildRoleUpdate)} GUILD_ROLE_UPDATE: Guild ID: {update.GuildId} Role ID: {update.Role.Id} Role Name: {update.Role.Name}");
                _client.CallHook("Discord_GuildRoleUpdate", updatedRole, oldRole);
            }
        }

        //https://discord.com/developers/docs/topics/gateway#guild-role-delete
        private void HandleDispatchGuildRoleDelete(RPayload payload)
        {
            GuildRoleDelete delete = payload.EventData.ToObject<GuildRoleDelete>();
            Guild guild = _client.GetGuild(delete.GuildId);
            if (guild != null && guild.IsAvailable)
            {
                Role role = guild.Roles.FirstOrDefault(x => x.Id == delete.RoleId);
                if (role != null)
                {
                    guild.Roles.Remove(role);
                    _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchGuildRoleDelete)} GUILD_ROLE_UPDATE: Guild ID: {delete.GuildId} Role ID: {role.Id} Role Name: {role.Name}");
                    _client.CallHook("Discord_GuildRoleDelete", role);
                }
            }
        }

        //https://discord.com/developers/docs/topics/gateway#message-create
        private void HandleDispatchMessageCreate(RPayload payload)
        {
            Message message = payload.EventData.ToObject<Message>();
            Channel channel = null;
            if (message.GuildId != null)
            {
                Guild guild = _client.GetGuild(message.GuildId);
                if (guild != null && guild.IsAvailable)
                {
                    channel = guild.Channels[message.ChannelId];
                }
            }
            else
            {
                channel = _client.DirectMessages[message.ChannelId];
            }
            
            if (channel != null)
            {
                channel.LastMessageId = message.Id;
            }

            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageCreate)} MESSAGE_CREATE: Guild ID: {message.GuildId} Channel ID: {message.ChannelId} Message ID: {message.Id}");
            _client.CallHook("Discord_MessageCreate", message);
        }

        //https://discord.com/developers/docs/topics/gateway#message-update
        private void HandleDispatchMessageUpdate(RPayload payload)
        {
            Message message = payload.EventData.ToObject<Message>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageUpdate)} MESSAGE_UPDATE: Guild ID: {message.GuildId} Channel ID: {message.ChannelId} Message ID: {message.Id}");
            _client.CallHook("Discord_MessageUpdate", message);
        }

        //https://discord.com/developers/docs/topics/gateway#message-delete
        private void HandleDispatchMessageDelete(RPayload payload)
        {
            MessageDelete message = payload.EventData.ToObject<MessageDelete>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageDelete)} MESSAGE_DELETE: Channel ID: {message.ChannelId} Message ID: {message.Id}");
            _client.CallHook("Discord_MessageDelete", message);
        }

        //https://discord.com/developers/docs/topics/gateway#message-delete-bulk
        private void HandleDispatchMessageDeleteBulk(RPayload payload)
        {
            MessageDeleteBulk bulkDelete = payload.EventData.ToObject<MessageDeleteBulk>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageDeleteBulk)} MESSAGE_DELETE: Channel ID: {bulkDelete.ChannelId}");
            _client.CallHook("Discord_MessageDeleteBulk", bulkDelete);
        }

        //https://discord.com/developers/docs/topics/gateway#message-reaction-add
        private void HandleDispatchMessageReactionAdd(RPayload payload)
        {
            MessageReactionAdd reaction = payload.EventData.ToObject<MessageReactionAdd>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageReactionAdd)} MESSAGE_REACTION_ADD: Channel ID: {reaction.ChannelId} Message ID: {reaction.MessageId} User ID: {reaction.UserId}");
            _client.CallHook("Discord_MessageReactionAdd", reaction);
        }

        //https://discord.com/developers/docs/topics/gateway#message-reaction-remove
        private void HandleDispatchMessageReactionRemove(RPayload payload)
        {
            MessageReactionRemove reaction = payload.EventData.ToObject<MessageReactionRemove>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageReactionRemove)} MESSAGE_REACTION_REMOVE: Channel ID: {reaction.ChannelId} Message ID: {reaction.MessageId} User ID: {reaction.UserId}");
            _client.CallHook("Discord_MessageReactionRemove", reaction);
        }

        //https://discord.com/developers/docs/topics/gateway#message-reaction-remove-all
        private void HandleDispatchMessageReactionRemoveAll(RPayload payload)
        {
            MessageReactionRemoveAll reaction = payload.EventData.ToObject<MessageReactionRemoveAll>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchMessageReactionRemoveAll)} MESSAGE_REACTION_REMOVE_ALL: Channel ID: {reaction.ChannelId} Message ID: {reaction.MessageId}");
            _client.CallHook("Discord_MessageReactionRemoveAll", reaction);
        }

        /// <summary>
        ///  * From Discord API docs:
        ///  * The user object within this event can be partial, the only field which must be sent is the id field, everything else is optional.
        ///  * Along with this limitation, no fields are required, and the types of the fields are not validated.
        ///  * Your client should expect any combination of fields and types within this event
        /// </summary>
        /// <param name="payload"></param>
        /// https://discord.com/developers/docs/topics/gateway#presence-update
        private void HandleDispatchPresenceUpdate(RPayload payload)
        {
            PresenceUpdate update = payload.EventData.ToObject<PresenceUpdate>();

            DiscordUser updateUser = update?.User;
            if (updateUser != null)
            {
                Guild guild = _client.GetGuild(update.GuildId);
                if (guild != null && guild.IsAvailable)
                {
                    GuildMember member = guild.Members[updateUser.Id];
                    member?.User.Update(updateUser);
                }

                _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchPresenceUpdate)} PRESENCE_UPDATE: Guild ID: {update.GuildId} User ID: {update.User} Status: {update.Status}");
            }

            _client.CallHook("Discord_PresenceUpdate", updateUser);
        }

        //https://discord.com/developers/docs/topics/gateway#typing-start
        private void HandleDispatchTypingStart(RPayload payload)
        {
            TypingStart typing = payload.EventData.ToObject<TypingStart>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchTypingStart)} TYPING_START: Channel ID: {typing.ChannelId} User ID: {typing.UserId}");
            _client.CallHook("Discord_TypingStart", typing);
        }

        //https://discord.com/developers/docs/topics/gateway#user-update
        private void HandleDispatchUserUpdate(RPayload payload)
        {
            DiscordUser user = payload.EventData.ToObject<DiscordUser>();

            foreach (Guild guild in _client.Servers.Values)
            {
                if (guild.IsAvailable)
                {
                    GuildMember memberUpdate = guild.Members[user.Id];
                    if (memberUpdate != null)
                    {
                        _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchUserUpdate)} USER_UPDATE: Guild ID: {guild.Id} User ID: {user.Id}");
                        memberUpdate.User = user;
                    }
                }
            }

            _client.CallHook("Discord_UserUpdate", user);
        }

        //https://discord.com/developers/docs/topics/gateway#voice-state-update
        private void HandleDispatchVoiceStateUpdate(RPayload payload)
        {
            VoiceState voice = payload.EventData.ToObject<VoiceState>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchVoiceStateUpdate)} USER_UPDATE: Guild ID: {voice.GuildId} Channel ID: {voice.ChannelId} User ID: {voice.UserId}");
            _client.CallHook("Discord_VoiceStateUpdate", voice);
        }

        //https://discord.com/developers/docs/topics/gateway#voice-server-update
        private void HandleDispatchVoiceServerUpdate(RPayload payload)
        {
            VoiceServerUpdate voice = payload.EventData.ToObject<VoiceServerUpdate>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchVoiceServerUpdate)} USER_UPDATE: Guild ID: {voice.GuildId}");
            _client.CallHook("Discord_VoiceServerUpdate", voice);
        }

        //https://discord.com/developers/docs/topics/gateway#webhooks-update
        private void HandleDispatchWebhooksUpdate(RPayload payload)
        {
            WebhooksUpdate webhook = payload.EventData.ToObject<WebhooksUpdate>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchWebhooksUpdate)} USER_UPDATE: Guild ID: {webhook.GuildId} Channel ID: {webhook.ChannelId}");
            _client.CallHook("Discord_WebhooksUpdate", webhook);
        }

        //https://discord.com/developers/docs/topics/gateway#invite-create
        private void HandleDispatchInviteCreate(RPayload payload)
        {
            InviteCreated invite = payload.EventData.ToObject<InviteCreated>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchInviteCreate)} INVITE_CREATE: Guild ID: {invite.GuildId} Channel ID: {invite.ChannelId} Code: {invite.Code}");
            _client.CallHook("Discord_InviteCreated", invite);
        }

        //https://discord.com/developers/docs/topics/gateway#invite-delete
        private void HandleDispatchInviteDelete(RPayload payload)
        {
            InviteDeleted invite = payload.EventData.ToObject<InviteDeleted>();
            _logger.Verbose($"{nameof(SocketListener)}.{nameof(HandleDispatchInviteDelete)} INVITE_DELETE: Guild ID: {invite.GuildId} Channel ID: {invite.ChannelId} Code: {invite.Code}");
            _client.CallHook("Discord_InviteDeleted", invite);
        }
        
        //https://discord.com/developers/docs/topics/gateway#interaction-create
        private void HandleDispatchInteractionCreate(RPayload payload)
        {
            Interaction interaction = payload.EventData.ToObject<Interaction>();
            _client.CallHook("Discord_InteractionCreate", interaction);
        }

        private void HandleDispatchUnhandledEvent(RPayload payload)
        {
            _client.CallHook("Discord_UnhandledEvent", payload);
            _logger.Warning($"Unhandled Dispatch Event: {payload.EventName}. Please contact Discord Extension authors.");
        }

        //https://discord.com/developers/docs/topics/gateway#heartbeat
        private void HandleHeartbeat(RPayload payload)
        {
            _logger.Info("Manually sent heartbeat (received opcode 1)");
            _client.SendHeartbeat();
        }

        //https://discord.com/developers/docs/topics/gateway#reconnect
        private void HandleReconnect(RPayload payload)
        {
            _logger.Info("Reconnect has been called (opcode 7)! Reconnecting...");
            //If we disconnect normally our session becomes invalid per: https://discord.com/developers/docs/topics/gateway#resuming
            _webSocket.Disconnect(true, true, true);
        }

        //https://discord.com/developers/docs/topics/gateway#invalid-session
        private void HandleInvalidSession(RPayload payload)
        {
            _logger.Warning("Invalid Session ID opcode received!");
            _webSocket.Disconnect(true, payload.TokenData?.ToObject<bool>() ?? false);
        }

        //https://discord.com/developers/docs/topics/gateway#hello
        private void HandleHello(RPayload payload)
        {
            Hello hello = payload.EventData.ToObject<Hello>();
            _client.SetupHeartbeat(hello.HeartbeatInterval);

            // Client should now perform identification
            if (_webSocket.ShouldAttemptResume)
            {
                _logger.Warning("Attempting resume opcode...");
                _client.Resume();
            }
            else
            {
                _client.Identify();
                _webSocket.ShouldAttemptResume = true;
            }
        }

        //https://discord.com/developers/docs/topics/gateway#heartbeating
        private void HandleHeartbeatAcknowledge(RPayload payload)
        {
            _client.HeartbeatAcknowledged = true;
        }

        private void UnhandledOpCode(RPayload payload)
        {
            _logger.Warning($"Unhandled OP code: {payload.OpCode}. Please contact Discord Extension authors.");
        }
    }
}

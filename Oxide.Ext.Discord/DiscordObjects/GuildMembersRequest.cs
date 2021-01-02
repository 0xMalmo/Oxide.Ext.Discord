﻿using System.Collections.Generic;

namespace Oxide.Ext.Discord.DiscordObjects
{
    using Newtonsoft.Json;

    public class GuildMembersRequest
    {
        [JsonProperty("guild_id")]
        public string GuildID { get; set; }

        [JsonProperty("query")]
        public string Query { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }
        
        [JsonProperty("presences")]
        public bool? Presences { get; set; }
        
        [JsonProperty("user_ids")]
        public List<string> UserIds { get; set; }        
        
        [JsonProperty("nonce")]
        public string Nonce { get; set; }
    }
}

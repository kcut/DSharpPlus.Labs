using System;
using Voltaic;
using Voltaic.Serialization;
using Wumpus.Entities;

namespace Wumpus.Events
{
    /// <summary> 
    ///     Sent when a user starts typing in a <see cref="Entities.Channel"/>. 
    ///     https://discordapp.com/developers/docs/topics/gateway#typing-start
    /// </summary>
    public class TypingStartEvent
    {
        /// <summary> Id of the <see cref="Entities.User"/>. </summary>
        [ModelProperty("user_id")]
        public Snowflake UserId { get; set; }
        // TODO: Undocumented (https://github.com/discordapp/discord-api-docs/issues/582)
        [ModelProperty("guild_id")]
        public Snowflake GuildId { get; set; }
        // TODO: Undocumented (https://github.com/discordapp/discord-api-docs/issues/582)
        [ModelProperty("member")]
        public Optional<GuildMember> Member { get; set; }
        /// <summary> Id of the <see cref="Entities.Channel"/>. </summary>
        [ModelProperty("channel_id")]
        public Snowflake ChannelId { get; set; }
        /// <summary> Unix time (in seconds) of when the <see cref="Entities.User"/> started typing. </summary>
        [ModelProperty("timestamp"), Epoch(EpochType.UnixSeconds)]
        public DateTimeOffset Timestamp { get; set; }
    }
}

namespace Wumpus.Entities
{
    /// <summary> https://discordapp.com/developers/docs/resources/channel#message-object-message-types </summary>
    public enum MessageType
    {
        Default = 0,
        RecipientAdd = 1,
        RecipientRemove = 2,
        Call = 3,
        ChannelNameChange = 4,
        ChannelIconChange = 5,
        ChannelPinnedMessage = 6,
        GuildMemberJoin = 7
    }
}

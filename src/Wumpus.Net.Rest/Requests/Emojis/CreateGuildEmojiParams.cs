using Voltaic;
using Voltaic.Serialization;

namespace Wumpus.Requests
{
    /// <summary> https://discordapp.com/developers/docs/resources/emoji#create-guild-emoji-json-params </summary>
    public class CreateGuildEmojiParams
    {
        /// <summary> Name of the <see cref="Entities.Emoji"/>. </summary>
        [ModelProperty("name")]
        public Utf8String Name { get; private set; }
        /// <summary> The 128x128 emoji image. </summary>
        [ModelProperty("image")]
        public Image Image { get; private set; }
        /// <summary> <see cref="Entities.Role"/>s for which this <see cref="Entities.Emoji"/> will be whitelisted. </summary>
        [ModelProperty("roles")]
        public Optional<Snowflake[]> RoleIds { get; set; }

        public CreateGuildEmojiParams(Utf8String name, Image image)
        {
            Name = name;
            Image = image;
        }

        public void Validate()
        {
            Preconditions.NotNullOrWhitespace(Name, nameof(Name));
        }
    }
}

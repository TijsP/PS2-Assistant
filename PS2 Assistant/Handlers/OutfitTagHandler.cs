using Newtonsoft.Json;
using PS2_Assistant.Data;
using PS2_Assistant.Logger;
using PS2_Assistant.Models.Database;

namespace PS2_Assistant.Handlers
{
    public class OutfitTagHandler
    {
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;

        public OutfitTagHandler(BotContext guildDb, SourceLogger logger)
        {
            _guildDb = guildDb;
            _logger = logger;
        }

        /// <summary>
        /// Sorts all registered users in <paramref name="guild"/> by outfit
        /// </summary>
        /// <param name="guild">The database entry of the guild for which to generate the Dictionary</param>
        /// <returns>A <see cref="Dictionary{TKey, TValue}"/> containing a list of registered users grouped by outfit, with the outfit tag as the key ("" if unaffiliated)</returns>
        private Dictionary<string, List<User>> RegisteredUsersByOutfit(Guild guild)
            {
            //  Each key represents an outfit tag ("" for no outfit), corresponding to a list of users with that tag
            Dictionary<string, List<User>> registeredOutfits = guild.Users.GroupBy(x => x.CurrentOutfit ?? "").ToDictionary(x => x.Key, y => y.ToList());
            _logger.SendLog(LogEventLevel.Debug, guild.GuildId, "Registered players grouped by outfit:\n{GroupedUsers}", JsonConvert.SerializeObject(registeredOutfits, Formatting.Indented));

            return registeredOutfits;
        }
    }
}

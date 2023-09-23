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
        /// Queries the database for all registered users within a guild
        /// </summary>
        /// <param name="guildId">The Id of the guild for which to generate the Dictionary</param>
        /// <returns>Null when no database entry was found for the guild, or a Dictionary containing a list of registered users grouped by outfit, with the outfit tag as the key ("" if unaffiliated)</returns>
        private async Task<Dictionary<string, List<User>>?> RegisteredUsersByOutfit(ulong guildId)
        {
            if (await _guildDb.GetGuildByGuildIdAsync(guildId) is not Guild guild)
            {
                _logger.SendLog(Serilog.Events.LogEventLevel.Error, guildId, "No database entry found for this guild");
                return null;
            }

            //  Each key represents an outfit tag ("" for no outfit), corresponding to a list of users with that tag
            Dictionary<string, List<User>> registeredOutfits = guild.Users.GroupBy(x => x.CurrentOutfit ?? "").ToDictionary(x => x.Key, y => y.ToList());
            _logger.SendLog(Serilog.Events.LogEventLevel.Debug, guildId, "Registered players grouped by outfit:\n{GroupedUsers}", JsonConvert.SerializeObject(registeredOutfits, Formatting.Indented));

            return registeredOutfits;
        }
    }
}

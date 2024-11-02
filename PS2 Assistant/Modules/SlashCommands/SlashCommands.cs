using Microsoft.Extensions.Configuration;

using Discord.Interactions;

using PS2_Assistant.Data;
using PS2_Assistant.Handlers;
using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules.SlashCommands
{
    public partial class SlashCommands : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;
        private readonly HttpClient _httpClient;
        private readonly OutfitTagHandler _tagHandler;
        private readonly IConfiguration _configuration;

        public SlashCommands(BotContext guildDb, SourceLogger logger, HttpClient httpClient, OutfitTagHandler tagHandler, IConfiguration configuration)
        {
            _guildDb = guildDb;
            _logger = logger;
            _httpClient = httpClient;
            _tagHandler = tagHandler;
            _configuration = configuration;
        }
    }
}

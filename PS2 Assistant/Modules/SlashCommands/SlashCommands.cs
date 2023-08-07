using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using PS2_Assistant.Data;
using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules.SlashCommands
{
    public partial class SlashCommands : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;

        private const ChannelPermission _channelWritePermissions = ChannelPermission.ViewChannel | ChannelPermission.SendMessages;

        public SlashCommands(BotContext guildDb, SourceLogger logger, HttpClient httpClient, IConfiguration configuration)
        {
            _guildDb = guildDb;
            _logger = logger;
            _httpClient = httpClient;
            _configuration = configuration;
        }
    }
}

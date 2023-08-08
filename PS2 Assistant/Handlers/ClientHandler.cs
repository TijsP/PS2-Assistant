using Discord;
using Discord.WebSocket;

using Serilog.Events;

using PS2_Assistant.Data;
using PS2_Assistant.Logger;
using PS2_Assistant.Models;
using PS2_Assistant.Modules;

namespace PS2_Assistant.Handlers
{
    public class ClientHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly BotContext _guildDb;
        private readonly AssistantUtils _utils;
        private readonly SourceLogger _logger;

        public ClientHandler(DiscordSocketClient client, BotContext guildDb, AssistantUtils utils, SourceLogger logger)
        {
            _client = client;
            _guildDb = guildDb;
            _utils = utils;
            _logger = logger;
        }

        public Task InitializeAsync()
        {
            _client.UserJoined += UserJoinedHandler;

            return Task.CompletedTask;
        }

        public async Task UserJoinedHandler(SocketGuildUser user)
        {
            await _utils.SendLogChannelMessageAsync(user.Guild.Id, $"User {user.Mention} joined the server");

            if (await _guildDb.GetGuildByGuildIdAsync(user.Guild.Id) is Guild guild && guild.Channels.WelcomeChannel is ulong welcomeChannelId && _client.GetChannel(welcomeChannelId) is ITextChannel welcomeChannel)
            {
                //  Both SendMessageInChannelAsync and SendPollToChannelAsync already check for bot write permissions, so this check can be omitted here
                if (guild.SendWelcomeMessage)
                    await _utils.SendMessageInChannelAsync(welcomeChannel, $"Welcome, {user.Mention}!");
                if (guild.AskNicknameUponWelcome)
                    await NicknameModule.SendPollToChannelAsync(welcomeChannel);
            }
            else
            {
                await _utils.SendLogChannelMessageAsync(user.Guild.Id, "Can't send welcome message: no welcome channel set!");
                _logger.SendLog(LogEventLevel.Warning, user.Guild.Id, "No welcome channel set");
            }

            _logger.SendLog(LogEventLevel.Information, user.Guild.Id, "User {UserId} joined the guild", user.Id);
        }
    }
}

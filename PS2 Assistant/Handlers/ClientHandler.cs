using Discord;
using Discord.WebSocket;

using Serilog.Events;

using PS2_Assistant.Data;
using PS2_Assistant.Logger;
using PS2_Assistant.Modules;
using PS2_Assistant.Models.Database;

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
            _client.UserLeft += UserLeftHandler;
            _client.JoinedGuild += JoinedGuildHandler;
            _client.LeftGuild += LeftGuildHandler;

            _client.Ready += ReadyHandler;

            return Task.CompletedTask;
        }

        public async Task ReadyHandler()
        {
            //  Check whether the bot was added to or removed from any guilds while offline
            List<ulong> subscribedGuilds = _client.Guilds.ToList().Select(x => x.Id).ToList();
            List<ulong> addedGuilds = subscribedGuilds.Except(_guildDb.Guilds.Select(x => x.GuildId).ToList()).ToList();
            List<ulong> removedGuilds = _guildDb.Guilds.Select(x => x.GuildId).ToList().Except(subscribedGuilds).ToList();

            if (addedGuilds.Count != 0)
            {
                foreach (ulong guildId in addedGuilds)
                {
                    _logger.SendLog(LogEventLevel.Information, guildId, "Bot was added to guild while offline");
                    await AddGuildAsync(guildId);
                }
            }
            if (removedGuilds.Count != 0)
            {
                foreach (ulong guildId in removedGuilds)
                {
                    _logger.SendLog(LogEventLevel.Information, guildId, "Bot was removed from guild while offline");
                    await RemoveGuildAsync(guildId);
                }
            }
        }

        public async Task UserJoinedHandler(SocketGuildUser user)
        {
            if (user.IsBot)
            {
                await _utils.SendLogChannelMessageAsync(user.Guild.Id, $"Bot {user.Mention} joined the server");
                _logger.SendLog(LogEventLevel.Information, user.Guild.Id, "Bot {BotId} joined the guild", user.Id);
                return;
            }

            await _utils.SendLogChannelMessageAsync(user.Guild.Id, $"User {user.Mention} joined the server");
            _logger.SendLog(LogEventLevel.Information, user.Guild.Id, "User {UserId} joined the guild", user.Id);

            if (await _guildDb.GetGuildByGuildIdAsync(user.Guild.Id) is not Guild guild)
                return;

            if (guild.Channels.WelcomeChannel is ulong welcomeChannelId && _client.GetChannel(welcomeChannelId) is ITextChannel welcomeChannel)
            {
                //  Both SendMessageInChannelAsync and SendPollToChannelAsync already check for bot write permissions, so this check can be omitted here
                if (guild.SendWelcomeMessage)
                    await _utils.SendMessageInChannelAsync(welcomeChannel, $"Welcome, {user.Mention}!");
                if (guild.AskNicknameUponWelcome)
                    await NicknameModule.SendPollToChannelAsync(welcomeChannel);
            }
            else
            {
                if (guild.SendWelcomeMessage)
                    await _utils.SendLogChannelMessageAsync(user.Guild.Id, "Can't send welcome message: no welcome channel set!");
                if (guild.AskNicknameUponWelcome)
                    await _utils.SendLogChannelMessageAsync(user.Guild.Id, "Can't send nickname poll: no welcome channel set!");

                _logger.SendLog(LogEventLevel.Warning, user.Guild.Id, "No welcome channel set");
            }
        }

        public async Task UserLeftHandler(SocketGuild guild, SocketUser user)
        {
            if (await _guildDb.GetGuildByGuildIdAsync(guild.Id) is Guild originalGuild && originalGuild.Users.Where(x => x.SocketUserId == user.Id).FirstOrDefault(defaultValue: null) is User savedUser)
            {
                originalGuild.Users.Remove(savedUser);
                await _guildDb.SaveChangesAsync();
            }

            _logger.SendLog(LogEventLevel.Information, guild.Id, "User {UserId} left the guild", user.Id);
        }

        public async Task JoinedGuildHandler(SocketGuild guild)
        {
            await AddGuildAsync(guild.Id);

            _logger.SendLog(LogEventLevel.Information, guild.Id, "Bot was added to guild");
        }
        public async Task AddGuildAsync(ulong guildId)
        {
            if (_guildDb.Guilds.Find(guildId) is null)
            {
                await _guildDb.Guilds.AddAsync(new Guild { GuildId = guildId, Channels = new Channels(), Roles = new Roles() });
                await _guildDb.SaveChangesAsync();
            }
        }

        public async Task LeftGuildHandler(SocketGuild guild)
        {
            await RemoveGuildAsync(guild.Id);

            _logger.SendLog(LogEventLevel.Information, guild.Id, "Bot left guild");
        }
        public async Task RemoveGuildAsync(ulong guildId)
        {
            if (_guildDb.Guilds.Find(guildId) is Guild guildToLeave)
            {
                _guildDb.Guilds.Remove(guildToLeave);
                await _guildDb.SaveChangesAsync();
            }
        }
    }
}

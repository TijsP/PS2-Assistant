﻿using Discord;
using Discord.WebSocket;

using Serilog.Events;

using PS2_Assistant.Data;
using PS2_Assistant.Logger;

namespace PS2_Assistant
{
    public class AssistantUtils
    {
        public const ChannelPermission channelWritePermissions = ChannelPermission.ViewChannel | ChannelPermission.SendMessages;
        public const string logFilePath = "Logs";

        public static readonly DateTime ServerMergeEventEndTime = new(2025, 3, 30, 7, 10, 0, DateTimeKind.Utc);

        private readonly DiscordSocketClient _client;
        private readonly SourceLogger _logger;
        private readonly BotContext _guildDb;

        public AssistantUtils(DiscordSocketClient client, SourceLogger logger, BotContext guildDb)
        {
            _client = client;
            _logger = logger;
            _guildDb = guildDb;
        }

        /// <summary>
        /// Sends a message to the specified guilds log channel, if configured
        /// </summary>
        /// <param name="guildId">Which guild to send a log message to</param>
        /// <param name="message">The message to send</param>
        /// <returns></returns>
        public async Task SendLogChannelMessageAsync(ulong guildId, string message)
        {
            if ((await _guildDb.GetGuildByGuildIdAsync(guildId))?.Channels.LogChannel is ulong logChannelId && _client.GetGuild(guildId).GetChannel(logChannelId) is ITextChannel logChannel)
                await SendMessageInChannelAsync(logChannel, message);
            else
                _logger.SendLog(LogEventLevel.Warning, guildId, "Failed to send log message. Has the log channel been set up properly?");
        }

        /// <summary>
        /// Tries sending a message to a channel
        /// </summary>
        /// <param name="targetChannel">The channel where to send <paramref name="message"/> to</param>
        /// <param name="message">The message to send</param>
        /// <returns></returns>
        public async Task SendMessageInChannelAsync(ITextChannel targetChannel, string message)
        {
            IGuildUser permissions = await targetChannel.GetUserAsync(_client.CurrentUser.Id);
            if (permissions.GetPermissions(targetChannel).Has(channelWritePermissions))
                await targetChannel.SendMessageAsync(message);
            else
                _logger.SendLog(LogEventLevel.Warning, targetChannel.Guild.Id, "Failed to send message to channel {ChannelId}, missing write permissions", targetChannel.Id);
        }
    }
}

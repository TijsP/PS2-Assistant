using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

using Discord.WebSocket;

using PS2_Assistant.Data;
using PS2_Assistant.Models.Database;

namespace PS2_Assistant.Handlers
{
    public class CLIHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly BotContext _guildDb;

        public CLIHandler(DiscordSocketClient client, BotContext guildDb)
        {
            _client = client;
            _guildDb = guildDb;
        }

        public async Task CommandHandlerAsync(CancellationTokenSource source)
        {
            do
            {
                if (Console.ReadLine() is string fullCommand)
                {
                    if (fullCommand.StartsWith("help"))
                        await Console.Out.WriteLineAsync("\nList of commands:\n" +
                                                            "help:      displays a list of commands\n" +
                                                            "stop:      stops the program\n" +
                                                            "info:      returns information about the bot status\n" +
                                                            "db-info:   returns information about the database (use \"db-info help\" for more information)");
                    else if (fullCommand.StartsWith("stop"))
                        source.Cancel();
                    else if (fullCommand.StartsWith("info"))
                        await Console.Out.WriteLineAsync(await CLIInfo());
                    else if (fullCommand.StartsWith("db-info"))
                    {
                        bool list = false;
                        ulong? id = null;
                        bool guildNotFound = false;
                        fullCommand = fullCommand.Trim("db-info ".ToCharArray());

                        try
                        {
                            if (fullCommand.StartsWith("list"))
                                list = true;
                            else if (fullCommand.StartsWith("help"))
                            {
                                await Console.Out.WriteLineAsync("\nUsage: db-info [list] [help] [guildId]\n" +
                                                                    "   list:       include \"list\" to get a list of all guilds registered in the database\n" +
                                                                    "   help:       include \"help\" to display the help page of this command\n" +
                                                                    "   guildId:    specify a guild ID to get all data in the database related to that guild\n" +
                                                                    "   none:       returns information about the database itself");
                                continue;
                            }
                            else if (!fullCommand.IsNullOrEmpty())
                            {
                                id = ulong.Parse(fullCommand);
                                if (!_guildDb.Guilds.Any(x => x.GuildId == id))
                                    guildNotFound = true;
                            }
                        }
                        catch
                        {
                            guildNotFound = true;
                        }

                        if (guildNotFound)
                        {
                            await Console.Out.WriteLineAsync($"No guild found in database with ID {fullCommand}");
                            continue;
                        }

                        await Console.Out.WriteLineAsync(await CLIDatabaseInfo(list, id));
                    }
                    else
                        await Console.Out.WriteLineAsync($"command not recognized: {fullCommand}. Use \"help\" for a list of commands");
                }
            }while (!source.Token.IsCancellationRequested);
        }

        private async Task<string> CLIInfo()
        {
            List<SocketGuild> guilds = _client.Guilds.ToList();
            int accumulativeUserCount = 0;
            foreach (SocketGuild guild in guilds)
            {
                accumulativeUserCount += guild.MemberCount;
            }

            string returnString =
                 "\nPS2 Assistant bot info:\n" +
                $"| Connected guilds:       {_client.Guilds.Count}\n" +
                $"| Recommended shards:     {await _client.GetRecommendedShardCountAsync()}\n" +
                $"| Accumulative users:     {accumulativeUserCount}\n" +
                $"| Bot running for:        {DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()}";
            return returnString;
        }

        private async Task<string> CLIDatabaseInfo(bool list = false, ulong? guildId = null)
        {
            string guildIdLabel = "Guild ID",
                guildNameLabel = "Guild name",
                guildMembersLabel = "Guild Members",
                tagLabel = "Outfit Tag",
                sendWelcomeLabel = "Send welcome message",
                sendNicknameLabel = "Send nickname poll",
                welcomeChannelLabel = "Welcome channel ID",
                logChannelLabel = "Log channel ID",
                memberRoleLabel = "Member role ID",
                nonMemberRoleLabel = "Non-member role ID",
                userLabel = "User ID",
                userOutfitLabel = "Current outfit",
                characterLabel = "Character name";

            string returnString = "\nPS2 Assistant database info:\n";

            if (list)
            {
                //  Get the longest ID, to ensure all entries share the same column width
                int longestGuildIdLength = 0;
                foreach (Guild guild in _guildDb.Guilds)
                    longestGuildIdLength = guild.GuildId.ToString().Length > longestGuildIdLength ? guild.GuildId.ToString().Length : longestGuildIdLength;

                //  Header
                returnString += $"| {CLIColumn(guildIdLabel, longestGuildIdLength)} {CLIColumn(guildMembersLabel, guildMembersLabel.Length)} {CLIColumn(guildNameLabel, guildNameLabel.Length)}";
                returnString = returnString.Remove(returnString.Length - 2) + "\n";     //  Get rid of the last " |" for a cleaner look

                //  Body
                foreach (Guild guild in _guildDb.Guilds)
                    returnString += $"| {CLIColumn(guild.GuildId.ToString(), longestGuildIdLength)} {CLIColumn((_client.GetGuild(guild.GuildId).MemberCount - 1).ToString(), guildMembersLabel.Length)} {_client.GetGuild(guild.GuildId).Name}\n";
            }
            else if (guildId is not null && await _guildDb.GetGuildByGuildIdAsync((ulong)guildId) is Guild guild)
            {
                //  Guild, channels and roles table headers and bodies
                returnString +=
                    $"| {CLIColumn(guildIdLabel, guildId.ToString()!.Length)} {CLIColumn(tagLabel, tagLabel.Length)} {CLIColumn(sendWelcomeLabel, sendWelcomeLabel.Length)} {CLIColumn(sendNicknameLabel, sendNicknameLabel.Length)}\n" +
                    $"| {guildId} | {CLIColumn(guild.OutfitTag, tagLabel.Length)} {CLIColumn(guild.SendWelcomeMessage.ToString(), sendWelcomeLabel.Length)} {CLIColumn(guild.AskNicknameUponWelcome.ToString(), sendNicknameLabel.Length)}\n" +
                     "\n" +
                    $"| {CLIColumn(welcomeChannelLabel, guild.Channels?.WelcomeChannel.ToString()?.Length)} {CLIColumn(logChannelLabel, guild.Channels?.LogChannel.ToString()?.Length)}\n" +
                    $"| {CLIColumn(guild.Channels?.WelcomeChannel.ToString(), guild.Channels?.WelcomeChannel.ToString()?.Length)} {CLIColumn(guild.Channels?.LogChannel.ToString(), guild.Channels?.LogChannel.ToString()?.Length)}\n" +
                     "\n" +
                    $"| {CLIColumn(memberRoleLabel, guild.Roles?.MemberRole.ToString()?.Length)} {CLIColumn(nonMemberRoleLabel, guild.Roles?.NonMemberRole.ToString()?.Length)}\n" +
                    $"| {CLIColumn(guild.Roles?.MemberRole.ToString(), guild.Roles?.MemberRole.ToString()?.Length)} {CLIColumn(guild.Roles?.NonMemberRole.ToString(), guild.Roles?.NonMemberRole.ToString()?.Length)}\n" +
                     "\n";

                //  Get the longest ID, to ensure all entries share the same column width
                int longestUserIdLength = 0;
                foreach (User user in guild.Users)
                    longestUserIdLength = user.SocketUserId.ToString().Length > longestUserIdLength ? user.SocketUserId.ToString().Length : longestUserIdLength;

                //  Users header
                returnString += $"| {CLIColumn(userLabel, longestUserIdLength)} {CLIColumn(userOutfitLabel, userOutfitLabel.Length)} {CLIColumn(characterLabel, 32)}\n";      //  Planetside doesn't allow character names with a length of more than 32 characters

                //  Users body
                foreach (User user in guild.Users)
                {
                    returnString += $"| {CLIColumn(user.SocketUserId.ToString(), longestUserIdLength)} {CLIColumn(user.CurrentOutfit, userOutfitLabel.Length)} {CLIColumn(user.CharacterName, 32)}\n";
                }
            }
            else
            {
                string dbPath = Path.GetFullPath(_guildDb.dbLocation);
                returnString +=
                    $"| Database file location: {dbPath}\n" +
                    $"| Database storage size:  {decimal.Round(new FileInfo(dbPath).Length / (decimal)1024, 2):0.00} KiB\n" +
                    $"| Guilds in database:     {await _guildDb.Guilds.CountAsync()}\n" +
                    $"| Registered users:       {await _guildDb.Guilds.SelectMany(x => x.Users).CountAsync()}";
            }

            return returnString;
        }

        private static string CLIColumn(string? content, int? width)
        {
            width ??= 0;
            if (!content.IsNullOrEmpty() && content!.Length > width)
                width = content.Length;
            return $"{content}{new string(' ', content.IsNullOrEmpty() ? width.Value : width.Value - content!.Length)} |";
        }
    }
}

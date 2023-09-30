using Microsoft.Extensions.Configuration;

using Newtonsoft.Json.Linq;
using Serilog.Events;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using PS2_Assistant.Attributes;
using PS2_Assistant.Attributes.Preconditions;
using PS2_Assistant.Models.Database;

namespace PS2_Assistant.Modules.SlashCommands
{
    public partial class SlashCommands
    {
        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("send-welcome-message", "Whether or not to send a welcome message when a new user joins the server")]
        public async Task SendWelcomeMessage(
            [Summary(description: "Whether a welcome message should be sent or not")]
            bool sendWelcomeMessage)
        {
            _guildDb.Guilds.Find(Context.Guild.Id)!.SendWelcomeMessage = sendWelcomeMessage;
            await _guildDb.SaveChangesAsync();
            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Welcome messages will {Confirmation} be sent", sendWelcomeMessage ? "now" : "not");
            await RespondAsync($"Welcome messages will {(sendWelcomeMessage ? "now" : "not")} be sent");
        }

        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("set-log-channel", "Sets the channel where this bot's log messages will be sent")]
        public async Task SetLogChannel(
            [TargetChannelPermission(AssistantUtils.channelWritePermissions)]
            [Summary(description: "Sets the log channel")]
            ITextChannel logChannel)
        {
            await DeferAsync();

            Channels channels = (await _guildDb.GetGuildByGuildIdAsync(Context.Guild.Id))!.Channels;
            channels.LogChannel = logChannel.Id;
            _guildDb.SaveChanges();

            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Log channel set to {LogChannelId}", logChannel.Id);
            await FollowupAsync($"Log channel set to <#{logChannel.Id}>");
        }

        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("set-welcome-channel", "Sets the channel where new users will be greeted by the bot")]
        public async Task SetWelcomeChannel(
            [TargetChannelPermission(AssistantUtils.channelWritePermissions)]
            [Summary(description: "Sets the welcome channel")]
            ITextChannel welcomeChannel)
        {
            await DeferAsync();

            Channels channels = (await _guildDb.GetGuildByGuildIdAsync(Context.Guild.Id))!.Channels;
            channels.WelcomeChannel = welcomeChannel.Id;
            _guildDb.SaveChanges();

            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Welcome channel set to {WelcomeChannelId}", welcomeChannel.Id);
            await FollowupAsync($"Welcome channel set to <#{welcomeChannel.Id}>");
        }

        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("set-member-role", "Sets the role users will get if their character is in the outfit represented by this server")]
        public async Task SetMemberRole(
            [Summary(description: "Sets the member role")]
            IRole memberRole)
        {
            await DeferAsync();

            Roles roles = (await _guildDb.GetGuildByGuildIdAsync(Context.Guild.Id))!.Roles;
            roles.MemberRole = memberRole.Id;
            _guildDb.SaveChanges();

            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Member role set to {MemberRoleId}", memberRole.Id);
            await FollowupAsync($"Member role set to {memberRole.Mention}");

            SocketRole? botRole = Context.Guild.CurrentUser.Roles.FirstOrDefault(x => x.IsManaged);
            if (memberRole.Position > botRole?.Position)
            {
                _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Bot doesn't have the right permissions to give role {MemberRoleId} to users", memberRole.Id);
                await FollowupAsync($"The bot won't be able to give role {memberRole.Mention} to users, because it outranks the bot's role. Please go to `Server Settings -> Roles` and make sure that the {botRole.Mention} role is higher on the list than the {memberRole.Mention} role.", allowedMentions: AllowedMentions.None);
            }
        }

        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("set-non-member-role", "Sets the role users will get if their character isn't in the outfit represented by this server")]
        public async Task SetNonMemberRole(
            [Summary(description: "Sets the non-member role")]
            IRole nonMemberRole
            )
        {
            await DeferAsync();

            Roles roles = (await _guildDb.GetGuildByGuildIdAsync(Context.Guild.Id))!.Roles;
            roles.NonMemberRole = nonMemberRole.Id;
            _guildDb.SaveChanges();

            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Member role set to {MemberRoleId}", nonMemberRole.Id);
            await FollowupAsync($"Member role set to {nonMemberRole.Mention}");

            SocketRole? botRole = Context.Guild.CurrentUser.Roles.FirstOrDefault(x => x.IsManaged);
            if (nonMemberRole.Position > botRole?.Position)
            {
                _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Bot doesn't have the right permissions to give role {MemberRoleId} to users", nonMemberRole.Id);
                await FollowupAsync($"The bot won't be able to give role {nonMemberRole.Mention} to users, because it outranks the bot's role. Please go to `Server Settings -> Roles` and make sure that the {botRole.Mention} role is higher on the list than the {nonMemberRole.Mention} role.", allowedMentions: AllowedMentions.None);
            }
        }

        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("set-main-outfit", "Sets the the main outfit represented by this server")]
        public async Task SetMainOutfit(
            [Summary(description: "The tag of the outfit")]
            string outfitTag)
        {
            await DeferAsync();

            var guild = _guildDb.Guilds.FindAsync(Context.Guild.Id);

            var outfitCountJson = await _httpClient.GetStringAsync($"http://census.daybreakgames.com/s:{_configuration.GetConnectionString("CensusAPIKey")}/count/ps2/outfit/?alias_lower={outfitTag.ToLower()}");
            int? count = JObject.Parse(outfitCountJson)["count"]?.ToObject<int>();      //  Only the number of results is returned by the query. If the result is 1 it is assumed the given outfit exists, though it might be different from what the user requested
            if (count == 0)
            {
                _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "No outfit found with tag {OutfitTag}", outfitTag);
                await FollowupAsync($"No outfit found with tag {outfitTag}!");
                return;
            }
            else if (!count.HasValue || count != 1)
            {
                _logger.SendLog(LogEventLevel.Warning, Context.Guild.Id, "Something went wrong requesting outfit tag {OutfitTag} from Census. Dumping JSON as a debug log message", outfitTag);
                _logger.SendLog(LogEventLevel.Debug, Context.Guild.Id, "Census returned after requesting outfit tag {OutfitTag}:\n{json}", outfitTag, outfitCountJson);
                await FollowupAsync($"Something went wrong while validating outfit tag {outfitTag}...");
                return;
            }

            await FollowupAsync($"Main outfit set to {outfitTag}");
            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, "Main outfit set to {OutfitTag}", outfitTag);

            (await guild)!.OutfitTag = outfitTag;
            _guildDb.SaveChanges();
        }

        [NeedsDatabaseEntry]
        [EnabledInDm(false)]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("update-outfit-tags", "Manually start the process of updating the outfit tags of all registered users")]
        public async Task UpdateOutfitTags()
        {
            await RespondAsync("Updating all outfit tags");
            await _tagHandler.UpdateOutfitTagsAsync(Context.Guild.Id);
            await FollowupAsync("Done");
        }
    }
}

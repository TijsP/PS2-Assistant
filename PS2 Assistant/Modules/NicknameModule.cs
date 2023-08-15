using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

using Discord;
using Discord.Interactions;
using Discord.WebSocket;

using Serilog.Events;

using PS2_Assistant.Attributes;
using PS2_Assistant.Attributes.Preconditions;
using PS2_Assistant.Data;
using PS2_Assistant.Handlers;
using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules
{
    [EnabledInDm(false)]
    public class NicknameModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly NicknameHandler _nicknameHandler;
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;

        public NicknameModule(NicknameHandler nicknameHandler, BotContext guildDb, SourceLogger sourceLogger)
        {
            _nicknameHandler = nicknameHandler;
            _guildDb = guildDb;
            _logger = sourceLogger;
        }

        [SlashCommand("send-nickname-poll", "Manually sends a poll that asks users for their in-game character name")]
        public async Task SendNicknamePoll(
            [TargetChannelPermission(ChannelPermission.ViewChannel | ChannelPermission.SendMessages)]
            [Summary(description: "The channel in which the poll will be sent")]
            ITextChannel? targetChannel = null)
        {
            await DeferAsync();

            bool respondEphemerally = true;
            targetChannel ??= (ITextChannel)Context.Channel;

            await FollowupAsync($"Attempting to send poll to <#{targetChannel.Id}>", ephemeral: respondEphemerally);
            await SendPollToChannelAsync(targetChannel);
        }

        [NeedsDatabaseEntry]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("include-nickname-poll", "Whether to include a nickname poll in every welcome message")]
        public async Task IncludeNicknamePoll(
            [Summary(description: "Whether to include the poll or not")]
            bool include)
        {
            await DeferAsync();

            _guildDb.Guilds.Find(Context.Guild.Id)!.AskNicknameUponWelcome = include;
            await FollowupAsync($"Welcome messages will {(include ? "" : "not")} include a nickname poll");
            _logger.SendLog(Serilog.Events.LogEventLevel.Information, Context.Guild.Id, "Welcome messages {WillWont} include a nickname poll", (include ? "will" : "will not"));
            _guildDb.SaveChanges();

        }

        [NeedsDatabaseEntry]
        [DefaultMemberPermissions(GuildPermission.ManageGuild)]
        [SlashCommand("register-users-manually", "Asks whether a users Discord nickname matches their in-game name and marks them to be registered")]
        public async Task RegisterUsersManually(
            [Summary(description: "If specified, only this user will be registered")]
            SocketGuildUser? userToRegister = null)
        {
            if(userToRegister is not null)
            {
                _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, $"User {Context.User.Id} registered user {userToRegister.Id}");
                await RespondAsync("Registering user...");

                string nickname = userToRegister.Nickname.IsNullOrEmpty() ? userToRegister.DisplayName : userToRegister.Nickname;

                //  exclude potential outfit tags from the nickname
                nickname = Regex.Split(nickname, @"(?<=[\[\]])").First(x => !x.Contains('[') && !x.Contains(']')).Trim();

                await _nicknameHandler.VerifyNicknameAsync(Context, nickname, userToRegister);
                return;
            }

            //  The Users collection is not guaranteed to be up to date. If it's not, all users will have to be downloaded
            if (Context.Guild.Users.Count != Context.Guild.MemberCount)
                await Context.Guild.DownloadUsersAsync();

            _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, $"User {Context.User.Id} started to register all users manually");
            await RespondAsync($"Does the nickname of user <@{Context.Guild.Users.ElementAt(0).Id}> equal their in-game username?", components: ButtonModule.RegisterUserButtons(Context.Guild.Users.ElementAt(0).Id), allowedMentions: AllowedMentions.None);
        }

        [NeedsDatabaseEntry]
        [RequireGuildPermission(GuildPermission.ManageGuild)]
        [SlashCommand("register-selected-users", "Registers the users that have been selected using /register-users-manually")]
        public async Task RegisterSelectedUsers()
        {
            await RespondAsync("Registering users...");
            foreach (var userId in ButtonModule.usersToRegister[Context.Guild.Id])
            {
                IGuildUser userToRegister = Context.Guild.GetUser(userId);
                string nickname = userToRegister.Nickname.IsNullOrEmpty() ? userToRegister.DisplayName : userToRegister.Nickname;

                //  exclude potential outfit tags from the nickname
                nickname = Regex.Split(nickname, @"(?<=[\[\]])").First(x => !x.Contains('[') && !x.Contains(']')).Trim();

                await _nicknameHandler.VerifyNicknameAsync(Context, nickname, userToRegister);
            }
        }

        /// <summary>
        /// Sends a message asking the user to start the nickname process by pressing a button.
        /// </summary>
        /// <param name="channel">The channel to which to send the poll to.</param>
        /// <returns></returns>
        public static async Task SendPollToChannelAsync(
            ITextChannel channel)
        {
            if (!(await channel.Guild.GetCurrentUserAsync()).GetPermissions(channel).Has(AssistantUtils.channelWritePermissions))
                return;

            var confirmationButton = new ComponentBuilder()
                    .WithButton("Get Started", "start-nickname-process");

            await channel.SendMessageAsync("To get started, press this button so we can set you up properly:", components: confirmationButton.Build());
        }
    }
}

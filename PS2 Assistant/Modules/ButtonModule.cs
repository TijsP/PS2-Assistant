using Discord;
using Discord.Interactions;

using Serilog.Events;

using PS2_Assistant.Attributes.Preconditions;
using PS2_Assistant.Data;
using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules
{
    public class ButtonModule : InteractionModuleBase<SocketInteractionContext>
    {
        public readonly static Dictionary<ulong, List<ulong>> usersToRegister = new();

        private readonly SourceLogger _logger;
        private readonly BotContext _guildDb;

        public ButtonModule(SourceLogger logger, BotContext guildDb)
        {
            _logger = logger;
            _guildDb = guildDb;
        }

        [ComponentInteraction("start-nickname-process")]
        public async Task StartNicknameProcess()
        {
            _logger.SendLog(LogEventLevel.Debug, Context.Guild.Id, "User {UserId} started the nickname process", Context.User.Id);

            await RespondWithModalAsync<ModalModule.NicknameModal>("nickname-modal");
        }

        [NeedsDatabaseEntry]
        [RequireGuildPermission(GuildPermission.ManageGuild)]
        [ComponentInteraction("put-user-in-register-list:*,*")]
        public async Task AddToRegisterList(string shouldRegisterUser, string userId)
        {
            await DeferAsync();
            ulong userToRegisterId = Convert.ToUInt64(userId);

            if (!usersToRegister.ContainsKey(Context.Guild.Id))
                usersToRegister.Add(Context.Guild.Id, new List<ulong>());

            if (Convert.ToBoolean(shouldRegisterUser))
            {
                usersToRegister[Context.Guild.Id].Add(userToRegisterId);
                await ModifyOriginalResponseAsync(x => { x.Content = $"Added user <@{userToRegisterId}> to the list of users to be registered"; x.Components = null; x.AllowedMentions = AllowedMentions.None; });
            }
            else
            {
                await ModifyOriginalResponseAsync(x => { x.Content = $"Excluded user <@{userToRegisterId}> from the list of users to be registered"; x.Components = null; x.AllowedMentions = AllowedMentions.None; });
            }
            _logger.SendLog(LogEventLevel.Debug, Context.Guild.Id, $"User {Context.User.Id} chose {(Convert.ToBoolean(shouldRegisterUser) ? "" : "not ")}to register user {userToRegisterId}");

            //  The Users collection is not guaranteed to be up to date. If it's not, all users will have to be downloaded
            if (Context.Guild.Users.Count != Context.Guild.MemberCount)
                await Context.Guild.DownloadUsersAsync();

            int indexOfNextUser = Context.Guild.Users.ToList().FindIndex(x => x.Id == userToRegisterId) + 1;

            //  Skip over users that have already been registered to this guild
            while (indexOfNextUser <= Context.Guild.Users.Count - 1 && (await _guildDb.GetGuildByGuildIdAsync(Context.Guild.Id))!.Users.Any(x => x.SocketUserId == Context.Guild.Users.ElementAt(indexOfNextUser).Id))
                indexOfNextUser++;

            if (indexOfNextUser > Context.Guild.Users.Count - 1)
            {
                _logger.SendLog(LogEventLevel.Information, Context.Guild.Id, $"Added {usersToRegister[Context.Guild.Id].Count} to the list of users to be registered");
                await FollowupAsync("All members have been presented! Please run `/register-selected-users` to complete the process");
                return;
            }

            await FollowupAsync($"Does the nickname of user <@{Context.Guild.Users.ElementAt(indexOfNextUser).Id}> equal their in-game username?", components: RegisterUserButtons(Context.Guild.Users.ElementAt(indexOfNextUser).Id), allowedMentions: AllowedMentions.None);
        }

        public static MessageComponent RegisterUserButtons(ulong userId)
        {
            var buttons = new ComponentBuilder()
                .WithButton("Yes, Register User", $"put-user-in-register-list:{true},{userId}")
                .WithButton("No, Skip User", $"put-user-in-register-list:{false},{userId}");
            return buttons.Build();
        }
    }
}

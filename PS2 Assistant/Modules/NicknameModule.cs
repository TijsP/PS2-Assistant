using Discord;
using Discord.Interactions;

using PS2_Assistant.Attributes;
using PS2_Assistant.Attributes.Preconditions;
using PS2_Assistant.Data;
using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules
{
    public class NicknameModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly BotContext _guildDb;
        private readonly SourceLogger _logger;

        public NicknameModule(BotContext guildDb, SourceLogger sourceLogger)
        {
            _guildDb = guildDb;
            _logger = sourceLogger;
        }

        [EnabledInDm(false)]
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
            await SendPollToChannel(targetChannel);
        }

        [NeedsDatabaseEntry]
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

        private static async Task SendPollToChannel(
            [TargetChannelPermission(ChannelPermission.ViewChannel | ChannelPermission.SendMessages)]
            ITextChannel channel)
        {
            var confirmationButton = new ComponentBuilder()
                    .WithButton("Get Started", "start-nickname-process");

            await channel.SendMessageAsync("To get started, press this button so we can set you up properly:", components: confirmationButton.Build());
            }
    }
}

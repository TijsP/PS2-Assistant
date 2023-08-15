using Discord;
using Discord.Interactions;

using Serilog.Events;

using PS2_Assistant.Logger;

namespace PS2_Assistant.Modules
{
    public class ButtonModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly SourceLogger _logger;

        public ButtonModule(SourceLogger logger)
        {
            _logger = logger;
        }

        [ComponentInteraction("start-nickname-process")]
        public async Task StartNicknameProcess()
        {
            _logger.SendLog(LogEventLevel.Debug, Context.Guild.Id, "User {UserId} started the nickname process", Context.User.Id);

            await RespondWithModalAsync<ModalModule.NicknameModal>("nickname-modal");
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

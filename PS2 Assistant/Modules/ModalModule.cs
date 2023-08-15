using Discord;
using Discord.Interactions;

using PS2_Assistant.Attributes.Preconditions;
using PS2_Assistant.Handlers;

namespace PS2_Assistant.Modules
{
    public class ModalModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly NicknameHandler _nicknameHandler;

        public ModalModule(NicknameHandler nicknameHandler)
        {
            _nicknameHandler = nicknameHandler;
        }

        public class NicknameModal : IModal
        {
            public string Title => "Planetside username";

            [InputLabel("Please enter your Planetside username:")]
            [ModalTextInput("nickname", TextInputStyle.Short, "name", 2, 32)]
            public string Nickname { get; set; } = "";
        }

        [EnabledInDm(false)]
        [NeedsDatabaseEntry]
        [ModalInteraction("nickname-modal")]
        public async Task NicknameModalInteraction(NicknameModal modal)
        {
            await RespondAsync("Validating character name...", allowedMentions: AllowedMentions.None);
            await _nicknameHandler.VerifyNicknameAsync(Context, modal.Nickname, (IGuildUser)Context.User);
        }
    }
}

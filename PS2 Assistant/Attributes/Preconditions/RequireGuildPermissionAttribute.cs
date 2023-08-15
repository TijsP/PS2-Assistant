using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace PS2_Assistant.Attributes.Preconditions
{
    /// <summary>
    /// Requires the user to have a specific permission
    /// </summary>
    public class RequireGuildPermissionAttribute : PreconditionAttribute
    {
        private readonly GuildPermission _guildPermission;

        /// <summary>
        /// Requires the user to have a specific permission
        /// </summary>
        /// <param name="guildPermission">The required permission. Multiple can be selected by using <code>|</code></param>
        public RequireGuildPermissionAttribute(GuildPermission guildPermission)
        {
            _guildPermission = guildPermission;
        }

        public override Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
        {
            if (context.User is not SocketGuildUser socketGuildUser)
                return Task.FromResult(PreconditionResult.FromError("Interaction didn't occur inside a guild"));
            if (!socketGuildUser.GuildPermissions.Has(_guildPermission))
                return Task.FromResult(PreconditionResult.FromError("User does not have the required permissions"));

            return Task.FromResult(PreconditionResult.FromSuccess());
        }
    }
}

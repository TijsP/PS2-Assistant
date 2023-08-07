using Discord;
using Discord.Interactions;

namespace PS2_Assistant.Attributes
{
    /// <summary>
    /// Checks whether the bot has the specified permissions in the target guild channel
    /// </summary>
    public class TargetChannelPermissionAttribute : ParameterPreconditionAttribute
    {
        public ChannelPermission ChannelPermissions { get; }

        /// <summary>
        /// Checks whether the bot has the specified permissions in the target channel
        /// </summary>
        /// <param name="permissions">The required permissions</param>
        public TargetChannelPermissionAttribute(ChannelPermission permissions)
        {
            ChannelPermissions = permissions;
        }

        public override async Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, IParameterInfo parameterInfo, object value, IServiceProvider services)
        {
            if(parameterInfo is not CommandParameterInfo)
                return PreconditionResult.FromError("Parameter info isn't associated with a command");

            if(value is not IGuildChannel targetChannel)
                return PreconditionResult.FromError($"Only parameters of type {parameterInfo.ParameterType} are accepted");

            var guildUserPerms = (await context.Guild.GetCurrentUserAsync()).GetPermissions(targetChannel);
            if (!guildUserPerms.Has(ChannelPermissions))
            {
                string missingPerms = "";
                ChannelPermissions seperatedPermissions = new ((ulong)ChannelPermissions);

                foreach(var perm in seperatedPermissions.ToList())
                if(!guildUserPerms.Has(perm))
                        missingPerms += perm + ", ";
                missingPerms = missingPerms.Remove(missingPerms.Length - 2, 2);
                
                    return PreconditionResult.FromError($"Bot is missing permissions for that channel. Missing permissions: " + missingPerms);
            }

            return PreconditionResult.FromSuccess();
        }
    }
}

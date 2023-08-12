using Discord;
using Discord.Interactions;

namespace PS2_Assistant.Attributes
{
    /// <summary>
    /// Checks whether the bot has the specified permissions in the target guild channel.
    /// </summary>
    public class TargetChannelPermissionAttribute : ParameterPreconditionAttribute
    {
        public ChannelPermission ChannelPermissions { get; }

        /// <summary>
        /// Checks whether the bot has the specified permissions in the target guild channel. If the target channel is left null, the target channel is assumed to be the channel where this interaction was triggered.
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

            //  Using "value is not IGuildChannel? targetChannel" results in a compiler error, so instead Type.IsAssignableFrom is used
            if(!typeof(IGuildChannel).IsAssignableFrom(parameterInfo.ParameterType))
                return PreconditionResult.FromError($"Only parameters of type {typeof(IGuildChannel)} are accepted. Used type: {parameterInfo.ParameterType}");
            
            IGuildChannel? targetChannel = value as IGuildChannel;
            targetChannel ??= context.Channel as IGuildChannel;

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

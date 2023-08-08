using Discord;
using Discord.Interactions;

namespace PS2_Assistant.Modules.SlashCommands
{
    public partial class SlashCommands : InteractionModuleBase<SocketInteractionContext>
    {

        [EnabledInDm(false)]
        [Group("help", "Shows a list of commands and their parameters")]
        public class Help : InteractionModuleBase<SocketInteractionContext>
        {
            public InteractionService Commands { get; set; }

            [SlashCommand("page", "Displays a page from the command list")]
            public async Task Page(
                [Summary(description: "The page number to display")]
                [MinValue(1)]
                int number = 1,
                [Summary(description: "The amount of commands to display per page")]
                [MinValue(1)]
                int commandsPerPage = 4)
            {
                List<Embed> embeds = new();
                List<SlashCommandInfo> availableCommands = AvailableCommands(Context, Commands);
                int totalPages = (int)Math.Ceiling((double)availableCommands.Count / commandsPerPage);

                if (number > totalPages)
                    number = totalPages;
                number--;


                for (int i = number * commandsPerPage; i < availableCommands.Count; i++)
                {
                    SlashCommandInfo slashCommand = availableCommands[i];
                    var embed = CommandHelpEmbed(slashCommand);

                    if (i == (number + 1) * commandsPerPage - 1 || i == availableCommands.Count - 1)
                    {
                        embed.WithFooter($"page {number + 1}/{totalPages}");
                        embeds.Add(embed.Build());
                        break;
                    }
                    embeds.Add(embed.Build());
                }
                await RespondAsync("Available commands:", embeds: embeds.ToArray());
            }

            [SlashCommand("setup", "Details how to set the bot up on this server")]
            public async Task Setup()
            {
                var embeds = new List<Embed>(){
                        new EmbedBuilder()
                        .WithTitle("Configure Channels")
                        .WithDescription("First of all, make sure all channels are configured properly. Use `/set-welcome-channel` to set a welcome channel, and use `/set-log-channel` to set a log channel. " +
                                            "The welcome channel will be used to send messages whenever a new user joins (such as a welcome message, or a nickname poll), while the log channel will be used to send " +
                                            "messages that are of interest to the admins. Because of this, it's recommended to set the welcome channel to a public channel, and the log channel to a private (admin only) " +
                                            "channel. While a welcome channel is optional, it's highly recommended to set up a log channel ASAP.\n" +
                                            "Also, please make sure the bot has the right permissions to post in these channels - namely the \"View Channel\" and \"Send Messages\" permissions")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Set Main Outfit")
                        .WithDescription("Next, please inform the bot of the main outfit represented by this server by using `/set-main-outfit`. If left unset, all users that join will be given the non-member role " +
                                            "(see the next step). The provided tag will be checked against the Planetside API, to ensure the outfit actually exists.")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Nickname Poll")
                        .WithDescription("Now we'll set up the behaviour of the nickname poll. This poll can ask the user for their in-game character name and, if the character exists, will set their Discord nickname to " +
                                            "their outfit tag + their character name (for example, \"[OUTF] xXCharacterNameXx\"). Using `/include-nickname-poll`, you can choose whether this poll should be sent whenever a new " +
                                            "user joins, while `/send-nickname-poll` will send a nickname poll to whichever channel the bot has access to (the rules channel, for instance).")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Configure Roles")
                        .WithDescription("Now we're ready to configure the roles that will be handed out by the bot. These can be set by `/set-member-role` and `/set-non-member-role`. The member role will be given whenever " +
                                            "the in-game character of the user is a member of the outfit specified by `/set-main-outfit`, while the non-member role will be given in any other case.\n" +
                                            "Please keep in mind that this bot has no way of actually verifying whether the user actually owns the character provided: for this reason, it's recommended not to hand out any " +
                                            "roles that have meaningfull permissions associated with them.\n" +
                                            "Also, make sure the role of the bot outranks any of the roles set by `/set-member-role` and `/set-non-member-role`. The bot will not be able to hand out these roles otherwise.")
                        .WithColor(247, 82, 37)
                        .Build(),
                    new EmbedBuilder()
                        .WithTitle("Optionally")
                        .WithDescription("Finally, you can choose whether the bot should send a general welcome message whenever a user joins. This can be done using `/send-welcome-message`. Welcome messages will be sent to " +
                                            "the channel specified with `/set-welcome-channel`.")
                        .WithColor(247, 82, 37)
                        .Build()};
                await RespondAsync(embeds: embeds.ToArray());
            }

            [SlashCommand("command", "Get help for a specific command")]
            public async Task Command(
                [Summary(description: "The name of the command")]
            string name
                )
            {
                if (name.StartsWith("/"))
                    name = name.TrimStart('/');

                if (AvailableCommands(Context, Commands).Where(x => x.Name.ToLower() == name.ToLower()).FirstOrDefault(defaultValue: null) is SlashCommandInfo commandInfo)
                {
                    var embed = CommandHelpEmbed(commandInfo);
                    await RespondAsync(embed: embed.Build());
                }
                else
                {
                    await RespondAsync($"Command `/{name}` doesn't exist");
                }
            }

            /// <summary>
            /// The commands available to a user, given the user's permissions
            /// </summary>
            /// <returns>A list of the available commands</returns>
            public static List<SlashCommandInfo> AvailableCommands(SocketInteractionContext context, InteractionService commands) {
                if (context.Guild is null)
                    return new List<SlashCommandInfo>();
                return commands.SlashCommands.Where(x => {
                    if (x.DefaultMemberPermissions is not null)
                        return context.Guild.GetUser(context.User.Id).GuildPermissions.Has(x.DefaultMemberPermissions.Value);
                    else
                        return true;
                }).ToList();
            }

            /// <summary>
            /// Generate an <see cref="Discord.EmbedBuilder"/> containing helpful information about a command
            /// </summary>
            /// <param name="slashCommand">The command for which to create the <see cref="Discord.EmbedBuilder"/></param>
            /// <returns></returns>
            private static EmbedBuilder CommandHelpEmbed(SlashCommandInfo slashCommand)
            {
                string description = slashCommand.Description;
                var embed = new EmbedBuilder()
                    .WithTitle("/" + (slashCommand.Module.IsSlashGroup ? slashCommand.Module.SlashGroupName + " " : "") + slashCommand.Name)
                    .WithColor(247, 82, 37);

                if (slashCommand.Parameters.Count > 0)
                {
                    List<SlashCommandParameterInfo> options = slashCommand.Parameters.ToList();

                    description += "\n\nOptions:\n";
                    foreach (var option in options)
                    {
                        bool required = option.IsRequired;
                        description += $"`{option.Name}`: {option.Description} {(required ? "(required)" : "(optional)")}\n";
                    }
                }

                embed.Description = description;
                return embed;
            }
        }


    }
}

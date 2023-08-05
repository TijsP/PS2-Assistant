using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

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
                List<SlashCommandInfo> availableCommands = AvailableCommands();
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
                await RespondAsync("Displaying setup page");
            }

            [SlashCommand("command", "Get help for a specific command")]
            public async Task Command(
                [Summary(description: "The name of the command")]
            string name
                )
            {
                if (name.StartsWith("/"))
                    name = name.TrimStart('/');
                foreach (var info in AvailableCommands())
                    await Console.Out.WriteLineAsync(info.Name);
                if (AvailableCommands().Where(x => x.Name.ToLower() == name.ToLower()).FirstOrDefault(defaultValue: null) is SlashCommandInfo commandInfo)
                {
                    var embed = CommandHelpEmbed(commandInfo);
                    await RespondAsync(embed: embed.Build());
                }
                else
                {
                    await RespondAsync($"Command `/{name}` doesn't exist");
                }
            }

            List<SlashCommandInfo> AvailableCommands() {
                if (Context.Guild is null)
                    return new List<SlashCommandInfo>();
                return Commands.SlashCommands.Where(x => {
                    if (x.DefaultMemberPermissions is not null)
                        return Context.Guild.GetUser(Context.User.Id).GuildPermissions.Has(x.DefaultMemberPermissions.Value);
                    else
                        return true;
                }).ToList();
            }

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

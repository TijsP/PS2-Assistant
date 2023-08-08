using Microsoft.Extensions.DependencyInjection;

using Discord;
using Discord.Interactions;

using PS2_Assistant.Modules.SlashCommands;

namespace PS2_Assistant.Handlers.Autocomplete
{
    public class HelpAutocompleteHandler : AutocompleteHandler
    {

        public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
        {
            if (context is not SocketInteractionContext socketContext)
                return Task.FromResult(AutocompletionResult.FromSuccess());

            List<SlashCommandInfo> propertiesList = SlashCommands.Help.AvailableCommands(socketContext, services.GetRequiredService<InteractionService>());
            List<AutocompleteResult> results = new();

            foreach (SlashCommandInfo properties in propertiesList)
            {
                string name = properties.Name;
                string nameIncludingGroup = properties.Module.IsSlashGroup ? properties.Module.SlashGroupName + " " + name : name;
                if (autocompleteInteraction.Data.Current.Value is string query && nameIncludingGroup.Contains(query))
                    results.Add(new AutocompleteResult { Name = nameIncludingGroup, Value = name });
            }

            return Task.FromResult(AutocompletionResult.FromSuccess(results.Take(25)));
        }
    }
}

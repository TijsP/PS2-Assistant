using Coravel.Invocable;
using Microsoft.Extensions.Configuration;
using PS2_Assistant.Handlers;
using PS2_Assistant.Logger;
using PS2_Assistant.Modules;

namespace PS2_Assistant.Invocables
{
    public class ServerMergeEmbedUpdateInvocable : IInvocable
    {
        private readonly HttpClient _httpClient;
        private readonly ServerMergeTrackerHandler _trackerHandler;
        private readonly SourceLogger _logger;
        private readonly IConfiguration _configuration;

        public ServerMergeEmbedUpdateInvocable(HttpClient httpClient, ServerMergeTrackerHandler trackerHandler, SourceLogger logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _trackerHandler = trackerHandler;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task Invoke()
        {
            await ServerMergeTrackerModule.UpdateEmbeds(_configuration.GetConnectionString("CensusAPIKey")!, _httpClient, _trackerHandler, _logger);    //  The existance of the API key was validated on startup
        }
    }
}

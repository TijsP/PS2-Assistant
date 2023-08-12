using Newtonsoft.Json.Linq;

namespace PS2_Assistant.Models.Census.API
{
    public record CensusObjectWrapper(
        int? Returned
        )
    {
        [Newtonsoft.Json.JsonExtensionData]
        public IDictionary<string, JToken>? Data { get; init; }
    }
}

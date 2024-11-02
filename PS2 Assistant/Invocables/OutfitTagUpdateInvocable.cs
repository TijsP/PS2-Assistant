using Coravel.Invocable;
using PS2_Assistant.Handlers;

namespace PS2_Assistant.Invocables
{
    public class OutfitTagUpdateInvocable : IInvocable
    {
        private readonly OutfitTagHandler _outfitTagHandler;
        private readonly ulong _guildId;

        public OutfitTagUpdateInvocable(OutfitTagHandler outfitTagHandler, ulong guildId)
        {
            _outfitTagHandler = outfitTagHandler;
            _guildId = guildId;
        }

        public async Task Invoke()
        {
            await _outfitTagHandler.UpdateOutfitTagsAsync(_guildId);
        }
    }
}

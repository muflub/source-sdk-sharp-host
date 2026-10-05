using SourceSharp.Host.Steam.Native;

namespace SourceSharp.Host.Steam;

/// <summary>
/// Call-result query area (ISteamGameServer APIs returning SteamAPICall_t handles):
/// reputation, clan association, and player-compatibility — resolved through
/// SteamAPICallCompleted_t → ManualDispatch_GetAPICallResult on the pump thread.
/// </summary>
public sealed partial class SteamGameServerHost
{
    public Task<ServerReputation> GetServerReputationAsync() =>
        CallResultAsync("GetServerReputation", api => api.GetServerReputation(_gs), CallbackReputation,
            p => CallbackDecoder.ReadReputation(p));

    public Task<ClanAssociation> AssociateWithClanAsync(SteamId clan) =>
        CallResultAsync("AssociateWithClan", api => api.AssociateWithClan(_gs, clan.Value), CallbackAssociateWithClan,
            p => new ClanAssociation(CallbackDecoder.ReadAssociateWithClan(p)));

    public Task<PlayerCompatibility> ComputeNewPlayerCompatibilityAsync(SteamId candidate) =>
        CallResultAsync("ComputeNewPlayerCompatibility", api => api.ComputeNewPlayerCompatibility(_gs, candidate.Value), CallbackPlayerCompatibility,
            p => CallbackDecoder.ReadPlayerCompatibility(p));
}

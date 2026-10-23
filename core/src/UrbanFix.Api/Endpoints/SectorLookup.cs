namespace UrbanFix.Api.Endpoints;

/// <summary>Finds the sector a location belongs to: its nearest ancestor of kind "sector", else its top-level ancestor (dashboards, DB-01, DB-02).</summary>
internal sealed class SectorLookup(IEnumerable<SectorLookup.Place> places)
{
    public sealed record Place(Guid Id, string Path, string Kind, string Name);

    private readonly Dictionary<Guid, Place> _byId = places.ToDictionary(p => p.Id);

    public (string Key, string Name) Of(Guid? locationId)
    {
        if (locationId is not { } id || !_byId.TryGetValue(id, out var loc)) return ("none", "Unknown location");
        var chain = loc.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Where(_byId.ContainsKey).Select(x => _byId[x]).ToList();
        var sector = chain.FirstOrDefault(l => l.Kind == "sector") ?? chain.FirstOrDefault() ?? loc;
        return (sector.Id.ToString(), sector.Name);
    }
}

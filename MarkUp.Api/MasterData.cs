using MarkUp.Shared;

namespace MarkUp.Api;

/// <summary>All master records loaded together, with quick lookups by Id.</summary>
public sealed class MasterSnapshot
{
    public required Dictionary<string, UserRecord> Users { get; init; }
    public required Dictionary<string, Dealer> Dealers { get; init; }
    public required Dictionary<string, Beat> Beats { get; init; }
    public required Dictionary<string, Party> Parties { get; init; }
    public required Dictionary<string, Product> Products { get; init; }
    public required Dictionary<string, Scheme> Schemes { get; init; }
    public required Dictionary<string, PartyType> PartyTypes { get; init; }

    public string UserName(string id) => Users.TryGetValue(id, out var u) ? u.Name : "";
    public string DealerName(string id) => Dealers.TryGetValue(id, out var d) ? d.Name : "";
    public string BeatName(string id) => Beats.TryGetValue(id, out var b) ? b.Name : "";
    public string PartyTypeName(string id) => PartyTypes.TryGetValue(id, out var t) ? t.Name : "";
    public string SchemeName(string id) => Schemes.TryGetValue(id, out var s) ? s.Name : "";

    public string UnitName(Product? p, string unitKind)
    {
        if (p == null) return "";
        return unitKind == UnitKinds.Second && !string.IsNullOrWhiteSpace(p.SecondUnit) ? p.SecondUnit : p.Unit;
    }
}

public sealed class MasterData
{
    private readonly SheetStore _store;

    public MasterData(SheetStore store) => _store = store;

    public async Task<MasterSnapshot> LoadAsync()
    {
        var users = await _store.GetAllAsync<UserRecord>();
        var dealers = await _store.GetAllAsync<Dealer>();
        var beats = await _store.GetAllAsync<Beat>();
        var parties = await _store.GetAllAsync<Party>();
        var products = await _store.GetAllAsync<Product>();
        var schemes = await _store.GetAllAsync<Scheme>();
        var partyTypes = await _store.GetAllAsync<PartyType>();

        return new MasterSnapshot
        {
            Users = ToDict(users),
            Dealers = ToDict(dealers),
            Beats = ToDict(beats),
            Parties = ToDict(parties),
            Products = ToDict(products),
            Schemes = ToDict(schemes),
            PartyTypes = ToDict(partyTypes)
        };
    }

    private static Dictionary<string, T> ToDict<T>(IEnumerable<T> items) where T : IEntity
    {
        var dict = new Dictionary<string, T>();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.Id)) dict[item.Id] = item;
        }
        return dict;
    }
}

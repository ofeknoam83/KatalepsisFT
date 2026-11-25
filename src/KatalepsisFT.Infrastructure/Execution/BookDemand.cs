using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;

namespace KatalepsisFT.Infrastructure.Execution;

/// <summary>
/// Which books the execution service should have open right now. A book is wanted while somebody is looking at the
/// instrument, while an order is working in it or a position is held in it, and always for a short list of the main
/// assets. Everything else is closed, so the number of open books stays within what the venues allow.
/// </summary>
/// <param name="always">Assets whose spot and perpetual books are kept open whoever is looking.</param>
/// <param name="most">The most instruments that may be open only because somebody looked at them.</param>
public sealed class BookDemand(IReadOnlyList<string> always, int most = BookDemand.DefaultMost)
{
    public const int DefaultMost = 80;

    private readonly HashSet<string> _always = always.Select(a => a.ToUpperInvariant()).ToHashSet();
    private volatile IReadOnlySet<(string Asset, InstrumentType Type)> _held = new HashSet<(string, InstrumentType)>();
    private volatile IReadOnlySet<(string Asset, InstrumentType Type)> _wanted = Both(always.Select(a => a.ToUpperInvariant())).ToHashSet();
    private volatile IReadOnlyDictionary<(Venue Venue, InstrumentType Type, string Quote), IReadOnlySet<string>> _listed = new Dictionary<(Venue, InstrumentType, string), IReadOnlySet<string>>();

    /// <summary>How many instruments are wanted at the moment.</summary>
    public int Count => _wanted.Count;

    /// <summary>What a venue lists for one kind of market, as last read. Replaces what was known before.</summary>
    public void Listed(Venue venue, InstrumentType type, IEnumerable<string> assets, string quote = "USDT")
    {
        Dictionary<(Venue, InstrumentType, string), IReadOnlySet<string>> next = new(_listed) { [(venue, type, quote)] = assets.Select(a => a.ToUpperInvariant()).ToHashSet() };
        _listed = next;
    }

    /// <summary>Sets what is wanted now.</summary>
    /// <param name="held">Instruments with an open order or a position: always kept.</param>
    /// <param name="asked">Instruments somebody asked for lately, the latest first: kept up to the limit.</param>
    public void Want(IEnumerable<(string Asset, InstrumentType Type)> held, IEnumerable<(string Asset, InstrumentType Type)> asked)
    {
        HashSet<(string, InstrumentType)> kept = held.Select(h => (h.Asset.ToUpperInvariant(), h.Type)).ToHashSet();
        _held = kept;
        HashSet<(string, InstrumentType)> next = Both(_always).ToHashSet();
        next.UnionWith(kept);
        int room = most;
        foreach ((string asset, InstrumentType type) in asked)
        {
            (string, InstrumentType) key = (asset.ToUpperInvariant(), type);
            if (next.Contains(key))
            {
                continue;
            }

            if (room-- <= 0)
            {
                break;
            }

            next.Add(key);
        }

        _wanted = next;
    }

    /// <summary>
    /// The assets one venue's stream should follow for one kind of market, in a fixed order. Only what the venue
    /// lists: asking a venue for a book it does not have can make it refuse the others asked for with it. "Lists"
    /// means has in its list at all, open for orders or not: a venue can report an instrument as closed to where
    /// this service runs, or as being wound down, and still publish its book, and that is exactly when a position
    /// in it has to be seen and reduced. Until a venue's list has been read, only the assets that are always
    /// followed are asked for, as before there was a list. The order is by importance, so that a stream which
    /// can only follow so many keeps the right ones: what is always followed, then what is held, then the rest.
    /// </summary>
    /// <param name="second">
    /// True for a venue's second market of the same kind (its USDC perpetuals beside its USDT ones). There only
    /// what the venue lists is asked for: nothing was followed there before there was a list.
    /// </param>
    public IReadOnlyList<string> For(Venue venue, InstrumentType type, string quote = "USDT", bool second = false)
    {
        IEnumerable<string> assets = _wanted.Where(w => w.Type == type).Select(w => w.Asset);
        assets = _listed.TryGetValue((venue, type, quote), out IReadOnlySet<string>? listed) ? assets.Where(listed.Contains) : assets.Where(a => !second && _always.Contains(a));
        IReadOnlySet<(string Asset, InstrumentType Type)> held = _held;
        return assets.OrderBy(a => _always.Contains(a) ? 0 : held.Contains((a, type)) ? 1 : 2).ThenBy(a => a, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<(string, InstrumentType)> Both(IEnumerable<string> assets) => assets.SelectMany(a => new[] { (a, InstrumentType.Spot), (a, InstrumentType.Perp) });
}

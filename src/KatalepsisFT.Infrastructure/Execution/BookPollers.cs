using System.Globalization;
using System.Text.Json;
using KatalepsisFT.Application.Execution;
using KatalepsisFT.Domain.Connections;
using KatalepsisFT.Domain.Instruments;
using Microsoft.Extensions.Logging;

namespace KatalepsisFT.Infrastructure.Execution;

/// <summary>A venue's books kept by asking for them, for a venue whose stream cannot be read here.</summary>
public abstract class BookPoller
{
    public abstract Venue Venue { get; }

    public abstract InstrumentType Type { get; }

    /// <summary>How often every book is asked for. Well inside the two seconds after which a book is no longer used.</summary>
    public virtual TimeSpan Every => TimeSpan.FromSeconds(1);

    public abstract Uri Address(string asset);

    /// <summary>Reads one answer into the two sides of a book, best prices first; null when it is not a book.</summary>
    public abstract (IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks)? Read(string answer);

    public async Task RunAsync(IReadOnlyList<string> assets, ConsolidatedBook books, VenueStatusBoard board, HttpClient http, TimeProvider clock, ILogger logger, CancellationToken stopping)
    {
        logger.LogInformation("The {Venue} {Type} books are read every {Seconds:0.#} s.", Venue, Type, Every.TotalSeconds);
        DateTimeOffset warned = DateTimeOffset.MinValue;
        while (!stopping.IsCancellationRequested)
        {
            DateTimeOffset started = clock.GetUtcNow();
            await Task.WhenAll(assets.Select(async asset =>
            {
                try
                {
                    using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    // An answer that takes longer than this is older than a book may be when it is used.
                    limit.CancelAfter(TimeSpan.FromSeconds(1.5));
                    using HttpResponseMessage response = await http.GetAsync(Address(asset), limit.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        return;
                    }

                    if (Read(await response.Content.ReadAsStringAsync(limit.Token)) is { } book)
                    {
                        // Stamped with when it was asked for: the answer cannot be newer than that.
                        books.Update(new VenueBook(Venue, asset.ToUpperInvariant(), Type, book.Bids, book.Asks, started));
                        board.Heard(Venue);
                    }
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                {
                    if (!stopping.IsCancellationRequested && clock.GetUtcNow() - warned > TimeSpan.FromMinutes(5))
                    {
                        warned = clock.GetUtcNow();
                        logger.LogWarning("A {Venue} {Type} book could not be read: {Reason}", Venue, Type, e.Message);
                    }
                }
            }));

            TimeSpan left = Every - (clock.GetUtcNow() - started);
            try
            {
                await Task.Delay(left > TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50), stopping);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>
/// MEXC spot against USDT. Its stream is in a binary form of its own, so the book is asked for once a second
/// instead, which is also what Katalepsis falls back to for MEXC.
/// </summary>
public sealed class MexcSpotBookPoller : BookPoller
{
    public override Venue Venue => Venue.Mexc;

    public override InstrumentType Type => InstrumentType.Spot;

    public override Uri Address(string asset) => new($"https://api.mexc.com/api/v3/depth?symbol={asset.ToUpperInvariant()}USDT&limit={BookStream.Depth}");

    public override (IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks)? Read(string answer)
    {
        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("bids", out JsonElement bids) && root.TryGetProperty("asks", out JsonElement asks) ? (Side(bids), Side(asks)) : null;
    }

    private static List<BookLevel> Side(JsonElement side)
    {
        List<BookLevel> levels = new(BookStream.Depth);
        foreach (JsonElement level in side.EnumerateArray())
        {
            decimal quantity = decimal.Parse(level[1].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (quantity > 0m && levels.Count < BookStream.Depth)
            {
                levels.Add(new BookLevel(decimal.Parse(level[0].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture), quantity));
            }
        }

        return levels;
    }
}

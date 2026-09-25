using System.Text;
using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.Advisor;

/// <summary>Plain-text description of a snapshot, for checking what the mod captured.</summary>
public static class SnapshotSummary
{
    public static string Render(RunSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.Decision} | {s.Run.Character} A{s.Run.Ascension} | act {s.Run.Act}, floor {s.Run.TotalFloor} | HP {s.Run.CurrentHp}/{s.Run.MaxHp} | {s.Run.Gold} gold | seed {s.Run.Seed}");
        sb.AppendLine($"game {s.GameVersion}, mod {s.ModVersion}, schema {s.SchemaVersion}");

        var deck = s.Deck
            .GroupBy(c => c.Upgraded ? c.Id + "+" : c.Id)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
        sb.AppendLine($"Deck ({s.Deck.Count}): {string.Join(", ", deck)}");
        sb.AppendLine($"Relics: {string.Join(", ", s.Relics)}");
        sb.AppendLine($"Potions: {string.Join(", ", s.Potions.Select(p => p.Id))}");

        if (s.Offer.Cards.Count > 0)
            sb.AppendLine($"Offered cards: {string.Join(", ", s.Offer.Cards.Select(c => c.Price > 0 ? $"{c.Id} ({c.Price}g)" : c.Id))}");
        if (s.Offer.Relics.Count > 0)
            sb.AppendLine($"Offered relics: {string.Join(", ", s.Offer.Relics.Select(r => r.Price > 0 ? $"{r.Id} ({r.Price}g)" : r.Id))}");
        if (s.Offer.Potions.Count > 0)
            sb.AppendLine($"Offered potions: {string.Join(", ", s.Offer.Potions.Select(p => $"{p.Id} ({p.Price}g)"))}");
        if (s.Offer.CardRemovalPrice is int removal)
            sb.AppendLine($"Card removal: {removal}g");

        if (s.EventId != null)
            sb.AppendLine($"Event: {s.EventId}");
        foreach (EventOptionSnapshot o in s.EventOptions)
            sb.AppendLine($"  option {o.TextKey}: {o.Title}{(o.Relic != null ? $" [relic {o.Relic}]" : "")}{(o.IsLocked ? " (locked)" : "")}{(o.IsProceed ? " (proceed)" : "")}");

        if (s.Map is MapSnapshot map)
        {
            string types = string.Join(", ", map.Points.GroupBy(p => p.Type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            sb.AppendLine($"Map: {map.Columns}x{map.Rows}, {map.Points.Count} nodes ({types}), {map.Points.Sum(p => p.Children.Count)} edges");
            sb.AppendLine($"  at {(map.Current is MapCoordinate c ? $"({c.Col},{c.Row})" : "start of act")}, {map.Visited.Count} visited, boss {(map.Boss is MapCoordinate b ? $"({b.Col},{b.Row})" : "unknown")}");
        }
        return sb.ToString();
    }
}

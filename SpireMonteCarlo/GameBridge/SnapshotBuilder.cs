using System;
using System.Linq;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;
using SpireMonteCarlo.Contracts;

namespace SpireMonteCarlo.GameBridge;

public static class SnapshotBuilder
{
	public static RunSnapshot Build(string decision, GameState state, string eventId = null)
	{
		var snapshot = new RunSnapshot
		{
			GameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "",
			ModVersion = Plugin.ModVersion,
			CapturedAt = DateTimeOffset.Now,
			Decision = decision,
			EventId = eventId,
			Run = new RunInfo
			{
				Character = state.Character ?? "",
				Ascension = state.AscensionLevel,
				Act = state.ActNumber,
				TotalFloor = state.Floor,
				CurrentHp = state.CurrentHP,
				MaxHp = state.MaxHP,
				Gold = state.Gold
			},
			Deck = state.DeckCards.Select(ToCard).ToList(),
			Relics = state.CurrentRelics.Select(r => r.Id).ToList(),
			Potions = state.CurrentPotions.Select(p => new PotionSnapshot { Id = p.Id, Price = p.Price }).ToList()
		};

		if (decision == DecisionType.Shop)
		{
			snapshot.Offer.Cards = state.ShopCards.Select(ToCard).ToList();
			snapshot.Offer.Relics = state.ShopRelics.Select(r => new RelicOffer { Id = r.Id, Price = r.Price }).ToList();
			snapshot.Offer.Potions = state.ShopPotions.Select(p => new PotionSnapshot { Id = p.Id, Price = p.Price }).ToList();
		}
		else
		{
			snapshot.Offer.Cards = state.OfferedCards.Select(ToCard).ToList();
			snapshot.Offer.Relics = state.OfferedRelics.Select(r => new RelicOffer { Id = r.Id, Price = r.Price }).ToList();
		}

		try
		{
			RunManager runManager = RunManager.Instance;
			RunState runState = runManager == null ? null : GameStateReader.GetRunState(runManager);
			if (runState != null)
			{
				snapshot.Run.Seed = runState.Rng?.StringSeed ?? "";
				snapshot.Map = BuildMap(runState);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log($"SnapshotBuilder: could not read run state extras: {ex.Message}");
		}
		return snapshot;
	}

	private static CardSnapshot ToCard(CardInfo c) => new CardSnapshot { Id = c.Id, Upgraded = c.Upgraded, Price = c.Price };

	private static MapCoordinate ToCoord(MapCoord c) => new MapCoordinate(c.col, c.row);

	private static MapSnapshot BuildMap(RunState runState)
	{
		ActMap map = runState.Map;
		if (map == null) return null;
		var result = new MapSnapshot
		{
			Columns = map.GetColumnCount(),
			Rows = map.GetRowCount(),
			Current = runState.CurrentMapCoord.HasValue ? ToCoord(runState.CurrentMapCoord.Value) : null,
			Visited = runState.VisitedMapCoords.Select(ToCoord).ToList(),
			Boss = map.BossMapPoint != null ? ToCoord(map.BossMapPoint.coord) : null
		};
		// The game's grid excludes the start and boss nodes, so add them explicitly.
		var points = map.GetAllMapPoints().ToList();
		foreach (MapPoint extra in new[] { map.StartingMapPoint, map.BossMapPoint, map.SecondBossMapPoint })
		{
			if (extra != null && !points.Contains(extra))
				points.Add(extra);
		}
		foreach (MapPoint point in points)
		{
			result.Points.Add(new MapPointSnapshot
			{
				Col = point.coord.col,
				Row = point.coord.row,
				Type = point.PointType.ToString(),
				Children = point.Children.OrderBy(c => c.coord.col).Select(c => ToCoord(c.coord)).ToList()
			});
		}
		return result;
	}
}

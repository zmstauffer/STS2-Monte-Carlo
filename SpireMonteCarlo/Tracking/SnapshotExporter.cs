using System;
using System.IO;
using System.Linq;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.GameBridge;

namespace SpireMonteCarlo.Tracking;

/// <summary>Writes a JSON snapshot of the run at each decision screen for the advisor app (and for offline testing).</summary>
public static class SnapshotExporter
{
	private const int MaxFiles = 500;
	private static string _lastFingerprint;
	private static readonly object _lock = new object();

	public static string Folder => Path.Combine(Plugin.AppDataFolder, "snapshots");

	public static void Export(string decision, GameState state, string eventId = null)
	{
		if (state == null) return;
		try
		{
			RunSnapshot snapshot = SnapshotBuilder.Build(decision, state, eventId);
			// The same screen often fires several hooks (e.g. ShowScreen then RefreshOptions); skip exact repeats.
			DateTimeOffset capturedAt = snapshot.CapturedAt;
			snapshot.CapturedAt = default;
			string fingerprint = SnapshotSerializer.Serialize(snapshot);
			snapshot.CapturedAt = capturedAt;
			lock (_lock)
			{
				if (fingerprint == _lastFingerprint) return;
				_lastFingerprint = fingerprint;
				Directory.CreateDirectory(Folder);
				string path = Path.Combine(Folder, $"{capturedAt:yyyyMMdd-HHmmss-fff}_{decision}.json");
				File.WriteAllText(path, SnapshotSerializer.Serialize(snapshot));
				Prune();
			}
		}
		catch (Exception ex)
		{
			Plugin.Log($"SnapshotExporter error ({decision}): {ex.Message}");
		}
	}

	private static void Prune()
	{
		var files = new DirectoryInfo(Folder).GetFiles("*.json").OrderBy(f => f.Name).ToList();
		foreach (FileInfo old in files.Take(Math.Max(0, files.Count - MaxFiles)))
			old.Delete();
	}
}

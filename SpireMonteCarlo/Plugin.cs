using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;
using SpireMonteCarlo.Core;
using SpireMonteCarlo.Tracking;
using SpireMonteCarlo.UI;

namespace SpireMonteCarlo;

[ModInitializer("Init")]
public static class Plugin
{
	public const string ModName = "Spire Monte Carlo";

	public const string ModVersion = "0.1.0";

	public const string HarmonyId = "com.spiremontecarlo.mod";

	private static Harmony _harmony;

	private static bool _initialized;

	public static string PluginFolder { get; private set; }

	public static string AppDataFolder { get; private set; }

	public static string LogPath { get; private set; }

	public static TierEngine TierEngine { get; private set; }

	public static DeckAnalyzer DeckAnalyzer { get; private set; }

	public static SynergyScorer SynergyScorer { get; private set; }

	public static AdaptiveScorer AdaptiveScorer { get; private set; }

	public static RunTracker RunTracker { get; private set; }

	public static RunDatabase RunDatabase { get; private set; }

	public static LocalStatsComputer LocalStats { get; private set; }

	public static CardPropertyScorer CardPropertyScorer { get; private set; }

	public static EventAdvisor EventAdvisor { get; private set; }

	public static EnemyAdvisor EnemyAdvisor { get; private set; }

	public static OverlayManager Overlay { get; set; }

	public static void Init()
	{
		if (_initialized) return;
		_initialized = true;
		PluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		if (string.IsNullOrEmpty(PluginFolder))
		{
			PluginFolder = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
		}
		if (string.IsNullOrEmpty(PluginFolder))
		{
			PluginFolder = Path.Combine(AppContext.BaseDirectory, "mods", "SpireMonteCarlo");
		}
		LogPath = Path.Combine(PluginFolder, "spiremontecarlo.log");
		// JSON data lives outside mods\ because the game's mod loader treats every .json under mods\ as a possible manifest.
		AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpireMonteCarlo");
		Directory.CreateDirectory(AppDataFolder);
		AppDomain.CurrentDomain.AssemblyResolve += delegate(object? sender, ResolveEventArgs args)
		{
			AssemblyName assemblyName = new AssemblyName(args.Name);
			string text = Path.Combine(PluginFolder, assemblyName.Name + ".dll");
			return File.Exists(text) ? Assembly.LoadFrom(text) : null;
		};
		Log($"{ModName} v{ModVersion} initializing...");
		TierEngine = new TierEngine(AppDataFolder);
		CardPropertyScorer = new CardPropertyScorer(Path.Combine(PluginFolder, "Data", "CardProperties"));
		DeckAnalyzer = new DeckAnalyzer();
		SynergyScorer = new SynergyScorer();
		RunDatabase = new RunDatabase(PluginFolder);
		RunTracker = new RunTracker(RunDatabase);
		RunTracker.Initialize(PluginFolder);
		LocalStats = new LocalStatsComputer(RunDatabase, TierEngine);
		LocalStats.RecomputeAll();
		new GameDataImporter(RunDatabase).ImportAll();
		AdaptiveScorer = new AdaptiveScorer(RunDatabase);
		EventAdvisor = new EventAdvisor(AppDataFolder);
		EnemyAdvisor = new EnemyAdvisor(AppDataFolder);
		_harmony = new Harmony(HarmonyId);
		_harmony.PatchAll(typeof(GamePatches).Assembly);
		GamePatches.ApplyManualPatches(_harmony);
		Log("Harmony patches applied.");
		Log($"{ModName} initialized successfully. Waiting for scene tree...");
	}

	private static volatile StreamWriter _logWriter;
	private static readonly object _logLock = new object();
	private static bool _exitHandlerRegistered;

	public static void Log(string message)
	{
		string text = $"[{DateTime.Now:HH:mm:ss}] [SpireMonteCarlo] {message}";
		try
		{
			lock (_logLock)
			{
				if (_logWriter == null)
				{
					_logWriter = new StreamWriter(LogPath, append: true) { AutoFlush = true };
					if (!_exitHandlerRegistered)
					{
						_exitHandlerRegistered = true;
						AppDomain.CurrentDomain.ProcessExit += (_, _) =>
						{
							lock (_logLock) { _logWriter?.Dispose(); _logWriter = null; }
						};
					}
				}
				_logWriter.WriteLine(text);
			}
		}
		catch
		{
		}
	}
}

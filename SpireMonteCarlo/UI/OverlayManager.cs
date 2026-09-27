using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SpireMonteCarlo.Contracts;
using SpireMonteCarlo.Core;
using SpireMonteCarlo.GameBridge;
using SpireMonteCarlo.Tracking;

namespace SpireMonteCarlo.UI;

/// <summary>
/// The in-game "Spire MC" panel. It shows what the advisor app (advisor watch) says about the current decision: "Thinking..." as soon as
/// the mod writes a snapshot, then the options from advice\latest.json once the app has finished that snapshot. It only reads files the
/// app writes (advice\latest.json and advice\status.json); all the thinking happens in the app. The game hooks still call the Show*
/// methods, which now only tell the panel which screen is up (the card-reward hooks read <see cref="CurrentScreen"/> to skip screens
/// that reuse the card-reward view). Drag the title to move it, double-click it to collapse, F7 (or Alt+H, or `) hides it.
/// </summary>
public class OverlayManager
{
	private enum PanelState { Waiting, Thinking, Advice, NothingToAdvise, Error, NotRunning, RunOver }

	// STS2 colour palette (matched from the game's scenes).
	private static readonly Color ClrBg = new Color(0.034f, 0.057f, 0.11f, 0.95f);
	private static readonly Color ClrBorder = new Color(0.624f, 0.490f, 0.322f);
	private static readonly Color ClrHeader = new Color(0.92f, 0.78f, 0.35f);
	private static readonly Color ClrSub = new Color(0.580f, 0.545f, 0.404f);
	private static readonly Color ClrCream = new Color(0.92f, 0.88f, 0.78f);
	private static readonly Color ClrWorse = new Color(0.72f, 0.60f, 0.55f);
	private static readonly Color ClrOutline = new Color(0.02f, 0.02f, 0.04f);

	private const float PanelWidth = 380f;
	private const int MaxOptionsShown = 7;
	private const double PollSeconds = 0.25, DotSeconds = 0.4;
	/// <summary>A heartbeat older than this means the advisor app isn't running (it refreshes every 3 s while idle).</summary>
	private const double HeartbeatStaleSeconds = 12;
	/// <summary>One decision never takes this long; a "thinking" older than this means the app died mid-way.</summary>
	private const double ThinkingStaleSeconds = 240;

	private static string AdviceFolder => Path.Combine(Plugin.AppDataFolder, "advice");

	private readonly OverlaySettings _settings;
	private CanvasLayer _layer;
	private PanelContainer _panel;
	private Label _statusLabel;
	private Label _collapseToggle;
	private VBoxContainer _body;
	private Font _fontBody, _fontBold;
	private StyleBoxFlat _sbPanel, _sbBest, _sbRow;

	private string _currentScreen = "IDLE";
	public string CurrentScreen => _currentScreen;

	private bool _userHidden;
	private bool _collapsed;
	private bool _inCombat;
	private bool _dragging;
	private Vector2 _dragOffset;

	// What the panel is showing and for which snapshot.
	private PanelState _state = PanelState.Waiting;
	private string _snapshotFile;
	private bool _decided;   // the player made this decision already: late advice for it is not shown
	private string _renderedFor;
	private string _statusText = "";
	private double _pollTimer, _dotTimer;
	private int _fitFrames;   // wrapped labels settle their height a frame or two late, so the panel is re-fitted for a few frames
	private int _dots;

	// Files the app writes, re-read only when they change.
	private DateTime _adviceStamp, _statusStamp;
	private AdviceResult _advice;
	private WatcherStatus _status;

	public OverlayManager()
	{
		_settings = OverlaySettings.Load();
		_collapsed = _settings.Collapsed;
		LoadFonts();
		InitializeStyles();
		Build();
	}

	// ---- what the game hooks call -------------------------------------------------------------------------------

	public void ShowCardAdvice(List<ScoredCard> cards, DeckAnalysis deckAnalysis = null, string character = null, string screenLabel = "CARD REWARD") => SetScreen(screenLabel);
	public void SetScreenLabel(string screen) => SetScreen(screen);
	public void ShowRelicAdvice(List<ScoredRelic> relics, DeckAnalysis deckAnalysis = null, string character = null) => SetScreen("RELIC REWARD");
	public void ShowCardRemovalAdvice(List<ScoredCard> removalCandidates, DeckAnalysis deckAnalysis = null, string character = null) => SetScreen("CARD REMOVAL");
	public void ShowRestSiteAdvice(DeckAnalysis deckAnalysis, int currentHP, int maxHP, int actNumber, int floor, GameState gameState = null) => SetScreen("REST SITE");
	public void ShowUpgradeAdvice(DeckAnalysis deckAnalysis, GameState gameState, string character) => SetScreen("CARD UPGRADE");
	public void ShowEventAdvice(DeckAnalysis deckAnalysis, int currentHP, int maxHP, int gold, int actNumber, int floor, string eventId = null) => SetScreen("EVENT");
	public void ShowMapAdvice(DeckAnalysis deckAnalysis, int currentHP, int maxHP, int gold, int actNumber, int floor) => SetScreen("MAP");
	public void ShowShopAdvice(List<ScoredCard> cards, List<ScoredRelic> relics, List<ScoredPotion> potions = null, DeckAnalysis deckAnalysis = null, string character = null) => SetScreen("MERCHANT SHOP");

	/// <summary>Combat has no run-level decision: the panel stays out of the way until the next decision screen.</summary>
	public void ShowCombatAdvice(DeckAnalysis deckAnalysis, int currentHP, int maxHP, int actNumber, int floor, GameState gameState = null, List<string> enemyIds = null)
	{
		SetScreen("COMBAT");
		_inCombat = true;
		ApplyVisibility();
	}

	/// <summary>The player took a card or relic: this decision is made, so its advice goes away until the next one.</summary>
	public void Clear()
	{
		_currentScreen = "MAP / COMBAT";
		_decided = true;
		SetState(PanelState.Waiting);
	}

	public void ShowRunSummary(RunOutcome outcome, int finalFloor, int finalAct)
	{
		_currentScreen = outcome == RunOutcome.Win ? "RUN WON!" : "RUN LOST";
		_decided = true;
		_inCombat = false;
		_statusText = outcome == RunOutcome.Win ? $"Run won! (act {finalAct}, floor {finalFloor})" : $"Run over on act {finalAct}, floor {finalFloor}.";
		SetState(PanelState.RunOver);
		ApplyVisibility();
	}

	private void SetScreen(string screen) => _currentScreen = screen;

	// ---- polling (called every frame by OverlayInputHandler) ------------------------------------------------------

	public void Tick(double delta)
	{
		if (!EnsureBuilt()) return;
		if (_fitFrames > 0 && --_fitFrames >= 0) _panel.ResetSize();
		_pollTimer += delta;
		_dotTimer += delta;
		if (_pollTimer >= PollSeconds)
		{
			_pollTimer = 0;
			try { Poll(); } catch (Exception ex) { Plugin.Log("Panel poll error: " + ex.Message); }
		}
		if (_state == PanelState.Thinking && _dotTimer >= DotSeconds)
		{
			_dotTimer = 0;
			_dots = (_dots + 1) % 3;
			_statusLabel.Text = "Thinking" + new string('.', _dots + 1);
		}
	}

	private void Poll()
	{
		// A new snapshot means a new decision screen.
		string file = SnapshotExporter.LastFile;
		if (file != null && file != _snapshotFile)
		{
			_snapshotFile = file;
			_decided = false;
			_renderedFor = null;
			_inCombat = false;
			SetState(PanelState.Thinking);
			ApplyVisibility();
		}
		if (_snapshotFile == null || _decided || _state == PanelState.RunOver) return;

		ReadStatus();
		ReadAdvice();

		if (_advice != null && _advice.SnapshotFile == _snapshotFile)
		{
			if (_renderedFor != _snapshotFile) ShowAdvice(_advice);
			return;
		}

		double heartbeatAge = _status == null ? double.MaxValue : (DateTimeOffset.Now - _status.UpdatedAt).TotalSeconds;
		if (_status != null && _status.SnapshotFile == _snapshotFile)
		{
			switch (_status.State)
			{
				case WatcherStatus.Unsupported:
					_statusText = "Nothing to advise on this screen.";
					SetState(PanelState.NothingToAdvise);
					return;
				case WatcherStatus.Error:
					_statusText = "The advisor couldn't handle this screen" + (string.IsNullOrEmpty(_status.Message) ? "." : ": " + _status.Message);
					SetState(PanelState.Error);
					return;
				case WatcherStatus.Thinking when heartbeatAge < ThinkingStaleSeconds:
					SetState(PanelState.Thinking);
					return;
			}
		}
		// The app hasn't reached this snapshot yet: fine while its heartbeat is fresh, otherwise it isn't running.
		if (heartbeatAge > HeartbeatStaleSeconds)
		{
			_statusText = "The advisor isn't running. Start it with scripts\\watch.ps1.";
			SetState(PanelState.NotRunning);
		}
		else SetState(PanelState.Thinking);
	}

	private void ReadStatus()
	{
		string path = Path.Combine(AdviceFolder, "status.json");
		if (!File.Exists(path)) { _status = null; _statusStamp = default; return; }
		DateTime stamp = File.GetLastWriteTimeUtc(path);
		if (stamp == _statusStamp) return;
		try
		{
			_status = AdviceSerializer.DeserializeStatus(File.ReadAllText(path));
			_statusStamp = stamp;
		}
		catch (Exception) { }   // being replaced right now; the next poll reads it
	}

	private void ReadAdvice()
	{
		string path = Path.Combine(AdviceFolder, "latest.json");
		if (!File.Exists(path)) return;
		DateTime stamp = File.GetLastWriteTimeUtc(path);
		if (stamp == _adviceStamp) return;
		try
		{
			_advice = AdviceSerializer.Deserialize(File.ReadAllText(path));
			_adviceStamp = stamp;
		}
		catch (Exception) { }
	}

	// ---- drawing ------------------------------------------------------------------------------------------------------

	private void SetState(PanelState state)
	{
		if (!EnsureBuilt()) return;
		if (state == _state && state != PanelState.Advice && state != PanelState.Waiting)
		{
			if (state != PanelState.Thinking) _statusLabel.Text = _statusText;
			return;
		}
		_state = state;
		ClearBody();
		switch (state)
		{
			case PanelState.Waiting:
				_statusLabel.Text = "Waiting for the next decision.";
				break;
			case PanelState.Thinking:
				_dots = 2;
				_statusLabel.Text = "Thinking...";
				break;
			default:
				_statusLabel.Text = _statusText;
				break;
		}
		FitToContent();
	}

	private void ShowAdvice(AdviceResult advice)
	{
		_renderedFor = advice.SnapshotFile;
		_state = PanelState.Advice;
		ClearBody();
		_statusLabel.Text = ScreenName(advice) + (advice.Options.Count > 1 ? $"  ·  {advice.Seconds:F0}s" : "");

		AddLabel(_body, advice.Suggestion, _fontBold, 17, ClrCream);
		if (advice.Options.Count > 1)
		{
			foreach (AdviceOption option in advice.Options.Take(MaxOptionsShown)) AddOptionRow(option, option == advice.Options[0]);
			if (advice.Options.Count > MaxOptionsShown)
				AddLabel(_body, $"... and {advice.Options.Count - MaxOptionsShown} more (see the advisor window).", _fontBody, 14, ClrSub);
			foreach (string line in advice.Why.Take(2))
				AddLabel(_body, "• " + line, _fontBody, 14, ClrSub);
		}
		FitToContent();
	}

	private void AddOptionRow(AdviceOption option, bool best)
	{
		var row = new PanelContainer();
		row.AddThemeStyleboxOverride("panel", best ? _sbBest : _sbRow);
		row.MouseFilter = Control.MouseFilterEnum.Ignore;
		var line = new HBoxContainer();
		line.AddThemeConstantOverride("separation", 10);
		row.AddChild(line);

		string name = string.IsNullOrEmpty(option.Display) ? option.Label : option.Display;
		Label label = AddLabel(line, name, best ? _fontBold : _fontBody, 16, best ? ClrHeader : ClrCream);
		label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

		string verdict = best ? "best" : option.AboutEqualToBest ? "~ equal" : $"-{Math.Abs(option.PointsVsBest):0.0}";
		Label right = AddLabel(line, verdict, _fontBold, 16, best ? ClrHeader : option.AboutEqualToBest ? ClrCream : ClrWorse);
		right.AutowrapMode = TextServer.AutowrapMode.Off;
		right.HorizontalAlignment = HorizontalAlignment.Right;
		right.CustomMinimumSize = new Vector2(64f, 0f);
		_body.AddChild(row);
	}

	private static string ScreenName(AdviceResult advice) => advice.Decision switch
	{
		DecisionType.CardReward => "Card reward",
		DecisionType.RestSite => "Rest site",
		DecisionType.CardUpgrade => "Upgrade",
		DecisionType.Map => "Map",
		DecisionType.Shop => "Shop",
		DecisionType.Event => "Event",
		_ => advice.Decision,
	};

	private Label AddLabel(Container parent, string text, Font font, int size, Color color)
	{
		var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore };
		label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		if (font != null) label.AddThemeFontOverride("font", font);
		label.AddThemeFontSizeOverride("font_size", size);
		label.AddThemeColorOverride("font_color", color);
		parent.AddChild(label);
		return label;
	}

	private void ClearBody()
	{
		foreach (Node child in _body.GetChildren())
		{
			_body.RemoveChild(child);
			child.QueueFree();
		}
		_body.Visible = !_collapsed;
	}

	/// <summary>Shrinks or grows the panel to its content (a container keeps its old size otherwise).</summary>
	private void FitToContent()
	{
		if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.CallDeferred(Control.MethodName.ResetSize);
		_fitFrames = 3;
	}

	private void ApplyVisibility()
	{
		if (_panel != null && GodotObject.IsInstanceValid(_panel)) _panel.Visible = !_userHidden && !_inCombat;
	}

	// ---- building, moving, hiding --------------------------------------------------------------------------------------

	private void LoadFonts()
	{
		try
		{
			_fontBody = ResourceLoader.Load<Font>("res://fonts/kreon_regular.ttf");
			_fontBold = ResourceLoader.Load<Font>("res://fonts/kreon_bold.ttf");
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not load game fonts, using defaults: " + ex.Message);
		}
	}

	private void InitializeStyles()
	{
		_sbPanel = new StyleBoxFlat
		{
			BgColor = ClrBg, BorderColor = ClrBorder, BorderWidthTop = 3, BorderWidthLeft = 3, BorderWidthRight = 1, BorderWidthBottom = 1,
			CornerRadiusTopRight = 18, CornerRadiusBottomLeft = 18, ShadowSize = 12, ShadowColor = new Color(0f, 0f, 0f, 0.5f),
			ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 14, ContentMarginBottom = 16,
		};
		_sbRow = new StyleBoxFlat
		{
			BgColor = new Color(0.06f, 0.08f, 0.14f, 0.6f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
			ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 5, ContentMarginBottom = 5,
		};
		_sbBest = (StyleBoxFlat)_sbRow.Duplicate();
		_sbBest.BgColor = new Color(0.831f, 0.714f, 0.357f, 0.12f);
		_sbBest.BorderWidthLeft = 4;
		_sbBest.BorderColor = ClrHeader;
	}

	private bool EnsureBuilt()
	{
		if (_layer != null && GodotObject.IsInstanceValid(_layer) && _panel != null && GodotObject.IsInstanceValid(_panel)) return true;
		return Build();
	}

	private bool Build()
	{
		if (Engine.GetMainLoop() is not SceneTree { Root: not null } tree)
		{
			Plugin.Log("SceneTree not ready; panel deferred.");
			return false;
		}
		if (_layer != null && GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();

		_layer = new CanvasLayer { Layer = 100 };
		_panel = new PanelContainer
		{
			AnchorLeft = 1f, AnchorRight = 1f, AnchorTop = 0f, AnchorBottom = 0f,
			OffsetLeft = _settings.OffsetLeft, OffsetTop = _settings.OffsetTop,
			OffsetRight = _settings.OffsetLeft + PanelWidth, OffsetBottom = _settings.OffsetTop + 40,
			GrowVertical = Control.GrowDirection.End,
			CustomMinimumSize = new Vector2(PanelWidth, 0f),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		_panel.AddThemeStyleboxOverride("panel", _sbPanel);

		var column = new VBoxContainer();
		column.AddThemeConstantOverride("separation", 8);
		_panel.AddChild(column);

		// Title bar: drag to move, double-click to collapse.
		var titleBar = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, MouseDefaultCursorShape = Control.CursorShape.Drag };
		titleBar.GuiInput += OnTitleBarInput;
		column.AddChild(titleBar);
		var titleRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
		titleBar.AddChild(titleRow);
		Label title = AddLabel(titleRow, "Spire MC", _fontBold, 24, ClrHeader);
		title.AutowrapMode = TextServer.AutowrapMode.Off;
		title.AddThemeConstantOverride("outline_size", 4);
		title.AddThemeColorOverride("font_outline_color", ClrOutline);
		_collapseToggle = AddLabel(titleRow, _collapsed ? "▼" : "▲", _fontBold, 16, ClrSub);
		_collapseToggle.AutowrapMode = TextServer.AutowrapMode.Off;
		_collapseToggle.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
		_collapseToggle.MouseFilter = Control.MouseFilterEnum.Stop;
		_collapseToggle.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
		_collapseToggle.GuiInput += ev =>
		{
			if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ToggleCollapsed();
		};
		var separator = new HSeparator { MouseFilter = Control.MouseFilterEnum.Ignore };
		separator.AddThemeStyleboxOverride("separator", new StyleBoxLine { Color = new Color(ClrBorder, 0.6f), Thickness = 2 });
		titleBar.AddChild(separator);

		_statusLabel = AddLabel(column, "Waiting for a decision.", _fontBold, 15, ClrSub);
		_body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Visible = !_collapsed };
		_body.AddThemeConstantOverride("separation", 6);
		column.AddChild(_body);

		_layer.AddChild(_panel);
		_layer.AddChild(new OverlayInputHandler(this));
		tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);
		ApplyVisibility();
		Plugin.Log("Spire MC panel attached to the scene tree.");
		return true;
	}

	private void OnTitleBarInput(InputEvent ev)
	{
		if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
		{
			if (mb.DoubleClick) { ToggleCollapsed(); return; }
			if (mb.Pressed)
			{
				_dragging = true;
				_dragOffset = mb.GlobalPosition - _panel.GlobalPosition;
			}
			else
			{
				_dragging = false;
				SavePosition();
			}
		}
		else if (ev is InputEventMouseMotion mm && _dragging)
		{
			// The panel is anchored to the top-right corner, so offsets are relative to the viewport's right edge.
			Vector2 position = mm.GlobalPosition - _dragOffset;
			float width = _panel.Size.X, height = _panel.Size.Y;
			float right = _panel.GetViewportRect().Size.X;
			_panel.OffsetLeft = position.X - right;
			_panel.OffsetRight = position.X - right + width;
			_panel.OffsetTop = position.Y;
			_panel.OffsetBottom = position.Y + height;
		}
	}

	private void SavePosition()
	{
		_settings.OffsetLeft = _panel.OffsetLeft;
		_settings.OffsetRight = _panel.OffsetLeft + PanelWidth;
		_settings.OffsetTop = _panel.OffsetTop;
		_settings.Save();
	}

	public void ToggleCollapsed()
	{
		_collapsed = !_collapsed;
		_body.Visible = !_collapsed;
		_collapseToggle.Text = _collapsed ? "▼" : "▲";
		_settings.Collapsed = _collapsed;
		_settings.Save();
		FitToContent();
	}

	public void ToggleVisible()
	{
		_userHidden = !_userHidden;
		ApplyVisibility();
	}

	public void HandleInput(InputEvent ev)
	{
		if (ev is not InputEventKey { Pressed: true, Echo: false } key) return;
		bool f7 = key.Keycode == Key.F7 || key.PhysicalKeycode == Key.F7;
		bool altH = key.AltPressed && (key.Keycode == Key.H || key.PhysicalKeycode == Key.H);
		bool backtick = !key.AltPressed && !key.CtrlPressed && !key.ShiftPressed && (key.Keycode == Key.Quoteleft || key.PhysicalKeycode == Key.Quoteleft);
		if (f7 || altH || backtick) ToggleVisible();
	}
}

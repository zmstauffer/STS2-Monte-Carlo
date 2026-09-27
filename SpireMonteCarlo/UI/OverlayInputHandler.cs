using Godot;

namespace SpireMonteCarlo.UI;

/// <summary>Drives the Spire MC panel from the game's frame loop: polls the advisor's files and handles the hide hotkeys.</summary>
internal class OverlayInputHandler : Node
{
	private readonly OverlayManager _owner;

	public OverlayInputHandler(OverlayManager owner)
	{
		_owner = owner;
		ProcessMode = ProcessModeEnum.Always;
	}

	public override void _UnhandledKeyInput(InputEvent ev) => _owner.HandleInput(ev);

	public override void _Process(double delta) => _owner.Tick(delta);
}

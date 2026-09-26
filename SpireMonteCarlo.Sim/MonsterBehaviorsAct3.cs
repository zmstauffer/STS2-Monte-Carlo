namespace SpireMonteCarlo.Sim;

/// <summary>
/// The Test Subject (TestSubject and AdaptablePower): killed, it goes down and next turn revives with a new form, 200 then 300 HP (212 and
/// 313 from Ascension 8), losing every power but Adaptable. The second form has Painful Stabs and a Multi Claw that gains a hit each time;
/// the third loses Adaptable and Painful Stabs and gains Nemesis. The fight isn't won while it is down.
/// </summary>
public sealed class TestSubjectBehavior : MonsterBehavior
{
    private const string Respawns = "respawns";

    public override IReadOnlyCollection<string> Handled => new[] { "RESPAWN", "Respawns", "AdaptablePower", "PainfulStabsPower" };

    public override void OnStart(Combat combat, Enemy self) => self.Powers[(int)PowerKind.Adaptable] = 1;

    public override bool? EvaluateCondition(Combat combat, Enemy self, string condition) => condition.Replace(" ", "") switch
    {
        "Respawns<2" => self.State.GetValueOrDefault(Respawns) < 2,
        "Respawns>=2" => self.State.GetValueOrDefault(Respawns) >= 2,
        _ => null,
    };

    public override bool OnDeath(Combat combat, Enemy self)
    {
        if (self.Powers[(int)PowerKind.Adaptable] <= 0) return false;
        Array.Clear(self.Powers);
        self.Powers[(int)PowerKind.Adaptable] = 1;
        self.Hp = 0;
        self.Block = 0;
        self.ExtraHits = 0;
        self.Reviving = true;
        return true;
    }

    public override void OnDeadTurn(Combat combat, Enemy self)
    {
        // Its Respawn move: the next form at full HP, then the branch to the form's first move.
        int respawns = self.State.GetValueOrDefault(Respawns) + 1;
        self.State[Respawns] = respawns;
        self.MaxHp = respawns == 1 ? (combat.ToughEnemies ? 212 : 200) : (combat.ToughEnemies ? 313 : 300);
        self.Hp = self.MaxHp;
        self.Reviving = false;
        if (respawns == 1) self.Powers[(int)PowerKind.PainfulStabs] = 1;
        else
        {
            self.Powers[(int)PowerKind.Nemesis] = 1;
            self.Powers[(int)PowerKind.Adaptable] = 0;
            self.Powers[(int)PowerKind.PainfulStabs] = 0;
        }
        self.Stun("REVIVE_BRANCH");   // reviving is this turn's move
    }

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id == "MULTI_CLAW") self.ExtraHits++;
    }
}

/// <summary>
/// The Queen (Queen and TorchHeadAmalgam): Burn Bright for Me gives her allies 1 Strength (and her 20 Block) while the Amalgam lives; once it
/// dies she turns to Off With Your Head, Execution and Enrage, and a Burn Bright she was about to use becomes Enrage at once.
/// </summary>
public sealed class QueenBehavior : MonsterBehavior
{
    public override IReadOnlyCollection<string> Handled => new[] { "HasAmalgamDied", "Strength applied to item", "BURN_BRIGHT_FOR_ME" };

    private static bool AmalgamDied(Combat combat) => !combat.Enemies.Any(e => e.Alive && e.Def.Id == "TORCH_HEAD_AMALGAM");

    public override bool? EvaluateCondition(Combat combat, Enemy self, string condition) => condition.Replace(" ", "") switch
    {
        "HasAmalgamDied" => AmalgamDied(combat),
        "!HasAmalgamDied" => !AmalgamDied(combat),
        _ => null,
    };

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id != "BURN_BRIGHT_FOR_ME") return;
        foreach (Enemy ally in combat.Enemies)
            if (ally != self && ally.Alive) ally.Powers[(int)PowerKind.Strength] += 1;
    }

    public override void OnAllyDeath(Combat combat, Enemy self, Enemy dead)
    {
        if (dead.Def.Id == "TORCH_HEAD_AMALGAM" && self.Move?.Id == "BURN_BRIGHT_FOR_ME") self.SetMoveNow("ENRAGE_MOVE", combat);
    }
}

/// <summary>
/// Aeonglass: Withering Presence on the player from the start (a Wither to the hand every 6 cards played), and Increasing Intensity gains
/// 3 Strength (4 from Ascension 9) plus 1 more each time it is used, and makes every Wither deal 3 more.
/// </summary>
public sealed class AeonglassBehavior : MonsterBehavior
{
    private const string Uses = "intensityUses";

    public override IReadOnlyCollection<string> Handled => new[] { "amount of Strength not resolved", "PowerCmd.Apply(instance)" };

    public override void OnStart(Combat combat, Enemy self) => combat.StartWitheringPresence();

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id != "INCREASING_INTENSITY") return;
        int uses = self.State.GetValueOrDefault(Uses);
        self.Powers[(int)PowerKind.Strength] += (combat.DeadlyEnemies ? 4 : 3) + uses;
        self.State[Uses] = uses + 1;
        combat.RaiseWitherLevel();
    }
}

/// <summary>The Magi Knight: Dampen downgrades every upgraded card in the fight until the knight dies.</summary>
public sealed class MagiKnightBehavior : MonsterBehavior
{
    public override IReadOnlyCollection<string> Handled => new[] { "DAMPEN", "MAGIC_BOMB" };

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id == "DAMPEN" && self.State.GetValueOrDefault("dampened") == 0)
        {
            self.State["dampened"] = 1;
            combat.Dampen();
        }
    }

    public override bool OnDeath(Combat combat, Enemy self)
    {
        if (self.State.GetValueOrDefault("dampened") > 0) combat.DampenCasterDied();
        return false;
    }
}

/// <summary>
/// The Lost and The Forgotten (PossessStrengthPower, PossessSpeedPower): each takes 2 Strength (Dexterity) from the player with its debuff
/// and gives it back when it dies. The Forgotten's Dread hits for 13 (15 from Ascension 9) plus its own Dexterity (in MonsterOverrides/Combat).
/// </summary>
public sealed class PossessBehavior : MonsterBehavior
{
    private readonly PowerKind _stat;
    private readonly string _stealMove;

    public PossessBehavior(PowerKind stat, string stealMove) { _stat = stat; _stealMove = stealMove; }

    public override IReadOnlyCollection<string> Handled => new[] { "attack damage not resolved" };

    public override void OnMove(Combat combat, Enemy self, MoveDef move)
    {
        if (move.Id == _stealMove) self.State["stolen"] = self.State.GetValueOrDefault("stolen") + 2;
    }

    public override bool OnDeath(Combat combat, Enemy self)
    {
        combat.PlayerPowers[(int)_stat] += self.State.GetValueOrDefault("stolen");
        self.State["stolen"] = 0;
        return false;
    }
}

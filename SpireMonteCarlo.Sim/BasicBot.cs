namespace SpireMonteCarlo.Sim;

/// <summary>
/// A mid-strength combat player: greedy, but it looks at the enemies' visible intents. Each playable card is scored
/// by what it accomplishes right now (damage after modifiers, kills, block actually needed, debuffs worth applying,
/// buffs, draw, energy) and the best value per energy is played. It does no search and no multi-turn planning, so
/// its absolute win rate will be a bit low; what matters is that it treats every deck the same way.
/// </summary>
public sealed class BasicBot
{
    public const double MinScoreToPlay = 0.75;

    public bool TryChoose(Combat combat, out int handIndex, out int target)
    {
        handIndex = -1;
        target = -1;
        double bestEfficiency = 0;

        int incoming = combat.IncomingDamage();
        int needBlock = Math.Max(0, incoming - combat.Block);
        bool lethalDanger = incoming - combat.Block >= combat.Hp;

        for (int i = 0; i < combat.Hand.Count; i++)
        {
            CardDef card = combat.Hand[i];
            if (!combat.CanPlay(card)) continue;

            int energyAfter = card.Cost == CardDef.XCost ? 0 : combat.Energy - card.Cost;
            double spent = card.Cost == CardDef.XCost ? Math.Max(1, combat.Energy) : Math.Max(0.5, card.Cost);

            foreach (int t in CandidateTargets(combat, card))
            {
                double score = Score(combat, card, i, t, needBlock, lethalDanger, energyAfter);
                double efficiency = score / spent;
                if (score >= MinScoreToPlay && efficiency > bestEfficiency)
                {
                    bestEfficiency = efficiency;
                    handIndex = i;
                    target = t;
                }
            }
        }
        return handIndex >= 0;
    }

    private static IEnumerable<int> CandidateTargets(Combat combat, CardDef card)
    {
        bool single = card.Effects.Any(e => e.Op is EffectOp.Damage or EffectOp.DebuffEnemy or EffectOp.DamageEqualBlock);
        if (!single) { yield return -1; yield break; }
        foreach (Enemy e in combat.Enemies)
            if (e.Alive && !e.Dying) yield return e.Index;
    }

    private double Score(Combat combat, CardDef card, int handIndex, int target, int needBlock, bool lethalDanger, int energyAfter)
    {
        double score = 0;
        Enemy? focus = target >= 0 ? combat.Enemies[target] : null;

        foreach (Effect effect in card.Effects)
        {
            int hits = effect.Hits == -1 ? Math.Max(0, combat.Energy) : effect.Hits;
            switch (effect.Op)
            {
                case EffectOp.Damage when focus != null:
                    score += DamageValue(combat, focus, effect.Amount, hits);
                    break;
                case EffectOp.DamageEqualBlock when focus != null:
                    score += DamageValue(combat, focus, combat.Block, 1);
                    break;
                case EffectOp.DamageAll:
                    foreach (Enemy e in combat.Enemies)
                        if (e.Alive && !e.Dying) score += DamageValue(combat, e, effect.Amount, hits);
                    break;
                case EffectOp.DamageRandom:
                    {
                        var alive = combat.Enemies.Where(e => e.Alive && !e.Dying).ToList();
                        foreach (Enemy e in alive) score += DamageValue(combat, e, effect.Amount, hits) / alive.Count;
                        break;
                    }
                case EffectOp.Block:
                    {
                        int gain = combat.PlayerBlockGain(effect.Amount);
                        int useful = Math.Min(gain, needBlock);
                        score += useful * (lethalDanger ? 3.0 : 1.1) + (gain - useful) * 0.1;
                        break;
                    }
                case EffectOp.Draw:
                    score += effect.Amount * (energyAfter > 0 ? 3.0 : 0.8);
                    break;
                case EffectOp.Energy:
                    score += effect.Amount * (HasPlayableCardLeft(combat, handIndex) ? 3.5 : 0.5);
                    break;
                case EffectOp.LoseHp:
                    {
                        double hpFraction = (double)combat.Hp / combat.MaxHp;
                        score -= effect.Amount * (1 + Math.Max(0, (0.5 - hpFraction) * 4));
                        break;
                    }
                case EffectOp.DebuffEnemy when focus != null:
                    score += DebuffValue(combat, focus, effect, handIndex);
                    break;
                case EffectOp.DebuffAll:
                    foreach (Enemy e in combat.Enemies)
                        if (e.Alive && !e.Dying) score += DebuffValue(combat, e, effect, handIndex);
                    break;
                case EffectOp.DebuffSelf:
                    score -= effect.Amount * 2;
                    break;
                case EffectOp.BuffSelf:
                    score += effect.Power switch
                    {
                        PowerKind.Strength => effect.Amount * 8,
                        PowerKind.Dexterity => effect.Amount * 6,
                        PowerKind.Plating or PowerKind.Metallicize => effect.Amount * 5,
                        _ => effect.Amount * 4,
                    };
                    break;
            }
        }

        // Getting a Beckon/Toxic-style status card out of the hand avoids what it would cost at the end of the turn.
        score += card.EndTurnHpLoss + 0.7 * card.EndTurnDamage;

        if (card.Exhaust && card.Kind != CardKind.Power) score -= 1.5;
        if (card.Approximate && score < 1) score = 1;
        return score;
    }

    /// <summary>HP an enemy would actually lose from these hits, given its block and defensive powers.</summary>
    private static int HpLoss(Enemy enemy, int perHit, int hits)
    {
        int block = enemy.Block, lost = 0;
        int slippery = enemy.Powers[(int)PowerKind.Slippery];
        int shellLeft = enemy.Powers[(int)PowerKind.HardenedShell] > 0 ? enemy.Powers[(int)PowerKind.HardenedShell] - enemy.ShellDamage : int.MaxValue;
        bool intangible = enemy.Powers[(int)PowerKind.Intangible] > 0;
        for (int h = 0; h < hits; h++)
        {
            int d = intangible ? Math.Min(perHit, 1) : perHit;
            int absorbed = Math.Min(block, d);
            block -= absorbed;
            int through = d - absorbed;
            if (through > 0 && slippery > 0) { through = 1; slippery--; }
            through = Math.Max(0, Math.Min(through, shellLeft));
            shellLeft -= through;
            lost += through;
        }
        return lost;
    }

    private static double DamageValue(Combat combat, Enemy enemy, int baseDamage, int hits)
    {
        int perHit = combat.PlayerAttackDamage(baseDamage, enemy);
        int total = perHit * hits;
        int hpDamage = Math.Min(HpLoss(enemy, perHit, hits), enemy.Hp);
        double value = hpDamage + Math.Min(total - hpDamage, enemy.Block) * 0.3;

        double threat = 3 + combat.IntentDamage(enemy);
        // Damage that finishes an enemy also removes everything it would have done later.
        if (hpDamage >= enemy.Hp) value += 4 + threat;
        else value += threat * hpDamage / enemy.Hp * 0.5;

        // Minions don't end the fight; don't spend the turn on them while a real enemy is still up.
        if (!enemy.Primary && combat.Enemies.Any(e => e.Alive && e.Primary)) value *= 0.6;
        return value;
    }

    private static double DebuffValue(Combat combat, Enemy enemy, Effect effect, int handIndex)
    {
        int turns = Math.Min(effect.Amount, 2);
        switch (effect.Power)
        {
            case PowerKind.Vulnerable when enemy.Powers[(int)PowerKind.Vulnerable] == 0:
                {
                    // Extra damage from the other attacks we can still afford this turn, plus a little for later turns.
                    double followUp = 0;
                    int energy = combat.Energy;
                    for (int i = 0; i < combat.Hand.Count; i++)
                    {
                        if (i == handIndex) continue;
                        CardDef other = combat.Hand[i];
                        if (!other.IsAttack || other.Cost > energy || other.Cost < 0) continue;
                        foreach (Effect e in other.Effects)
                            if (e.Op is EffectOp.Damage or EffectOp.DamageAll) followUp += e.Amount * Math.Max(1, e.Hits) * 0.5;
                    }
                    return followUp + turns * 1.5;
                }
            case PowerKind.Weak when enemy.Powers[(int)PowerKind.Weak] == 0:
                {
                    double incoming = combat.IntentDamage(enemy);
                    return incoming * 0.25 * turns + 0.5;
                }
            case PowerKind.Poison:
                return effect.Amount * 2.5;
            default:
                return 0.5;
        }
    }

    /// <summary>True when another paid card is waiting in hand, so extra energy would actually get used.</summary>
    private static bool HasPlayableCardLeft(Combat combat, int excludingHandIndex)
    {
        for (int i = 0; i < combat.Hand.Count; i++)
            if (i != excludingHandIndex && combat.Hand[i].Cost >= 1) return true;
        return false;
    }
}

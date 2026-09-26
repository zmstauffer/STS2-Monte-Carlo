namespace SpireMonteCarlo.Sim;

/// <summary>What a card does when played, written from the game's own card classes (numbers come from the extracted card data).</summary>
internal sealed record Recipe(
    Effect[] Effects,
    int Growth = 0,
    bool CheaperPerAttack = false,
    int EnergyOnExhaust = 0,
    bool FromExhaustPile = false,
    CardTweaks? Tweaks = null);

/// <summary>The things a card does besides its effects when played (end-of-turn drawbacks, returning to hand, limits on other cards).</summary>
internal sealed record CardTweaks(
    bool ReturnsNextTurn = false,
    PowerKind EndTurnPower = PowerKind.Unsupported,
    int EndTurnPowerAmount = 0,
    int EndTurnGold = 0,
    bool RegretLike = false,
    bool OnlyIfHandAllAttacks = false,
    bool MustPlayFirst = false,
    int PlayCap = 0,
    int LoseEnergyWhenDrawn = 0,
    int EndTurnDamage = 0,
    int EndTurnHpLoss = 0);

/// <summary>Reads a card variable at the card's current upgrade level, e.g. <c>v("Damage")</c>.</summary>
internal delegate int Vars(string name);

/// <summary>
/// One recipe per card, in the order the card's OnPlay does things. Cards that exist only in multiplayer are left out.
/// Every recipe cites the decompiled class it was written from (MegaCrit.Sts2.Core.Models.Cards).
/// </summary>
internal static class CardRecipes
{
    private static Effect Damage(int amount, int hits = 1) => new(EffectOp.Damage, amount, hits);
    private static Effect DamageAll(int amount, int hits = 1) => new(EffectOp.DamageAll, amount, hits);
    private static Effect Block(int amount) => new(EffectOp.Block, amount);
    private static Effect Draw(int n) => new(EffectOp.Draw, n);
    private static Effect Vuln(int n) => new(EffectOp.DebuffEnemy, n, Power: PowerKind.Vulnerable);
    private static Effect Self(PowerKind power, int amount) => new(EffectOp.BuffSelf, amount, Power: power);
    private static Effect LoseHp(int n) => new(EffectOp.LoseHp, n);
    private static Effect Simple(EffectOp op, int amount = 0) => new(op, amount);
    private static Effect Special(SpecialEffect kind, int amount = 0) => new(EffectOp.Special, amount, Arg: (int)kind);

    public static Recipe? Get(string id, bool upgraded, Vars v)
    {
        Effect[]? effects = Effects(id, upgraded, v);
        if (effects == null) return null;
        return id switch
        {
            "RAMPAGE" => new Recipe(effects, Growth: v("Increase")),
            "STOMP" => new Recipe(effects, CheaperPerAttack: true),
            "DRUM_OF_BATTLE" => new Recipe(effects, EnergyOnExhaust: v("Energy")),
            "HOWL_FROM_BEYOND" => new Recipe(effects, FromExhaustPile: true),
            "BOLAS" or "THRUMMING_HATCHET" => new Recipe(effects, Tweaks: new CardTweaks(ReturnsNextTurn: true)),
            "CLASH" => new Recipe(effects, Tweaks: new CardTweaks(OnlyIfHandAllAttacks: true)),
            "BAD_LUCK" => new Recipe(effects, Tweaks: new CardTweaks(EndTurnHpLoss: v("HpLoss"))),
            "DEBT" => new Recipe(effects, Tweaks: new CardTweaks(EndTurnGold: v("Gold"))),
            "DECAY" or "BURN" or "INFECTION" or "TOXIC" or "WITHER" => new Recipe(effects, Tweaks: new CardTweaks(EndTurnDamage: v("Damage"))),
            "BECKON" => new Recipe(effects, Tweaks: new CardTweaks(EndTurnHpLoss: v("HpLoss"))),
            "DOUBT" => new Recipe(effects, Tweaks: new CardTweaks(EndTurnPower: PowerKind.Weak, EndTurnPowerAmount: v("WeakPower"))),
            "SHAME" => new Recipe(effects, Tweaks: new CardTweaks(EndTurnPower: PowerKind.Frail, EndTurnPowerAmount: v("Frail"))),
            "ENTHRALLED" => new Recipe(effects, Tweaks: new CardTweaks(MustPlayFirst: true)),
            "NORMALITY" => new Recipe(effects, Tweaks: new CardTweaks(PlayCap: v("CalculationBase"))),
            "REGRET" => new Recipe(effects, Tweaks: new CardTweaks(RegretLike: true)),
            "VOID" => new Recipe(effects, Tweaks: new CardTweaks(LoseEnergyWhenDrawn: v("Energy"))),
            _ => new Recipe(effects),
        };
    }

    private static Effect[]? Effects(string id, bool up, Vars v)
    {
        switch (id)
        {
            // ---- basic
            case "STRIKE_IRONCLAD": return new[] { Damage(v("Damage")) };
            case "DEFEND_IRONCLAD": return new[] { Block(v("Block")) };
            case "BASH": return new[] { Damage(v("Damage")), Vuln(v("VulnerablePower")) };

            // ---- common
            case "ANGER": return new[] { Damage(v("Damage")), Simple(EffectOp.CopyToDiscard) };
            case "ARMAMENTS": return new[] { Block(v("Block")), Simple(up ? EffectOp.UpgradeAllInHand : EffectOp.UpgradeChosenInHand) };
            case "BLOOD_WALL": return new[] { LoseHp(v("HpLoss")), Block(v("Block")) };
            case "BODY_SLAM": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.Block, Per: v("ExtraDamage")) };
            case "BREAKTHROUGH": return new[] { LoseHp(v("HpLoss")), DamageAll(v("Damage")) };
            case "CINDER": return new[] { Damage(v("Damage")), Simple(EffectOp.ExhaustRandomFromHand) };
            case "HAVOC": return new[] { new Effect(EffectOp.PlayTopOfDraw, 1, Hits: 1) };
            case "HEADBUTT": return new[] { Damage(v("Damage")), Simple(EffectOp.DiscardToDrawTop) };
            case "IRON_WAVE": return new[] { Block(v("Block")), Damage(v("Damage")) };
            case "MOLTEN_FIST": return new[] { Damage(v("Damage")), new Effect(EffectOp.DoubleDebuff, 0, Power: PowerKind.Vulnerable) };
            case "PERFECTED_STRIKE": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.StrikeCards, Per: v("ExtraDamage")) };
            case "POMMEL_STRIKE": return new[] { Damage(v("Damage")), Draw(v("Cards")) };
            case "SETUP_STRIKE": return new[] { Damage(v("Damage")), Self(PowerKind.TempStrength, v("StrengthPower")) };
            case "SHRUG_IT_OFF": return new[] { Block(v("Block")), Draw(v("Cards")) };
            case "SWORD_BOOMERANG": return new[] { new Effect(EffectOp.DamageRandom, v("Damage"), Hits: v("Repeat")) };
            case "THUNDERCLAP": return new[] { DamageAll(v("Damage")), new Effect(EffectOp.DebuffAll, v("VulnerablePower"), Power: PowerKind.Vulnerable) };
            case "TREMBLE": return new[] { Vuln(v("VulnerablePower")) };
            case "TRUE_GRIT": return new[] { Block(v("Block")), Simple(up ? EffectOp.ExhaustChosenFromHand : EffectOp.ExhaustRandomFromHand) };
            case "TWIN_STRIKE": return new[] { Damage(v("Damage"), 2) };

            // ---- uncommon
            case "ASHEN_STRIKE": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.ExhaustPileCount, Per: v("ExtraDamage")) };
            case "BATTLE_TRANCE": return new[] { Draw(v("Cards")), new Effect(EffectOp.DebuffSelf, 1, Power: PowerKind.NoDraw) };
            case "BLOODLETTING": return new[] { LoseHp(v("HpLoss")), Simple(EffectOp.Energy, v("Energy")) };
            case "BLUDGEON": return new[] { Damage(v("Damage")) };
            case "BULLY": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.TargetVulnerable, Per: v("ExtraDamage")) };
            case "BURNING_PACT": return new[] { Simple(EffectOp.ExhaustChosenFromHand), Draw(v("Cards")) };
            case "COLOSSUS": return new[] { Block(v("Block")), Self(PowerKind.Colossus, v("Colossus")) };
            case "CRUELTY": return new[] { Self(PowerKind.Cruelty, v("CrueltyPower")) };
            case "DISMANTLE": return new[] { new Effect(EffectOp.Damage, v("Damage"), Hits: 1, HitsSource: Source.TargetIsVulnerable, HitsPer: 1) };
            case "DRUM_OF_BATTLE": return new[] { Draw(v("Cards")) };
            case "EVIL_EYE": return new[] { new Effect(EffectOp.Block, v("Block"), Hits: 1, HitsSource: Source.ExhaustedThisTurn, HitsPer: 1) };
            case "EXPECT_A_FIGHT": return new[] { new Effect(EffectOp.Block, v("CalculationBase"), AmountSource: Source.Strength, Per: v("CalculationExtra")) };
            case "FEEL_NO_PAIN": return new[] { Self(PowerKind.FeelNoPain, v("Power")) };
            case "FIGHT_ME":
                return new[]
                {
                    Damage(v("Damage"), v("Repeat")),
                    Self(PowerKind.Strength, v("StrengthPower")),
                    new Effect(EffectOp.BuffEnemy, v("EnemyStrength"), Power: PowerKind.Strength),
                };
            case "FLAME_BARRIER": return new[] { Block(v("Block")), Self(PowerKind.FlameBarrier, v("DamageBack")) };
            case "FORGOTTEN_RITUAL": return new[] { Simple(EffectOp.Energy, v("Energy")) };
            case "HEMOKINESIS": return new[] { LoseHp(v("HpLoss")), Damage(v("Damage")) };
            case "HOWL_FROM_BEYOND": return new[] { DamageAll(v("Damage")) };
            case "INFERNAL_BLADE": return new[] { Simple(EffectOp.GenerateFreeAttack, 1) };
            case "INFERNO": return new[] { Self(PowerKind.Inferno, v("InfernoPower")), Self(PowerKind.InfernoSelfDamage, 1) };
            case "INFLAME": return new[] { Self(PowerKind.Strength, v("StrengthPower")) };
            case "JUGGLING": return new[] { Self(PowerKind.Juggling, 1) };
            case "PILLAGE": return new[] { Damage(v("Damage")), Simple(EffectOp.DrawUntilNonAttack) };
            case "RAGE": return new[] { Self(PowerKind.Rage, v("Power")) };
            case "RAMPAGE": return new[] { Damage(v("Damage")) };
            case "RUPTURE": return new[] { Self(PowerKind.Rupture, v("StrengthPower")) };
            case "SECOND_WIND": return new[] { Simple(EffectOp.ExhaustNonAttacksForBlock, v("Block")) };
            case "SPITE": return new[] { new Effect(EffectOp.Damage, v("Damage"), Hits: 1, HitsSource: Source.LostHpThisTurn, HitsPer: v("Repeat") - 1) };
            case "STAMPEDE": return new[] { Self(PowerKind.Stampede, v("Power")) };
            case "STOMP": return new[] { DamageAll(v("Damage")) };
            case "STONE_ARMOR": return new[] { Self(PowerKind.Plating, v("PlatingPower")) };
            case "TAUNT": return new[] { Block(v("Block")), Vuln(v("VulnerablePower")) };
            case "UNRELENTING": return new[] { Damage(v("Damage")), Self(PowerKind.FreeAttack, 1) };
            case "UPPERCUT":
                return new[]
                {
                    Damage(v("Damage")),
                    new Effect(EffectOp.DebuffEnemy, v("Power"), Power: PowerKind.Weak),
                    Vuln(v("Power")),
                };
            case "VICIOUS": return new[] { Self(PowerKind.Vicious, v("Cards")) };
            case "WHIRLWIND": return new[] { new Effect(EffectOp.DamageAll, v("Damage"), Hits: 0, HitsSource: Source.X, HitsPer: 1) };

            // ---- rare and ancient
            case "AGGRESSION": return new[] { Self(PowerKind.Aggression, 1) };
            case "BARRICADE": return new[] { Self(PowerKind.Barricade, 1) };
            case "BRAND": return new[] { LoseHp(v("HpLoss")), Simple(EffectOp.ExhaustChosenFromHand), Self(PowerKind.Strength, v("StrengthPower")) };
            case "BREAK": return new[] { Damage(v("Damage")), Vuln(v("VulnerablePower")) };
            case "CASCADE": return new[] { new Effect(EffectOp.PlayTopOfDraw, up ? 1 : 0, Hits: 0, AmountSource: Source.X, Per: 1) };
            case "CONFLAGRATION": return new[] { DamageAll(v("Damage"), v("Repeat")) };
            case "CORRUPTION": return new[] { Self(PowerKind.Corruption, v("Power")) };
            case "CRIMSON_MANTLE": return new[] { Self(PowerKind.CrimsonMantle, v("CrimsonMantlePower")), Self(PowerKind.CrimsonSelfDamage, 1) };
            case "DARK_EMBRACE": return new[] { Self(PowerKind.DarkEmbrace, 1) };
            case "DEMON_FORM": return new[] { Self(PowerKind.DemonForm, v("StrengthPower")) };
            case "DOMINATE": return new[] { Vuln(v("VulnerablePower")), Simple(EffectOp.StrengthFromVulnerable) };
            case "FEED": return new[] { Damage(v("Damage")), Simple(EffectOp.GainMaxHpIfFatal, v("MaxHp")) };
            case "FIEND_FIRE": return new[] { Simple(EffectOp.ExhaustHand), new Effect(EffectOp.Damage, v("Damage"), Hits: 0, HitsSource: Source.HandExhausted, HitsPer: 1) };
            case "HELLRAISER": return new[] { Self(PowerKind.Hellraiser, 1) };
            case "IMPERVIOUS": return new[] { Block(v("Block")) };
            case "JUGGERNAUT": return new[] { Self(PowerKind.Juggernaut, v("JuggernautPower")) };
            case "MANGLE": return new[] { Damage(v("Damage")), new Effect(EffectOp.DebuffEnemy, v("StrengthLoss"), Power: PowerKind.TempStrengthDown) };
            case "NOT_YET": return new[] { Simple(EffectOp.Heal, v("Heal")) };
            case "OFFERING": return new[] { LoseHp(v("HpLoss")), Simple(EffectOp.Energy, v("Energy")), Draw(v("Cards")) };
            case "ONE_TWO_PUNCH": return new[] { Self(PowerKind.OneTwoPunch, v("Attacks")) };
            case "PACTS_END": return new[] { new Effect(EffectOp.DamageAll, v("Damage"), Hits: 0, HitsSource: Source.ExhaustAtLeast, HitsPer: 1, Arg: v("Cards")) };
            case "PRIMAL_FORCE": return new[] { new Effect(EffectOp.TransformAttacks, 0, Hits: up ? 1 : 0) };
            case "PYRE": return new[] { Self(PowerKind.Pyre, v("Energy")) };
            case "STOKE": return new[] { Simple(EffectOp.ExhaustHand), new Effect(EffectOp.GenerateForExhausted, 0, Hits: up ? 1 : 0) };
            case "TEAR_ASUNDER": return new[] { new Effect(EffectOp.Damage, v("Damage"), Hits: 0, HitsSource: Source.TimesHurtPlusOne, HitsPer: 1) };
            case "THRASH": return new[] { Damage(v("Damage"), 2), Simple(EffectOp.AbsorbRandomAttack) };
            case "UNMOVABLE": return new[] { Self(PowerKind.Unmovable, 1) };

            // ---- tokens made by Ironclad cards
            case "GIANT_ROCK": return new[] { Damage(v("Damage")) };

            // ---- colorless cards (MegaCrit.Sts2.Core.Models.Cards; the multiplayer-only ones are left out)
            case "ALCHEMIZE": return new[] { Special(SpecialEffect.ProcurePotion) };
            case "ANOINTED": return new[] { Special(SpecialEffect.AnointedRares) };
            case "AUTOMATION": return new[] { Self(PowerKind.Automation, v("Energy")) };
            case "BEAT_DOWN": return new[] { Special(SpecialEffect.BeatDown, v("Cards")) };
            case "BOLAS": return new[] { Damage(v("Damage")) };
            case "CALAMITY": return new[] { Self(PowerKind.Calamity, 1) };
            case "CATASTROPHE": return new[] { new Effect(EffectOp.PlayTopOfDraw, v("Cards"), Hits: 0) };
            case "DARK_SHACKLES": return new[] { new Effect(EffectOp.DebuffEnemy, v("StrengthLoss"), Power: PowerKind.TempStrengthDown) };
            case "DISCOVERY": return new[] { Special(SpecialEffect.Discovery) };
            case "DRAMATIC_ENTRANCE": return new[] { DamageAll(v("Damage")) };
            case "ENTROPY": return new[] { Self(PowerKind.Entropy, v("Cards")) };
            case "EQUILIBRIUM": return new[] { Block(v("Block")), Special(SpecialEffect.RetainHandThisTurn) };
            case "ETERNAL_ARMOR": return new[] { Self(PowerKind.Plating, v("PlatingPower")) };
            case "FASTEN": return new[] { Self(PowerKind.Fasten, v("ExtraBlock")) };
            case "FINESSE": return new[] { Block(v("Block")), Draw(v("Cards")) };
            case "FISTICUFFS": return new[] { Damage(v("Damage")), Special(SpecialEffect.BlockFromDamage) };
            case "FLASH_OF_STEEL": return new[] { Damage(v("Damage")), Draw(v("Cards")) };
            case "GOLD_AXE": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.CardsPlayedInCombat, Per: v("ExtraDamage")) };
            case "HAND_OF_GREED": return new[] { Damage(v("Damage")), Special(SpecialEffect.GoldIfFatal, v("Gold")) };
            case "HIDDEN_GEM": return new[] { Special(SpecialEffect.HiddenGem, v("Replay")) };
            case "IMPATIENCE": return new[] { Special(SpecialEffect.ImpatienceDraw, v("Cards")) };
            case "JACKPOT": return new[] { Damage(v("Damage")), Special(SpecialEffect.Jackpot, v("Cards")) };
            case "JACK_OF_ALL_TRADES": return new[] { Special(SpecialEffect.JackOfAllTrades) };
            case "MASTER_OF_STRATEGY": return new[] { Draw(v("Cards")) };
            case "MAYHEM": return new[] { Self(PowerKind.Mayhem, 1) };
            case "MIND_BLAST": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.DrawPileCount, Per: v("ExtraDamage")) };
            case "NOSTALGIA": return new[] { Self(PowerKind.Nostalgia, 1) };
            case "OMNISLICE": return new[] { Damage(v("Damage")), Special(SpecialEffect.OmnisliceSpread) };
            case "PANACHE": return new[] { Self(PowerKind.Panache, v("PanacheDamage")) };
            case "PANIC_BUTTON": return new[] { Block(v("Block")), Special(SpecialEffect.PanicButton, v("Turns")) };
            case "PREP_TIME": return new[] { Self(PowerKind.PrepTime, v("PrepTimePower")) };
            case "PRODUCTION": return new[] { Simple(EffectOp.Energy, v("Energy")) };
            case "PROLONG": return new[] { Special(SpecialEffect.Prolong) };
            case "PROWESS": return new[] { Self(PowerKind.Strength, v("StrengthPower")), Self(PowerKind.Dexterity, v("DexterityPower")) };
            case "PURITY": return new[] { Special(SpecialEffect.Purity, v("Cards")) };
            case "REND": return new[] { new Effect(EffectOp.Damage, v("CalculationBase"), AmountSource: Source.TargetDebuffs, Per: v("ExtraDamage")) };
            case "RESTLESSNESS": return new[] { new Effect(EffectOp.Special, v("Cards"), Hits: v("Energy"), Arg: (int)SpecialEffect.RestlessnessDraw) };
            case "ROLLING_BOULDER": return new[] { Self(PowerKind.RollingBoulder, v("RollingBoulderPower")) };
            case "SALVO": return new[] { Damage(v("Damage")), Special(SpecialEffect.RetainHandThisTurn) };
            case "SCRAWL": return new[] { Special(SpecialEffect.ScrawlDraw) };
            case "SECRET_TECHNIQUE": return new[] { Special(SpecialEffect.SecretTechnique) };
            case "SECRET_WEAPON": return new[] { Special(SpecialEffect.SecretWeapon) };
            case "SEEKER_STRIKE": return new[] { Damage(v("Damage")), Special(SpecialEffect.SeekerStrike, v("Cards")) };
            case "SHOCKWAVE": return new[] { new Effect(EffectOp.DebuffAll, v("Power"), Power: PowerKind.Weak), new Effect(EffectOp.DebuffAll, v("Power"), Power: PowerKind.Vulnerable) };
            case "SPLASH": return new[] { Simple(EffectOp.GenerateFreeAttack, 1) };   // the game offers another characters Attacks; this gives one of the players own
            case "STRATAGEM": return new[] { Self(PowerKind.Stratagem, 1) };
            case "THE_BOMB": return new[] { new Effect(EffectOp.Special, v("Turns"), Hits: v("BombDamage"), Arg: (int)SpecialEffect.TheBomb) };
            case "THE_GAMBIT": return new[] { Block(v("Block")), Special(SpecialEffect.Gambit) };
            case "THINKING_AHEAD": return new[] { Special(SpecialEffect.ThinkingAhead, v("Cards")) };
            case "THRUMMING_HATCHET": return new[] { Damage(v("Damage")) };
            case "ULTIMATE_DEFEND": return new[] { Block(v("Block")) };
            case "ULTIMATE_STRIKE": return new[] { Damage(v("Damage")) };
            case "VOLLEY": return new[] { new Effect(EffectOp.DamageRandom, v("Damage"), Hits: 0, HitsSource: Source.X, HitsPer: 1) };

            // ---- event and Ancient cards
            case "APOTHEOSIS": return new[] { Special(SpecialEffect.Apotheosis) };
            case "APPARITION": return new[] { Self(PowerKind.Intangible, v("IntangiblePower")) };
            case "BRIGHTEST_FLAME": return new[] { Simple(EffectOp.Energy, v("Energy")), Draw(v("Cards")), Special(SpecialEffect.BrightestFlame, v("MaxHp")) };
            case "BYRD_SWOOP": return new[] { Damage(v("Damage")) };
            case "CALTROPS": return new[] { Self(PowerKind.Thorns, v("ThornsPower")) };
            case "CLASH": return new[] { Damage(v("Damage")) };
            case "DISTRACTION": return new[] { Special(SpecialEffect.Distraction) };
            case "DUAL_WIELD": return new[] { Special(SpecialEffect.DualWield, v("Cards")) };
            case "ENLIGHTENMENT": return new[] { Special(SpecialEffect.Enlightenment) };
            case "ENTRENCH": return new[] { Simple(EffectOp.DoubleBlock) };
            case "EXTERMINATE": return new[] { DamageAll(v("Damage"), v("Repeat")) };
            case "FEEDING_FRENZY": return new[] { Self(PowerKind.TempStrength, v("StrengthPower")) };
            case "HELLO_WORLD": return new[] { Self(PowerKind.HelloWorld, 1) };
            case "MAD_SCIENCE": return new[] { Damage(v("Damage")) };   // the Tinker Time event makes it an Attack, Skill or Power with a rider; the plain Attack is modelled
            case "MAUL": return new[] { new Effect(EffectOp.Damage, v("Damage"), Hits: 2, AmountSource: Source.MaulPlays, Per: v("Increase")) };
            case "METAMORPHOSIS": return new[] { Special(SpecialEffect.Metamorphosis, v("Cards")) };
            case "NEOWS_FURY": return new[] { Damage(v("Damage")), Special(SpecialEffect.NeowsFury, v("Cards")) };
            case "OUTMANEUVER": return new[] { Special(SpecialEffect.Outmaneuver, v("Energy")) };
            case "PECK": return new[] { Damage(v("Damage"), v("Repeat")) };
            case "REBOUND": return new[] { Damage(v("Damage")), Special(SpecialEffect.Rebound) };
            case "RELAX": return new[] { Block(v("Block")), new Effect(EffectOp.Special, v("Cards"), Hits: v("Energy"), Arg: (int)SpecialEffect.Relax) };
            case "RIP_AND_TEAR": return new[] { new Effect(EffectOp.DamageRandom, v("Damage"), Hits: 2) };
            case "SQUASH": return new[] { Damage(v("Damage")), Vuln(v("VulnerablePower")) };
            case "STACK": return new[] { new Effect(EffectOp.Block, v("CalculationBase"), AmountSource: Source.DiscardPileCount, Per: v("CalculationExtra")) };
            case "TORIC_TOUGHNESS": return new[] { Block(v("Block")), new Effect(EffectOp.Special, v("Block"), Hits: v("Turns"), Arg: (int)SpecialEffect.TorcToughness) };
            case "WHISTLE": return new[] { Damage(v("Damage")), Special(SpecialEffect.Stun) };
            case "WISH": return new[] { Special(SpecialEffect.Wish) };

            // ---- curses, statuses and quest cards (mostly drawbacks that come from being in hand)
            case "ASCENDERS_BANE" or "CLUMSY" or "CURSE_OF_THE_BELL" or "FOLLY" or "GREED" or "INJURY" or "POOR_SLEEP" or "WRITHE" or "SPORE_MIND" or "GUILTY":
            case "BAD_LUCK" or "DEBT" or "DECAY" or "DOUBT" or "ENTHRALLED" or "NORMALITY" or "REGRET" or "SHAME":
            case "BURN" or "DAZED" or "DEBRIS" or "INFECTION" or "SOOT" or "TOXIC" or "VOID" or "WITHER" or "WOUND" or "BECKON":
            case "BYRDONIS_EGG" or "LANTERN_KEY" or "SPOILS_MAP":
                return Array.Empty<Effect>();
            case "SLIMED": return new[] { Draw(v("Cards")) };

            // ---- tokens
            case "FUEL": return new[] { Simple(EffectOp.Energy, v("Energy")), Draw(1) };
            case "LUMINESCE": return new[] { Simple(EffectOp.Energy, v("Energy")) };
            case "SHIV": return new[] { Damage(v("Damage")) };
            case "SOUL": return new[] { Draw(v("Cards")) };
            case "MINION_DIVE_BOMB": return new[] { Damage(v("Damage")) };
            case "MINION_SACRIFICE": return new[] { Block(v("Block")) };
            case "MINION_STRIKE": return new[] { Damage(v("Damage")), Draw(v("Cards")) };
            default: return null;
        }
    }
}

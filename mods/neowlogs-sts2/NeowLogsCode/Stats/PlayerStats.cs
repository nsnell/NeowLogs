namespace NeowLogs.NeowLogsCode;

public sealed class PlayerStats
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string? ManualName { get; set; }
    public double Damage { get; set; }
    public double DirectDamage { get; set; }
    public double DamageAssist { get; set; }
    public double PoisonDamage { get; set; }
    public double CompanionDamage { get; set; }
    public double UtilityDamage { get; set; }
    public double Block { get; set; }
    public double CompanionBlock { get; set; }
    public double DamageBlocked { get; set; }
    public double DamageTaken { get; set; }
    public double PreventedDamage { get; set; }
    public double Healing { get; set; }
    public double CardsDrawn { get; set; }
    public double EnergyGiven { get; set; }
    public int CardsPlayed { get; set; }
    public int AttackCardsPlayed { get; set; }
    public int SkillCardsPlayed { get; set; }
    public int PowerCardsPlayed { get; set; }
    public double EnergySpent { get; set; }
    public int UtilityEvents { get; set; }
    public Dictionary<string, int> Statuses { get; } = new();

    public double DamageContribution => Damage + DamageAssist;
    public double DamageBarTotal => DirectDamage + DamageAssist + PoisonDamage + CompanionDamage;
    public double BlockContribution => Block + PreventedDamage + CompanionBlock;
    public double UtilityScore => DamageAssist + PreventedDamage;

    public double Contribution => DamageContribution + BlockContribution + Healing * 2;
}

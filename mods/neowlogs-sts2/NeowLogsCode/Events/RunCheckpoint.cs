using System.Text.Json.Serialization;

namespace NeowLogs.NeowLogsCode.Events;

public sealed class RunCheckpoint
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = "";

    [JsonPropertyName("run_key")]
    public string RunKey { get; set; } = "";

    [JsonPropertyName("log_path")]
    public string LogPath { get; set; } = "";

    [JsonPropertyName("act")]
    public int? Act { get; set; }

    [JsonPropertyName("floor")]
    public int? Floor { get; set; }

    [JsonPropertyName("turn")]
    public int? Turn { get; set; }

    [JsonPropertyName("combat_id")]
    public string? CombatId { get; set; }

    [JsonPropertyName("updated_at_utc")]
    public DateTime UpdatedAtUtc { get; set; }

    [JsonPropertyName("stats")]
    public StatsCheckpoint Stats { get; set; } = new();
}

public sealed class StatsCheckpoint
{
    [JsonPropertyName("combat_id")]
    public string? CombatId { get; set; }

    [JsonPropertyName("players")]
    public List<PlayerStatsCheckpoint> Players { get; set; } = [];

    [JsonPropertyName("vulnerable_by_target")]
    public List<UtilitySetupCheckpoint> VulnerableByTarget { get; set; } = [];

    [JsonPropertyName("damage_reduction_by_source")]
    public List<UtilitySetupCheckpoint> DamageReductionBySource { get; set; } = [];

    [JsonPropertyName("indirect_damage_by_target")]
    public List<UtilitySetupCheckpoint> IndirectDamageByTarget { get; set; } = [];

    [JsonPropertyName("source_owner_by_name")]
    public List<UtilitySetupCheckpoint> SourceOwnerByName { get; set; } = [];
}

public sealed class PlayerStatsCheckpoint
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("manual_name")]
    public string? ManualName { get; set; }

    [JsonPropertyName("damage")]
    public double Damage { get; set; }

    [JsonPropertyName("direct_damage")]
    public double DirectDamage { get; set; }

    [JsonPropertyName("damage_assist")]
    public double DamageAssist { get; set; }

    [JsonPropertyName("poison_damage")]
    public double PoisonDamage { get; set; }

    [JsonPropertyName("companion_damage")]
    public double CompanionDamage { get; set; }

    [JsonPropertyName("utility_damage")]
    public double UtilityDamage { get; set; }

    [JsonPropertyName("block")]
    public double Block { get; set; }

    [JsonPropertyName("companion_block")]
    public double CompanionBlock { get; set; }

    [JsonPropertyName("damage_blocked")]
    public double DamageBlocked { get; set; }

    [JsonPropertyName("damage_taken")]
    public double DamageTaken { get; set; }

    [JsonPropertyName("prevented_damage")]
    public double PreventedDamage { get; set; }

    [JsonPropertyName("healing")]
    public double Healing { get; set; }

    [JsonPropertyName("cards_drawn")]
    public double CardsDrawn { get; set; }

    [JsonPropertyName("energy_given")]
    public double EnergyGiven { get; set; }

    [JsonPropertyName("cards_played")]
    public int CardsPlayed { get; set; }

    [JsonPropertyName("attack_cards_played")]
    public int AttackCardsPlayed { get; set; }

    [JsonPropertyName("skill_cards_played")]
    public int SkillCardsPlayed { get; set; }

    [JsonPropertyName("power_cards_played")]
    public int PowerCardsPlayed { get; set; }

    [JsonPropertyName("energy_spent")]
    public double EnergySpent { get; set; }

    [JsonPropertyName("utility_events")]
    public int UtilityEvents { get; set; }

    [JsonPropertyName("statuses")]
    public Dictionary<string, int> Statuses { get; set; } = new();
}

public sealed class UtilitySetupCheckpoint
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("player_id")]
    public string PlayerId { get; set; } = "";

    [JsonPropertyName("player_name")]
    public string PlayerName { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("stacks")]
    public double Stacks { get; set; }

    [JsonPropertyName("applied_turn")]
    public int? AppliedTurn { get; set; }
}

using System.Text.Json.Serialization;

namespace NeowLogs.NeowLogsCode.Events;

public sealed class LogEvent
{
    // Schema 2 adds: real per-turn values, turn_started/creature_died events, corrected
    // base_amount (pre-amplification), vulnerable_bonus, and resolved amplified_by rows.
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 2;

    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = "";

    [JsonPropertyName("combat_id")]
    public string? CombatId { get; init; }

    [JsonPropertyName("timestamp_ms")]
    public long TimestampMs { get; init; }

    [JsonPropertyName("act")]
    public int? Act { get; init; }

    [JsonPropertyName("floor")]
    public int? Floor { get; init; }

    [JsonPropertyName("turn")]
    public int? Turn { get; init; }

    [JsonPropertyName("actor_player_id")]
    public string? ActorPlayerId { get; init; }

    [JsonPropertyName("actor_name")]
    public string? ActorName { get; init; }

    [JsonPropertyName("target_id")]
    public string? TargetId { get; init; }

    [JsonPropertyName("target_name")]
    public string? TargetName { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = "";

    [JsonPropertyName("amount")]
    public double? Amount { get; init; }

    [JsonPropertyName("base_amount")]
    public double? BaseAmount { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("source_name")]
    public string? SourceName { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; init; } = new();
}


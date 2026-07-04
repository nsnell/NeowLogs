using NeowLogs.NeowLogsCode.Events;

namespace NeowLogs.NeowLogsCode;

public sealed class StatsAccumulator
{
    private static readonly HashSet<string> UtilityStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vulnerable", "debilitate", "weak", "frail", "lock_on", "mark", "doom", "strength", "strength_down", "artifact_strip"
    };

    private readonly Dictionary<string, PlayerStats> _players = new();
    private readonly AttributionEngine _attribution = new();
    private string? _currentCombatId;

    public IReadOnlyCollection<PlayerStats> Players => _players.Values;

    public void Reset()
    {
        _players.Clear();
        _attribution.Reset();
        _currentCombatId = null;
    }

    public StatsCheckpoint CreateCheckpoint()
    {
        var checkpoint = new StatsCheckpoint
        {
            CombatId = _currentCombatId,
            Players = _players.Values.Select(ToCheckpoint).ToList()
        };
        _attribution.FillCheckpoint(checkpoint);
        return checkpoint;
    }

    public void Restore(StatsCheckpoint checkpoint)
    {
        Reset();
        _currentCombatId = checkpoint.CombatId;
        foreach (var player in checkpoint.Players)
        {
            var restored = new PlayerStats
            {
                Id = player.Id,
                Name = player.Name,
                ManualName = player.ManualName,
                Damage = player.Damage,
                DirectDamage = player.DirectDamage,
                DamageAssist = player.DamageAssist,
                PoisonDamage = player.PoisonDamage,
                CompanionDamage = player.CompanionDamage,
                UtilityDamage = player.UtilityDamage,
                Block = player.Block,
                CompanionBlock = player.CompanionBlock,
                DamageBlocked = player.DamageBlocked,
                DamageTaken = player.DamageTaken,
                PreventedDamage = player.PreventedDamage,
                Healing = player.Healing,
                CardsDrawn = player.CardsDrawn,
                EnergyGiven = player.EnergyGiven,
                CardsPlayed = player.CardsPlayed,
                AttackCardsPlayed = player.AttackCardsPlayed,
                SkillCardsPlayed = player.SkillCardsPlayed,
                PowerCardsPlayed = player.PowerCardsPlayed,
                EnergySpent = player.EnergySpent,
                UtilityEvents = player.UtilityEvents
            };

            foreach (var status in player.Statuses)
            {
                restored.Statuses[status.Key] = status.Value;
            }

            if (!string.IsNullOrWhiteSpace(restored.Id))
            {
                _players[restored.Id] = restored;
            }
        }

        _attribution.Restore(checkpoint);
    }

    public void StartCombat(string combatId)
    {
        if (!string.IsNullOrWhiteSpace(combatId) && string.Equals(_currentCombatId, combatId, StringComparison.Ordinal))
        {
            return;
        }

        _currentCombatId = combatId;
        _attribution.ResetCombat();
    }

    public void Consume(LogEvent ev)
    {
        switch (ev.EventType)
        {
            case "damage_observed":
                ConsumeDamageObserved(ev);
                break;
            case "damage_dealt":
                ConsumeDamageDealt(ev);
                break;
            case "block_gained":
                if (TryGetActor(ev, out var actor))
                {
                    actor.Block += ev.Amount ?? 0;
                }
                break;
            case "damage_taken":
            case "hp_lost":
                ConsumeDamageTaken(ev);
                break;
            case "healing_done":
                if (ev.Amount is null or <= 0 || Bool(ev.Metadata, "initial_heal"))
                {
                    return;
                }
                if (TryGetActor(ev, out actor))
                {
                    actor.Healing += ev.Amount ?? 0;
                }
                break;
            case "card_played":
                ConsumeCardPlayed(ev);
                break;
            case "card_drawn":
                if (TryGetActor(ev, out actor))
                {
                    actor.CardsDrawn += ev.Amount ?? 1;
                }
                break;
            case "energy_gained":
                if (TryGetActor(ev, out actor))
                {
                    actor.EnergyGiven += ev.Amount ?? 0;
                }
                break;
            case "buff_applied":
            case "debuff_applied":
            case "power_applied":
                ConsumePowerApplied(ev);
                break;
            case "potion_used":
            case "relic_triggered":
                if (TryGetActor(ev, out actor))
                {
                    actor.UtilityEvents += 1;
                }
                break;
            case "player_renamed":
                RenamePlayer(Text(ev.Metadata, "player_id"), Text(ev.Metadata, "display_name"));
                break;
        }
    }

    private void ConsumeDamageObserved(LogEvent ev)
    {
        if (!_attribution.IsPromotableObservedDamage(ev) || AttributionEngine.IsPlayerTarget(ev))
        {
            return;
        }

        _attribution.ObserveCanonicalDamage(ev);
        if (TryGetActor(ev, out var actor))
        {
            RecordDamage(actor, ev);
            return;
        }

        if (_attribution.TryResolveSourceOwner(ev, out var owner))
        {
            RecordDamage(GetPlayer(owner.PlayerId, owner.PlayerName), ev);
            return;
        }

        if (_attribution.TryApplyIndirectDamage(ev, ApplyIndirectDamage))
        {
            return;
        }

        if (_attribution.TryResolveGenericPowerProcOwner(ev, out owner))
        {
            RecordDamage(GetPlayer(owner.PlayerId, owner.PlayerName), ev);
        }
    }

    private void ConsumeDamageDealt(LogEvent ev)
    {
        if (AttributionEngine.IsPlayerTarget(ev))
        {
            return;
        }

        if (TryGetActor(ev, out var actor))
        {
            _attribution.ObserveCanonicalDamage(ev);
            RecordDamage(actor, ev);
            return;
        }

        if (_attribution.TryResolveSourceOwner(ev, out var owner))
        {
            RecordDamage(GetPlayer(owner.PlayerId, owner.PlayerName), ev);
            return;
        }

        if (_attribution.TryApplyIndirectDamage(ev, ApplyIndirectDamage))
        {
            return;
        }

        if (_attribution.TryResolveGenericPowerProcOwner(ev, out owner))
        {
            RecordDamage(GetPlayer(owner.PlayerId, owner.PlayerName), ev);
        }
    }

    private void ConsumeDamageTaken(LogEvent ev)
    {
        if (!TryGetActor(ev, out var actor))
        {
            return;
        }

        var blocked = Number(ev.Metadata, "blocked_damage");
        actor.DamageTaken += ev.Amount ?? 0;
        actor.DamageBlocked += blocked;
        if (Bool(ev.Metadata, "pet_damage_absorbed"))
        {
            actor.CompanionBlock += blocked;
        }

        _attribution.ApplyPreventedDamage(ev, (player, credit) =>
        {
            var helper = GetPlayer(player.PlayerId, player.PlayerName);
            helper.PreventedDamage += credit;
            helper.UtilityDamage += credit;
        });
    }

    private void ConsumeCardPlayed(LogEvent ev)
    {
        if (!TryGetActor(ev, out var actor))
        {
            return;
        }

        actor.CardsPlayed += 1;
        actor.EnergySpent += Number(ev.Metadata, "energy_spent");
        switch (NormalizeCardType(Text(ev.Metadata, "card_type")))
        {
            case "attack":
                actor.AttackCardsPlayed += 1;
                break;
            case "skill":
                actor.SkillCardsPlayed += 1;
                break;
            case "power":
                actor.PowerCardsPlayed += 1;
                _attribution.TrackOwnedSource(ev, new AttributionEngine.ActorRef(actor.Id, actor.Name), Text(ev.Metadata, "card_name"));
                break;
        }
    }

    private void ConsumePowerApplied(LogEvent ev)
    {
        if (!TryGetActor(ev, out var actor))
        {
            return;
        }

        var status = ev.Metadata.TryGetValue("status", out var value) ? value?.ToString() ?? "status" : ev.SourceName ?? "status";
        actor.Statuses[status] = actor.Statuses.GetValueOrDefault(status) + Math.Max(1, (int)(ev.Amount ?? 1));
        if (UtilityStatuses.Contains(status))
        {
            actor.UtilityEvents += 1;
        }

        _attribution.TrackPower(ev, new AttributionEngine.ActorRef(actor.Id, actor.Name), status);
    }

    private void RecordDamage(PlayerStats actor, LogEvent ev)
    {
        var amount = ev.Amount ?? 0;
        var baseAmount = ev.BaseAmount ?? amount;
        var bucket = DamageBucket(ev);
        actor.Damage += amount;
        switch (bucket)
        {
            case "poison":
            case "doom":
                actor.PoisonDamage += amount;
                break;
            case "companion":
                actor.CompanionDamage += amount;
                break;
            default:
                actor.DirectDamage += Math.Min(amount, baseAmount);
                break;
        }

        _attribution.ApplyDamageAssist(ev, amount, (player, credit) =>
        {
            var helper = GetPlayer(player.PlayerId, player.PlayerName);
            helper.DamageAssist += credit;
            helper.UtilityDamage += credit;
        });
    }

    private void ApplyIndirectDamage(AttributionEngine.ActorRef player, double credit, string kind)
    {
        var helper = GetPlayer(player.PlayerId, player.PlayerName);
        helper.Damage += credit;
        if (kind.Equals("companion", StringComparison.OrdinalIgnoreCase))
        {
            helper.CompanionDamage += credit;
        }
        else
        {
            helper.PoisonDamage += credit;
        }
    }

    public void RenamePlayer(string playerId, string name)
    {
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (_players.TryGetValue(playerId, out var player))
        {
            player.ManualName = name.Trim();
            player.Name = player.ManualName;
        }
    }

    private PlayerStats GetPlayer(string? id, string? name)
    {
        var key = string.IsNullOrWhiteSpace(id) ? "unknown" : id;
        if (!_players.TryGetValue(key, out var stats))
        {
            stats = new PlayerStats { Id = key, Name = DisplayNameFor(key, name) };
            _players[key] = stats;
        }
        else if (string.IsNullOrWhiteSpace(stats.ManualName) && IsUsefulDisplayName(name) && !string.Equals(stats.Name, name, StringComparison.Ordinal))
        {
            stats.Name = name!;
        }

        return stats;
    }

    private bool TryGetActor(LogEvent ev, out PlayerStats actor)
    {
        actor = null!;
        if (Bool(ev.Metadata, "actor_is_pet") || IsCompanionName(ev.ActorName) || IsCompanionName(Text(ev.Metadata, "actor_display_name")))
        {
            return false;
        }

        if (ev.Metadata.TryGetValue("actor_is_player", out var isPlayer) && isPlayer is bool b && !b)
        {
            return false;
        }
        if (Bool(ev.Metadata, "actor_is_enemy") || Text(ev.Metadata, "actor_side").Equals("Enemy", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ev.ActorPlayerId) && string.IsNullOrWhiteSpace(ev.ActorName))
        {
            return false;
        }

        actor = GetPlayer(ev.ActorPlayerId, DisplayNameFromEvent(ev));
        return true;
    }

    private static bool IsCompanionName(string? name)
    {
        var text = name ?? "";
        return text.Contains("Otsy", StringComparison.OrdinalIgnoreCase);
    }

    private static double Number(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null)
        {
            return 0;
        }

        return double.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static string Text(Dictionary<string, object?> data, string key)
    {
        return data.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    }

    private static bool Bool(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null)
        {
            return false;
        }

        return value is bool b ? b : bool.TryParse(value.ToString(), out var parsed) && parsed;
    }

    private static string DisplayNameFor(string id, string? name)
    {
        if (IsUsefulDisplayName(name))
        {
            return name!;
        }

        return string.Equals(id, "unknown", StringComparison.OrdinalIgnoreCase) ? "Unknown" : $"Player {id}";
    }

    private static string? DisplayNameFromEvent(LogEvent ev)
    {
        var displayName = Text(ev.Metadata, "actor_display_name");
        return IsUsefulDisplayName(displayName) ? displayName : ev.ActorName;
    }

    private static string NormalizeCardType(string cardType)
    {
        var type = cardType.ToLowerInvariant();
        return type.Contains('.') ? type.Split('.').Last() : type;
    }

    private static PlayerStatsCheckpoint ToCheckpoint(PlayerStats player)
    {
        return new PlayerStatsCheckpoint
        {
            Id = player.Id,
            Name = player.Name,
            ManualName = player.ManualName,
            Damage = player.Damage,
            DirectDamage = player.DirectDamage,
            DamageAssist = player.DamageAssist,
            PoisonDamage = player.PoisonDamage,
            CompanionDamage = player.CompanionDamage,
            UtilityDamage = player.UtilityDamage,
            Block = player.Block,
            CompanionBlock = player.CompanionBlock,
            DamageBlocked = player.DamageBlocked,
            DamageTaken = player.DamageTaken,
            PreventedDamage = player.PreventedDamage,
            Healing = player.Healing,
            CardsDrawn = player.CardsDrawn,
            EnergyGiven = player.EnergyGiven,
            CardsPlayed = player.CardsPlayed,
            AttackCardsPlayed = player.AttackCardsPlayed,
            SkillCardsPlayed = player.SkillCardsPlayed,
            PowerCardsPlayed = player.PowerCardsPlayed,
            EnergySpent = player.EnergySpent,
            UtilityEvents = player.UtilityEvents,
            Statuses = new Dictionary<string, int>(player.Statuses)
        };
    }

    private static bool IsUsefulDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var text = name.Trim();
        if (text.StartsWith("MegaCrit.", StringComparison.Ordinal)
            || text.StartsWith("System.", StringComparison.Ordinal)
            || text.StartsWith("PlayerId ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<", StringComparison.Ordinal)
            || text.Contains("#", StringComparison.Ordinal))
        {
            return false;
        }

        return !double.TryParse(text, out _);
    }

    private static string DamageBucket(LogEvent ev)
    {
        var kind = Text(ev.Metadata, "indirect_damage_kind");
        if (!string.IsNullOrWhiteSpace(kind))
        {
            return kind;
        }

        var source = $"{ev.SourceType} {ev.SourceName} {Text(ev.Metadata, "power")} {Text(ev.Metadata, "status")} {Text(ev.Metadata, "damage_source_type")} {Text(ev.Metadata, "damage_source_name")} {Text(ev.Metadata, "damage_source_power")} {Text(ev.Metadata, "observed_power")}".ToLowerInvariant();
        if (source.Contains("poison") || source.Contains("doom"))
        {
            return "poison";
        }

        if (source.Contains("otsy") || source.Contains("pet") || source.Contains("companion"))
        {
            return "companion";
        }

        return "direct";
    }
}

using NeowLogs.NeowLogsCode.Events;

namespace NeowLogs.NeowLogsCode;

internal sealed class AttributionEngine
{
    private sealed record UtilitySetup(string PlayerId, string PlayerName, string Status, double Stacks, int? AppliedTurn = null);
    internal sealed record ActorRef(string PlayerId, string PlayerName);

    private readonly Dictionary<string, List<UtilitySetup>> _vulnerableByTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<UtilitySetup>> _damageReductionBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<UtilitySetup>> _indirectDamageByTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UtilitySetup> _sourceOwnerByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentCombatDamageOrder = new();
    private readonly HashSet<string> _recentCombatDamage = new(StringComparer.OrdinalIgnoreCase);

    // Fix 3d.1: once a target's doom kill is credited, every later doom-source event for
    // that target (a duplicate observed/dealt pair, or a death hook) is ignored so doom
    // credit pays out exactly once and never leaks to the last applier via a stale path.
    private readonly HashSet<string> _doomCreditedTargets = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        ResetCombat();
    }

    public void ResetCombat()
    {
        _vulnerableByTarget.Clear();
        _damageReductionBySource.Clear();
        _indirectDamageByTarget.Clear();
        _sourceOwnerByName.Clear();
        _recentCombatDamageOrder.Clear();
        _recentCombatDamage.Clear();
        _doomCreditedTargets.Clear();
    }

    public void FillCheckpoint(StatsCheckpoint checkpoint)
    {
        checkpoint.VulnerableByTarget = _vulnerableByTarget.SelectMany(pair => pair.Value.Select(setup => ToCheckpoint(pair.Key, setup))).ToList();
        checkpoint.DamageReductionBySource = _damageReductionBySource.SelectMany(pair => pair.Value.Select(setup => ToCheckpoint(pair.Key, setup))).ToList();
        checkpoint.IndirectDamageByTarget = _indirectDamageByTarget.SelectMany(pair => pair.Value.Select(setup => ToCheckpoint(pair.Key, setup))).ToList();
        checkpoint.SourceOwnerByName = _sourceOwnerByName.Select(pair => ToCheckpoint(pair.Key, pair.Value)).ToList();
    }

    public void Restore(StatsCheckpoint checkpoint)
    {
        _vulnerableByTarget.Clear();
        _damageReductionBySource.Clear();
        _indirectDamageByTarget.Clear();
        _sourceOwnerByName.Clear();
        RestoreSetups(checkpoint.VulnerableByTarget, _vulnerableByTarget);
        RestoreSetups(checkpoint.DamageReductionBySource, _damageReductionBySource);
        RestoreSetups(checkpoint.IndirectDamageByTarget, _indirectDamageByTarget);
        RestoreSingleSetups(checkpoint.SourceOwnerByName, _sourceOwnerByName);
    }

    public void ObserveCanonicalDamage(LogEvent ev)
    {
        AddRecentCombatDamage(DamageSignature(ev));
    }

    public bool IsPromotableObservedDamage(LogEvent ev)
    {
        if (!Text(ev.Metadata, "observed_method").Equals("MegaCrit.Sts2.Core.Commands.CreatureCmd.Damage", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsPlayerTarget(ev) || (!Bool(ev.Metadata, "target_is_enemy") && !Bool(ev.Metadata, "observed_is_enemy_target")))
        {
            return false;
        }

        if (_recentCombatDamage.Contains(DamageSignature(ev)))
        {
            return false;
        }

        var source = SourceText(ev);
        if (source.Contains("omnislice"))
        {
            return true;
        }

        var sourceType = ev.SourceType ?? "";
        var hasCardSource = !string.IsNullOrWhiteSpace(Text(ev.Metadata, "card_id")) || !string.IsNullOrWhiteSpace(Text(ev.Metadata, "card_name"));
        if (hasCardSource)
        {
            return false;
        }

        if (source.Contains("poison") || source.Contains("doom") || source.Contains("potion"))
        {
            return true;
        }

        if (IsGenericUnownedEnemyDamage(ev))
        {
            return true;
        }

        return !hasCardSource
            && (sourceType.Equals("power", StringComparison.OrdinalIgnoreCase)
                || sourceType.Equals("orb", StringComparison.OrdinalIgnoreCase)
                || sourceType.Equals("potion", StringComparison.OrdinalIgnoreCase)
                || sourceType.Equals("relic", StringComparison.OrdinalIgnoreCase));
    }

    public void TrackPower(LogEvent ev, ActorRef actor, string status)
    {
        TrackSourceOwner(ev, actor, status);
        TrackUtilitySetup(ev, actor, status);
    }

    public void TrackOwnedSource(LogEvent ev, ActorRef actor, string sourceName)
    {
        var keys = SourceOwnerKeys(ev).ToList();
        if (!string.IsNullOrWhiteSpace(sourceName))
        {
            keys.Add(sourceName);
        }

        if (keys.Count == 0)
        {
            return;
        }

        var label = !string.IsNullOrWhiteSpace(sourceName) ? sourceName : keys[0];
        var setup = new UtilitySetup(actor.PlayerId, actor.PlayerName, label, 1);
        foreach (var key in keys)
        {
            var normalized = NormalizeSourceKey(key);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            _sourceOwnerByName[normalized] = setup;
            var shortKey = ShortSourceKey(normalized);
            if (!string.Equals(shortKey, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _sourceOwnerByName[shortKey] = setup;
            }
        }
    }

    public bool TryResolveSourceOwner(LogEvent ev, out ActorRef actor)
    {
        actor = null!;
        foreach (var key in SourceOwnerKeys(ev))
        {
            if (_sourceOwnerByName.TryGetValue(key, out var setup))
            {
                actor = new ActorRef(setup.PlayerId, setup.PlayerName);
                return true;
            }
        }

        return false;
    }

    public bool TryResolveGenericPowerProcOwner(LogEvent ev, out ActorRef actor)
    {
        actor = null!;
        var amount = ev.Amount ?? 0;
        if (amount <= 0 || amount > 10 || !IsGenericUnownedEnemyDamage(ev))
        {
            return false;
        }

        var candidates = _sourceOwnerByName.Values
            .Where(setup => IsRegentProcSource(setup.Status))
            .GroupBy(setup => setup.PlayerId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (candidates.Length != 1)
        {
            return false;
        }

        var owner = candidates[0];
        actor = new ActorRef(owner.PlayerId, owner.PlayerName);
        return true;
    }

    public bool TryApplyIndirectDamage(LogEvent ev, Action<ActorRef, double, string> apply)
    {
        var targetKey = TargetKey(ev);
        if (string.IsNullOrWhiteSpace(targetKey) || !_indirectDamageByTarget.TryGetValue(targetKey, out var ledger))
        {
            return false;
        }

        if (!IsIndirectDamageSource(ev) && !IsGenericUnownedEnemyDamage(ev))
        {
            return false;
        }

        var kind = IndirectDamageKind(ev);
        if (kind.Equals("poison", StringComparison.OrdinalIgnoreCase) && IsPoisonDamageSource(ev))
        {
            var amount = IndirectDamageAmount(ev);
            return SplitPoisonDamageAndDecay(ledger, amount, apply);
        }
        else if (kind.Equals("doom", StringComparison.OrdinalIgnoreCase) && IsDoomDamageSource(ev))
        {
            // Fix 3d.1: a doom kill can surface as both damage_observed and damage_dealt (and
            // now a creature_died event). Once credited, swallow the duplicates so they never
            // fall through to the source-owner path and re-credit the last applier.
            if (_doomCreditedTargets.Contains(targetKey))
            {
                return true;
            }

            if (!Bool(ev.Metadata, "target_killed"))
            {
                return false;
            }

            var amount = DoomKillCreditAmount(ev);
            var credited = SplitStatusDamageAndClear(ledger, amount, "doom", IsDoomStatus, apply);
            if (credited)
            {
                _doomCreditedTargets.Add(targetKey);
            }

            return credited;
        }
        else
        {
            var amount = IndirectDamageAmount(ev);
            return SplitUtilityDamage(ledger.Where(entry => !IsDoomStatus(entry.Status)), amount, kind, apply);
        }
    }

    public bool ClearPendingDoomDamageSource(LogEvent ev)
    {
        if (!IsDoomDamageSource(ev) || !Bool(ev.Metadata, "target_killed"))
        {
            return false;
        }

        var targetKey = TargetKey(ev);
        if (string.IsNullOrWhiteSpace(targetKey) || !_indirectDamageByTarget.TryGetValue(targetKey, out var ledger))
        {
            return false;
        }

        // Fix 3d.1: only tidy up doom entries once the kill has actually been credited. Never
        // wipe an uncredited doom ledger here — that used to destroy it before any split ran,
        // sending the credit to the last applier via the source-owner fallback.
        if (!_doomCreditedTargets.Contains(targetKey))
        {
            return false;
        }

        ledger.RemoveAll(entry => IsDoomStatus(entry.Status));
        return true;
    }

    public void ApplyDamageAssist(LogEvent ev, double amount, Action<ActorRef, double> apply)
    {
        if (TryApplyExplicitAmplification(ev, apply))
        {
            var explicitTargetKey = TargetKey(ev);
            if (ev.Turn.HasValue
                && !string.IsNullOrWhiteSpace(explicitTargetKey)
                && _vulnerableByTarget.TryGetValue(explicitTargetKey, out var explicitLedger))
            {
                AdvanceTimedLedger(explicitLedger, ev.Turn, IsDamageAmplifierStatus);
            }

            return;
        }

        var targetKey = TargetKey(ev);
        if (string.IsNullOrWhiteSpace(targetKey) || !_vulnerableByTarget.TryGetValue(targetKey, out var ledger) || amount <= 0)
        {
            return;
        }

        // Fix 2: prefer the measured Vulnerable delta (final - observed base). The hard-coded
        // 1.5x estimate is a last resort, and only when a vuln window is genuinely active this
        // turn, so ledger/game desyncs stop producing phantom credit.
        var measuredBonus = MeasuredAmplifierBonus(ev, amount);
        if (measuredBonus > 0)
        {
            ApplyTimedCredit(ledger, measuredBonus, ev.Turn, IsDamageAmplifierStatus, apply);
            return;
        }

        if (!ev.Turn.HasValue || ActiveTimedIndex(ledger, ev.Turn.Value, IsDamageAmplifierStatus) < 0)
        {
            AdvanceTimedLedger(ledger, ev.Turn, IsDamageAmplifierStatus);
            return;
        }

        var estimatedBase = amount / 1.5;
        var bonus = Math.Max(0, amount - estimatedBase);
        if (bonus > 0)
        {
            ApplyTimedCredit(ledger, bonus, ev.Turn, IsDamageAmplifierStatus, apply);
        }
    }

    private static double MeasuredAmplifierBonus(LogEvent ev, double amount)
    {
        var bonus = Number(ev.Metadata, "vulnerable_bonus");
        if (bonus > 0)
        {
            return bonus;
        }

        var baseAmount = ev.BaseAmount ?? 0;
        return baseAmount > 0 && amount > baseAmount ? amount - baseAmount : 0;
    }

    // Fix 5: read-only resolution of the active amplifier owner(s) for a target, used at
    // capture time to stamp amplified_by rows into the log. Does not mutate the ledger — the
    // subsequent ApplyDamageAssist call advances it.
    public IReadOnlyList<(string PlayerId, string PlayerName, string Status, double Bonus)> ResolveAmplifiers(string? targetKey, int? turn, double bonus)
    {
        var result = new List<(string, string, string, double)>();
        if (bonus <= 0 || string.IsNullOrWhiteSpace(targetKey) || !_vulnerableByTarget.TryGetValue(targetKey, out var ledger))
        {
            return result;
        }

        var index = turn.HasValue ? ActiveTimedIndex(ledger, turn.Value, IsDamageAmplifierStatus) : -1;
        if (index < 0)
        {
            index = ledger.FindIndex(entry => entry.Stacks > 0 && IsDamageAmplifierStatus(entry.Status));
        }

        if (index < 0)
        {
            return result;
        }

        var active = ledger[index];
        result.Add((active.PlayerId, active.PlayerName, active.Status, bonus));
        return result;
    }

    // Fix 3: expire timed ledgers on turn boundaries (from turn_started events), so stale
    // Vulnerable/Weak windows on targets nobody hit this turn are cleaned up too.
    public void AdvanceTurn(int turn)
    {
        foreach (var ledger in _vulnerableByTarget.Values)
        {
            AdvanceTimedLedger(ledger, turn, IsDamageAmplifierStatus);
        }

        foreach (var ledger in _damageReductionBySource.Values)
        {
            AdvanceTimedLedger(ledger, turn, IsWeakStatus);
        }

        foreach (var key in _vulnerableByTarget.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList())
        {
            _vulnerableByTarget.Remove(key);
        }

        foreach (var key in _damageReductionBySource.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToList())
        {
            _damageReductionBySource.Remove(key);
        }
    }

    // Fix 5 (#3): read-only proportional doom split for stamping doom_kill_credit rows into the
    // log at capture time. Does not mutate or mark the ledger — the live meter still credits via
    // the Consume path; these rows exist so report viewers render the same resolved numbers.
    public IReadOnlyList<(string PlayerId, string PlayerName, double DoomApplied, double Credit)> ResolveDoomCredit(string? targetKey, double killAmount)
    {
        var result = new List<(string, string, double, double)>();
        if (string.IsNullOrWhiteSpace(targetKey)
            || _doomCreditedTargets.Contains(targetKey)
            || !_indirectDamageByTarget.TryGetValue(targetKey, out var ledger))
        {
            return result;
        }

        var doomEntries = ledger.Where(entry => IsDoomStatus(entry.Status) && entry.Stacks > 0).ToArray();
        var total = doomEntries.Sum(entry => entry.Stacks);
        if (total <= 0)
        {
            return result;
        }

        var payout = killAmount > 0 ? Math.Min(killAmount, total) : total;
        foreach (var entry in doomEntries)
        {
            result.Add((entry.PlayerId, entry.PlayerName, entry.Stacks, payout * entry.Stacks / total));
        }

        return result;
    }

    // Fix 4.4: proportional doom split driven by a creature_died event, for the case where the
    // damage-based doom event was dropped at capture. Guarded by the credited-targets set so it
    // never double-pays with the damage path.
    public bool TryCreditDoomOnDeath(LogEvent ev, double killHp, Action<ActorRef, double, string> apply)
    {
        var targetKey = TargetKey(ev);
        if (string.IsNullOrWhiteSpace(targetKey) || _doomCreditedTargets.Contains(targetKey))
        {
            return false;
        }

        if (!_indirectDamageByTarget.TryGetValue(targetKey, out var ledger))
        {
            return false;
        }

        var totalDoom = ledger.Where(entry => IsDoomStatus(entry.Status) && entry.Stacks > 0).Sum(entry => entry.Stacks);
        if (totalDoom <= 0)
        {
            return false;
        }

        var amount = killHp > 0 ? Math.Min(killHp, totalDoom) : totalDoom;
        var credited = SplitStatusDamageAndClear(ledger, amount, "doom", IsDoomStatus, apply);
        if (credited)
        {
            _doomCreditedTargets.Add(targetKey);
        }

        return credited;
    }

    public void ApplyPreventedDamage(LogEvent ev, Action<ActorRef, double> apply)
    {
        var sourceKey = !string.IsNullOrWhiteSpace(ev.TargetId) ? ev.TargetId : ev.TargetName;
        if (string.IsNullOrWhiteSpace(sourceKey) || !_damageReductionBySource.TryGetValue(sourceKey, out var ledger))
        {
            return;
        }

        var prevented = Number(ev.Metadata, "prevented_damage");
        if (prevented <= 0)
        {
            prevented = EstimatePreventedDamage(ev, ledger);
            if (prevented <= 0)
            {
                return;
            }
        }

        if (ledger.Any(IsWeakSetup))
        {
            ApplyTimedCredit(ledger, prevented, ev.Turn, IsWeakStatus, apply);
            return;
        }

        SplitUtilityDamage(ledger, prevented, apply);
    }

    public static bool IsPlayerTarget(LogEvent ev)
    {
        return Bool(ev.Metadata, "target_is_player")
            || Text(ev.Metadata, "target_side").Equals("Player", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackUtilitySetup(LogEvent ev, ActorRef actor, string status)
    {
        var targetKey = TargetKey(ev);
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            return;
        }

        if (IsDamageAmplifierStatus(status) || IsDebilitateSource(ev))
        {
            AddVulnerableLedgerEntry(_vulnerableByTarget, targetKey, actor, IsDebilitateSource(ev) ? "debilitate" : status, ev.Amount ?? 1, ev.Turn);
            return;
        }

        if (status.Contains("poison", StringComparison.OrdinalIgnoreCase))
        {
            AddLedgerEntry(_indirectDamageByTarget, targetKey, actor, status, ev.Amount ?? 1);
            return;
        }

        if (status.Contains("doom", StringComparison.OrdinalIgnoreCase))
        {
            AddLedgerEntry(_indirectDamageByTarget, targetKey, actor, status, ev.Amount ?? 1);
            return;
        }

        if (status.Contains("weak", StringComparison.OrdinalIgnoreCase)
            || status.Contains("strength_down", StringComparison.OrdinalIgnoreCase)
            || (status.Contains("strength", StringComparison.OrdinalIgnoreCase) && (ev.Amount ?? 0) < 0)
            || status.Contains("damage_decrease", StringComparison.OrdinalIgnoreCase))
        {
            if (status.Contains("weak", StringComparison.OrdinalIgnoreCase))
            {
                AddTimedLedgerEntry(_damageReductionBySource, targetKey, actor, status, Math.Abs(ev.Amount ?? 1), ev.Turn);
            }
            else
            {
                AddLedgerEntry(_damageReductionBySource, targetKey, actor, status, Math.Abs(ev.Amount ?? 1));
            }
        }
    }

    private void TrackSourceOwner(LogEvent ev, ActorRef actor, string status)
    {
        // Fix 3c: doom and poison are shared, multi-owner debuffs and must only ever resolve
        // through the per-target ledger. Registering them here would let whoever applied them
        // most recently own the key and collect 100% of the credit in coop.
        if (IsDoomStatus(status) || IsPoisonStatus(status))
        {
            return;
        }

        var setup = new UtilitySetup(actor.PlayerId, actor.PlayerName, status, ev.Amount ?? 1);
        foreach (var key in SourceOwnerKeys(ev))
        {
            _sourceOwnerByName[key] = setup;
        }
    }

    private static bool TryApplyExplicitAmplification(LogEvent ev, Action<ActorRef, double> apply)
    {
        if (!ev.Metadata.TryGetValue("amplified_by", out var amplified) || amplified is not IEnumerable<object> rows)
        {
            return false;
        }

        var recorded = false;
        foreach (var row in rows)
        {
            if (row is not Dictionary<string, object?> attribution)
            {
                continue;
            }

            var utilityId = attribution.GetValueOrDefault("applied_by_player_id")?.ToString();
            var utilityName = attribution.GetValueOrDefault("applied_by_name")?.ToString() ?? utilityId ?? "Unknown";
            var bonus = attribution.GetValueOrDefault("bonus_damage") is double d ? d : 0;
            if (string.IsNullOrWhiteSpace(utilityId) || bonus <= 0)
            {
                continue;
            }

            apply(new ActorRef(utilityId, utilityName), bonus);
            recorded = true;
        }

        return recorded;
    }

    private static double EstimatePreventedDamage(LogEvent ev, IEnumerable<UtilitySetup> ledger)
    {
        var observed = Number(ev.Metadata, "total_damage");
        if (observed <= 0)
        {
            observed = (ev.Amount ?? 0) + Number(ev.Metadata, "blocked_damage");
        }

        if (observed <= 0)
        {
            return 0;
        }

        var setups = ledger.ToArray();
        if (setups.Any(IsWeakSetup))
        {
            return Math.Max(0, (observed / 0.75) - observed);
        }

        var strengthReduction = setups
            .Where(setup => setup.Status.Contains("strength", StringComparison.OrdinalIgnoreCase))
            .Sum(setup => Math.Abs(setup.Stacks));
        return strengthReduction > 0 ? strengthReduction : 0;
    }

    private static void AddLedgerEntry(Dictionary<string, List<UtilitySetup>> ledger, string targetKey, ActorRef actor, string status, double stacks)
    {
        if (!ledger.TryGetValue(targetKey, out var entries))
        {
            entries = [];
            ledger[targetKey] = entries;
        }

        stacks = Math.Max(1, Math.Abs(stacks));
        var existing = entries.FindIndex(entry =>
            string.Equals(entry.PlayerId, actor.PlayerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Status, status, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            var prior = entries[existing];
            entries[existing] = prior with { PlayerName = actor.PlayerName, Stacks = prior.Stacks + stacks };
        }
        else
        {
            entries.Add(new UtilitySetup(actor.PlayerId, actor.PlayerName, status, stacks));
        }
    }

    private static void AddVulnerableLedgerEntry(Dictionary<string, List<UtilitySetup>> ledger, string targetKey, ActorRef actor, string status, double stacks, int? appliedTurn)
    {
        AddTimedLedgerEntry(ledger, targetKey, actor, status, stacks, appliedTurn);
    }

    private static void AddTimedLedgerEntry(Dictionary<string, List<UtilitySetup>> ledger, string targetKey, ActorRef actor, string status, double stacks, int? appliedTurn)
    {
        if (!ledger.TryGetValue(targetKey, out var entries))
        {
            entries = [];
            ledger[targetKey] = entries;
        }

        stacks = Math.Max(1, Math.Abs(stacks));
        entries.Add(new UtilitySetup(actor.PlayerId, actor.PlayerName, status, stacks, appliedTurn));
    }

    private static bool SplitUtilityDamage(IEnumerable<UtilitySetup> ledger, double amount, Action<ActorRef, double> apply)
    {
        return SplitUtilityDamage(ledger, amount, "", (actor, credit, _) => apply(actor, credit));
    }

    private static bool SplitUtilityDamage(IEnumerable<UtilitySetup> ledger, double amount, string kind, Action<ActorRef, double, string> apply)
    {
        var entries = ledger.Where(entry => entry.Stacks > 0).ToArray();
        var total = entries.Sum(entry => entry.Stacks);
        if (amount <= 0 || total <= 0)
        {
            return false;
        }

        foreach (var entry in entries)
        {
            apply(new ActorRef(entry.PlayerId, entry.PlayerName), amount * entry.Stacks / total, kind);
        }

        return true;
    }

    private static bool SplitPoisonDamageAndDecay(List<UtilitySetup> ledger, double amount, Action<ActorRef, double, string> apply)
    {
        var entries = ledger
            .Select((entry, index) => new { Entry = entry, Index = index })
            .Where(row => row.Entry.Stacks > 0 && IsPoisonStatus(row.Entry.Status))
            .ToArray();
        var total = entries.Sum(row => row.Entry.Stacks);
        if (amount <= 0 || total <= 0)
        {
            ledger.RemoveAll(entry => entry.Stacks <= 0);
            return false;
        }

        foreach (var row in entries)
        {
            apply(new ActorRef(row.Entry.PlayerId, row.Entry.PlayerName), amount * row.Entry.Stacks / total, "poison");
        }

        for (var i = ledger.Count - 1; i >= 0; i--)
        {
            var entry = ledger[i];
            if (!IsPoisonStatus(entry.Status))
            {
                continue;
            }

            var remainingStacks = entry.Stacks - 1;
            if (remainingStacks <= 0.001)
            {
                ledger.RemoveAt(i);
            }
            else
            {
                ledger[i] = entry with { Stacks = remainingStacks };
            }
        }

        return true;
    }

    private static void SplitStatusDamage(List<UtilitySetup> ledger, double amount, string kind, Func<string, bool> statusPredicate, Action<ActorRef, double, string> apply)
    {
        var entries = ledger.Where(entry => entry.Stacks > 0 && statusPredicate(entry.Status)).ToArray();
        var total = entries.Sum(entry => entry.Stacks);
        if (amount <= 0 || total <= 0)
        {
            return;
        }

        foreach (var entry in entries)
        {
            apply(new ActorRef(entry.PlayerId, entry.PlayerName), amount * entry.Stacks / total, kind);
        }
    }

    private static bool SplitStatusDamageAndClear(List<UtilitySetup> ledger, double amount, string kind, Func<string, bool> statusPredicate, Action<ActorRef, double, string> apply)
    {
        var total = ledger.Where(entry => entry.Stacks > 0 && statusPredicate(entry.Status)).Sum(entry => entry.Stacks);
        var credited = total > 0 && amount > 0;
        SplitStatusDamage(ledger, Math.Min(amount, total), kind, statusPredicate, apply);
        ledger.RemoveAll(entry => statusPredicate(entry.Status));
        return credited;
    }

    private static void ApplyTimedCredit(List<UtilitySetup> ledger, double amount, int? currentTurn, Func<string, bool> statusPredicate, Action<ActorRef, double> apply)
    {
        if (amount <= 0)
        {
            return;
        }

        if (currentTurn.HasValue && ledger.Any(entry => entry.AppliedTurn.HasValue && statusPredicate(entry.Status)))
        {
            var activeIndex = ActiveTimedIndex(ledger, currentTurn.Value, statusPredicate);
            if (activeIndex >= 0)
            {
                var active = ledger[activeIndex];
                apply(new ActorRef(active.PlayerId, active.PlayerName), amount);
            }

            AdvanceTimedLedger(ledger, currentTurn, statusPredicate);
            return;
        }

        ApplyAndConsumeOldestTimedUse(ledger, amount, statusPredicate, apply);
    }

    private static void ApplyAndConsumeOldestTimedUse(List<UtilitySetup> ledger, double amount, Func<string, bool> statusPredicate, Action<ActorRef, double> apply)
    {
        for (var i = 0; i < ledger.Count; i++)
        {
            var entry = ledger[i];
            if (entry.Stacks <= 0 || !statusPredicate(entry.Status))
            {
                continue;
            }

            apply(new ActorRef(entry.PlayerId, entry.PlayerName), amount);
            var remainingStacks = entry.Stacks - 1;
            if (remainingStacks <= 0.001)
            {
                ledger.RemoveAt(i);
            }
            else
            {
                ledger[i] = entry with { Stacks = remainingStacks };
            }

            return;
        }

        ledger.RemoveAll(entry => entry.Stacks <= 0);
    }

    private static void AdvanceTimedLedger(List<UtilitySetup> ledger, int? currentTurn)
    {
        AdvanceTimedLedger(ledger, currentTurn, _ => true);
    }

    private static void AdvanceTimedLedger(List<UtilitySetup> ledger, int? currentTurn, Func<string, bool> statusPredicate)
    {
        // Fix 3: expiry for Vulnerable/Weak is duration-based (turn windows), never per-hit.
        // With no turn we only drop already-depleted entries; we never decrement a stack per
        // hit (that per-hit decay was the poison model leaking into the timed-debuff path).
        if (!currentTurn.HasValue || !ledger.Any(entry => entry.AppliedTurn.HasValue && statusPredicate(entry.Status)))
        {
            ledger.RemoveAll(entry => entry.Stacks <= 0 && statusPredicate(entry.Status));
            return;
        }

        var expiredIndexes = ExpiredTimedIndexes(ledger, currentTurn.Value, statusPredicate);
        for (var i = expiredIndexes.Count - 1; i >= 0; i--)
        {
            ledger.RemoveAt(expiredIndexes[i]);
        }
    }

    private static int ActiveTimedIndex(List<UtilitySetup> ledger, int currentTurn, Func<string, bool> statusPredicate)
    {
        var windows = TimedWindows(ledger, currentTurn, statusPredicate);
        foreach (var (index, start, end) in windows)
        {
            if (currentTurn >= start && currentTurn < end)
            {
                return index;
            }
        }

        return -1;
    }

    private static List<int> ExpiredTimedIndexes(List<UtilitySetup> ledger, int currentTurn, Func<string, bool> statusPredicate)
    {
        return TimedWindows(ledger, currentTurn, statusPredicate)
            .Where(window => currentTurn >= window.End)
            .Select(window => window.Index)
            .ToList();
    }

    private static IEnumerable<(int Index, int Start, int End)> TimedWindows(List<UtilitySetup> ledger, int currentTurn, Func<string, bool> statusPredicate)
    {
        var cursor = ledger
            .Where(entry => entry.AppliedTurn.HasValue && statusPredicate(entry.Status))
            .Select(entry => entry.AppliedTurn.GetValueOrDefault())
            .DefaultIfEmpty(currentTurn)
            .Min();

        for (var i = 0; i < ledger.Count; i++)
        {
            var entry = ledger[i];
            if (entry.Stacks <= 0 || !statusPredicate(entry.Status))
            {
                continue;
            }

            var start = Math.Max(cursor, entry.AppliedTurn ?? cursor);
            var duration = Math.Max(1, (int)Math.Ceiling(entry.Stacks));
            var end = start + duration;
            yield return (i, start, end);
            cursor = Math.Max(cursor, end);
        }
    }

    private static bool IsWeakSetup(UtilitySetup setup)
    {
        return IsWeakStatus(setup.Status);
    }

    private static bool IsWeakStatus(string status)
    {
        return status.Contains("weak", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPoisonStatus(string status)
    {
        return status.Contains("poison", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDoomStatus(string status)
    {
        return status.Contains("doom", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVulnerableStatus(string status)
    {
        return status.Equals("vulnerable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDamageAmplifierStatus(string status)
    {
        return IsVulnerableStatus(status)
            || status.Contains("debilitate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDebilitateSource(LogEvent ev)
    {
        return SourceText(ev).Contains("debilitate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIndirectDamageSource(LogEvent ev)
    {
        var sourceType = ev.SourceType ?? "";
        if (sourceType.Equals("poison", StringComparison.OrdinalIgnoreCase)
            || sourceType.Equals("doom", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Fix 3b/4.3: use the same broad SourceText helper as IsDoomDamageSource so an event
        // whose only doom/poison marker is its SourceName (or source_name metadata) still
        // passes the gate. The narrow field list here previously dropped those events.
        var statusText = SourceText(ev);
        if (!statusText.Contains("poison") && !statusText.Contains("doom"))
        {
            return false;
        }

        var hasCardSource = !string.IsNullOrWhiteSpace(Text(ev.Metadata, "card_id"))
            || !string.IsNullOrWhiteSpace(Text(ev.Metadata, "card_name"))
            || !string.IsNullOrWhiteSpace(Text(ev.Metadata, "observed_card"));
        return !hasCardSource;
    }

    private static bool IsPoisonDamageSource(LogEvent ev)
    {
        var sourceType = ev.SourceType ?? "";
        if (sourceType.Equals("poison", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var source = SourceText(ev);
        return source.Contains("poison") && !source.Contains("doom");
    }

    private static bool IsDoomDamageSource(LogEvent ev)
    {
        var sourceType = ev.SourceType ?? "";
        if (sourceType.Equals("doom", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return SourceText(ev).Contains("doom");
    }

    private static bool IsRegentProcSource(string source)
    {
        var text = source.Replace("_", " ", StringComparison.Ordinal).ToLowerInvariant();
        return text.Contains("stardust") || text.Contains("black hole");
    }

    private static bool IsGenericUnownedEnemyDamage(LogEvent ev)
    {
        if (!Bool(ev.Metadata, "target_is_enemy") && !Bool(ev.Metadata, "observed_is_enemy_target"))
        {
            return false;
        }

        if (Bool(ev.Metadata, "actor_is_player") || Bool(ev.Metadata, "actor_is_enemy"))
        {
            return false;
        }

        var sourceType = ev.SourceType ?? "";
        var sourceName = ev.SourceName ?? "";
        if (!sourceType.Equals("combat_history", StringComparison.OrdinalIgnoreCase)
            && !sourceType.Equals("command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasCardSource = !string.IsNullOrWhiteSpace(Text(ev.Metadata, "card_id"))
            || !string.IsNullOrWhiteSpace(Text(ev.Metadata, "card_name"))
            || !string.IsNullOrWhiteSpace(Text(ev.Metadata, "observed_card"));
        return !hasCardSource
            && (sourceName.Equals("Block", StringComparison.OrdinalIgnoreCase)
                || sourceName.Equals("Attack", StringComparison.OrdinalIgnoreCase)
                || sourceName.Equals("combat_history", StringComparison.OrdinalIgnoreCase));
    }

    private static double IndirectDamageAmount(LogEvent ev)
    {
        var amount = ev.Amount ?? 0;
        if (amount > 0)
        {
            return amount;
        }

        if (!Bool(ev.Metadata, "target_killed"))
        {
            return 0;
        }

        return Math.Max(
            Math.Max(Number(ev.Metadata, "total_damage"), Number(ev.Metadata, "unblocked_damage")),
            Math.Max(Number(ev.Metadata, "original_damage"), Number(ev.Metadata, "lethal_damage")));
    }

    private static double DoomKillCreditAmount(LogEvent ev)
    {
        var overkill = Number(ev.Metadata, "overkill_damage");
        var total = Number(ev.Metadata, "total_damage");
        var unblocked = Number(ev.Metadata, "unblocked_damage");
        var lethal = Number(ev.Metadata, "lethal_damage");
        var amount = ev.Amount ?? 0;
        var candidates = new[]
        {
            total - overkill,
            unblocked - overkill,
            amount - overkill,
            Number(ev.Metadata, "current_hp"),
            Number(ev.Metadata, "target_current_hp"),
            unblocked,
            total,
            lethal,
            amount
        }.Where(value => value > 0).ToArray();

        return candidates.Length == 0 ? 0 : candidates.Min();
    }

    private static string SourceText(LogEvent ev)
    {
        return $"{ev.SourceType} {ev.SourceName} {Text(ev.Metadata, "power")} {Text(ev.Metadata, "status")} {Text(ev.Metadata, "damage_source_type")} {Text(ev.Metadata, "damage_source_name")} {Text(ev.Metadata, "damage_source_power")} {Text(ev.Metadata, "card_id")} {Text(ev.Metadata, "card_name")} {Text(ev.Metadata, "observed_card")} {Text(ev.Metadata, "observed_power")}".ToLowerInvariant();
    }

    private static string IndirectDamageKind(LogEvent ev)
    {
        // Fix 3d.2: an explicit kind wins, then doom, then poison, then companion. Companion
        // is matched via structured flags rather than a substring sweep over everything,
        // because metadata snapshots carry stale pet fields that misclassified doom/poison
        // as companion damage.
        var explicitKind = Text(ev.Metadata, "indirect_damage_kind");
        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            return explicitKind.ToLowerInvariant();
        }

        var text = SourceText(ev);
        if (text.Contains("doom"))
        {
            return "doom";
        }

        if (text.Contains("poison"))
        {
            return "poison";
        }

        if (Bool(ev.Metadata, "pet_is_pet") || Bool(ev.Metadata, "actor_is_pet") || IsCompanionText(text))
        {
            return "companion";
        }

        return "poison";
    }

    private static bool IsCompanionText(string text)
    {
        // The companion is spelled "Otsy" in code but "Osty" in real logs; accept both.
        return text.Contains("otsy") || text.Contains("osty") || text.Contains("companion");
    }

    private static IEnumerable<string> SourceOwnerKeys(LogEvent ev)
    {
        foreach (var value in new[]
                 {
                     ev.SourceName,
                     Text(ev.Metadata, "source_name"),
                     Text(ev.Metadata, "power"),
                     Text(ev.Metadata, "damage_source_name"),
                     Text(ev.Metadata, "damage_source_id"),
                     Text(ev.Metadata, "damage_source_power"),
                     Text(ev.Metadata, "potion_name"),
                     Text(ev.Metadata, "potion_id"),
                     Text(ev.Metadata, "card_name"),
                     Text(ev.Metadata, "card_id"),
                     Text(ev.Metadata, "observed_card"),
                     Text(ev.Metadata, "observed_power")
                 })
        {
            var key = NormalizeSourceKey(value);
            if (!string.IsNullOrWhiteSpace(key))
            {
                yield return key;
                var shortKey = ShortSourceKey(key);
                if (!string.Equals(shortKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    yield return shortKey;
                }
            }
        }
    }

    private static string? TargetKey(LogEvent ev)
    {
        return !string.IsNullOrWhiteSpace(ev.TargetId) ? ev.TargetId : ev.TargetName;
    }

    private void AddRecentCombatDamage(string signature)
    {
        _recentCombatDamage.Add(signature);
        _recentCombatDamageOrder.Enqueue(signature);
        while (_recentCombatDamageOrder.Count > 48)
        {
            _recentCombatDamage.Remove(_recentCombatDamageOrder.Dequeue());
        }
    }

    private static string DamageSignature(LogEvent ev)
    {
        var target = !string.IsNullOrWhiteSpace(ev.TargetId) ? ev.TargetId : ev.TargetName;
        var source = ShortSourceKey(NormalizeSourceKey(Text(ev.Metadata, "card_id")) != "" ? Text(ev.Metadata, "card_id") : ev.SourceName);
        var observedMethod = Text(ev.Metadata, "observed_method");
        var sequence = Text(ev.Metadata, "event_sequence");
        if (string.IsNullOrWhiteSpace(sequence))
        {
            sequence = ev.TimestampMs.ToString();
        }

        return $"{target}|{Math.Round(ev.Amount ?? 0, 2)}|{source}|{observedMethod}|{sequence}";
    }

    private static UtilitySetupCheckpoint ToCheckpoint(string key, UtilitySetup setup)
    {
        return new UtilitySetupCheckpoint
        {
            Key = key,
            PlayerId = setup.PlayerId,
            PlayerName = setup.PlayerName,
            Status = setup.Status,
            Stacks = setup.Stacks,
            AppliedTurn = setup.AppliedTurn
        };
    }

    private static void RestoreSetups(IEnumerable<UtilitySetupCheckpoint> checkpoints, Dictionary<string, List<UtilitySetup>> target)
    {
        foreach (var checkpoint in checkpoints)
        {
            if (string.IsNullOrWhiteSpace(checkpoint.Key) || string.IsNullOrWhiteSpace(checkpoint.PlayerId))
            {
                continue;
            }

            if (!target.TryGetValue(checkpoint.Key, out var entries))
            {
                entries = [];
                target[checkpoint.Key] = entries;
            }

            entries.Add(new UtilitySetup(checkpoint.PlayerId, checkpoint.PlayerName, checkpoint.Status, checkpoint.Stacks, checkpoint.AppliedTurn));
        }
    }

    private static void RestoreSingleSetups(IEnumerable<UtilitySetupCheckpoint> checkpoints, Dictionary<string, UtilitySetup> target)
    {
        foreach (var checkpoint in checkpoints)
        {
            if (string.IsNullOrWhiteSpace(checkpoint.Key) || string.IsNullOrWhiteSpace(checkpoint.PlayerId))
            {
                continue;
            }

            target[checkpoint.Key] = new UtilitySetup(checkpoint.PlayerId, checkpoint.PlayerName, checkpoint.Status, checkpoint.Stacks, checkpoint.AppliedTurn);
        }
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

    private static string NormalizeSourceKey(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)
            || text.StartsWith("MegaCrit.", StringComparison.Ordinal)
            || text.StartsWith("System.", StringComparison.Ordinal)
            || text.Equals("power", StringComparison.OrdinalIgnoreCase)
            || text.Equals("status", StringComparison.OrdinalIgnoreCase)
            || text.Equals("combat_history", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var paren = text.IndexOf('(');
        return paren >= 0 ? text[..paren].Trim() : text;
    }

    private static string ShortSourceKey(string? value)
    {
        var text = NormalizeSourceKey(value);
        foreach (var prefix in new[] { "POWER.", "CARD.", "RELIC.", "ORB." })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.Replace("_", " ", StringComparison.Ordinal).Trim();
    }
}

using System.Reflection;
using System.Collections;

namespace NeowLogs.NeowLogsCode;

public static class EventTap
{
    private sealed record RecentCard(string ActorId, string? CardId, string? CardName, string? CardType, long SeenAtTicks);

    private static readonly bool CaptureVerboseSnapshots =
        string.Equals(Environment.GetEnvironmentVariable("NEOWLOGS_VERBOSE_SNAPSHOTS"), "1", StringComparison.Ordinal);

    private static readonly Dictionary<MethodBase, string> MethodEventTypes = new();
    private static readonly Dictionary<Type, MemberInfo[]> SnapshotMemberCache = new();
    private static readonly Dictionary<(Type Type, string Name), MemberInfo?> DirectMemberCache = new();
    private static readonly Dictionary<string, RecentCard> RecentCardByActor = new(StringComparer.OrdinalIgnoreCase);

    public static void RegisterMethodEventType(MethodBase method, string eventType)
    {
        MethodEventTypes[method] = eventType;
    }

    public static void RecordFromCommand(MethodBase originalMethod, object? instance, object[] args)
    {
        UiRuntime.EnsureAttached();

        var eventType = MethodEventTypes.GetValueOrDefault(originalMethod);
        if (TryRecordCombatHistory(originalMethod, instance, args, eventType))
        {
            return;
        }
        RecordFromCommand(originalMethod, instance, args, eventType);
    }

    public static void RecordFromCommand(object? instance, object[] args)
    {
        RecordFromCommand(null, instance, args, null);
    }

    private static void RecordFromCommand(MethodBase? originalMethod, object? instance, object[] args, string? preferredEventType)
    {
        var typeName = instance?.GetType().FullName ?? instance?.GetType().Name ?? "static_hook";
        var eventType = preferredEventType ?? GuessEventType(typeName);
        if (eventType == null)
        {
            return;
        }

        var metadata = Snapshot(instance, args);
        if (eventType is "debuff_applied" or "buff_applied" or "power_applied" && TryRecordPowerApplied(originalMethod, args, metadata))
        {
            return;
        }

        if (eventType == "healing_done" && TryRecordHealing(args, metadata))
        {
            return;
        }

        if (eventType == "damage_observed" && TryRecordDamageObserved(originalMethod, instance, args, metadata))
        {
            return;
        }

        var amountNames = new[] { "Amount", "amount", "Damage", "damage", "DamageAmount", "FinalDamage", "Block", "block", "Heal", "heal" };
        var amount = FirstNumber(instance, amountNames) ?? FirstNumber(args, amountNames);
        var baseAmount = FirstNumber(instance, ["BaseAmount", "baseAmount", "BaseDamage", "baseDamage", "UnmodifiedDamage"]) ?? FirstNumber(args, ["BaseAmount", "baseAmount", "BaseDamage", "baseDamage", "UnmodifiedDamage"]);
        if (eventType == "healing_done" && (amount ?? 0) <= 0)
        {
            return;
        }

        var actorId = FirstString(instance, ["PlayerId", "playerId", "OwnerId", "ownerId", "ActorId", "actorId", "AttackerId"]) ?? FirstString(args, ["PlayerId", "playerId", "OwnerId", "ownerId", "ActorId", "actorId", "AttackerId"]);
        var actorName = FirstString(instance, ["PlayerName", "playerName", "OwnerName", "ownerName", "ActorName", "actorName", "Attacker", "SourceCreature"]) ?? FirstString(args, ["PlayerName", "playerName", "OwnerName", "ownerName", "ActorName", "actorName", "Attacker", "SourceCreature"]);
        var targetId = FirstString(instance, ["TargetId", "targetId", "EnemyId", "enemyId", "CreatureId"]) ?? FirstString(args, ["TargetId", "targetId", "EnemyId", "enemyId", "CreatureId"]);
        var targetName = FirstString(instance, ["TargetName", "targetName", "EnemyName", "enemyName", "Target", "Creature"]) ?? FirstString(args, ["TargetName", "targetName", "EnemyName", "enemyName", "Target", "Creature"]);
        var sourceName = FirstString(instance, ["SourceName", "sourceName", "CardName", "cardName", "Card", "CardModel", "Name", "name", "Id", "id"]) ?? FirstString(args, ["SourceName", "sourceName", "CardName", "cardName", "Card", "CardModel", "Name", "name", "Id", "id"]);

        NeowLogsMod.Recorder.Record(
            eventType,
            actorPlayerId: actorId,
            actorName: actorName,
            targetId: targetId,
            targetName: targetName,
            amount: amount,
            baseAmount: baseAmount,
            sourceType: "command",
            sourceName: sourceName ?? typeName,
            metadata: metadata);
    }

    private static bool TryRecordPowerApplied(MethodBase? originalMethod, object[] args, Dictionary<string, object?> metadata)
    {
        var powerIndex = FindPowerArgument(originalMethod, args);
        if (powerIndex < 0)
        {
            return false;
        }

        var power = args[powerIndex];
        var target = ParameterValue(originalMethod, args, "target", "receiver", "creature", "enemy") ?? ArgAt(args, powerIndex + 1);
        var amount = Number(ParameterValue(originalMethod, args, "amount", "stacks", "stackAmount"));
        if (amount == 0)
        {
            amount = Number(ArgAt(args, powerIndex + 2));
        }
        if (amount == 0)
        {
            amount = NumberMember(power, "Amount") ?? 1;
        }
        var applier = ParameterValue(originalMethod, args, "applier", "dealer", "sourceCreature", "sourcePlayer")
            ?? ArgAt(args, powerIndex + 3)
            ?? ReadMemberDeep(power, "Applier", 1, []);
        var card = ParameterValue(originalMethod, args, "card", "cardSource", "sourceCard", "model") ?? ArgAt(args, powerIndex + 4);
        var powerType = StringMember(power, "Type");
        var powerName = PowerName(power) ?? "power";
        var status = SimplifyStatus(powerName);

        AddCreatureMetadata(metadata, "actor", applier);
        AddCardMetadata(metadata, card);
        metadata["status"] = status;
        metadata["power"] = powerName;
        metadata["power_type"] = powerType;

        var eventType = string.Equals(powerType, "Debuff", StringComparison.OrdinalIgnoreCase)
            ? "debuff_applied"
            : string.Equals(powerType, "Buff", StringComparison.OrdinalIgnoreCase)
                ? "buff_applied"
                : "power_applied";

        var targets = TargetCreatures(target).ToArray();
        if (targets.Length == 0 && target != null)
        {
            targets = [target];
        }

        foreach (var targetCreature in targets)
        {
            var eventMetadata = new Dictionary<string, object?>(metadata);
            AddCreatureMetadata(eventMetadata, "target", targetCreature);
            NeowLogsMod.Recorder.Record(
                eventType,
                actorPlayerId: CreatureId(applier),
                actorName: CreatureName(applier),
                targetId: CreatureId(targetCreature),
                targetName: CreatureName(targetCreature),
                amount: amount == 0 ? 1 : amount,
                baseAmount: amount == 0 ? 1 : amount,
                sourceType: "power",
                sourceName: CardName(card) ?? status,
                metadata: eventMetadata);
        }

        return true;
    }

    private static bool TryRecordDamageObserved(MethodBase? originalMethod, object? instance, object[] args, Dictionary<string, object?> metadata)
    {
        var amountNames = new[] { "Amount", "amount", "Damage", "damage", "DamageAmount", "FinalDamage", "HpDamage", "UnblockedDamage" };
        var amount = Number(ParameterValue(originalMethod, args, "amount", "damage", "damagePerHit"));
        if (amount <= 0)
        {
            amount = FirstNumber(instance, amountNames) ?? FirstNumber(args, amountNames) ?? 0;
        }
        if (amount <= 0)
        {
            return false;
        }

        metadata["observed_method"] = originalMethod == null
            ? instance?.GetType().FullName ?? "unknown"
            : $"{originalMethod.DeclaringType?.FullName}.{originalMethod.Name}";
        AddParameterMetadata(metadata, originalMethod, args);

        var targetValue = ParameterValue(originalMethod, args, "target", "receiver", "victim", "creature", "enemy");
        var source = ParameterValue(originalMethod, args, "source", "attacker", "owner", "applier", "player", "dealer", "sourceCreature");
        var card = ParameterValue(originalMethod, args, "card", "cardModel", "model");
        var power = ParameterValue(originalMethod, args, "power", "status");

        var targets = TargetCreatures(targetValue).ToArray();
        if (targets.Length == 0)
        {
            var fallbackTarget = FirstCreature(args.AsEnumerable()) ?? FirstCreature(instance);
            targets = fallbackTarget == null ? [] : [fallbackTarget];
        }

        if (source == null)
        {
            source = FirstPlayerCreature(args.Where(arg => !ContainsAnyTarget(arg, targets)))
                ?? FirstCreature(args.Where(arg => !ContainsAnyTarget(arg, targets)))
                ?? FirstCreature(instance);
        }

        if (card == null)
        {
            card = FirstCard(args) ?? FirstCard(instance);
        }

        if (power == null)
        {
            power = FirstPower(args) ?? FirstPower(instance);
        }

        var actor = DamageActor(source, card ?? power ?? source);
        if (actor == null && source != null)
        {
            actor = OwnerOrSelf(source);
        }

        var sourceObject = card ?? power ?? source;
        var sourceType = DamageSourceType(null, sourceObject);
        var sourceName = CardName(card) ?? PowerName(power) ?? DamageSourceName(null, sourceObject);

        foreach (var target in targets)
        {
            var eventMetadata = new Dictionary<string, object?>(metadata);
            AddCreatureMetadata(eventMetadata, "actor", actor);
            AddCreatureMetadata(eventMetadata, "target", target);
            AddCardMetadata(eventMetadata, card);
            eventMetadata["observed_power"] = PowerName(power);
            eventMetadata["observed_card"] = CardName(card);
            eventMetadata["observed_is_player_target"] = IsPlayerLike(target);
            eventMetadata["observed_is_enemy_target"] = IsEnemyLike(target);

            var eventSourceType = sourceType;
            var eventSourceName = sourceName;
            if (actor != null)
            {
                ApplyRecentCardAttribution(eventMetadata, actor, target, ref eventSourceType, ref eventSourceName);
            }
            if (IsDefectOrbDamage(actor, target, eventSourceType, eventSourceName, eventMetadata))
            {
                eventSourceType = "orb";
                eventSourceName = "Lightning/Dark Orb";
                eventMetadata["inferred_orb_source"] = true;
                eventMetadata["orb_name"] = eventSourceName;
            }

            NeowLogsMod.Recorder.Record(
                "damage_observed",
                actorPlayerId: CreatureId(actor),
                actorName: CreatureName(actor),
                targetId: CreatureId(target),
                targetName: CreatureName(target),
                amount: amount,
                baseAmount: amount,
                sourceType: eventSourceType,
                sourceName: eventSourceName,
                metadata: eventMetadata);
        }

        return true;
    }

    private static IEnumerable<object> TargetCreatures(object? value)
    {
        foreach (var item in Enumerate(value))
        {
            if (item == null)
            {
                continue;
            }

            var creature = FirstCreature(item);
            if (creature != null)
            {
                yield return creature;
            }
        }
    }

    private static bool ContainsAnyTarget(object? value, IEnumerable<object> targets)
    {
        if (value == null)
        {
            return false;
        }

        foreach (var item in Enumerate(value))
        {
            if (item != null && targets.Any(target => ReferenceEquals(item, target)))
            {
                return true;
            }
        }

        return false;
    }

    private static object? ArgAt(object[] args, int index)
    {
        return index >= 0 && index < args.Length ? args[index] : null;
    }

    private static int FindPowerArgument(MethodBase? originalMethod, object[] args)
    {
        var parameters = originalMethod?.GetParameters() ?? Array.Empty<ParameterInfo>();
        for (var i = 0; i < Math.Min(parameters.Length, args.Length); i++)
        {
            var name = parameters[i].Name ?? "";
            if (name.Contains("power", StringComparison.OrdinalIgnoreCase) && LooksLikePower(args[i]))
            {
                return i;
            }
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (LooksLikePower(args[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool LooksLikePower(object? value)
    {
        return LooksLikePowerObject(value);
    }

    private static void AddParameterMetadata(Dictionary<string, object?> metadata, MethodBase? originalMethod, object[] args)
    {
        if (originalMethod == null)
        {
            return;
        }

        var parameters = originalMethod.GetParameters();
        for (var i = 0; i < Math.Min(parameters.Length, args.Length); i++)
        {
            metadata[$"param_{i}_name"] = parameters[i].Name;
            metadata[$"param_{i}_type"] = parameters[i].ParameterType.FullName;
        }
    }

    private static object? ParameterValue(MethodBase? originalMethod, object[] args, params string[] names)
    {
        if (originalMethod == null)
        {
            return null;
        }

        var parameters = originalMethod.GetParameters();
        for (var i = 0; i < Math.Min(parameters.Length, args.Length); i++)
        {
            var parameterName = parameters[i].Name ?? "";
            if (names.Any(name => parameterName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                return args[i];
            }
        }

        return null;
    }

    private static object? FirstCreature(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (LooksLikeCreature(value))
        {
            return value;
        }

        foreach (var item in Enumerate(value))
        {
            if (item != null && LooksLikeCreature(item))
            {
                return item;
            }
        }

        return null;
    }

    private static object? FirstCreature(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            var creature = FirstCreature(value);
            if (creature != null)
            {
                return creature;
            }
        }

        return null;
    }

    private static object? FirstPlayerCreature(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            var creature = FirstCreature(value);
            if (IsPlayerLike(creature))
            {
                return creature;
            }
        }

        return null;
    }

    private static object? FirstCard(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (LooksLikeCard(value))
        {
            return value;
        }

        foreach (var item in Enumerate(value))
        {
            if (item != null && LooksLikeCard(item))
            {
                return item;
            }
        }

        return null;
    }

    private static object? FirstPower(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (LooksLikePower(value))
        {
            return value;
        }

        foreach (var item in Enumerate(value))
        {
            if (item != null && LooksLikePower(item))
            {
                return item;
            }
        }

        return null;
    }

    private static bool LooksLikeCreature(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var typeName = value.GetType().FullName ?? "";
        return typeName.Contains(".Creatures.", StringComparison.Ordinal)
            || typeName.Contains("Creature", StringComparison.Ordinal)
            || BoolMember(value, "IsPlayer")
            || BoolMember(value, "IsEnemy")
            || BoolMember(value, "IsMonster")
            || BoolMember(value, "IsPet");
    }

    private static bool TryRecordHealing(object[] args, Dictionary<string, object?> metadata)
    {
        if (args.Length < 2)
        {
            return false;
        }

        var creature = args[0];
        var requested = Number(args[1]);
        var currentHp = NumberMember(creature, "CurrentHp") ?? 0;
        var maxHp = NumberMember(creature, "MaxHp") ?? 0;
        var inCombat = ReadMemberDeep(creature, "CombatState", 1, []) != null;
        if (requested <= 0 || !NeowLogsMod.Recorder.HasActiveRun || NeowLogsMod.Recorder.IsLikelyActOpeningHeal(inCombat, requested, currentHp, maxHp))
        {
            metadata["initial_heal"] = true;
            metadata["in_combat"] = inCombat;
            metadata["requested_heal"] = requested;
            metadata["current_hp"] = currentHp;
            metadata["max_hp"] = maxHp;
            return true;
        }
        metadata["in_combat"] = inCombat;
        metadata["non_combat_heal"] = !inCombat;
        metadata["requested_heal"] = requested;
        metadata["current_hp"] = currentHp;
        metadata["max_hp"] = maxHp;

        AddCreatureMetadata(metadata, "actor", creature);
        NeowLogsMod.Recorder.Record(
            "healing_done",
            actorPlayerId: CreatureId(creature),
            actorName: CreatureName(creature),
            amount: requested,
            baseAmount: requested,
            sourceType: "command",
            sourceName: "Heal",
            metadata: metadata);

        return true;
    }

    private static bool TryRecordCombatHistory(MethodBase originalMethod, object? instance, object[] args, string? eventType)
    {
        if (eventType == null || originalMethod.DeclaringType?.FullName != "MegaCrit.Sts2.Core.Combat.History.CombatHistory")
        {
            return false;
        }

        var methodName = originalMethod.Name;
        var metadata = Snapshot(instance, args);
        metadata["history_method"] = methodName;

        switch (methodName)
        {
            case "CreatureAttacked":
                RecordCreatureAttacked(args, metadata);
                return true;
            case "DamageReceived":
                RecordDamageReceived(args, metadata);
                return true;
            case "BlockGained":
                RecordBlockGained(args, metadata);
                return true;
            case "CardPlayStarted":
                RecordCardPlay(args, metadata, "card_played");
                return true;
            case "CardPlayFinished":
                RecordCardPlay(args, metadata, "card_play_finished");
                return true;
        }

        return false;
    }

    private static void RecordCreatureAttacked(object[] args, Dictionary<string, object?> metadata)
    {
        var attacker = args.Length > 1 ? args[1] : null;
        var results = args.Length > 2 ? Enumerate(args[2]).ToArray() : [];
        foreach (var result in results)
        {
            if (result == null)
            {
                continue;
            }

            var receiver = ReadMemberDeep(result, "Receiver", 1, []);
            AddDamageResultMetadata(metadata, result);
            var amount = DamageDealtAmount(result, receiver);
            var damageSource = DamageSource(result) ?? attacker;
            var owner = DamageActor(attacker, damageSource);
            var sourceType = DamageSourceType(result, damageSource);
            var sourceName = DamageSourceName(result, damageSource);
            var isCompanionDamage = IsPlayerOwnedCompanionDamage(owner, damageSource);
            if (isCompanionDamage)
            {
                sourceType = "pet";
                sourceName = CreatureName(damageSource) ?? sourceName;
                metadata["indirect_damage_kind"] = "companion";
                metadata["companion_name"] = sourceName;
            }
            if (amount <= 0 && BoolMember(result, "WasTargetKilled") && IsIndirectDamageText(sourceType, sourceName, damageSource))
            {
                amount = LethalDamageAmount(result);
                metadata["lethal_damage"] = amount;
            }
            if (amount <= 0)
            {
                continue;
            }

            AddCreatureMetadata(metadata, "actor", owner);
            if (!ReferenceEquals(owner, damageSource))
            {
                AddCreatureMetadata(metadata, "pet", damageSource);
            }
            AddCreatureMetadata(metadata, "target", receiver);
            AddDamageSourceMetadata(metadata, damageSource);
            ApplyRecentCardAttribution(metadata, owner, receiver, ref sourceType, ref sourceName);
            if (IsDefectOrbDamage(owner, receiver, sourceType, sourceName, metadata))
            {
                sourceType = "orb";
                sourceName = "Lightning/Dark Orb";
                metadata["inferred_orb_source"] = true;
                metadata["orb_name"] = sourceName;
            }
            NeowLogsMod.Recorder.Record(
                "damage_dealt",
                actorPlayerId: CreatureId(owner),
                actorName: CreatureName(owner),
                targetId: CreatureId(receiver),
                targetName: CreatureName(receiver),
                amount: amount,
                baseAmount: amount,
                sourceType: sourceType,
                sourceName: sourceName,
                metadata: metadata);
        }
    }

    private static void RecordDamageReceived(object[] args, Dictionary<string, object?> metadata)
    {
        var receiver = args.Length > 1 ? args[1] : null;
        var isPlayer = BoolMember(receiver, "IsPlayer");
        var isPlayerPet = BoolMember(receiver, "IsPet") && string.Equals(StringMember(receiver, "Side"), "Player", StringComparison.OrdinalIgnoreCase);
        if (!isPlayer && !isPlayerPet)
        {
            return;
        }

        var source = args.Length > 2 ? args[2] : null;
        var result = args.Length > 3 ? args[3] : null;
        var card = args.Length > 4 ? args[4] : null;
        AddDamageResultMetadata(metadata, result);
        var actor = isPlayerPet ? OwnerOrSelf(receiver) : receiver;
        if (isPlayerPet)
        {
            metadata["pet_damage_absorbed"] = true;
            AddCreatureMetadata(metadata, "pet", receiver);
            metadata["blocked_damage"] = Math.Max(Number(metadata.GetValueOrDefault("blocked_damage")), Number(metadata.GetValueOrDefault("total_damage")));
        }

        var amount = isPlayerPet ? 0 : DamageTakenAmount(result);
        if (amount <= 0 && Number(metadata.GetValueOrDefault("blocked_damage")) <= 0)
        {
            return;
        }

        AddCreatureMetadata(metadata, "actor", actor);
        AddCreatureMetadata(metadata, "target", source);
        AddCardMetadata(metadata, card);
        NeowLogsMod.Recorder.Record(
            "damage_taken",
            actorPlayerId: CreatureId(actor),
            actorName: CreatureName(actor),
            targetId: CreatureId(source),
            targetName: CreatureName(source),
            amount: amount,
            sourceType: "combat_history",
            sourceName: CardName(card) ?? CreatureName(source),
            metadata: metadata);
    }

    private static void RecordBlockGained(object[] args, Dictionary<string, object?> metadata)
    {
        var creature = args.Length > 1 ? args[1] : null;
        var amount = args.Skip(2).Select(Number).FirstOrDefault(value => value > 0);
        if (amount <= 0)
        {
            amount = NumberMember(creature, "Block") ?? 0;
        }

        if (amount <= 0)
        {
            return;
        }

        var source = BlockSource(args);
        var actor = creature;
        AddCreatureMetadata(metadata, "actor", actor);
        AddCardMetadata(metadata, source);
        AddDamageSourceMetadata(metadata, source);
        var sourceType = DamageSourceType(null, source);
        var sourceName = DamageSourceName(null, source);
        if (IsDefectFrostBlock(actor, sourceType, sourceName, metadata))
        {
            sourceType = "orb";
            sourceName = "Frost Orb";
            metadata["inferred_orb_source"] = true;
            metadata["orb_name"] = sourceName;
            metadata["damage_source_type"] = sourceType;
            metadata["damage_source_name"] = sourceName;
        }
        NeowLogsMod.Recorder.Record(
            "block_gained",
            actorPlayerId: CreatureId(actor),
            actorName: CreatureName(actor),
            amount: amount,
            sourceType: sourceType,
            sourceName: sourceName,
            metadata: metadata);
    }

    private static void RecordCardPlay(object[] args, Dictionary<string, object?> metadata, string eventType)
    {
        var combatState = args.Length > 0 ? args[0] : null;
        var cardPlay = args.Length > 1 ? args[1] : null;
        if (cardPlay == null)
        {
            return;
        }

        var card = ReadMemberDeep(cardPlay, "Card", 1, []);
        var target = ReadMemberDeep(cardPlay, "Target", 1, []);
        var owner = card == null ? null : ReadMemberDeep(card, "Owner", 1, []);
        var actor = CreatureForPlayer(combatState, owner) ?? owner;

        AddCreatureMetadata(metadata, "actor", actor);
        AddCreatureMetadata(metadata, "target", target);
        AddCardMetadata(metadata, card);
        RememberRecentCard(eventType, actor, card);
        NeowLogsMod.Recorder.Record(
            eventType,
            actorPlayerId: CreatureId(actor),
            actorName: CreatureName(actor),
            targetId: CreatureId(target),
            targetName: CreatureName(target),
            amount: NumberMember(cardPlay, "PlayCount"),
            sourceType: "card",
            sourceName: CardName(card),
            metadata: metadata);
    }

    private static string? GuessEventType(string typeName)
    {
        var lower = typeName.ToLowerInvariant();
        if (lower.Contains("attack") || lower.Contains("damage"))
        {
            return "damage_dealt";
        }
        if (lower.Contains("block"))
        {
            return "block_gained";
        }
        if (lower.Contains("heal"))
        {
            return "healing_done";
        }
        if (lower.Contains("power"))
        {
            return "debuff_applied";
        }
        if (lower.Contains("cardplay"))
        {
            return "card_played";
        }
        return null;
    }

    private static Dictionary<string, object?> Snapshot(object? instance, object[] args)
    {
        var data = new Dictionary<string, object?>
        {
            ["command_type"] = instance?.GetType().FullName ?? "static_hook",
            ["arg_count"] = args.Length
        };

        if (CaptureVerboseSnapshots && instance != null)
        {
            foreach (var member in SnapshotMembersFor(instance.GetType()))
            {
                try
                {
                    if (member is PropertyInfo property && property.GetIndexParameters().Length == 0)
                    {
                        data[property.Name] = SafeValue(property.GetValue(instance));
                    }
                    else if (member is FieldInfo field)
                    {
                        data[field.Name] = SafeValue(field.GetValue(instance));
                    }
                }
                catch
                {
                }
            }
        }

        for (var i = 0; i < args.Length; i++)
        {
            data[$"arg_{i}"] = SafeValue(args[i]);
            if (CaptureVerboseSnapshots)
            {
                data[$"arg_{i}_members"] = SnapshotMembers(args[i], 1);
            }
        }

        return data;
    }

    private static object? SafeValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var type = value.GetType();
        if (type.IsPrimitive || value is string || value is decimal)
        {
            return value;
        }

        try
        {
            return value.ToString();
        }
        catch
        {
            return type.FullName ?? type.Name;
        }
    }

    private static Dictionary<string, object?> SnapshotMembers(object? value, int depth)
    {
        var data = new Dictionary<string, object?>();
        if (value == null || depth < 0)
        {
            return data;
        }

        var type = value.GetType();
        foreach (var member in SnapshotMembersFor(type))
        {
            try
            {
                object? memberValue = null;
                if (member is PropertyInfo property && property.GetIndexParameters().Length == 0)
                {
                    memberValue = property.GetValue(value);
                }
                else if (member is FieldInfo field)
                {
                    memberValue = field.GetValue(value);
                }
                else
                {
                    continue;
                }

                data[member.Name] = SafeValue(memberValue);
            }
            catch
            {
            }
        }

        return data;
    }

    private static MemberInfo[] SnapshotMembersFor(Type type)
    {
        lock (SnapshotMemberCache)
        {
            if (SnapshotMemberCache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var members = type
                .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(member => member is FieldInfo || member is PropertyInfo property && property.GetIndexParameters().Length == 0)
                .ToArray();
            SnapshotMemberCache[type] = members;
            return members;
        }
    }

    private static double? FirstNumber(object? instance, string[] names)
    {
        if (instance == null)
        {
            return null;
        }
        foreach (var name in names)
        {
            var value = ReadMemberDeep(instance, name, 2, []);
            if (value == null)
            {
                continue;
            }

            if (double.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? FirstString(object? instance, string[] names)
    {
        if (instance == null)
        {
            return null;
        }
        foreach (var name in names)
        {
            var value = ReadMemberDeep(instance, name, 2, []);
            if (value != null)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static object? ReadMember(object instance, string name)
    {
        var type = instance.GetType();
        var key = (type, name.ToLowerInvariant());
        MemberInfo? member;
        lock (DirectMemberCache)
        {
            if (!DirectMemberCache.TryGetValue(key, out member))
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                member = type.GetProperty(name, flags);
                if (member is PropertyInfo property && property.GetIndexParameters().Length > 0)
                {
                    member = null;
                }

                member ??= type.GetField(name, flags);
                DirectMemberCache[key] = member;
            }
        }

        return member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => null
        };
    }

    private static object? ReadMemberDeep(object instance, string name, int depth, HashSet<object> seen)
    {
        if (depth < 0 || !seen.Add(instance))
        {
            return null;
        }

        if (instance is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }
                var nestedItem = ReadMemberDeep(item, name, depth - 1, seen);
                if (nestedItem != null)
                {
                    return nestedItem;
                }
            }
        }

        var direct = ReadMember(instance, name);
        if (direct != null)
        {
            return direct;
        }

        foreach (var member in instance.GetType().GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            try
            {
                object? value = member switch
                {
                    PropertyInfo property when property.GetIndexParameters().Length == 0 => property.GetValue(instance),
                    FieldInfo field => field.GetValue(instance),
                    _ => null
                };

                if (value == null || value is string || value.GetType().IsPrimitive)
                {
                    continue;
                }

                var nested = ReadMemberDeep(value, name, depth - 1, seen);
                if (nested != null)
                {
                    return nested;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<object?> Enumerate(object? value)
    {
        if (value == null)
        {
            yield break;
        }

        if (value is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                yield return item;
            }
            yield break;
        }

        yield return value;
    }

    private static double Number(object? value)
    {
        if (value == null)
        {
            return 0;
        }

        if (value is int i)
        {
            return i;
        }

        if (value is double d)
        {
            return d;
        }

        if (value is float f)
        {
            return f;
        }

        if (value is decimal m)
        {
            return (double)m;
        }

        return double.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static double? NumberMember(object? value, string member)
    {
        if (value == null)
        {
            return null;
        }

        var raw = ReadMemberDeep(value, member, 1, []);
        if (raw == null)
        {
            return null;
        }

        return Number(raw);
    }

    private static bool BoolMember(object? value, string member)
    {
        if (value == null)
        {
            return false;
        }

        var raw = ReadMemberDeep(value, member, 1, []);
        return raw is bool b && b;
    }

    private static string? CreatureName(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (IsPlayerLike(value))
        {
            var playerName = PlayerDisplayName(value);
            if (!string.IsNullOrWhiteSpace(playerName))
            {
                return playerName;
            }
        }

        foreach (var member in new[] { "Name", "LogName", "ModelId", "Id" })
        {
            var raw = ReadMemberDeep(value, member, 1, []);
            var text = CleanDisplayName(raw);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return value.ToString();
    }

    private static string? CreatureId(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (IsPlayerLike(value))
        {
            var stablePlayerId = PlayerStableId(value);
            if (!string.IsNullOrWhiteSpace(stablePlayerId))
            {
                return stablePlayerId;
            }
        }

        foreach (var member in new[] { "CombatId", "ModelId", "Id" })
        {
            var raw = ReadMemberDeep(value, member, 1, []);
            if (raw != null)
            {
                return raw.ToString();
            }
        }

        return null;
    }

    private static string? PlayerStableId(object value)
    {
        foreach (var member in new[] { "SteamId", "SteamID", "steam_id", "UserId", "UserID", "PlayerId", "Id", "LogName" })
        {
            var raw = ReadMemberDeep(value, member, 1, [])?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (raw.StartsWith("PlayerId ", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw["PlayerId ".Length..].Trim();
            }

            if (raw.Length >= 6 && raw.All(char.IsDigit))
            {
                return raw;
            }
        }

        return null;
    }

    private static object? OwnerOrSelf(object? creature)
    {
        if (creature == null)
        {
            return null;
        }

        var owner = ReadMemberDeep(creature, "PetOwner", 1, [])
            ?? ReadMemberDeep(creature, "_petOwner", 1, [])
            ?? ReadMemberDeep(creature, "Owner", 1, []);
        return owner ?? creature;
    }

    private static object? DamageOwner(object? attacker, object? damageSource)
    {
        return PlayerOwnerOrSelf(attacker)
            ?? PlayerOwnerOrSelf(damageSource)
            ?? PlayerOwnerFromMembers(damageSource)
            ?? PlayerOwnerFromMembers(attacker);
    }

    private static object? DamageActor(object? attacker, object? damageSource)
    {
        if (IsEnemyLike(attacker))
        {
            return attacker;
        }

        return DamageOwner(attacker, damageSource)
            ?? OwnerOrSelf(attacker)
            ?? OwnerOrSelf(damageSource);
    }

    private static void RememberRecentCard(string eventType, object? actor, object? card)
    {
        if (eventType != "card_played" || actor == null || card == null || !IsPlayerLike(actor))
        {
            return;
        }

        var actorId = CreatureId(actor);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return;
        }

        RecentCardByActor[actorId] = new RecentCard(
            actorId,
            StringMember(card, "Id"),
            CardName(card),
            StringMember(card, "Type"),
            DateTime.UtcNow.Ticks);
    }

    private static void ApplyRecentCardAttribution(Dictionary<string, object?> metadata, object? actor, object? receiver, ref string sourceType, ref string sourceName)
    {
        if (!IsPlayerLike(actor) || !IsEnemyLike(receiver) || !IsGenericAttackSource(sourceType, sourceName))
        {
            return;
        }

        var actorId = CreatureId(actor);
        if (string.IsNullOrWhiteSpace(actorId) || !RecentCardByActor.TryGetValue(actorId, out var recent))
        {
            return;
        }

        if (DateTime.UtcNow.Ticks - recent.SeenAtTicks > TimeSpan.FromSeconds(12).Ticks)
        {
            RecentCardByActor.Remove(actorId);
            return;
        }

        if (string.IsNullOrWhiteSpace(recent.CardName))
        {
            return;
        }

        if (!string.Equals(NormalizeCardType(recent.CardType), "attack", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sourceType = "card";
        sourceName = recent.CardName!;
        metadata["inferred_card_source"] = true;
        PutIfMissing(metadata, "card_id", recent.CardId);
        PutIfMissing(metadata, "card_name", recent.CardName);
        PutIfMissing(metadata, "card_type", recent.CardType);
    }

    private static void PutIfMissing(Dictionary<string, object?> metadata, string key, object? value)
    {
        if (!metadata.ContainsKey(key) || metadata[key] == null)
        {
            metadata[key] = value;
        }
    }

    private static string NormalizeCardType(string? cardType)
    {
        var type = cardType?.ToLowerInvariant() ?? "";
        return type.Contains('.') ? type.Split('.').Last() : type;
    }

    private static bool IsGenericAttackSource(string? sourceType, string? sourceName)
    {
        var type = sourceType ?? "";
        var name = sourceName ?? "";
        return name.Equals("Attack", StringComparison.OrdinalIgnoreCase)
            || name.Equals("combat_history", StringComparison.OrdinalIgnoreCase)
            || type.Equals("combat_history", StringComparison.OrdinalIgnoreCase);
    }

    private static object? PlayerOwnerOrSelf(object? value)
    {
        var owner = OwnerOrSelf(value);
        return IsPlayerLike(owner) ? owner : null;
    }

    private static object? PlayerOwnerFromMembers(object? value)
    {
        if (value == null)
        {
            return null;
        }

        foreach (var member in new[] { "Owner", "PetOwner", "_petOwner", "Applier", "SourcePlayer", "SourceOwner", "Player", "Card", "Power" })
        {
            var nested = ReadMemberDeep(value, member, 2, []);
            var owner = PlayerOwnerOrSelf(nested);
            if (owner != null)
            {
                return owner;
            }
        }

        return null;
    }

    private static object? DamageSource(object? result)
    {
        if (result == null)
        {
            return null;
        }

        foreach (var member in new[] { "Source", "DamageSource", "Attacker", "Owner", "Applier", "Player", "Creature", "Power", "Orb", "Card" })
        {
            var value = ReadMemberDeep(result, member, 1, []);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static object? BlockSource(object[] args)
    {
        foreach (var arg in args.Reverse())
        {
            if (arg == null)
            {
                continue;
            }

            var name = CardName(arg) ?? PowerName(arg) ?? StringMember(arg, "Id") ?? StringMember(arg, "ModelId") ?? arg.ToString();
            var text = name?.ToLowerInvariant() ?? "";
            if (CardName(arg) != null
                || PowerName(arg) != null
                || text.Contains("orb")
                || text.Contains("frost")
                || text.Contains("block"))
            {
                return arg;
            }
        }

        return null;
    }

    private static bool LooksLikeOrb(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var typeName = value.GetType().FullName ?? "";
        if (typeName.Contains(".Orbs.", StringComparison.Ordinal)
            || typeName.Contains("Orb", StringComparison.Ordinal))
        {
            return true;
        }

        var text = $"{value} {StringMember(value, "Id")} {StringMember(value, "ModelId")} {StringMember(value, "Name")}".ToLowerInvariant();
        return text.Contains("orb")
            || text.Contains("lightning")
            || text.Contains("frost")
            || text.Contains("dark");
    }

    private static bool IsDefectOrbDamage(object? actor, object? target, string sourceType, string sourceName, Dictionary<string, object?> metadata)
    {
        return IsDefectLike(actor)
            && IsEnemyLike(target)
            && IsGenericAttackSource(sourceType, sourceName)
            && !HasCardOrPowerSource(metadata);
    }

    private static bool IsDefectFrostBlock(object? actor, string sourceType, string sourceName, Dictionary<string, object?> metadata)
    {
        return IsDefectLike(actor)
            && sourceType.Equals("combat_history", StringComparison.OrdinalIgnoreCase)
            && sourceName.Equals("Block", StringComparison.OrdinalIgnoreCase)
            && !HasCardOrPowerSource(metadata);
    }

    private static bool IsPlayerOwnedCompanionDamage(object? owner, object? damageSource)
    {
        return owner != null
            && damageSource != null
            && !ReferenceEquals(owner, damageSource)
            && IsPlayerLike(owner)
            && (BoolMember(damageSource, "IsPet")
                || string.Equals(StringMember(damageSource, "Side"), "Player", StringComparison.OrdinalIgnoreCase)
                || (CreatureName(damageSource) ?? "").Contains("Otsy", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDefectLike(object? actor)
    {
        if (actor == null)
        {
            return false;
        }

        var modelId = StringMember(actor, "ModelId") ?? "";
        var name = CreatureName(actor) ?? "";
        return modelId.Contains("DEFECT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Defect", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerChoiceContext(Dictionary<string, object?> metadata)
    {
        return metadata.TryGetValue("arg_0", out var value)
            && value?.ToString()?.Contains("PlayerChoiceContext", StringComparison.Ordinal) == true;
    }

    private static bool HasCardOrPowerSource(Dictionary<string, object?> metadata)
    {
        return !string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("card_id")?.ToString())
            || !string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("card_name")?.ToString())
            || !string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("observed_card")?.ToString())
            || !string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("power")?.ToString())
            || !string.IsNullOrWhiteSpace(metadata.GetValueOrDefault("observed_power")?.ToString());
    }

    private static string DamageSourceType(object? result, object? source)
    {
        var text = $"{result} {source} {StringMember(source, "Id")} {StringMember(source, "ModelId")}".ToLowerInvariant();
        if (LooksLikeCard(source))
        {
            return "card";
        }
        if (text.Contains("poison"))
        {
            return "poison";
        }
        if (text.Contains("doom"))
        {
            return "doom";
        }
        if (LooksLikeOrb(source))
        {
            return "orb";
        }
        if (text.Contains("power"))
        {
            return "power";
        }
        return "combat_history";
    }

    private static string DamageSourceName(object? result, object? source)
    {
        if (source == null)
        {
            return "Block";
        }

        if (BoolMember(source, "IsPlayer") || BoolMember(source, "IsMonster") || BoolMember(source, "IsEnemy") || BoolMember(source, "IsPet"))
        {
            return "Attack";
        }

        return CardName(source)
            ?? PowerName(source)
            ?? StringMember(source, "Title")
            ?? StringMember(source, "Name")
            ?? StringMember(source, "Id")
            ?? StringMember(result, "SourceName")
            ?? DamageSourceType(result, source);
    }

    private static void AddDamageSourceMetadata(Dictionary<string, object?> metadata, object? source)
    {
        if (source == null)
        {
            return;
        }

        metadata["damage_source_id"] = StringMember(source, "Id") ?? StringMember(source, "ModelId");
        metadata["damage_source_name"] = DamageSourceName(null, source);
        metadata["damage_source_type"] = DamageSourceType(null, source);
        metadata["damage_source_power"] = PowerName(source);
        metadata["damage_source_card"] = CardName(source);
    }

    private static object? CreatureForPlayer(object? combatState, object? player)
    {
        if (combatState == null || player == null)
        {
            return null;
        }

        foreach (var member in new[] { "PlayerCreatures", "Creatures", "Allies" })
        {
            var creatures = ReadMemberDeep(combatState, member, 1, []);
            foreach (var creature in Enumerate(creatures))
            {
                if (creature == null || !BoolMember(creature, "IsPlayer"))
                {
                    continue;
                }

                var creaturePlayer = ReadMemberDeep(creature, "Player", 1, []);
                if (ReferenceEquals(creaturePlayer, player))
                {
                    return creature;
                }
            }
        }

        return null;
    }

    private static void AddDamageResultMetadata(Dictionary<string, object?> metadata, object? result)
    {
        if (result == null)
        {
            return;
        }

        var total = NumberMemberAny(result, "TotalDamage", "FinalDamage", "Damage", "Amount");
        var unblocked = NumberMemberAny(result, "UnblockedDamage", "HpDamage", "HealthDamage", "DamageToHp", "HpLoss");
        var blocked = NumberMemberAny(result, "BlockedDamage", "BlockDamage", "DamageBlocked", "BlockedAmount", "DamageToBlock");
        var original = NumberMemberAny(result, "OriginalDamage", "BaseDamage", "RawDamage", "IntentDamage", "IncomingDamage", "PreventionBaseDamage");

        metadata["total_damage"] = total ?? 0;
        metadata["unblocked_damage"] = unblocked ?? total ?? 0;
        metadata["blocked_damage"] = blocked ?? Math.Max(0, (total ?? 0) - (unblocked ?? total ?? 0));
        metadata["original_damage"] = original ?? total ?? unblocked ?? 0;
        metadata["overkill_damage"] = NumberMemberAny(result, "OverkillDamage") ?? 0;
        metadata["target_killed"] = BoolMember(result, "WasTargetKilled");
        metadata["lethal_damage"] = LethalDamageAmount(result);
        metadata["prevented_damage"] = Math.Max(0, (original ?? 0) - (total ?? unblocked ?? 0));
    }

    private static double DamageTakenAmount(object? result)
    {
        var unblocked = NumberMemberAny(result, "UnblockedDamage", "HpDamage", "HealthDamage", "DamageToHp", "HpLoss");
        if (unblocked != null)
        {
            return unblocked.Value;
        }

        var total = NumberMemberAny(result, "TotalDamage", "FinalDamage", "Damage", "Amount") ?? 0;
        var blocked = NumberMemberAny(result, "BlockedDamage", "BlockDamage", "DamageBlocked", "BlockedAmount", "DamageToBlock") ?? 0;
        return Math.Max(0, total - blocked);
    }

    private static double DamageDealtAmount(object? result, object? receiver)
    {
        var total = NumberMemberAny(result, "TotalDamage", "FinalDamage", "Damage", "Amount") ?? 0;
        var overkill = NumberMemberAny(result, "OverkillDamage") ?? 0;
        var overkillFriendly = Math.Max(total + overkill, NumberMemberAny(result, "OriginalDamage", "BaseDamage", "RawDamage", "IntentDamage", "UnblockedDamage") ?? 0);
        var hpDamage = NumberMemberAny(result, "UnblockedDamage", "HpDamage", "HealthDamage", "DamageToHp", "HpLoss") ?? overkillFriendly;
        var receiverHp = NumberMember(receiver, "CurrentHp") ?? 0;
        if (receiverHp <= 0 && overkillFriendly > hpDamage)
        {
            return overkillFriendly;
        }

        return Math.Max(overkillFriendly, hpDamage);
    }

    private static double LethalDamageAmount(object? result)
    {
        return Math.Max(
            Math.Max(NumberMemberAny(result, "TotalDamage", "FinalDamage", "Damage", "Amount") ?? 0,
                NumberMemberAny(result, "UnblockedDamage", "HpDamage", "HealthDamage", "DamageToHp", "HpLoss") ?? 0),
            Math.Max(NumberMemberAny(result, "OriginalDamage", "BaseDamage", "RawDamage", "IntentDamage") ?? 0,
                NumberMemberAny(result, "OverkillDamage") ?? 0));
    }

    private static bool IsIndirectDamageText(string? sourceType, string? sourceName, object? source)
    {
        var text = $"{sourceType} {sourceName} {source} {StringMember(source, "Id")} {StringMember(source, "ModelId")}".ToLowerInvariant();
        return text.Contains("poison") || text.Contains("doom");
    }

    private static double? NumberMemberAny(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var number = NumberMember(value, member);
            if (number != null)
            {
                return number;
            }
        }

        return null;
    }

    private static string? CardName(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (!LooksLikeCard(value))
        {
            return null;
        }

        foreach (var member in new[] { "Title", "Id", "Name" })
        {
            var raw = ReadMemberDeep(value, member, 1, []);
            if (raw != null)
            {
                return raw.ToString();
            }
        }

        var text = value.ToString();
        return text?.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase) == true ? text : null;
    }

    private static void AddCreatureMetadata(Dictionary<string, object?> metadata, string prefix, object? creature)
    {
        if (creature == null)
        {
            return;
        }

        var side = StringMember(creature, "Side");
        var isPet = BoolMember(creature, "IsPet");
        metadata[$"{prefix}_is_player"] = IsPlayerLike(creature);
        metadata[$"{prefix}_is_pet"] = isPet;
        metadata[$"{prefix}_is_enemy"] = (BoolMember(creature, "IsEnemy") || BoolMember(creature, "IsMonster"))
            && !isPet
            && !string.Equals(side, "Player", StringComparison.OrdinalIgnoreCase);
        metadata[$"{prefix}_side"] = side;
        metadata[$"{prefix}_model_id"] = StringMember(creature, "ModelId");
        metadata[$"{prefix}_display_name"] = CreatureName(creature);
    }

    private static string? PlayerDisplayName(object creature)
    {
        var player = ReadMemberDeep(creature, "Player", 1, []);
        foreach (var source in new[] { creature, player })
        {
            if (source == null)
            {
                continue;
            }

            foreach (var member in new[]
                     {
                         "Name",
                         "DisplayName",
                         "displayName",
                         "PlayerName",
                         "playerName",
                         "UserName",
                         "Username",
                         "SteamName",
                         "SteamPersonaName",
                         "PersonaName",
                         "NickName",
                         "Nickname",
                         "ProfileName",
                         "LogName"
                     })
            {
                var text = CleanDisplayName(ReadMemberDeep(source, member, 2, []));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return CharacterName(creature);
    }

    private static bool IsPlayerLike(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var side = StringMember(value, "Side");
        var isPet = BoolMember(value, "IsPet");
        if (string.Equals(side, "Enemy", StringComparison.OrdinalIgnoreCase)
            || ((BoolMember(value, "IsEnemy") || BoolMember(value, "IsMonster"))
                && !isPet
                && !string.Equals(side, "Player", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (BoolMember(value, "IsPlayer"))
        {
            return true;
        }

        var typeName = value.GetType().FullName ?? "";
        if (typeName.Contains(".Players.", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(side, "Player", StringComparison.OrdinalIgnoreCase)
            || ReadMemberDeep(value, "Player", 1, []) != null;
    }

    private static bool IsEnemyLike(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var side = StringMember(value, "Side");
        if (string.Equals(side, "Enemy", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isPet = BoolMember(value, "IsPet");
        return (BoolMember(value, "IsEnemy") || BoolMember(value, "IsMonster"))
            && !isPet
            && !string.Equals(side, "Player", StringComparison.OrdinalIgnoreCase);
    }

    private static string? CharacterName(object creature)
    {
        var modelId = CleanDisplayName(ReadMemberDeep(creature, "ModelId", 1, []));
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        if (modelId.StartsWith("CHARACTER.", StringComparison.OrdinalIgnoreCase))
        {
            modelId = modelId["CHARACTER.".Length..];
        }

        return string.Join(" ", modelId.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(TitleCase));
    }

    private static string? CleanDisplayName(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var text = value.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.StartsWith("MegaCrit.", StringComparison.Ordinal)
            || text.StartsWith("System.", StringComparison.Ordinal)
            || text.StartsWith("PlayerId ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("<", StringComparison.Ordinal)
            || text.Contains("#", StringComparison.Ordinal))
        {
            return null;
        }

        if (double.TryParse(text, out _))
        {
            return null;
        }

        return text;
    }

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var lower = value.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static void AddCardMetadata(Dictionary<string, object?> metadata, object? card)
    {
        if (card == null)
        {
            return;
        }

        metadata["card_id"] = StringMember(card, "Id");
        metadata["card_name"] = CardName(card);
        metadata["card_type"] = StringMember(card, "Type");
        metadata["card_rarity"] = StringMember(card, "Rarity");
        metadata["energy_spent"] = NumberMember(card, "LastEnergySpent")
            ?? NumberMember(card, "LastEnergyCost")
            ?? NumberMember(card, "CanonicalEnergyCost")
            ?? NumberMember(ReadMemberDeep(card, "EnergyCost", 1, []), "Amount")
            ?? 0;
    }

    private static string? PowerName(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (!LooksLikePower(value))
        {
            return null;
        }

        foreach (var member in new[] { "Id", "Title", "Name" })
        {
            var raw = ReadMemberDeep(value, member, 1, []);
            if (raw != null)
            {
                return raw.ToString();
            }
        }

        var text = value.ToString();
        return text?.StartsWith("POWER.", StringComparison.OrdinalIgnoreCase) == true ? text : null;
    }

    private static bool LooksLikeCard(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var text = value.ToString() ?? "";
        if (text.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var typeName = value.GetType().FullName ?? "";
        if (typeName.Contains(".Cards.", StringComparison.Ordinal)
            || typeName.Contains("CardModel", StringComparison.Ordinal)
            || typeName.EndsWith(".Card", StringComparison.Ordinal)
            || typeName.EndsWith("Card", StringComparison.Ordinal))
        {
            return true;
        }

        var id = ReadMemberDeep(value, "Id", 1, [])?.ToString();
        return id?.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool LooksLikePowerObject(object? value)
    {
        if (value == null)
        {
            return false;
        }

        var text = value.ToString() ?? "";
        if (text.StartsWith("POWER.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var typeName = value.GetType().FullName ?? "";
        if (typeName.Contains(".Powers.", StringComparison.Ordinal)
            || typeName.Contains("PowerModel", StringComparison.Ordinal)
            || typeName.EndsWith(".Power", StringComparison.Ordinal))
        {
            return true;
        }

        var id = ReadMemberDeep(value, "Id", 1, [])?.ToString();
        return id?.StartsWith("POWER.", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? StringMember(object? value, string member)
    {
        if (value == null)
        {
            return null;
        }

        return ReadMemberDeep(value, member, 1, [])?.ToString();
    }

    private static string SimplifyStatus(string value)
    {
        var text = value;
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text[..paren];
        }

        text = text.Trim();
        if (text.StartsWith("POWER.", StringComparison.OrdinalIgnoreCase))
        {
            text = text["POWER.".Length..];
        }

        if (text.EndsWith("_POWER", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^"_POWER".Length];
        }

        return text.ToLowerInvariant();
    }

    private static double? FirstNumber(IEnumerable<object?> values, string[] names)
    {
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }
            if (double.TryParse(value.ToString(), out var direct))
            {
                return direct;
            }
            if (value is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    var nestedItem = FirstNumber(item, names);
                    if (nestedItem != null)
                    {
                        return nestedItem;
                    }
                }
            }
            var nested = FirstNumber(value, names);
            if (nested != null)
            {
                return nested;
            }
        }
        return null;
    }

    private static string? FirstString(IEnumerable<object?> values, string[] names)
    {
        foreach (var value in values)
        {
            if (value == null)
            {
                continue;
            }
            if (value is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    var nestedItem = FirstString(item, names);
                    if (!string.IsNullOrWhiteSpace(nestedItem))
                    {
                        return nestedItem;
                    }
                }
            }
            var nested = FirstString(value, names);
            if (!string.IsNullOrWhiteSpace(nested))
            {
                return nested;
            }
        }
        return null;
    }
}

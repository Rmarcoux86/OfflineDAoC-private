using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Globalization;

namespace OfflineDaoc.Launcher;

/// <summary>
/// The Character Planner's data, read once (on first use) from the game's SQLite database, read-only, and combined with
/// the tables lifted from the server (<see cref="CharacterPlannerClasses"/>, <see cref="CharacterPlannerRealmAbilities"/>).
/// Each list mirrors how the server builds it (gameutils/SkillBase.cs and gameutils/Specialization.cs):
/// - specs: the class's ClassXSpecialization career rows (they already include the base class's career); negative
///   LevelAcquired values only order the list and mean "from level 1" (Specialization.LevelRequired, GamePlayer adds a
///   spec once Level >= LevelAcquired). Career lines (LiveCareerSpecialization, ...) are untrainable and follow the
///   character's level; the realm ability, champion and master level lines and the ClassID 0 career are left out.
/// - spells: Specialization.GetSpellLinesForLiving - the spec's baseline lines with the class's ClassIDHint (else hint 0)
///   and its specialization lines with the class's hint (else hint 0). Baseline spells unlock at the character's level
///   and their LineName ends with <see cref="BaselineSuffix"/>; the others unlock at the spec's level.
/// - styles: Specialization.GetStylesForLiving - the class's styles for the spec, else the ClassId 0 ones.
/// - abilities: SkillBase.GetSpecAbilityList - SpecXAbility rows for ClassId 0 or the class; Level is the spec level.
/// - realm abilities: ClassXRealmAbility_Atlas, priced by the handler table.
/// Never throws: a missing, locked or broken database leaves the lists empty and sets <see cref="LoadError"/>.
/// </summary>
internal sealed class CharacterPlannerData : ICharacterPlannerSource
{
    /// <summary>Appended to the LineName of a baseline spell line; those spells unlock at the character's level.</summary>
    public const string BaselineSuffix = " (baseline)";

    // Specialization.Implementation classes whose Trainable is false (UntrainableSpecialization and its subclasses,
    // gameutils/Specialization.cs and gameutils/Specializations/*.cs; the LiveCL*Spec mini lines are handled by prefix).
    private static readonly HashSet<string> UntrainableImplementations = new(StringComparer.OrdinalIgnoreCase)
    {
        "UntrainableSpecialization", "CareerSpecialization", "LiveCareerSpecialization", "LiveWideCareerSpecialization",
        "LiveChampionsSpecialization", "LiveMasterLevelsSpecialization", "LiveRealmAbilitiesSpecialization",
        "MiniLineSpecialization", "LiveChampionsLineSpec",
    };

    // Lines with their own planner (realm abilities) or out of this era (champion and master levels).
    private static readonly HashSet<string> HiddenImplementations = new(StringComparer.OrdinalIgnoreCase)
    {
        "LiveRealmAbilitiesSpecialization", "LiveChampionsSpecialization", "LiveMasterLevelsSpecialization",
    };

    private readonly string _databasePath;
    private readonly Lazy<Snapshot> _snapshot;
    private readonly ConcurrentDictionary<int, IReadOnlyList<PlannerSpec>> _specs = new();
    private readonly ConcurrentDictionary<(int ClassId, string SpecKey), IReadOnlyList<PlannerSpell>> _spells = new();
    private readonly ConcurrentDictionary<(int ClassId, string SpecKey), IReadOnlyList<PlannerStyle>> _styles = new();
    private readonly ConcurrentDictionary<(int ClassId, string SpecKey), IReadOnlyList<PlannerAbility>> _abilities = new();
    private readonly ConcurrentDictionary<int, IReadOnlyList<PlannerRealmAbility>> _realmAbilities = new();

    public CharacterPlannerData(string databasePath)
    {
        _databasePath = databasePath ?? string.Empty;
        _snapshot = new Lazy<Snapshot>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Why the database could not be read, or null when it was.</summary>
    public string? LoadError => _snapshot.Value.Error;

    /// <summary>Gaps found while loading: realm abilities without a cost table, rows the server would skip, ...</summary>
    public IReadOnlyList<string> Warnings => _snapshot.Value.Warnings;

    /// <summary>
    /// The playable classes. Without a readable database this is the whole static roster; with one, classes that have
    /// no career rows in it (the experimental Sluaghbinder in a normal install) are left out.
    /// </summary>
    public IReadOnlyList<PlannerClass> Classes => _snapshot.Value.Classes;

    public IReadOnlyList<PlannerSpec> SpecsFor(int classId)
    {
        return _specs.GetOrAdd(classId, id => BuildSpecs(_snapshot.Value, id));
    }

    public IReadOnlyList<PlannerSpell> SpellsFor(int classId, string specKey)
    {
        return _spells.GetOrAdd((classId, specKey ?? string.Empty), key => BuildSpells(_snapshot.Value, key.ClassId, key.SpecKey));
    }

    public IReadOnlyList<PlannerStyle> StylesFor(int classId, string specKey)
    {
        return _styles.GetOrAdd((classId, specKey ?? string.Empty), key => BuildStyles(_snapshot.Value, key.ClassId, key.SpecKey));
    }

    public IReadOnlyList<PlannerAbility> AbilitiesFor(int classId, string specKey)
    {
        return _abilities.GetOrAdd((classId, specKey ?? string.Empty), key => BuildAbilities(_snapshot.Value, key.ClassId, key.SpecKey));
    }

    public IReadOnlyList<PlannerRealmAbility> RealmAbilitiesFor(int classId)
    {
        return _realmAbilities.GetOrAdd(classId, id => BuildRealmAbilities(_snapshot.Value, id));
    }

    #region Building the lists

    private static IReadOnlyList<PlannerSpec> BuildSpecs(Snapshot data, int classId)
    {
        if (!data.Careers.TryGetValue(classId, out List<(string Key, int LevelAcquired)>? career))
        {
            return [];
        }

        var specs = new List<(PlannerSpec Spec, int Order)>();

        foreach ((string key, int levelAcquired) in career)
        {
            // GetSpecializationCareer skips career keys missing from the Specialization table.
            if (!data.Specializations.TryGetValue(key, out SpecRow? row))
            {
                continue;
            }

            string implementation = ShortTypeName(row.Implementation);

            if (HiddenImplementations.Contains(implementation))
            {
                continue;
            }

            bool trainable = !UntrainableImplementations.Contains(implementation) &&
                             !implementation.StartsWith("LiveCL", StringComparison.OrdinalIgnoreCase);
            specs.Add((new PlannerSpec(row.KeyName, row.Name, Math.Max(1, levelAcquired), trainable), levelAcquired));
        }

        return specs.OrderByDescending(entry => entry.Spec.Trainable)
            .ThenBy(entry => entry.Order)
            .ThenBy(entry => entry.Spec.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Spec)
            .ToList();
    }

    private static IReadOnlyList<PlannerSpell> BuildSpells(Snapshot data, int classId, string specKey)
    {
        string implementation = data.Specializations.TryGetValue(specKey, out SpecRow? spec) ? ShortTypeName(spec.Implementation) : string.Empty;

        // LiveAbilitySpecialization (Envenom) and LiveWeaponSpecialization list no spells.
        if (implementation.Equals("LiveAbilitySpecialization", StringComparison.OrdinalIgnoreCase) ||
            implementation.Equals("LiveWeaponSpecialization", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        List<LineRow> lines = data.SpellLines.Where(line => line.Spec.Equals(specKey, StringComparison.OrdinalIgnoreCase)).ToList();
        List<LineRow> baseline = lines.Where(line => line.IsBaseLine && line.ClassIdHint == classId).ToList();

        if (baseline.Count == 0)
        {
            baseline = lines.Where(line => line.IsBaseLine && line.ClassIdHint == 0).ToList();
        }

        List<LineRow> specialization = lines.Where(line => !line.IsBaseLine && line.ClassIdHint == classId).ToList();

        if (specialization.Count == 0)
        {
            specialization = lines.Where(line => !line.IsBaseLine && line.ClassIdHint == 0).ToList();
        }

        var spells = new List<PlannerSpell>();

        foreach (LineRow line in baseline.Concat(specialization))
        {
            if (!data.LineSpells.TryGetValue(line.KeyName, out List<(int SpellId, int Level)>? entries))
            {
                continue;
            }

            string lineName = (line.Name.Length > 0 ? line.Name : line.KeyName) + (line.IsBaseLine ? BaselineSuffix : string.Empty);
            var seen = new HashSet<int>();

            foreach ((int spellId, int level) in entries)
            {
                if (!seen.Add(spellId) || !data.Spells.TryGetValue(spellId, out SpellRow? spell))
                {
                    // LineXSpell rows whose spell is missing are skipped, as SkillBase.LoadSpellLines does.
                    continue;
                }

                // SkillBase.LoadSpellLines: spell.Level = Math.Max(1, LineXSpell.Level).
                spells.Add(new PlannerSpell(spell.Name, Math.Max(1, level), lineName, spell.Description, SpellKind(spell.Type)));
            }
        }

        return spells.OrderBy(spell => spell.Level)
            .ThenBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<PlannerStyle> BuildStyles(Snapshot data, int classId, string specKey)
    {
        string implementation = data.Specializations.TryGetValue(specKey, out SpecRow? spec) ? ShortTypeName(spec.Implementation) : string.Empty;

        // LiveAbilitySpecialization and LiveSpellLineSpecialization list no styles.
        if (implementation.Equals("LiveAbilitySpecialization", StringComparison.OrdinalIgnoreCase) ||
            implementation.Equals("LiveSpellLineSpecialization", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        List<StyleRow> styles = data.Styles
            .Where(style => style.ClassId == classId && style.SpecKeyName.Equals(specKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (styles.Count == 0)
        {
            styles = data.Styles
                .Where(style => style.ClassId == 0 && style.SpecKeyName.Equals(specKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return styles.OrderBy(style => style.Level)
            .ThenBy(style => style.Id)
            .Select(style => new PlannerStyle(style.Name, style.Level, StyleOpening(data, style, classId), StyleDescription(data, style, classId)))
            .ToList();
    }

    private static IReadOnlyList<PlannerAbility> BuildAbilities(Snapshot data, int classId, string specKey)
    {
        var abilities = new List<PlannerAbility>();

        foreach (SpecAbilityRow row in data.SpecAbilities)
        {
            if (!row.Spec.Equals(specKey, StringComparison.OrdinalIgnoreCase) || (row.ClassId != 0 && row.ClassId != classId))
            {
                continue;
            }

            // LoadSpecializations skips SpecXAbility rows whose ability is missing.
            if (!data.Abilities.TryGetValue(row.AbilityKey, out AbilityRow? ability))
            {
                continue;
            }

            var entry = new PlannerAbility(AbilityName(ability, row.AbilityLevel), row.SpecLevel);

            if (!abilities.Contains(entry))
            {
                abilities.Add(entry);
            }
        }

        return abilities.OrderBy(ability => ability.Level)
            .ThenBy(ability => ability.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<PlannerRealmAbility> BuildRealmAbilities(Snapshot data, int classId)
    {
        if (!data.ClassRealmAbilities.TryGetValue(classId, out List<string>? keys))
        {
            return [];
        }

        var realmAbilities = new List<PlannerRealmAbility>();

        foreach (string key in keys)
        {
            if (!data.Abilities.TryGetValue(key, out AbilityRow? ability))
            {
                continue;
            }

            string implementation = ShortTypeName(ability.Implementation);

            if (!CharacterPlannerRealmAbilities.ByImplementation.TryGetValue(implementation, out CharacterPlannerRealmAbilities.Entry? entry))
            {
                // No handler table (see Warnings): listed with the database's name and description only.
                realmAbilities.Add(new PlannerRealmAbility(ability.KeyName, ability.Name.Trim(), ability.Description, false, [], []));

                continue;
            }

            IReadOnlyList<(string Requirement, int Level)> requirements = entry.Prerequisites;

            foreach (var (target, classRequirements) in CharacterPlannerRealmAbilities.ClassPrerequisites)
            {
                if (target.ClassId == classId && target.Implementation.Equals(implementation, StringComparison.OrdinalIgnoreCase))
                {
                    requirements = classRequirements;
                }
            }

            var prerequisites = new List<string>();

            foreach ((string requirement, int level) in requirements)
            {
                prerequisites.Add($"{RequirementKey(data, keys, requirement)}:{level}");
            }

            realmAbilities.Add(new PlannerRealmAbility(ability.KeyName, ability.Name.Trim(), ability.Description, entry.Passive,
                entry.CostPerLevel, prerequisites));
        }

        return realmAbilities;
    }

    // The AbilityKey of the realm ability whose handler is this class: the class's own one first, else any ability.
    private static string RequirementKey(Snapshot data, List<string> classKeys, string requirement)
    {
        if (requirement == CharacterPlannerRealmAbilities.CharacterLevelRequirement)
        {
            return requirement;
        }

        foreach (string key in classKeys)
        {
            if (data.Abilities.TryGetValue(key, out AbilityRow? ability) &&
                ShortTypeName(ability.Implementation).Equals(requirement, StringComparison.OrdinalIgnoreCase))
            {
                return ability.KeyName;
            }
        }

        AbilityRow? any = data.Abilities.Values.FirstOrDefault(ability =>
            ShortTypeName(ability.Implementation).Equals(requirement, StringComparison.OrdinalIgnoreCase));

        return any?.KeyName ?? requirement;
    }

    #endregion

    #region Presentation (mirrors the server's delve text)

    /// <summary>A short word for a spell's handler type (Spell.Type); unknown types pass through unchanged.</summary>
    internal static string SpellKind(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Other";
        }

        if (type.StartsWith("DirectDamage", StringComparison.OrdinalIgnoreCase) ||
            type is "Bolt" or "NightshadeNuke" or "DamageSpeedDecrease" or "FrontalPulseConeDD" or "TurretPBAoE")
        {
            return "DD";
        }

        if (type is "DamageOverTime" or "ArcheryDoT" or "Disease")
        {
            return "DoT";
        }

        if (type is "Lifedrain" or "LifeTransfer")
        {
            return "Lifetap";
        }

        if (type is "HealOverTime")
        {
            return "HoT";
        }

        if (type.Contains("Heal", StringComparison.OrdinalIgnoreCase))
        {
            return "Heal";
        }

        if (type.StartsWith("Cure", StringComparison.OrdinalIgnoreCase))
        {
            return "Cure";
        }

        if (type.Contains("Debuff", StringComparison.OrdinalIgnoreCase))
        {
            return "Debuff";
        }

        if (type.Contains("Buff", StringComparison.OrdinalIgnoreCase) ||
            type is "Bladeturn" or "AblativeArmor" or "DamageShield" or "DamageAdd" or "OffensiveProc" or "DefensiveProc"
                or "SpeedEnhancement")
        {
            return "Buff";
        }

        if (type.StartsWith("Summon", StringComparison.OrdinalIgnoreCase) || type is "PetSpell" or "Charm" or "Bomber")
        {
            return "Pet";
        }

        return type switch
        {
            "Mesmerize" => "Mez",
            "Stun" => "Stun",
            "SpeedDecrease" or "UnbreakableSpeedDecrease" => "Snare",
            "Resurrect" => "Rez",
            "Archery" => "Archery",
            _ => type,
        };
    }

    // StyleProcessor.DelveWeaponStyle's "Opening" line.
    private static string StyleOpening(Snapshot data, StyleRow style, int classId)
    {
        // Style.eOpening.Positional: Style.eOpeningPosition Back = 0, Side = 1, Front = 2.
        if (style.OpeningType == 2)
        {
            return style.OpeningValue switch
            {
                0 => "Back",
                1 => "Side",
                2 => "Front",
                _ => "Positional",
            };
        }

        // Style.eOpening.Defensive: the defender's (the style user's) result against the target's attack.
        if (style.OpeningType == 1)
        {
            return style.AttackResult switch
            {
                1 => "Target misses",
                2 => "Target hits",
                3 => "After parry",
                4 => "After block",
                5 => "After evade",
                6 => "Target fumbles",
                7 => "Target uses a style",
                _ => "Any",
            };
        }

        // Style.eOpening.Offensive; eAttackResultRequirement Style chains on the style with ID OpeningRequirementValue
        // (SkillBase.GetStyleByID with the class, else ClassId 0).
        if (style.AttackResult == 7)
        {
            StyleRow? previous = data.Styles.FirstOrDefault(other => other.Id == style.OpeningValue && other.ClassId == classId) ??
                                 data.Styles.FirstOrDefault(other => other.Id == style.OpeningValue && other.ClassId == 0);

            return "Follow-up of " + (previous?.Name ?? $"style {style.OpeningValue}");
        }

        return style.AttackResult switch
        {
            1 => "After a miss",
            2 => "After a hit",
            3 => "Target parries",
            4 => "Target blocks",
            5 => "Target evades",
            6 => "After a fumble",
            _ => "Any",
        };
    }

    // The rest of StyleProcessor.DelveWeaponStyle: fatigue, growth rate, to-hit, defense, stealth and the style's effect.
    private static string StyleDescription(Snapshot data, StyleRow style, int classId)
    {
        string fatigue = style.EnduranceCost switch
        {
            < 5 => "Very Low",
            < 10 => "Low",
            < 15 => "Medium",
            < 20 => "High",
            _ => "Very High",
        };
        var parts = new List<string>
        {
            "Fatigue: " + fatigue,
            "Growth rate: " + style.GrowthRate.ToString("0.00", CultureInfo.InvariantCulture),
        };

        if (style.BonusToHit != 0)
        {
            parts.Add("To-hit: " + style.BonusToHit.ToString("+0;-0", CultureInfo.InvariantCulture));
        }

        if (style.BonusToDefense != 0)
        {
            parts.Add("Defense: " + style.BonusToDefense.ToString("+0;-0", CultureInfo.InvariantCulture));
        }

        if (style.StealthRequirement)
        {
            parts.Add("Requires stealth");
        }

        // StyleProcessor: the class's own procs replace the ClassID 0 ones.
        List<StyleProcRow> procs = data.StyleProcs.Where(proc => proc.StyleId == style.Id && proc.ClassId == classId).ToList();

        if (procs.Count == 0)
        {
            procs = data.StyleProcs.Where(proc => proc.StyleId == style.Id && proc.ClassId == 0).ToList();
        }

        foreach (StyleProcRow proc in procs)
        {
            if (data.Spells.TryGetValue(proc.SpellId, out SpellRow? spell))
            {
                parts.Add(proc.Chance > 0 && proc.Chance < 100 ? $"Effect: {spell.Name} ({proc.Chance}%)" : $"Effect: {spell.Name}");
            }
        }

        return string.Join("; ", parts);
    }

    // Ability.UpdateCurrentName: "level|name;level|name" (or a plain name), "%n" replaced by the level in roman numerals.
    internal static string AbilityName(AbilityRow ability, int level)
    {
        var nameByLevel = new Dictionary<int, string>();

        foreach (string pair in ability.Name.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.Trim().Length <= 0)
            {
                continue;
            }

            string[] levelAndName = pair.Trim().Split('|');

            if (levelAndName.Length < 2)
            {
                nameByLevel.TryAdd(0, pair);
            }
            else if (int.TryParse(levelAndName[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nameLevel))
            {
                nameByLevel.TryAdd(nameLevel, levelAndName[1]);
            }
        }

        if (!nameByLevel.TryGetValue(level, out string? name))
        {
            name = nameByLevel.OrderBy(entry => entry.Key).FirstOrDefault(entry => entry.Key <= level).Value ?? "??" + ability.KeyName;
        }

        string roman = string.Empty;
        int remain = Math.Clamp(level, 0, 3999);
        (int Value, string Numeral)[] numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"),
            (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];

        foreach ((int value, string numeral) in numerals)
        {
            while (remain >= value)
            {
                roman += numeral;
                remain -= value;
            }
        }

        return name.Replace("%n", roman, StringComparison.Ordinal).Trim();
    }

    private static string ShortTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return string.Empty;
        }

        string trimmed = typeName.Trim();

        return trimmed[(trimmed.LastIndexOf('.') + 1)..];
    }

    #endregion

    #region Loading

    private Snapshot Load()
    {
        var data = new Snapshot();

        try
        {
            if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
            {
                data.Error = $"Database not found: {_databasePath}";

                return data;
            }

            using var connection = new SQLiteConnection($"Data Source={_databasePath};Version=3;Read Only=True;Pooling=False;Default Timeout=5");
            connection.Open();
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Read(connection, "SELECT name FROM sqlite_master WHERE type='table'", reader => tables.Add(Text(reader, 0)));
            string[] required =
            [
                "ClassXSpecialization", "Specialization", "SpellLine", "LineXSpell", "Spell", "Style", "SpecXAbility", "Ability",
                "ClassXRealmAbility_Atlas",
            ];
            string[] missing = required.Where(table => !tables.Contains(table)).ToArray();

            if (missing.Length > 0)
            {
                data.Error = "Database is missing table(s): " + string.Join(", ", missing);

                return data;
            }

            Read(connection, "SELECT ClassID, SpecKeyName, LevelAcquired FROM ClassXSpecialization ORDER BY ClassXSpecializationID", reader =>
            {
                int classId = Int(reader, 0);

                if (!data.Careers.TryGetValue(classId, out List<(string Key, int LevelAcquired)>? career))
                {
                    career = [];
                    data.Careers.Add(classId, career);
                }

                // LoadClassSpecializations keeps the first row of a duplicated key.
                string key = Text(reader, 1);

                if (!career.Any(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                {
                    career.Add((key, Int(reader, 2)));
                }
            });
            Read(connection, "SELECT KeyName, Name, Implementation FROM Specialization ORDER BY SpecializationID", reader =>
                data.Specializations.TryAdd(Text(reader, 0), new SpecRow(Text(reader, 0), Text(reader, 1), Text(reader, 2))));
            Read(connection, "SELECT KeyName, Name, Spec, IsBaseLine, ClassIDHint FROM SpellLine ORDER BY SpellLineID", reader =>
                data.SpellLines.Add(new LineRow(Text(reader, 0), Text(reader, 1), Text(reader, 2), Int(reader, 3) != 0, Int(reader, 4))));
            Read(connection, "SELECT LineName, SpellID, Level FROM LineXSpell", reader =>
            {
                string line = Text(reader, 0);

                if (!data.LineSpells.TryGetValue(line, out List<(int SpellId, int Level)>? entries))
                {
                    entries = [];
                    data.LineSpells.Add(line, entries);
                }

                entries.Add((Int(reader, 1), Int(reader, 2)));
            });
            Read(connection, "SELECT SpellID, Name, Description, Type FROM Spell", reader =>
                data.Spells.TryAdd(Int(reader, 0), new SpellRow(Text(reader, 1), Text(reader, 2), Text(reader, 3))));
            Read(connection, """
                SELECT ID, ClassId, Name, SpecKeyName, SpecLevelRequirement, OpeningRequirementType, OpeningRequirementValue,
                       AttackResultRequirement, EnduranceCost, GrowthRate, BonusToHit, BonusToDefense, StealthRequirement
                FROM Style ORDER BY StyleID
                """, reader => data.Styles.Add(new StyleRow(Int(reader, 0), Int(reader, 1), Text(reader, 2), Text(reader, 3),
                Int(reader, 4), Int(reader, 5), Int(reader, 6), Int(reader, 7), Int(reader, 8), Real(reader, 9), Int(reader, 10),
                Int(reader, 11), Int(reader, 12) != 0)));

            if (tables.Contains("StyleXSpell"))
            {
                Read(connection, "SELECT StyleID, ClassID, SpellID, Chance FROM StyleXSpell", reader =>
                    data.StyleProcs.Add(new StyleProcRow(Int(reader, 0), Int(reader, 1), Int(reader, 2), Int(reader, 3))));
            }

            Read(connection, "SELECT Spec, SpecLevel, AbilityKey, AbilityLevel, ClassId FROM SpecXAbility ORDER BY SpecXabilityID", reader =>
                data.SpecAbilities.Add(new SpecAbilityRow(Text(reader, 0), Int(reader, 1), Text(reader, 2), Int(reader, 3), Int(reader, 4))));
            Read(connection, "SELECT KeyName, Name, Description, Implementation FROM Ability ORDER BY AbilityID", reader =>
                data.Abilities.TryAdd(Text(reader, 0), new AbilityRow(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3))));
            Read(connection, "SELECT CharClass, AbilityKey FROM ClassXRealmAbility_Atlas ORDER BY rowid", reader =>
            {
                int classId = Int(reader, 0);

                if (!data.ClassRealmAbilities.TryGetValue(classId, out List<string>? keys))
                {
                    keys = [];
                    data.ClassRealmAbilities.Add(classId, keys);
                }

                // LoadClassRealmAbilities keeps known abilities only, once each, under their Ability.KeyName.
                if (data.Abilities.TryGetValue(Text(reader, 1), out AbilityRow? ability) && !keys.Contains(ability.KeyName))
                {
                    keys.Add(ability.KeyName);
                }
            });

            if (tables.Contains("ServerProperty"))
            {
                Read(connection, """
                    SELECT Key, Value FROM ServerProperty
                    WHERE Key IN ('use_new_passives_ras_scaling', 'use_new_actives_ras_scaling')
                    """, reader =>
                {
                    if (Text(reader, 1).Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        data.Warnings.Add($"{Text(reader, 0)} is True: the server raises the base maximum levels, but every " +
                                          "extra level costs 1000 points, so the planner's levels and costs are unchanged.");
                    }
                });
            }

            data.Classes = CharacterPlannerClasses.All.Where(playerClass => data.Careers.ContainsKey(playerClass.Id)).ToList();
            CheckRealmAbilities(data);
        }
        catch (Exception exception)
        {
            data.Error = $"Could not read the database: {exception.Message}";
        }

        return data;
    }

    // Warn about every realm ability of a planner class that has no cost table (the server cannot buy those either when
    // their handler class does not exist: SkillBase.GetClassRealmAbilities keeps RealmAbility instances only).
    private static void CheckRealmAbilities(Snapshot data)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PlannerClass playerClass in CharacterPlannerClasses.All)
        {
            if (!data.ClassRealmAbilities.TryGetValue(playerClass.Id, out List<string>? keys))
            {
                continue;
            }

            foreach (string key in keys)
            {
                string implementation = ShortTypeName(data.Abilities[key].Implementation);

                if (!CharacterPlannerRealmAbilities.ByImplementation.ContainsKey(implementation) && reported.Add(key))
                {
                    data.Warnings.Add($"Realm ability {key} ({implementation}) has no cost table; listed without levels.");
                }
            }
        }
    }

    private static void Read(SQLiteConnection connection, string sql, Action<SQLiteDataReader> row)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            row(reader);
        }
    }

    private static string Text(SQLiteDataReader reader, int column) =>
        reader.IsDBNull(column) ? string.Empty : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture) ?? string.Empty;

    private static int Int(SQLiteDataReader reader, int column) =>
        reader.IsDBNull(column) ? 0 : Convert.ToInt32(reader.GetValue(column), CultureInfo.InvariantCulture);

    private static double Real(SQLiteDataReader reader, int column) =>
        reader.IsDBNull(column) ? 0 : Convert.ToDouble(reader.GetValue(column), CultureInfo.InvariantCulture);

    #endregion

    #region Rows

    private sealed class Snapshot
    {
        public string? Error;
        public readonly List<string> Warnings = [];
        public IReadOnlyList<PlannerClass> Classes = CharacterPlannerClasses.All;
        public readonly Dictionary<int, List<(string Key, int LevelAcquired)>> Careers = [];
        public readonly Dictionary<string, SpecRow> Specializations = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<LineRow> SpellLines = [];
        public readonly Dictionary<string, List<(int SpellId, int Level)>> LineSpells = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, SpellRow> Spells = [];
        public readonly List<StyleRow> Styles = [];
        public readonly List<StyleProcRow> StyleProcs = [];
        public readonly List<SpecAbilityRow> SpecAbilities = [];
        public readonly Dictionary<string, AbilityRow> Abilities = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<int, List<string>> ClassRealmAbilities = [];
    }

    private sealed record SpecRow(string KeyName, string Name, string Implementation);

    private sealed record LineRow(string KeyName, string Name, string Spec, bool IsBaseLine, int ClassIdHint);

    private sealed record SpellRow(string Name, string Description, string Type);

    private sealed record StyleRow(int Id, int ClassId, string Name, string SpecKeyName, int Level, int OpeningType, int OpeningValue,
        int AttackResult, int EnduranceCost, double GrowthRate, int BonusToHit, int BonusToDefense, bool StealthRequirement);

    private sealed record StyleProcRow(int StyleId, int ClassId, int SpellId, int Chance);

    private sealed record SpecAbilityRow(string Spec, int SpecLevel, string AbilityKey, int AbilityLevel, int ClassId);

    internal sealed record AbilityRow(string KeyName, string Name, string Description, string Implementation);

    #endregion
}

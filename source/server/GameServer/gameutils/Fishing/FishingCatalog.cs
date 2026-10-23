using System;
using System.Collections.Generic;
using DOL.Database;

namespace DOL.GS.Fishing
{
    public enum eFishingRarity
    {
        Common,
        Uncommon,
        Rare,
        Legendary,
        Junk
    }

    /// <summary>One roll of the line: what came up, how heavy it is and how much skill it is worth.</summary>
    public sealed class FishingCatch
    {
        /// <summary>Id_nb of the DbItemTemplate to create (species + size tier template, or a junk template).</summary>
        public string TemplateId;

        /// <summary>What the player is told they caught, e.g. "brown trout" or "Old Bartholomew, the legendary pike".</summary>
        public string DisplayName;

        /// <summary>Pounds * 10; 0 for junk.</summary>
        public int WeightTenths;

        public eFishingRarity Rarity;

        /// <summary>Points of fishing skill (0-100 scale) the catch is worth. FishingProgress.RecordCatch awards them.</summary>
        public int SkillGain;
    }

    /// <summary>
    /// The fish, junk and fishing pole of the /fish pastime, and the dice behind a catch. Pure logic: nothing here
    /// touches the database or a player, so it is unit tested directly. The item templates are code-defined (like the
    /// siege repair kit in BotSiegeRuntime); FishingStartup writes them to the ItemTemplate table at server start.
    ///
    /// Model ids are objects.csv ids of the stock client (source/tools/observer-model-inspection/objects.csv):
    /// 544 "Small fish", 545 "Large fish", 150 "Crude Leather Boots", 535 "Herbs", 505 "Chest", 442 "Quarterstaff".
    /// They should still be checked in game against the client's items.csv / objects.csv.
    /// </summary>
    public static class FishingCatalog
    {
        public const string PoleTemplateId = "offline_fishing_pole";
        public const string PackageId = "OfflineFishing";

        private const int SmallFishModel = 544;
        private const int LargeFishModel = 545;
        private const int OldBootModel = 150;
        private const int SeaweedModel = 535;
        private const int ChestModel = 505;
        private const int PoleModel = 442;

        private const int MaxTemplateWeightTenths = 200;

        private static readonly string[] TierPrefixes = { "small ", "", "large ", "trophy " };

        // Upper bounds of each size tier as a fraction of the species' weight range, and the fraction used for the
        // template's own (inventory) weight.
        private static readonly double[] TierUpperFraction = { 0.25, 0.65, 0.90, 1.0 };
        private static readonly double[] TierMidFraction = { 0.125, 0.45, 0.775, 0.95 };
        private static readonly int[] TierPriceMultiplier = { 1, 2, 4, 10 };

        // Rarity weights in the order Common, Uncommon, Rare, Legendary, Junk: at skill 0 and at skill 100.
        private static readonly double[] RarityWeightsNovice = { 65, 22, 9, 1, 3 };
        private static readonly double[] RarityWeightsMaster = { 46, 30, 18, 5, 1 };

        private static readonly int[] SkillGainByRarity = { 1, 2, 4, 10, 0 };

        private sealed class Species
        {
            public string Realm;
            public string Key;
            public string Name;
            public int MinTenths;
            public int MaxTenths;
            public eFishingRarity Rarity;
            public int Frequency;
            public int BaseValueCopper;
            public string LegendaryName;

            public string TemplateId(int tier)
            {
                return "offline_fish_" + Realm + "_" + Key + "_" + (tier + 1);
            }
        }

        private sealed class Junk
        {
            public string TemplateId;
            public string Name;
            public int Frequency;
            public int Model;
            public int WeightTenths;
            public int PriceCopper;
        }

        private static Species S(string realm, string key, string name, int minTenths, int maxTenths, eFishingRarity rarity,
            int frequency, int baseValue, string legendaryName = null)
        {
            return new Species
            {
                Realm = realm, Key = key, Name = name, MinTenths = minTenths, MaxTenths = maxTenths, Rarity = rarity,
                Frequency = frequency, BaseValueCopper = baseValue, LegendaryName = legendaryName
            };
        }

        private static readonly Species[] Albion =
        {
            S("alb", "brown_trout", "brown trout", 5, 60, eFishingRarity.Common, 40, 10),
            S("alb", "perch", "perch", 3, 30, eFishingRarity.Common, 35, 8),
            S("alb", "carp", "carp", 20, 250, eFishingRarity.Common, 25, 9),
            S("alb", "eel", "eel", 10, 80, eFishingRarity.Uncommon, 50, 20),
            S("alb", "pike", "pike", 30, 300, eFishingRarity.Uncommon, 50, 25, "Old Bartholomew"),
            S("alb", "salmon", "salmon", 40, 350, eFishingRarity.Rare, 60, 60),
            S("alb", "sturgeon", "sturgeon", 100, 900, eFishingRarity.Rare, 40, 80, "Abbot Ambrose"),
        };

        private static readonly Species[] Midgard =
        {
            S("mid", "herring", "herring", 3, 15, eFishingRarity.Common, 40, 6),
            S("mid", "grayling", "grayling", 5, 40, eFishingRarity.Common, 35, 9),
            S("mid", "arctic_char", "arctic char", 10, 100, eFishingRarity.Common, 25, 12),
            S("mid", "cod", "cod", 30, 400, eFishingRarity.Uncommon, 55, 22, "Svartbuk"),
            S("mid", "burbot", "burbot", 10, 120, eFishingRarity.Uncommon, 45, 20),
            S("mid", "king_salmon", "king salmon", 60, 400, eFishingRarity.Rare, 60, 60),
            S("mid", "halibut", "halibut", 100, 1500, eFishingRarity.Rare, 40, 90, "Old Jarl Bloodgill"),
        };

        private static readonly Species[] Hibernia =
        {
            S("hib", "roach", "roach", 3, 25, eFishingRarity.Common, 40, 7),
            S("hib", "bream", "bream", 10, 80, eFishingRarity.Common, 30, 9),
            S("hib", "mackerel", "mackerel", 5, 30, eFishingRarity.Common, 30, 8),
            S("hib", "sea_trout", "sea trout", 20, 150, eFishingRarity.Uncommon, 55, 22),
            S("hib", "pollan", "pollan", 5, 40, eFishingRarity.Uncommon, 45, 18),
            S("hib", "salmon", "salmon", 40, 400, eFishingRarity.Rare, 60, 60, "Fintan"),
            S("hib", "pike", "pike", 30, 350, eFishingRarity.Rare, 40, 70, "Glas Fiacail"),
        };

        // Waters with no player realm (eRealm.None, Door and anything else) share this table.
        private static readonly Species[] Shared =
        {
            S("any", "minnow", "minnow", 1, 5, eFishingRarity.Common, 30, 4),
            S("any", "perch", "perch", 3, 30, eFishingRarity.Common, 35, 8),
            S("any", "trout", "trout", 5, 60, eFishingRarity.Common, 35, 10),
            S("any", "catfish", "catfish", 20, 200, eFishingRarity.Uncommon, 50, 20),
            S("any", "eel", "eel", 10, 80, eFishingRarity.Uncommon, 50, 20),
            S("any", "carp", "carp", 20, 250, eFishingRarity.Rare, 50, 40),
            S("any", "sturgeon", "sturgeon", 100, 900, eFishingRarity.Rare, 50, 80, "Old Whiskers"),
        };

        private static readonly Species[][] AllSpecies = { Albion, Midgard, Hibernia, Shared };

        private static readonly Junk[] JunkItems =
        {
            new Junk { TemplateId = "offline_fish_junk_old_boot", Name = "old boot", Frequency = 45, Model = OldBootModel, WeightTenths = 15, PriceCopper = 3 },
            new Junk { TemplateId = "offline_fish_junk_tangled_seaweed", Name = "tangled seaweed", Frequency = 45, Model = SeaweedModel, WeightTenths = 5, PriceCopper = 2 },
            new Junk { TemplateId = "offline_fish_junk_waterlogged_chest", Name = "waterlogged chest", Frequency = 10, Model = ChestModel, WeightTenths = 60, PriceCopper = 250 },
        };

        private static readonly string[] WaitingLines =
        {
            "The line drifts gently in the current.",
            "Your float bobs lazily on the water.",
            "A dragonfly lands on the tip of your pole, then darts away.",
            "Ripples spread slowly across the surface.",
            "Somewhere nearby a fish jumps, but not on your line.",
            "The breeze tugs softly at your line.",
            "You feel a faint nibble... then nothing.",
            "The water laps quietly at the bank.",
            "A bird calls out across the water.",
            "You settle in and wait patiently.",
            "Sunlight glints off the surface of the water.",
            "Your line sways back and forth in the current.",
        };

        /// <summary>Every fish template (four size tiers per species), every junk template and the fishing pole.</summary>
        public static IReadOnlyList<DbItemTemplate> AllTemplates()
        {
            var templates = new List<DbItemTemplate>();

            foreach (Species[] table in AllSpecies)
            {
                foreach (Species species in table)
                {
                    for (int tier = 0; tier < 4; tier++)
                    {
                        int weight = species.MinTenths + (int)Math.Round((species.MaxTenths - species.MinTenths) * TierMidFraction[tier]);

                        templates.Add(new DbItemTemplate
                        {
                            Id_nb = species.TemplateId(tier),
                            Name = TierPrefixes[tier] + species.Name,
                            Level = 1,
                            Object_Type = (int)eObjectType.GenericItem,
                            Item_Type = (int)eInventorySlot.FirstBackpack,
                            Model = tier < 2 ? SmallFishModel : LargeFishModel,
                            Weight = Math.Clamp(weight, 1, MaxTemplateWeightTenths),
                            Price = species.BaseValueCopper * TierPriceMultiplier[tier],
                            MaxCount = tier == 3 ? 1 : 20,
                            PackSize = 1,
                            IsPickable = true,
                            IsDropable = true,
                            IsTradable = true,
                            PackageID = PackageId,
                        });
                    }
                }
            }

            foreach (Junk junk in JunkItems)
            {
                templates.Add(new DbItemTemplate
                {
                    Id_nb = junk.TemplateId,
                    Name = junk.Name,
                    Level = 1,
                    Object_Type = (int)eObjectType.GenericItem,
                    Item_Type = (int)eInventorySlot.FirstBackpack,
                    Model = junk.Model,
                    Weight = junk.WeightTenths,
                    Price = junk.PriceCopper,
                    MaxCount = 20,
                    PackSize = 1,
                    IsPickable = true,
                    IsDropable = true,
                    IsTradable = true,
                    PackageID = PackageId,
                });
            }

            // The pole is a GenericWeapon rather than a Staff: AbstractServerRules.CheckAbilityToUseItem lets every class
            // hold a GenericWeapon, while a Staff needs the Weapon_Staves ability most melee classes lack. Two-handed
            // (Hand = 1, Item_Type = TwoHandWeapon) with 0.1 DPS: it is not meant to fight with.
            templates.Add(new DbItemTemplate
            {
                Id_nb = PoleTemplateId,
                Name = "Fishing Pole",
                Level = 1,
                Object_Type = (int)eObjectType.GenericWeapon,
                Item_Type = (int)eInventorySlot.TwoHandWeapon,
                Hand = 1,
                Type_Damage = (int)eDamageType.Crush,
                DPS_AF = 1,
                SPD_ABS = 40,
                Model = PoleModel,
                Weight = 30,
                Price = 500,
                Quality = 100,
                Condition = 50000,
                MaxCondition = 50000,
                Durability = 50000,
                MaxDurability = 50000,
                MaxCount = 1,
                PackSize = 1,
                IsPickable = true,
                IsDropable = true,
                IsTradable = true,
                PackageID = PackageId,
            });

            return templates;
        }

        /// <summary>Rolls one catch for waters of <paramref name="realm"/> at fishing skill 0-100.</summary>
        public static FishingCatch Roll(eRealm realm, int skill, Random rng)
        {
            skill = Math.Clamp(skill, 0, 100);
            double mastery = skill / 100.0;

            double total = 0;
            double[] weights = new double[RarityWeightsNovice.Length];

            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = RarityWeightsNovice[i] + (RarityWeightsMaster[i] - RarityWeightsNovice[i]) * mastery;
                total += weights[i];
            }

            double roll = rng.NextDouble() * total;
            eFishingRarity rarity = eFishingRarity.Common;

            for (int i = 0; i < weights.Length; i++)
            {
                if (roll < weights[i])
                {
                    rarity = (eFishingRarity)i;

                    break;
                }

                roll -= weights[i];
            }

            if (rarity == eFishingRarity.Junk)
            {
                int junkTotal = 0;

                foreach (Junk junk in JunkItems)
                {
                    junkTotal += junk.Frequency;
                }

                int junkRoll = rng.Next(junkTotal);
                Junk pickedJunk = JunkItems[JunkItems.Length - 1];

                foreach (Junk junk in JunkItems)
                {
                    if (junkRoll < junk.Frequency)
                    {
                        pickedJunk = junk;

                        break;
                    }

                    junkRoll -= junk.Frequency;
                }

                return new FishingCatch
                {
                    TemplateId = pickedJunk.TemplateId,
                    DisplayName = pickedJunk.Name,
                    WeightTenths = 0,
                    Rarity = eFishingRarity.Junk,
                    SkillGain = SkillGainByRarity[(int)eFishingRarity.Junk],
                };
            }

            Species[] table = realm switch
            {
                eRealm.Albion => Albion,
                eRealm.Midgard => Midgard,
                eRealm.Hibernia => Hibernia,
                _ => Shared,
            };

            // Pick a species of the rolled rarity by its frequency; legendary catches come from species with a named
            // specimen. Every table has at least one species of each kind.
            var candidates = new List<Species>();
            int frequencyTotal = 0;

            foreach (Species species in table)
            {
                bool matches = rarity == eFishingRarity.Legendary ? species.LegendaryName != null : species.Rarity == rarity;

                if (matches)
                {
                    candidates.Add(species);
                    frequencyTotal += species.Frequency;
                }
            }

            int speciesRoll = rng.Next(frequencyTotal);
            Species picked = candidates[candidates.Count - 1];

            foreach (Species species in candidates)
            {
                if (speciesRoll < species.Frequency)
                {
                    picked = species;

                    break;
                }

                speciesRoll -= species.Frequency;
            }

            int range = picked.MaxTenths - picked.MinTenths;

            if (rarity == eFishingRarity.Legendary)
            {
                // A named specimen is heavier than anything else of its kind: 110-140% of the species' maximum.
                int legendaryWeight = picked.MaxTenths + (int)Math.Round(picked.MaxTenths * (0.10 + rng.NextDouble() * 0.30));

                return new FishingCatch
                {
                    TemplateId = picked.TemplateId(3),
                    DisplayName = picked.LegendaryName + ", the legendary " + picked.Name,
                    WeightTenths = Math.Max(legendaryWeight, picked.MaxTenths + 1),
                    Rarity = eFishingRarity.Legendary,
                    SkillGain = SkillGainByRarity[(int)eFishingRarity.Legendary],
                };
            }

            // Skill skews the weight toward the top of the range: u^1.5 at skill 0 (mean 40% of the range) down to u^0.5 at
            // skill 100 (mean 67%).
            double fraction = Math.Pow(rng.NextDouble(), 1.5 - mastery);
            int weightTenths = Math.Clamp(picked.MinTenths + (int)Math.Round(range * fraction), picked.MinTenths, picked.MaxTenths);
            double placed = range == 0 ? 0 : (weightTenths - picked.MinTenths) / (double)range;
            int tier = 3;

            for (int i = 0; i < TierUpperFraction.Length; i++)
            {
                if (placed < TierUpperFraction[i])
                {
                    tier = i;

                    break;
                }
            }

            return new FishingCatch
            {
                TemplateId = picked.TemplateId(tier),
                DisplayName = TierPrefixes[tier] + picked.Name,
                WeightTenths = weightTenths,
                Rarity = rarity,
                SkillGain = SkillGainByRarity[(int)rarity],
            };
        }

        /// <summary>The weight range (tenths of a pound) of the species behind a fish template; false for junk, the pole or
        /// an unknown id. Legendary specimens are heavier than <paramref name="maxTenths"/> by design.</summary>
        public static bool TryGetSpeciesWeightRange(string templateId, out int minTenths, out int maxTenths)
        {
            foreach (Species[] table in AllSpecies)
            {
                foreach (Species species in table)
                {
                    for (int tier = 0; tier < 4; tier++)
                    {
                        if (species.TemplateId(tier) == templateId)
                        {
                            minTenths = species.MinTenths;
                            maxTenths = species.MaxTenths;

                            return true;
                        }
                    }
                }
            }

            minTenths = 0;
            maxTenths = 0;

            return false;
        }

        /// <summary>Milliseconds until the next bite: 8-25 s for a novice, 6-17 s for a master.</summary>
        public static int RollBiteDelayMs(int skill, Random rng)
        {
            skill = Math.Clamp(skill, 0, 100);
            int min = 8000 - skill * 20;
            int max = 25000 - skill * 80;

            return Math.Clamp(rng.Next(min, max + 1), 6000, 25000);
        }

        /// <summary>"a brown trout weighing 3.2 lbs", "Old Bartholomew, the legendary pike weighing 33.0 lbs", "an old boot".</summary>
        public static string DescribeCatch(FishingCatch c)
        {
            if (c == null || string.IsNullOrEmpty(c.DisplayName))
            {
                return "nothing";
            }

            string name = c.DisplayName;

            if (c.Rarity != eFishingRarity.Legendary)
            {
                name = ("aeiouAEIOU".IndexOf(name[0]) >= 0 ? "an " : "a ") + name;
            }

            if (c.Rarity == eFishingRarity.Junk || c.WeightTenths <= 0)
            {
                return name;
            }

            return name + " weighing " + (c.WeightTenths / 10) + "." + (c.WeightTenths % 10) + " lbs";
        }

        /// <summary>An ambient line to show while waiting for a bite, or null (about 70% of the time).</summary>
        public static string RollWaitingFlavorLine(Random rng)
        {
            if (rng.NextDouble() >= 0.30)
            {
                return null;
            }

            return WaitingLines[rng.Next(WaitingLines.Length)];
        }
    }
}

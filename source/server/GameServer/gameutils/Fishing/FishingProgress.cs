using System;
using System.Collections.Concurrent;
using DOL.Database;
using DOL.Database.Attributes;

namespace DOL.GS.Fishing
{
    /// <summary>A character's fishing skill and records, in memory. Saved through <see cref="FishingProgressStore"/>.</summary>
    public sealed class FishingProgress
    {
        /// <summary>The character's database id (DOLCharacters ObjectId).</summary>
        public string CharacterId;
        public string CharacterName;

        public int Skill;
        public int Casts;
        public int Catches;
        public int BestWeightTenths;
        public string BestCatchName;

        internal OfflineFishingRecord Record;

        public void RecordCast()
        {
            Casts++;
        }

        public void RecordCatch(FishingCatch c)
        {
            if (c == null)
            {
                return;
            }

            Catches++;
            Skill = Math.Clamp(Skill + c.SkillGain, 0, 100);

            if (c.WeightTenths > BestWeightTenths)
            {
                BestWeightTenths = c.WeightTenths;
                BestCatchName = c.DisplayName;
            }
        }
    }

    /// <summary>
    /// Loads and saves <see cref="FishingProgress"/> in the offline_fishing table, one row per character keyed by the
    /// character's ObjectId. Rows are cached for the life of the server. The table is created automatically: the server
    /// registers every [DataTable] type it finds (GameServer.InitDB).
    /// </summary>
    public static class FishingProgressStore
    {
        private static readonly ConcurrentDictionary<string, FishingProgress> Cache = new();

        public static FishingProgress Load(GamePlayer player)
        {
            string characterId = player.ObjectId;

            if (Cache.TryGetValue(characterId, out FishingProgress cached))
            {
                return cached;
            }

            OfflineFishingRecord record = DOLDB<OfflineFishingRecord>.FindObjectByKey(characterId);

            if (record == null)
            {
                record = new OfflineFishingRecord
                {
                    CharacterId = characterId,
                    CharacterName = player.Name ?? string.Empty,
                    BestCatchName = string.Empty,
                };
                GameServer.Database.AddObject(record);
            }

            var progress = new FishingProgress
            {
                CharacterId = characterId,
                CharacterName = player.Name,
                Skill = Math.Clamp(record.Skill, 0, 100),
                Casts = record.Casts,
                Catches = record.Catches,
                BestWeightTenths = record.BestWeightTenths,
                BestCatchName = string.IsNullOrEmpty(record.BestCatchName) ? null : record.BestCatchName,
                Record = record,
            };

            return Cache.GetOrAdd(characterId, progress);
        }

        public static void Save(FishingProgress p)
        {
            if (p == null || string.IsNullOrEmpty(p.CharacterId))
            {
                return;
            }

            OfflineFishingRecord record = p.Record ?? DOLDB<OfflineFishingRecord>.FindObjectByKey(p.CharacterId);
            bool isNew = record == null;

            if (isNew)
            {
                record = new OfflineFishingRecord { CharacterId = p.CharacterId };
            }

            p.Record = record;
            record.CharacterName = p.CharacterName ?? string.Empty;
            record.Skill = Math.Clamp(p.Skill, 0, 100);
            record.Casts = p.Casts;
            record.Catches = p.Catches;
            record.BestWeightTenths = p.BestWeightTenths;
            record.BestCatchName = p.BestCatchName ?? string.Empty;

            // A row whose first insert failed is inserted again rather than updated.
            if (isNew || !record.IsPersisted)
            {
                GameServer.Database.AddObject(record);

                return;
            }

            GameServer.Database.SaveObject(record);
        }
    }

    /// <summary>One character's fishing progress. Listed in ProgressImport's progress-policy.json so it survives updates.</summary>
    [DataTable(TableName = "offline_fishing")]
    public sealed class OfflineFishingRecord : DataObject
    {
        private string m_characterId = string.Empty;
        private string m_characterName = string.Empty;
        private int m_skill;
        private int m_casts;
        private int m_catches;
        private int m_bestWeightTenths;
        private string m_bestCatchName = string.Empty;

        [PrimaryKey]
        public string CharacterId
        {
            get { return m_characterId; }
            set
            {
                Dirty = true;
                m_characterId = value;
            }
        }

        [DataElement(AllowDbNull = false, Varchar = 64)]
        public string CharacterName
        {
            get { return m_characterName; }
            set
            {
                Dirty = true;
                m_characterName = value;
            }
        }

        [DataElement(AllowDbNull = false)]
        public int Skill
        {
            get { return m_skill; }
            set
            {
                Dirty = true;
                m_skill = value;
            }
        }

        [DataElement(AllowDbNull = false)]
        public int Casts
        {
            get { return m_casts; }
            set
            {
                Dirty = true;
                m_casts = value;
            }
        }

        [DataElement(AllowDbNull = false)]
        public int Catches
        {
            get { return m_catches; }
            set
            {
                Dirty = true;
                m_catches = value;
            }
        }

        [DataElement(AllowDbNull = false)]
        public int BestWeightTenths
        {
            get { return m_bestWeightTenths; }
            set
            {
                Dirty = true;
                m_bestWeightTenths = value;
            }
        }

        [DataElement(AllowDbNull = false, Varchar = 128)]
        public string BestCatchName
        {
            get { return m_bestCatchName; }
            set
            {
                Dirty = true;
                m_bestCatchName = value;
            }
        }
    }
}

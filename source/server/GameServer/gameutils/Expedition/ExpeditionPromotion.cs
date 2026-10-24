using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.Expedition;

/// <summary>
/// Automatic class promotion on an Expedition run. The 1.65 client creates a character in its base class
/// (Naturalist, Fighter, Viking...), and a run's camps have no trainers, so:
/// - at run start the player picks the advanced class through a chain of yes/no dialogs (one class per dialog, No
///   shows the next, after the last it starts again); a base class with one path (Disciple: Necromancer) is chosen silently;
/// - the choice is saved per character as a DOLCharactersXCustomParam row (same pattern as /junk auto);
/// - at level 5 the player is promoted with the trainers' own code (<see cref="GameTrainer.CanPromotePlayer"/> and
///   <see cref="GameTrainer.PromotePlayer(GamePlayer, int, string, DbInventoryItem[])"/>), with that trainer's welcome
///   text and starter gift where its script gives one.
/// Wired in by ExpeditionManager: <see cref="OnRunStarted"/> after a run starts and <see cref="OnRunResumed"/> after one resumes.
/// </summary>
public static class ExpeditionPromotion
{
    /// <summary>The custom-param key of the saved choice; the value is the class id (see <see cref="FormatChoice"/>).</summary>
    public const string ChoiceKey = "expedition_promote_class";

    /// <summary>
    /// The level from which a run promotes. Trainers wait for level 5 (GameTrainer.CanPromotePlayer); a run promotes at
    /// once so the advanced class's specs and abilities are there from level 1 (owner 2026-10-23).
    /// </summary>
    public const int PromotionLevel = 1;

    private static readonly Logger Log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

    /// <summary>
    /// Advanced classes that came after 1.65 (Catacombs and later). Heretic, the Maulers, Warlock and Vampiir are already
    /// missing from GlobalConstants.STARTING_CLASSES_DICT; Valkyrie and Bainshee are listed there but are not part of the
    /// Classic + SI roster (AutonomousBotIdentityGenerator's class list has neither).
    /// </summary>
    private static readonly HashSet<int> Post165Classes = new()
    {
        (int)eCharacterClass.Heretic,
        (int)eCharacterClass.MaulerAlb,
        (int)eCharacterClass.MaulerMid,
        (int)eCharacterClass.MaulerHib,
        (int)eCharacterClass.Valkyrie,
        (int)eCharacterClass.Warlock,
        (int)eCharacterClass.Bainshee,
        (int)eCharacterClass.Vampiir
    };

    /// <summary>
    /// What each class trainer says and gives when it promotes (trainer/*/*Trainer.cs WhisperReceive; the translated
    /// lines are copied from language/EN). {0} is the player's name. Trainers that let the player pick a weapon
    /// (Armsman, Paladin, Mercenary) give no gift here. A class missing from the table gets a generic welcome.
    /// </summary>
    private static readonly Dictionary<eCharacterClass, (string Text, string Gift)> Welcomes = new()
    {
        // Albion
        [eCharacterClass.Paladin] = ("Welcome to the Church of Albion.", null),
        [eCharacterClass.Armsman] = ("Welcome to the Defenders of Albion.", null),
        [eCharacterClass.Mercenary] = ("Welcome to the Guild of Shadows.", null),
        [eCharacterClass.Reaver] = ("Welcome to the Temple of Arawn, {0}.", null),
        [eCharacterClass.Cleric] = ("Welcome my child! Walk the path of light, shout to all the words of our beloved church and rid the land of the faithless! Here is your Mace of the Initiate. It is our standard gift to all new members.", "crush_sword_item"),
        [eCharacterClass.Friar] = ("We welcome you into our society as an equal! We are at your disposal. We will now issue your Robes of the Novice. Wear them always and let it serve to remind you of your faith. When you have reached the title of Lesser Chaplain, return them to me. We shall then see if you require another.", "friar_item"),
        [eCharacterClass.Scout] = ("Welcome young warrior! May your time in Albion's army be rewarding! Here is your Huntsman's Longbow. The bow is your guild weapon! Be sure to carry it with you always.", "scout_item"),
        [eCharacterClass.Minstrel] = ("Welcome! Stand proud and sing the tales of old so that all may know of our name!", null),
        [eCharacterClass.Sorcerer] = ("You are now part of our shadow! You shall forever have a place among us! Here too is your guild weapon, a Staff of Focus!", "sorcerer_item"),
        [eCharacterClass.Cabalist] = ("Welcome to our guild! You have much to learn, but I see greatness in your future! Here too is your guild weapon, a Staff of Focus!", "cabalist_item"),
        [eCharacterClass.Theurgist] = ("I know you shall do your best to guard the realm from those that would harm it! To help you with this task, here is a gift from the Defenders! Use it well!", "theurgist_item"),
        [eCharacterClass.Wizard] = ("Welcome to our guild! You have much to learn, but I see greatness in your future! Here too is your guild weapon, a Staff of Focus!", "wizard_item"),
        [eCharacterClass.Necromancer] = ("Lord Arawn has accepted you into his Temple. Here is his gift to you. Use it well, Disciple.", "necromancer_item"),

        // Midgard
        [eCharacterClass.Thane] = ("Welcome young Thane! May your time in Midgard army be rewarding!", "thane_item"),
        [eCharacterClass.Warrior] = ("Welcome young Warrior! May your time in Midgard army be rewarding!", "warrior_item"),
        [eCharacterClass.Skald] = ("Welcome young Skald! May your time in Midgard army be rewarding!", "skald_item"),
        [eCharacterClass.Berserker] = ("Welcome young warrior! May your time in Midgard army be rewarding!", null),
        [eCharacterClass.Savage] = ("Welcome young Savage! May your time in Midgard army be rewarding!", null),
        [eCharacterClass.Spiritmaster] = ("Welcome young Spiritmaster! May your time in Midgard army be rewarding!", "spiritmaster_item"),
        [eCharacterClass.Runemaster] = ("Welcome young Runemaster! May your time in Midgard army be rewarding!", "runemaster_item"),
        [eCharacterClass.Bonedancer] = ("Welcome young warrior! May your time in Midgard army be rewarding!", "bonedancer_item"),
        [eCharacterClass.Healer] = ("Welcome young Healer! May your time in Midgard army be rewarding!", null),
        [eCharacterClass.Shaman] = ("Welcome young Shaman! May your time in Midgard army be rewarding!", null),
        [eCharacterClass.Shadowblade] = ("Welcome young Shadowblade! May your time in Midgard army be rewarding!", null),
        [eCharacterClass.Hunter] = ("Welcome young Hunter! May your time in Midgard army be rewarding!", "hunter_item"),

        // Hibernia
        [eCharacterClass.Blademaster] = ("Very well, {0}. I gladly take your training into my hands. Congratulations, from this day forth, you are a Blademaster. Here, take this gift to aid you.", "blademaster_item"),
        [eCharacterClass.Hero] = ("Let me welcome you then, {0}. Go, honor us with your deeds. Here is a gift for you. Try not to drop it, eh Hero.", "hero_item"),
        [eCharacterClass.Champion] = ("Welcome {0}. Let us see if you will become a worthy Champion. Take this gift, {0}. It is to aid you while you grow into a true Champion.", "champion_item"),
        [eCharacterClass.Warden] = ("Good then! Welcome to the ways of the Warden! Here, take this as a gift, to start you on the path of a Warden.", "warden_item"),
        [eCharacterClass.Druid] = ("The path of the Druid suits you, {0}. Welcome. Take this, {0}. You are a Druid now. Stick to our ways, and you shall go far.", "druid_item"),
        [eCharacterClass.Bard] = ("Welcome then, {0}, to the Bard's life. Here, take this. Keep it well, {0}, for the tools of our trade can be quite expensive.", "bard_item"),
        [eCharacterClass.Nightshade] = ("Some would think you mad, choosing to walk through life as a Nightshade. It is not meant for everyone, but I think it will suit you, {0}. Here, from me, a small gift to aid you in your journeys.", null),
        [eCharacterClass.Ranger] = ("Good then. Your path as a Ranger is before you. Walk it with care, friend. Take these, {0}, to help make walking the path a bit easier.", "ranger_item"),
        [eCharacterClass.Eldritch] = ("I can give you the gift of knowledge, but wisdom you must seek on your own. I welcome you, {0}. Here, take this welcoming gift. Use it wisely.", "eldritch_item"),
        [eCharacterClass.Enchanter] = ("Welcome, {0}. From this day forth you shall be known as an Enchanter. Here, {0}. Take this gift to aid you on your path.", "enchanter_item"),
        [eCharacterClass.Mentalist] = ("Well met then, {0}. It is a hard road, but I see hardness is no stranger to you. Welcome, Mentalist, welcome. Here, take this. It will aid you in your first encounters as a Mentalist.", "mentalist_item"),
        [eCharacterClass.Animist] = ("You are now an Animist, {0}. Welcome to the Path of Affinity.", "animist_item"),
        [eCharacterClass.Valewalker] = ("Welcome, then, to the Path of Affinity. Here is a gift. Consider it a welcoming gesture. Welcome, {0}.", "valewalker_item"),
        [eCharacterClass.Sluaghbinder] = ("The barrow-bond is yours. Welcome, Sluaghbinder.", null)
    };

    /// <summary>One advanced class from the class scripts: its id, name, base class id and eligible races.</summary>
    public sealed record PromotionPath(int ClassId, string Name, int BaseClassId, IReadOnlyList<eRace> Races);

    private static readonly Lazy<IReadOnlyList<PromotionPath>> Paths = new(LoadPaths);

    /// <summary>
    /// A GameTrainer that is never added to the world, so a run promotes through the trainers' own
    /// CanPromotePlayer / PromotePlayer. Neither touches CurrentRegion (the gift's inventory log reads CurrentRegionID,
    /// which is 0 without a region).
    /// </summary>
    private sealed class PromotionTrainer : GameTrainer
    {
        private readonly eCharacterClass _trainedClass;

        public PromotionTrainer(eCharacterClass trainedClass, string className)
        {
            _trainedClass = trainedClass;
            Name = $"{className} Trainer";
        }

        public override eCharacterClass TrainedClass => _trainedClass;

        /// <summary>GameTrainer.CanPromotePlayer without its level-5 rule: base class, race and gender still apply.</summary>
        public override bool CanPromotePlayer(GamePlayer player)
        {
            ICharacterClass baseClass = ScriptMgr.FindCharacterBaseClass((int) TrainedClass);
            ICharacterClass pickedClass = ScriptMgr.FindCharacterClass((int) TrainedClass);

            if (baseClass == null || pickedClass == null || (eCharacterClass) baseClass.ID == TrainedClass)
            {
                return false;
            }

            if (player.Level < PromotionLevel || player.CharacterClass.ID != baseClass.ID)
            {
                return false;
            }

            if (!pickedClass.EligibleRaces.Exists(s => s.ID == (eRace) player.Race))
            {
                return false;
            }

            if (GlobalConstants.CLASS_GENDER_CONSTRAINTS_DICT.TryGetValue(TrainedClass, out eGender gender) && gender != player.Gender)
            {
                return false;
            }

            return true;
        }
    }

    #region Server events

    [GameServerStartedEvent]
    public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.AddHandler(GamePlayerEvent.LevelUp, new DOLEventHandler(OnLevelUp));
    }

    [GameServerStoppedEvent]
    public static void OnServerStopped(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.LevelUp, new DOLEventHandler(OnLevelUp));
    }

    /// <summary>GamePlayer.Level's setter notifies LevelUp after OnLevelUp: promote at level 5 on an active run.</summary>
    private static void OnLevelUp(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || player.Level < PromotionLevel)
        {
            return;
        }

        if (ExpeditionManager.GetRun(player) == null || !IsBaseClass(player))
        {
            return;
        }

        PromoteOrAsk(player);
    }

    #endregion

    #region Entry points

    /// <summary>A run has just begun: ask which advanced class to train at level 5 (or choose it when there is only one).</summary>
    public static void OnRunStarted(GamePlayer player)
    {
        if (player == null || !IsBaseClass(player))
        {
            return;
        }

        // A new run asks again; an answer from an earlier run of this character is forgotten.
        SaveChoice(player, null);

        List<PromotionPath> candidates = GetCandidates(player);

        if (candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count == 1)
        {
            SaveChoice(player, candidates[0].ClassId);

            if (player.Level >= PromotionLevel)
            {
                Promote(player, candidates[0].ClassId);
            }

            return;
        }

        Ask(player, candidates.Select(path => path.ClassId).ToList(), 0);
    }

    /// <summary>A run has been resumed at login: promote a level 5+ base class whose choice is saved, or ask again if none is.</summary>
    public static void OnRunResumed(GamePlayer player)
    {
        if (player == null || !IsBaseClass(player) || ExpeditionManager.GetRun(player) == null)
        {
            return;
        }

        PromoteOrAsk(player);
    }

    #endregion

    #region Choice and promotion

    /// <summary>Promotes now (level 5+) with a valid saved choice; otherwise picks the only path or starts the dialog chain.</summary>
    private static void PromoteOrAsk(GamePlayer player)
    {
        List<PromotionPath> candidates = GetCandidates(player);

        if (candidates.Count == 0)
        {
            return;
        }

        int? choice = GetChoice(player);

        if (choice.HasValue && candidates.Any(path => path.ClassId == choice.Value))
        {
            if (player.Level >= PromotionLevel)
            {
                Promote(player, choice.Value);
            }

            return;
        }

        if (candidates.Count == 1)
        {
            SaveChoice(player, candidates[0].ClassId);

            if (player.Level >= PromotionLevel)
            {
                Promote(player, candidates[0].ClassId);
            }

            return;
        }

        Ask(player, candidates.Select(path => path.ClassId).ToList(), 0);
    }

    /// <summary>One link of the chain: "train as a X?" Yes saves X; No shows the next class, wrapping after the last.</summary>
    private static void Ask(GamePlayer player, List<int> classIds, int index)
    {
        PromotionPath path = FindPath(classIds[index]);

        if (path == null)
        {
            return;
        }

        string text = $"Train as a {path.Name} now? (No = see the next path)";
        CustomDialogResponse callback = null;
        callback = (responder, response) => OnAnswer(responder, response, callback, classIds, index);
        player.Out.SendCustomDialog(text, callback);
    }

    private static void OnAnswer(GamePlayer player, byte response, CustomDialogResponse self, List<int> classIds, int index)
    {
        if (player == null)
        {
            return;
        }

        // A new dialog answers the open one with 0x00 before replacing it (PacketLib168.SendCustomDialog), while the
        // client's own answer clears the callback first (DialogResponseHandler). Still set: replaced, not answered.
        // Stop the chain then (it is asked again at login or level 5), or two dialogs would keep closing each other.
        if (ReferenceEquals(player.CustomDialogCallback, self))
        {
            return;
        }

        if (ExpeditionManager.GetRun(player) == null || !IsBaseClass(player))
        {
            return;
        }

        if (response != 0x01)
        {
            Ask(player, classIds, NextIndex(index, classIds.Count));

            return;
        }

        PromotionPath path = FindPath(classIds[index]);

        if (path == null)
        {
            return;
        }

        SaveChoice(player, path.ClassId);

        if (player.Level >= PromotionLevel)
        {
            Promote(player, path.ClassId);

            return;
        }

        player.Out.SendMessage($"You will be trained as a {path.Name} when you reach level {PromotionLevel}.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
    }

    /// <summary>Promotes through GameTrainer's own checks and code, then gives the trainer's gift.</summary>
    private static bool Promote(GamePlayer player, int classId)
    {
        PromotionPath path = FindPath(classId);

        if (path == null || player.Client?.Account == null)
        {
            return false;
        }

        var trainer = new PromotionTrainer((eCharacterClass)classId, path.Name);

        if (!trainer.CanPromotePlayer(player))
        {
            Log.Warn($"[EXPEDITION] {player.Name} could not be promoted to {path.Name} (class {player.CharacterClass?.ID}, race {player.Race}, level {player.Level}).");
            SaveChoice(player, null);
            player.Out.SendMessage($"You can't be trained as a {path.Name}. Choose another path.", eChatType.CT_Important, eChatLoc.CL_SystemWindow);

            return false;
        }

        Welcomes.TryGetValue((eCharacterClass)classId, out (string Text, string Gift) welcome);

        if (!trainer.PromotePlayer(player, classId, WelcomeText(classId, path.Name, player.Name), null))
        {
            Log.Warn($"[EXPEDITION] PromotePlayer failed for {player.Name} to {path.Name}.");

            return false;
        }

        // The trainers give their gift after PromotePlayer, with ReceiveItem(this, templateId).
        if (!string.IsNullOrEmpty(welcome.Gift) && !player.ReceiveItem(trainer, welcome.Gift))
        {
            player.Out.SendMessage($"The {path.Name} trainer's gift could not be given (is your backpack full?).", eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        }

        string text = $"You are now a {player.CharacterClass.Name}! Type /train to spend your specialization points.";
        player.Out.SendMessage(text, eChatType.CT_Important, eChatLoc.CL_SystemWindow);
        player.Out.SendMessage(text, eChatType.CT_ScreenCenter, eChatLoc.CL_SystemWindow);

        ExpeditionManager.GetRun(player)?.AddLog($"{player.Name} was trained as a {player.CharacterClass.Name} at level {player.Level}.");

        return true;
    }

    private static bool IsBaseClass(GamePlayer player)
    {
        return player.CharacterClass != null && !player.CharacterClass.HasAdvancedFromBaseClass();
    }

    private static List<PromotionPath> GetCandidates(GamePlayer player)
    {
        return FilterCandidates(AllPaths(), player.CharacterClass.ID, player.Realm, (eRace)player.Race, player.Gender, Properties.ENABLE_SLUAGHBINDER);
    }

    private static PromotionPath FindPath(int classId)
    {
        return AllPaths().FirstOrDefault(path => path.ClassId == classId);
    }

    #endregion

    #region Pure logic (unit tested)

    /// <summary>Every advanced class script with its base class (ScriptMgr.FindCharacterClass / FindCharacterBaseClass), read once.</summary>
    public static IReadOnlyList<PromotionPath> AllPaths()
    {
        return Paths.Value;
    }

    private static IReadOnlyList<PromotionPath> LoadPaths()
    {
        List<PromotionPath> paths = new();

        foreach (eCharacterClass id in Enum.GetValues<eCharacterClass>())
        {
            if (id == eCharacterClass.Unknown)
            {
                continue;
            }

            ICharacterClass characterClass = ScriptMgr.FindCharacterClass((int)id);

            if (characterClass == null || !characterClass.HasAdvancedFromBaseClass())
            {
                continue;
            }

            ICharacterClass baseClass = ScriptMgr.FindCharacterBaseClass((int)id);

            if (baseClass == null || baseClass.ID == characterClass.ID)
            {
                continue;
            }

            List<eRace> races = characterClass.EligibleRaces?.Select(race => race.ID).ToList() ?? new List<eRace>();
            paths.Add(new PromotionPath(characterClass.ID, characterClass.Name, baseClass.ID, races));
        }

        return paths.OrderBy(path => path.ClassId).ToList();
    }

    /// <summary>
    /// The advanced classes a base class can be promoted to: same base class id, creatable in the realm
    /// (GlobalConstants.STARTING_CLASSES_DICT) and part of 1.65, open to the race and gender (null: not checked),
    /// and the Sluaghbinder only when it is enabled. Ordered by class id, which is the dialog order.
    /// </summary>
    public static List<PromotionPath> FilterCandidates(IEnumerable<PromotionPath> paths, int baseClassId, eRealm realm, eRace? race, eGender? gender, bool sluaghbinderEnabled)
    {
        if (paths == null || !GlobalConstants.STARTING_CLASSES_DICT.TryGetValue(realm, out List<eCharacterClass> realmClasses))
        {
            return new List<PromotionPath>();
        }

        return paths
            .Where(path => path.BaseClassId == baseClassId && path.ClassId != baseClassId)
            .Where(path => realmClasses.Contains((eCharacterClass)path.ClassId) && !Post165Classes.Contains(path.ClassId))
            .Where(path => sluaghbinderEnabled || path.ClassId != (int)eCharacterClass.Sluaghbinder)
            .Where(path => race == null || path.Races.Contains(race.Value))
            .Where(path => gender == null || !GlobalConstants.CLASS_GENDER_CONSTRAINTS_DICT.TryGetValue((eCharacterClass)path.ClassId, out eGender only) || only == gender.Value)
            .OrderBy(path => path.ClassId)
            .ToList();
    }

    /// <summary>The dialog after this one: the next class, back to the first after the last.</summary>
    public static int NextIndex(int index, int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        return ((index + 1) % count + count) % count;
    }

    /// <summary>The trainer's welcome for the class, or a generic one; {0} in a trainer text is the player's name.</summary>
    public static string WelcomeText(int classId, string className, string playerName)
    {
        if (Welcomes.TryGetValue((eCharacterClass)classId, out (string Text, string Gift) welcome) && !string.IsNullOrEmpty(welcome.Text))
        {
            return string.Format(CultureInfo.InvariantCulture, welcome.Text, playerName);
        }

        return $"Welcome to the ways of the {className}!";
    }

    /// <summary>The starter gift's item template id for the class, or null.</summary>
    public static string GiftTemplate(int classId)
    {
        return Welcomes.TryGetValue((eCharacterClass)classId, out (string Text, string Gift) welcome) ? welcome.Gift : null;
    }

    /// <summary>The saved value of a choice: the class id in invariant digits.</summary>
    public static string FormatChoice(int classId)
    {
        return classId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a saved choice: a class id from 1 to 255 (eCharacterClass is a byte). Anything else is no choice.</summary>
    public static bool TryParseChoice(string value, out int classId)
    {
        classId = 0;

        if (!int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0 || parsed > byte.MaxValue)
        {
            return false;
        }

        classId = parsed;

        return true;
    }

    #endregion

    #region Persistence (DOLCharactersXCustomParam, as ExpeditionLoot.GetAutoJunk / SetAutoJunk)

    /// <summary>The saved choice, read from the database once per login and cached in TempProperties.</summary>
    public static int? GetChoice(GamePlayer player)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return null;
        }

        string value = player.TempProperties.GetProperty<string>(ChoiceKey);

        if (value == null)
        {
            value = string.Empty;

            try
            {
                DbCoreCharacterXCustomParam record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
                    DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId).And(DB.Column("KeyName").IsEqualTo(ChoiceKey)));

                if (!string.IsNullOrWhiteSpace(record?.Value))
                {
                    value = record.Value;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[EXPEDITION] Could not read the promotion choice of {player.Name}.", ex);
            }

            player.TempProperties.SetProperty(ChoiceKey, value);
        }

        return TryParseChoice(value, out int classId) ? classId : null;
    }

    /// <summary>Saves the choice (null clears it). The in-memory value applies even if the save fails.</summary>
    public static void SaveChoice(GamePlayer player, int? classId)
    {
        if (player == null || string.IsNullOrEmpty(player.ObjectId))
        {
            return;
        }

        string value = classId.HasValue ? FormatChoice(classId.Value) : string.Empty;
        player.TempProperties.SetProperty(ChoiceKey, value);

        try
        {
            DbCoreCharacterXCustomParam record = DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
                DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId).And(DB.Column("KeyName").IsEqualTo(ChoiceKey)));

            if (record == null)
            {
                if (value.Length > 0)
                {
                    GameServer.Database.AddObject(new DbCoreCharacterXCustomParam(player.ObjectId, ChoiceKey, value));
                }

                return;
            }

            record.Value = value;
            GameServer.Database.SaveObject(record);
        }
        catch (Exception ex)
        {
            Log.Error($"[EXPEDITION] Could not save the promotion choice of {player.Name}.", ex);
        }
    }

    #endregion
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DOL.Database;
using DOL.Database.Attributes;
using DOL.GS.PacketHandler;
using DOL.Logging;

namespace DOL.GS.Commands
{
    /// <summary>
    /// Fork (owner 2026-10-25): brings back a deleted character of this account, such as a hardcore Expedition character
    /// that died. CharacterCreateRequestHandler.DeleteCharacter copies a character to DOLCharactersBackup before it
    /// deletes it, and leaves its items, quests and run record in place under the same character id, so restoring the row
    /// with its old id brings everything back. The restored character has no deaths and stands at its bind point; when the
    /// death ended an Expedition run, the run carries on (ExpeditionRecovery) and the character stays hardcore.
    /// </summary>
    [CmdAttribute(
        "&recover",
        ePrivLevel.Player,
        "Restores a deleted character of this account",
        "/recover - lists this account's deleted characters",
        "/recover <name> - restores that character")]
    public class RecoverCommandHandler : AbstractCommandHandler, ICommandHandler
    {
        private static readonly Logger log = LoggerManager.Create(MethodBase.GetCurrentMethod().DeclaringType);

        private const int SlotsPerRealm = 10;

        public void OnCommand(GameClient client, string[] args)
        {
            if (client?.Account == null || IsSpammingCommand(client.Player, "recover"))
            {
                return;
            }

            List<DbCoreCharacterBackup> backups = GameServer.Database
                .SelectObjects<DbCoreCharacterBackup>(DB.Column("AccountName").IsEqualTo(client.Account.Name))
                .OrderByDescending(b => b.DeleteDate)
                .ToList();

            if (args.Length < 2)
            {
                if (backups.Count == 0)
                {
                    DisplayMessage(client, "This account has no deleted characters to recover.");

                    return;
                }

                DisplayMessage(client, "Deleted characters of this account (newest first):");

                foreach (DbCoreCharacterBackup backup in backups)
                {
                    string className = ((eCharacterClass) backup.Class).ToString();
                    DisplayMessage(client, $"  {backup.Name} - level {backup.Level} {className}, deleted {backup.DeleteDate:yyyy-MM-dd HH:mm}");
                }

                DisplayMessage(client, "Use /recover <name> to restore one.");

                return;
            }

            string name = args[1];
            DbCoreCharacterBackup chosen = backups.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (chosen == null)
            {
                DisplayMessage(client, $"No deleted character named {name} on this account. Use /recover to list them.");

                return;
            }

            if (GameServer.Database.SelectObjects<DbCoreCharacter>(DB.Column("Name").IsEqualTo(chosen.Name)).Any())
            {
                DisplayMessage(client, $"A character named {chosen.Name} already exists, so it can't be restored.");

                return;
            }

            if (GameServer.Database.FindObjectByKey<DbCoreCharacter>(chosen.DOLCharacters_ID) != null)
            {
                DisplayMessage(client, $"{chosen.Name} is already restored.");

                return;
            }

            int? slot = FreeSlot(client.Account, chosen.Realm, chosen.AccountSlot);

            if (slot == null)
            {
                DisplayMessage(client, $"There is no free character slot in {chosen.Name}'s realm. Delete or move a character first.");

                return;
            }

            DbCoreCharacter character = Restore(chosen, slot.Value);

            // An Expedition run that ended in this death carries on: the character is hardcore again and the next
            // login takes it to the act's camp (ExpeditionRecovery).
            bool runRevived = DOL.GS.Expedition.ExpeditionRecovery.ReviveRun(chosen.DOLCharacters_ID);
            character.HCFlag = runRevived;

            try
            {
                GameServer.Database.AddObject(character);

                foreach (DbCoreCharacterXCustomParam param in character.CustomParams)
                {
                    GameServer.Database.AddObject(param);
                }

                // Only one copy may restore: the backup goes once the character is back.
                GameServer.Database.DeleteObject(chosen);
            }
            catch (Exception ex)
            {
                log.Error($"[RECOVER] Could not restore {chosen.Name} for account {client.Account.Name}.", ex);
                DisplayMessage(client, $"{chosen.Name} could not be restored. The server log has the details.");

                return;
            }

            client.Account.Characters = null;
            GameServer.Database.FillObjectRelations(client.Account);

            if (client.Account.Realm == 0)
            {
                client.Account.Realm = chosen.Realm;
                GameServer.Database.SaveObject(client.Account);
            }

            log.Warn($"[RECOVER] {client.Account.Name} restored {chosen.Name} (level {chosen.Level}, id {chosen.DOLCharacters_ID}).");
            if (runRevived)
            {
                DisplayMessage(client, $"{chosen.Name} is restored with its items, and its expedition carries on from the act it died in (still hardcore). Go back to character select to play it.");
            }
            else
            {
                DisplayMessage(client, $"{chosen.Name} is restored at its bind point with its items, no longer hardcore. Go back to character select to play it.");
            }
        }

        /// <summary>The character's old slot when it is free, else the first free slot of its realm.</summary>
        private static int? FreeSlot(DbAccount account, int realm, int oldSlot)
        {
            HashSet<int> used = new((account.Characters ?? Array.Empty<DbCoreCharacter>()).Select(c => c.AccountSlot));

            if (oldSlot / 100 == realm && !used.Contains(oldSlot))
            {
                return oldSlot;
            }

            for (int i = 0; i < SlotsPerRealm; i++)
            {
                int candidate = realm * 100 + i;

                if (!used.Contains(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>Every saved field of the backup on a new character row with the old id, made safe to log in.</summary>
        private static DbCoreCharacter Restore(DbCoreCharacterBackup backup, int slot)
        {
            DbCoreCharacter character = new();

            foreach (PropertyInfo property in typeof(DbCoreCharacter).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name == nameof(DataObject.ObjectId) || !property.CanRead || !property.CanWrite || property.GetCustomAttribute<DataElement>(true) == null)
                {
                    continue;
                }

                property.SetValue(character, property.GetValue(backup));
            }

            character.ObjectId = backup.DOLCharacters_ID;
            character.AccountSlot = slot;
            character.DeathCount = 0;
            character.HCFlag = false;

            // Back at the bind point (the start town for a run character), never where it died.
            character.Region = backup.BindRegion;
            character.Xpos = backup.BindXpos;
            character.Ypos = backup.BindYpos;
            character.Zpos = backup.BindZpos;
            character.Direction = backup.BindHeading;

            // Full again: the load clamps these to the maximum.
            character.Health = 100000;
            character.Mana = 100000;
            character.Endurance = 100000;

            character.CustomParams = (backup.CustomParams ?? Array.Empty<DbCoreCharacterBackupXCustomParam>())
                .Select(p => new DbCoreCharacterXCustomParam(character.ObjectId, p.KeyName, p.Value))
                .ToArray();

            return character;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx;
using UnityEngine;

namespace SaveSlotsMod
{
    public static class SaveSlotManager
    {
        public const int    MaxSlots     = 5;
        public const string SaveFileName = "SaveFile.gwsave";

        private static string SaveDir        => Application.persistentDataPath;
        private static string SlotsDir       => Path.Combine(SaveDir, "SaveSlots");
        private static string ActiveSlotFile => Path.Combine(SlotsDir, "active_slot.txt");
        public  static string LiveSavePath   => Path.Combine(SaveDir, SaveFileName);

        public static int ActiveSlot { get; private set; } = 0;

        // ── Init ─────────────────────────────────────────────────────────────────
        public static void Initialize()
        {
            Directory.CreateDirectory(SlotsDir);

            // Read the previously active slot (if any)
            if (File.Exists(ActiveSlotFile) &&
                int.TryParse(File.ReadAllText(ActiveSlotFile).Trim(), out int saved))
                ActiveSlot = Math.Min(Math.Max(saved, 0), MaxSlots - 1);

            // First-launch migration: if no slot files exist but a live save does,
            // copy it into Slot 0 so the user doesn't lose their progress.
            bool noSlotFiles = !Enumerable.Range(0, MaxSlots).Any(SlotHasSave);
            if (noSlotFiles && File.Exists(LiveSavePath))
            {
                File.Copy(LiveSavePath, SlotSaveFile(0), overwrite: true);
                var meta = new SlotMeta
                {
                    LastSaved = File.GetLastWriteTimeUtc(LiveSavePath),
                    ModGuids  = GetCurrentModGuids()
                };
                SaveSlotMeta(0, meta);
                ActiveSlot = 0;
                File.WriteAllText(ActiveSlotFile, "0");
                Plugin.Log.LogInfo("[SaveSlotManager] First-launch: migrated live save → Slot 0.");
            }

            Plugin.Log.LogInfo(
                $"[SaveSlotManager] Active slot: {ActiveSlot}. Slots dir: {SlotsDir}");
        }

        // ── Paths ─────────────────────────────────────────────────────────────────
        public static string SlotSaveFile(int slot) =>
            Path.Combine(SlotsDir, $"Slot{slot}_SaveFile.gwsave");
        public static string SlotMetaFile(int slot) =>
            Path.Combine(SlotsDir, $"Slot{slot}_mods.json");
        public static bool   SlotHasSave(int slot) =>
            File.Exists(SlotSaveFile(slot));

        // ── Serialisation ─────────────────────────────────────────────────────────
        private static readonly DataContractJsonSerializerSettings _dcjSettings =
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true };

        public static SlotMeta? LoadSlotMeta(int slot)
        {
            string path = SlotMetaFile(slot);
            if (!File.Exists(path)) return null;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(SlotMeta), _dcjSettings);
                using var stream = File.OpenRead(path);
                return (SlotMeta?)ser.ReadObject(stream);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] Meta read failed slot {slot}: {ex.Message}");
                return null;
            }
        }

        private static void SaveSlotMeta(int slot, SlotMeta meta)
        {
            var ser = new DataContractJsonSerializer(typeof(SlotMeta), _dcjSettings);
            using var ms = new MemoryStream();
            ser.WriteObject(ms, meta);
            File.WriteAllBytes(SlotMetaFile(slot), ms.ToArray());
        }

        public static List<string> GetCurrentModGuids() =>
            BepInEx.Bootstrap.Chainloader.PluginInfos.Keys.OrderBy(g => g).ToList();

        // ── Backup live save → active slot ────────────────────────────────────────
        /// <summary>
        /// Call this BEFORE showing the slot picker so the current game state is
        /// reflected. Safe to call even if no live save exists.
        /// </summary>
        public static void BackupLiveSave()
        {
            if (!File.Exists(LiveSavePath)) return;
            try
            {
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                // Update meta timestamp (keep old mod list if file existed)
                var existing = LoadSlotMeta(ActiveSlot);
                var meta = new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = existing?.ModGuids ?? GetCurrentModGuids()
                };
                SaveSlotMeta(ActiveSlot, meta);
                Plugin.Log.LogInfo($"[SaveSlotManager] Backed up live save → Slot {ActiveSlot}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] Backup failed: {ex.Message}");
            }
        }

        // ── Switch ────────────────────────────────────────────────────────────────
        public static void SwitchToSlot(int targetSlot)
        {
            if (targetSlot < 0 || targetSlot >= MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(targetSlot));

            // Remember whether target slot had a save BEFORE we do anything,
            // so backup-then-copy doesn't accidentally find its own backup.
            bool targetWasEmpty = !SlotHasSave(targetSlot);

            // Save current live game to the active slot (skip if same slot & was empty —
            // we'd just be backing up nothing useful and creating a "ghost" save).
            bool sameSlotNewGame = (targetSlot == ActiveSlot && targetWasEmpty);
            if (File.Exists(LiveSavePath) && !sameSlotNewGame)
            {
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                Plugin.Log.LogInfo($"[SaveSlotManager] Backed up live → Slot {ActiveSlot}");
            }

            // Install the chosen slot (or clear for a fresh game)
            if (!targetWasEmpty)
            {
                File.Copy(SlotSaveFile(targetSlot), LiveSavePath, overwrite: true);
                Plugin.Log.LogInfo($"[SaveSlotManager] Slot {targetSlot} → live save");
            }
            else
            {
                if (File.Exists(LiveSavePath)) File.Delete(LiveSavePath);
                Plugin.Log.LogInfo($"[SaveSlotManager] Slot {targetSlot} is empty — new game");
            }

            ActiveSlot = targetSlot;
            File.WriteAllText(ActiveSlotFile, targetSlot.ToString());
        }

        // ── Game saved hook ───────────────────────────────────────────────────────
        public static void OnGameSaved()
        {
            if (!File.Exists(LiveSavePath)) return;
            File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
            var meta = new SlotMeta
            {
                LastSaved = DateTime.UtcNow,
                ModGuids  = GetCurrentModGuids()
            };
            SaveSlotMeta(ActiveSlot, meta);
            Plugin.Log.LogInfo($"[SaveSlotManager] Slot {ActiveSlot} saved ({meta.ModGuids.Count} mods).");
        }

        // ── Mod diff ──────────────────────────────────────────────────────────────
        public static ModDiff ComputeDiff(int slot)
        {
            var current = GetCurrentModGuids();
            var saved   = LoadSlotMeta(slot)?.ModGuids ?? new List<string>();
            return new ModDiff
            {
                Added   = current.Except(saved).ToList(),
                Removed = saved.Except(current).ToList(),
                Same    = current.SequenceEqual(saved)
            };
        }

        // ── Delete slot ───────────────────────────────────────────────────────────
        public static void DeleteSlot(int slot)
        {
            if (File.Exists(SlotSaveFile(slot))) File.Delete(SlotSaveFile(slot));
            if (File.Exists(SlotMetaFile(slot)))  File.Delete(SlotMetaFile(slot));
            Plugin.Log.LogInfo($"[SaveSlotManager] Slot {slot} deleted.");
        }
    }

    [DataContract]
    public class SlotMeta
    {
        [DataMember] public DateTime     LastSaved { get; set; }
        [DataMember] public List<string> ModGuids  { get; set; } = new List<string>();
    }

    public class ModDiff
    {
        public List<string> Added   { get; set; } = new List<string>();
        public List<string> Removed { get; set; } = new List<string>();
        public bool         Same    { get; set; }
    }
}

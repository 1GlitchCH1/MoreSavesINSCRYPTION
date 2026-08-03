using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using BepInEx;
using UnityEngine;

namespace SaveSlotsMod
{
    /// <summary>
    /// Управляет слотами сохранений.
    ///
    /// Слот 0 (отображается как «Слот 1») — ОСНОВНОЕ СОХРАНЕНИЕ.
    ///   Удалить нельзя. При первом запуске сюда мигрирует существующий SaveFile.gwsave.
    ///
    /// Слоты 1–4 (отображаются как «Слот 2–5») — независимые сохранения, изначально пустые.
    /// </summary>
    public static class SaveSlotManager
    {
        public const int    MaxSlots     = 5;
        public const string SaveFileName = "SaveFile.gwsave";

        private static string SaveDir        => Application.persistentDataPath;
        private static string SlotsDir       => Path.Combine(SaveDir, "SaveSlots");
        private static string ActiveSlotFile => Path.Combine(SlotsDir, "active_slot.txt");
        public  static string LiveSavePath   => Path.Combine(SaveDir, SaveFileName);

        public static int ActiveSlot { get; private set; } = 0;

        /// <summary>Количество слотов с файлом сохранения.</summary>
        public static int SlotCount => Enumerable.Range(0, MaxSlots).Count(SlotHasSave);

        /// <summary>Слот 0 — основное сохранение, удалять нельзя.</summary>
        public static bool IsMainSaveSlot(int slot) => slot == 0;

        // ── Init ─────────────────────────────────────────────────────────────────
        public static void Initialize()
        {
            Directory.CreateDirectory(SlotsDir);

            bool hasSlotFiles = Enumerable.Range(0, MaxSlots).Any(SlotHasSave);
            bool hasLiveSave  = File.Exists(LiveSavePath);

            Plugin.Log.LogInfo($"[SaveSlotManager] LiveSavePath : {LiveSavePath} (exists={hasLiveSave})");
            Plugin.Log.LogInfo($"[SaveSlotManager] SlotsDir    : {SlotsDir} (hasSlotFiles={hasSlotFiles})");

            if (!hasSlotFiles)
            {
                // ── Первый запуск или сломанное состояние ──────────────────────────
                ActiveSlot = 0;

                if (hasLiveSave)
                {
                    File.Copy(LiveSavePath, SlotSaveFile(0), overwrite: true);
                    SaveSlotMeta(0, new SlotMeta
                    {
                        LastSaved = File.GetLastWriteTimeUtc(LiveSavePath),
                        ModGuids  = GetCurrentModGuids()
                    });
                    Plugin.Log.LogInfo("[SaveSlotManager] Первый запуск: перенесли живое сохранение → Слот 0 (основное).");
                }
                else
                {
                    Plugin.Log.LogInfo("[SaveSlotManager] Первый запуск: живого сохранения нет. Слот 0 будет пустым.");
                }

                File.WriteAllText(ActiveSlotFile, "0");
            }
            else
            {
                // ── Восстанавливаем ранее активный слот ───────────────────────────
                if (File.Exists(ActiveSlotFile) &&
                    int.TryParse(File.ReadAllText(ActiveSlotFile).Trim(), out int saved))
                    ActiveSlot = Math.Min(Math.Max(saved, 0), MaxSlots - 1);
                else
                    ActiveSlot = 0;

                // Защита: если у активного слота нет файла — сбрасываемся на Слот 0
                if (!SlotHasSave(ActiveSlot))
                {
                    Plugin.Log.LogWarning(
                        $"[SaveSlotManager] Слот {ActiveSlot} не имеет файла — сброс на Слот 0.");
                    ActiveSlot = 0;
                    File.WriteAllText(ActiveSlotFile, "0");
                }

                // Защита: если живого сохранения нет, но файл слота есть — восстанавливаем
                if (!hasLiveSave && SlotHasSave(ActiveSlot))
                {
                    File.Copy(SlotSaveFile(ActiveSlot), LiveSavePath, overwrite: true);
                    Plugin.Log.LogInfo($"[SaveSlotManager] Восстановили Слот {ActiveSlot} → живое сохранение.");
                }
            }

            // ── Подписываемся на выход из приложения ──────────────────────────────
            Application.quitting += OnApplicationQuitting;

            Plugin.Log.LogInfo(
                $"[SaveSlotManager] Активный слот: {ActiveSlot}. Слотов с сохранением: {SlotCount}.");
        }

        /// <summary>
        /// Вызывается когда Unity закрывает приложение.
        /// Гарантирует, что живое сохранение всегда попадёт в файл слота.
        /// </summary>
        private static void OnApplicationQuitting()
        {
            if (!File.Exists(LiveSavePath)) return;
            try
            {
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                SaveSlotMeta(ActiveSlot, new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = GetCurrentModGuids()
                });
                Plugin.Log.LogInfo($"[SaveSlotManager] OnApplicationQuitting: Слот {ActiveSlot} сохранён.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] OnApplicationQuitting error: {ex.Message}");
            }
        }

        // ── Пути ─────────────────────────────────────────────────────────────────
        public static string SlotSaveFile(int slot) =>
            Path.Combine(SlotsDir, $"Slot{slot}_SaveFile.gwsave");
        public static string SlotMetaFile(int slot) =>
            Path.Combine(SlotsDir, $"Slot{slot}_mods.json");
        public static bool   SlotHasSave(int slot) =>
            File.Exists(SlotSaveFile(slot));

        // ── Сериализация ─────────────────────────────────────────────────────────
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
                Plugin.Log.LogWarning($"[SaveSlotManager] Ошибка чтения мета слота {slot}: {ex.Message}");
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

        // ── Резервная копия живого сохранения → активный слот ───────────────────
        /// <summary>
        /// Копирует живое сохранение в файл активного слота.
        /// Безопасен, если живого сохранения нет.
        /// </summary>
        public static void BackupLiveSave()
        {
            if (!File.Exists(LiveSavePath)) return;
            try
            {
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                var existing = LoadSlotMeta(ActiveSlot);
                SaveSlotMeta(ActiveSlot, new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = existing?.ModGuids ?? GetCurrentModGuids()
                });
                Plugin.Log.LogInfo($"[SaveSlotManager] BackupLiveSave → Слот {ActiveSlot}.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] Ошибка BackupLiveSave: {ex.Message}");
            }
        }

        // ── Переключение слота ────────────────────────────────────────────────────
        public static void SwitchToSlot(int targetSlot)
        {
            if (targetSlot < 0 || targetSlot >= MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(targetSlot));

            bool targetWasEmpty = !SlotHasSave(targetSlot);

            // Сохраняем текущее живое сохранение в файл активного слота
            if (File.Exists(LiveSavePath))
            {
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                Plugin.Log.LogInfo($"[SaveSlotManager] Живое сохранение → Слот {ActiveSlot}.");
            }

            // Устанавливаем целевой слот как живое сохранение
            if (!targetWasEmpty)
            {
                File.Copy(SlotSaveFile(targetSlot), LiveSavePath, overwrite: true);
                Plugin.Log.LogInfo($"[SaveSlotManager] Слот {targetSlot} → живое сохранение.");
            }
            else
            {
                // Пустой слот — удаляем живое сохранение; игра создаст новое
                if (File.Exists(LiveSavePath))
                    File.Delete(LiveSavePath);
                Plugin.Log.LogInfo($"[SaveSlotManager] Слот {targetSlot} пуст — начинаем новую игру.");
            }

            ActiveSlot = targetSlot;
            File.WriteAllText(ActiveSlotFile, targetSlot.ToString());
        }

        // ── Вызывается после сохранения игрой ────────────────────────────────────
        public static void OnGameSaved()
        {
            if (!File.Exists(LiveSavePath)) return;
            try
            {
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                SaveSlotMeta(ActiveSlot, new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = GetCurrentModGuids()
                });
                Plugin.Log.LogInfo($"[SaveSlotManager] OnGameSaved → Слот {ActiveSlot} сохранён.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] Ошибка OnGameSaved: {ex.Message}");
            }
        }

        // ── Разница модов ─────────────────────────────────────────────────────────
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

        // ── Удаление слота ────────────────────────────────────────────────────────
        public static void DeleteSlot(int slot)
        {
            if (IsMainSaveSlot(slot))
            {
                Plugin.Log.LogWarning("[SaveSlotManager] Основное сохранение (Слот 0) удалить нельзя.");
                return;
            }
            if (File.Exists(SlotSaveFile(slot))) File.Delete(SlotSaveFile(slot));
            if (File.Exists(SlotMetaFile(slot)))  File.Delete(SlotMetaFile(slot));
            Plugin.Log.LogInfo($"[SaveSlotManager] Слот {slot} удалён.");
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

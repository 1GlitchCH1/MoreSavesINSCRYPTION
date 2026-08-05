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

        private static string SaveDir             => Application.persistentDataPath;
        private static string DefaultLiveSavePath => Path.Combine(SaveDir, SaveFileName);
        private static string SlotsDir            => Path.Combine(SaveDir, "SaveSlots");
        private static string ActiveSlotFile      => Path.Combine(SlotsDir, "active_slot.txt");

        // В некоторых сборках/режимах модов игра может писать сейв не туда, где мы ожидаем.
        // Поэтому живой сейв "разрешаем" динамически (по наличию .gwsave в папке).
        private static string _liveSavePath = DefaultLiveSavePath;
        public  static string LiveSavePath => _liveSavePath;

        public static int ActiveSlot { get; private set; } = 0;

        // Время последней УСПЕШНОЙ копии живого сейва в слот.
        // Нельзя обновлять это значение до успешного File.Copy, иначе при file-lock мы потеряем сейв.
        private static DateTime _lastCopiedLiveWriteUtc = DateTime.MinValue;

        // Чтобы не заспамить лог, если живого сейва нет, логируем это не чаще ~раз в 10 секунд.
        private static int _missingLivePollTicks = 0;

        /// <summary>Количество слотов с файлом сохранения.</summary>
        public static int SlotCount => Enumerable.Range(0, MaxSlots).Count(SlotHasSave);

        /// <summary>Слот 0 — основное сохранение, удалять нельзя.</summary>
        public static bool IsMainSaveSlot(int slot) => slot == 0;

        // ── Init ─────────────────────────────────────────────────────────────────
        public static void Initialize()
        {
            Directory.CreateDirectory(SlotsDir);

            bool hasSlotFiles = Enumerable.Range(0, MaxSlots).Any(SlotHasSave);
            RefreshLiveSavePath(logIfChanged: false);
            bool hasLiveSave = File.Exists(LiveSavePath);

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
                        $"[SaveSlotManager] Слот {ActiveSlot + 1} не имеет файла — сброс на Слот 1.");
                    ActiveSlot = 0;
                    File.WriteAllText(ActiveSlotFile, "0");
                }

                // Защита: если живого сохранения нет, но файл слота есть — восстанавливаем
                if (!hasLiveSave && SlotHasSave(ActiveSlot))
                {
                    File.Copy(SlotSaveFile(ActiveSlot), LiveSavePath, overwrite: true);
                    Plugin.Log.LogInfo($"[SaveSlotManager] Восстановили Слот {ActiveSlot + 1} → живое сохранение.");
                }
            }

            // ── Подписываемся на выход из приложения ──────────────────────────────
            Application.quitting += OnApplicationQuitting;

            // Инициализируем "последний успешно скопированный" timestamp
            // (после всех возможных копирований/восстановлений в Initialize).
            _lastCopiedLiveWriteUtc = File.Exists(LiveSavePath)
                ? File.GetLastWriteTimeUtc(LiveSavePath)
                : DateTime.MinValue;

            Plugin.Log.LogInfo(
                $"[SaveSlotManager] Активный слот: {ActiveSlot + 1}. Слотов с сохранением: {SlotCount}.");
        }

        /// <summary>
        /// Вызывается когда Unity закрывает приложение.
        /// Гарантирует, что живое сохранение всегда попадёт в файл слота.
        /// </summary>
        private static void OnApplicationQuitting()
        {
            CopyLiveSaveToActiveSlot("OnApplicationQuitting", allowSameTimestamp: true);
        }

        // ── Поиск "живого" сейва ────────────────────────────────────────────────
        private static void RefreshLiveSavePath(bool logIfChanged)
        {
            string resolved = ResolveLiveSavePath();
            if (string.IsNullOrWhiteSpace(resolved))
                resolved = DefaultLiveSavePath;

            if (string.Equals(_liveSavePath, resolved, StringComparison.OrdinalIgnoreCase))
                return;

            string old = _liveSavePath;
            _liveSavePath = resolved;

            if (logIfChanged)
                Plugin.Log.LogInfo($"[SaveSlotManager] LiveSavePath сменился: {old} -> {_liveSavePath}");
        }

        private static string ResolveLiveSavePath()
        {
            // 1) Стандартный путь (то, что ожидает игра почти всегда)
            if (File.Exists(DefaultLiveSavePath))
                return DefaultLiveSavePath;

            // 2) Если его нет — пытаемся найти любой .gwsave в корне папки сохранений.
            // Это покрывает странные сборки/режимы, где имя файла отличается.
            try
            {
                var files = Directory.GetFiles(SaveDir, "*.gwsave", SearchOption.TopDirectoryOnly);
                if (files.Length == 0) return DefaultLiveSavePath;
                return files
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .First();
            }
            catch
            {
                return DefaultLiveSavePath;
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
            try
            {
                RefreshLiveSavePath(logIfChanged: false);
                if (!File.Exists(LiveSavePath)) return;

                var wt = File.GetLastWriteTimeUtc(LiveSavePath);
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                _lastCopiedLiveWriteUtc = wt;
                var existing = LoadSlotMeta(ActiveSlot);
                SaveSlotMeta(ActiveSlot, new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = existing?.ModGuids ?? GetCurrentModGuids()
                });
                Plugin.Log.LogInfo($"[SaveSlotManager] BackupLiveSave → Слот {ActiveSlot + 1}.");
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
            RefreshLiveSavePath(logIfChanged: false);
            if (File.Exists(LiveSavePath))
            {
                var wt = File.GetLastWriteTimeUtc(LiveSavePath);
                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                _lastCopiedLiveWriteUtc = wt;
                Plugin.Log.LogInfo($"[SaveSlotManager] Живое сохранение → Слот {ActiveSlot + 1}.");
            }

            // Устанавливаем целевой слот как живое сохранение
            if (!targetWasEmpty)
            {
                File.Copy(SlotSaveFile(targetSlot), LiveSavePath, overwrite: true);
                _lastCopiedLiveWriteUtc = File.GetLastWriteTimeUtc(LiveSavePath);
                Plugin.Log.LogInfo($"[SaveSlotManager] Слот {targetSlot + 1} → живое сохранение.");
            }
            else
            {
                // Пустой слот — удаляем живое сохранение; игра создаст новое
                if (File.Exists(LiveSavePath))
                    File.Delete(LiveSavePath);
                _lastCopiedLiveWriteUtc = DateTime.MinValue;
                Plugin.Log.LogInfo($"[SaveSlotManager] Слот {targetSlot + 1} пуст — начинаем новую игру.");
            }

            ActiveSlot = targetSlot;
            File.WriteAllText(ActiveSlotFile, targetSlot.ToString());
        }

        // ── Вызывается после сохранения игрой ────────────────────────────────────
        public static void OnGameSaved()
        {
            CopyLiveSaveToActiveSlot("OnGameSaved", allowSameTimestamp: true);
        }

        // ── Поллинг изменения файла живого сейва ─────────────────────────────────
        /// <summary>
        /// Отслеживает изменение <see cref="LiveSavePath"/> и сохраняет в активный слот.
        /// Работает даже если Harmony-патч SaveManager.SaveToFile не вызывается (MonoMod/APIPatcher).
        /// </summary>
        public static void PollLiveSaveChanges()
        {
            RefreshLiveSavePath(logIfChanged: false);
            if (!File.Exists(LiveSavePath))
            {
                _missingLivePollTicks++;
                // 0.25s * 40 ≈ 10 секунд
                if (_missingLivePollTicks % 40 == 0)
                    Plugin.Log.LogInfo($"[SaveSlotManager] Живого сейва пока нет ({LiveSavePath}).");
                return;
            }

            _missingLivePollTicks = 0;

            DateTime wt;
            try { wt = File.GetLastWriteTimeUtc(LiveSavePath); }
            catch { return; }

            if (wt <= _lastCopiedLiveWriteUtc) return;

            // Пытаемся скопировать. Если файл залочен — не обновляем _lastCopiedLiveWriteUtc,
            // чтобы попробовать снова на следующем тике.
            CopyLiveSaveToActiveSlot("Poll", allowSameTimestamp: false);
        }

        private static void CopyLiveSaveToActiveSlot(string reason, bool allowSameTimestamp)
        {
            try
            {
                RefreshLiveSavePath(logIfChanged: false);
                if (!File.Exists(LiveSavePath)) return;

                DateTime wt = File.GetLastWriteTimeUtc(LiveSavePath);
                if (!allowSameTimestamp && wt <= _lastCopiedLiveWriteUtc) return;

                File.Copy(LiveSavePath, SlotSaveFile(ActiveSlot), overwrite: true);
                _lastCopiedLiveWriteUtc = wt;

                SaveSlotMeta(ActiveSlot, new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = GetCurrentModGuids()
                });

                Plugin.Log.LogInfo($"[SaveSlotManager] {reason}: живой сейв → Слот {ActiveSlot + 1}.");
            }
            catch (IOException ioEx)
            {
                // Чаще всего это file-lock во время записи сейва. Просто попробуем снова на следующем тике.
                Plugin.Log.LogWarning($"[SaveSlotManager] {reason}: файл сейва занят, повторим. ({ioEx.Message})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] {reason} error: {ex.Message}");
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
            Plugin.Log.LogInfo($"[SaveSlotManager] Слот {slot + 1} удалён.");
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

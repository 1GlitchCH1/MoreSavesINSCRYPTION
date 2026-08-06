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
    /// Слоты 1–4 (отображается как «Слот 2–5») — независимые сохранения, изначально пустые.
    /// </summary>
    public static class SaveSlotManager
    {
        public const int    MaxSlots     = 5;
        public const string SaveFileName = "SaveFile.gwsave";

        // ── Папки ────────────────────────────────────────────────────────────────
        // Живой сейв Inscryption (Steam) лежит в КОРНЕ папки установки игры,
        // т.е. на уровень выше Application.dataPath (которая = "<игра>/Inscryption_Data").
        // РАНЬШЕ мод искал его в Application.persistentDataPath — это было НЕВЕРНО,
        // из-за чего мод вообще не видел сейв и слоты оставались пустыми.
        private static string GameFolder => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // Файлы слотов храним в persistentDataPath — переживают переустановку игры
        // и не требуют прав на запись в Program Files.
        private static string SlotsDir       => Path.Combine(Application.persistentDataPath, "SaveSlots");
        private static string ActiveSlotFile => Path.Combine(SlotsDir, "active_slot.txt");

        private static string DefaultLiveSavePath => Path.Combine(GameFolder, SaveFileName);

        // Живой сейв "разрешаем" динамически (по наличию .gwsave), чтобы покрыть
        // нестандартные сборки/магазинные версии.
        private static string _liveSavePath = "";
        public  static string LiveSavePath
        {
            get
            {
                if (string.IsNullOrEmpty(_liveSavePath))
                    _liveSavePath = ResolveLiveSavePath();
                return _liveSavePath;
            }
        }

        public static int ActiveSlot { get; private set; } = 0;

        // Время последней УСПЕШНОЙ копии живого сейва в слот.
        private static DateTime _lastCopiedLiveWriteUtc = DateTime.MinValue;

        // Чтобы не заспамить лог, если живого сейва нет.
        private static int _missingLivePollTicks = 0;

        /// <summary>Количество слотов с файлом сохранения.</summary>
        public static int SlotCount => Enumerable.Range(0, MaxSlots).Count(SlotHasSave);

        /// <summary>Слот 0 — основное сохранение, удалять нельзя.</summary>
        public static bool IsMainSaveSlot(int slot) => slot == 0;

        // ── Авторемонт повреждённого сейва ───────────────────────────────────────
        // Если SaveFile.gwsave повреждён (не читается как валидный JSON) и рядом
        // лежит SaveFile-backup.gwsave — тихо подменяем основной файл резервным.
        private static void AutoRepairCorruptedSave()
        {
            string mainPath   = DefaultLiveSavePath;
            string backupPath = Path.Combine(GameFolder, "SaveFile-backup.gwsave");

            if (!File.Exists(backupPath)) return;

            bool mainCorrupted = false;
            if (!File.Exists(mainPath))
            {
                mainCorrupted = true;
            }
            else
            {
                try
                {
                    string text = File.ReadAllText(mainPath);
                    mainCorrupted = string.IsNullOrWhiteSpace(text) || !IsValidJson(text);
                }
                catch
                {
                    mainCorrupted = true;
                }
            }

            if (!mainCorrupted) return;

            try
            {
                File.Copy(backupPath, mainPath, overwrite: true);
                Plugin.Log.LogInfo("[SaveSlotManager] Повреждённый сейв заменён резервной копией автоматически.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] Не удалось восстановить сейв из бэкапа: {ex.Message}");
            }
        }

        private static bool IsValidJson(string text)
        {
            text = text.Trim();
            return (text.StartsWith("{") && text.EndsWith("}"))
                || (text.StartsWith("[") && text.EndsWith("]"));
        }

        // ── Init ─────────────────────────────────────────────────────────────────
        public static void Initialize()
        {
            Directory.CreateDirectory(SlotsDir);

            AutoRepairCorruptedSave();

            bool hasSlotFiles = Enumerable.Range(0, MaxSlots).Any(SlotHasSave);
            _liveSavePath = ResolveLiveSavePath();
            bool hasLiveSave = File.Exists(LiveSavePath);

            Plugin.Log.LogInfo($"[SaveSlotManager] GameFolder   : {GameFolder}");
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
            Application.quitting -= OnApplicationQuitting;
            Application.quitting += OnApplicationQuitting;

            // Инициализируем "последний успешно скопированный" timestamp
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
        private static string ResolveLiveSavePath()
        {
            // 1) Стандартный путь — корень папки установки игры (Steam-версия).
            if (File.Exists(DefaultLiveSavePath))
                return DefaultLiveSavePath;

            // 2) На всякий случай проверяем persistentDataPath (некоторые магазинные
            //    сборки пишут сейв туда).
            string persistent = Path.Combine(Application.persistentDataPath, SaveFileName);
            if (File.Exists(persistent))
                return persistent;

            // 3) Ищем любой .gwsave в корне папки игры.
            try
            {
                var files = Directory.GetFiles(GameFolder, "*.gwsave", SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                    return files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            catch { /* ignore */ }

            // 4) Ищем в persistentDataPath.
            try
            {
                var files = Directory.GetFiles(Application.persistentDataPath, "*.gwsave", SearchOption.TopDirectoryOnly);
                if (files.Length > 0)
                    return files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            catch { /* ignore */ }

            // 5) Ничего не нашли — возвращаем стандартный путь (игра создаст файл сама).
            return DefaultLiveSavePath;
        }

        /// <summary>Пересчитывает живой сейв (если файл появился/переместился).</summary>
        private static void RefreshLiveSavePath(bool logIfChanged)
        {
            string resolved = ResolveLiveSavePath();
            if (string.Equals(_liveSavePath, resolved, StringComparison.OrdinalIgnoreCase))
                return;

            string old = _liveSavePath;
            _liveSavePath = resolved;

            if (logIfChanged)
                Plugin.Log.LogInfo($"[SaveSlotManager] LiveSavePath сменился: {old} -> {_liveSavePath}");
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
            try
            {
                var ser = new DataContractJsonSerializer(typeof(SlotMeta), _dcjSettings);
                using var ms = new MemoryStream();
                ser.WriteObject(ms, meta);
                File.WriteAllBytes(SlotMetaFile(slot), ms.ToArray());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] Ошибка записи мета слота {slot}: {ex.Message}");
            }
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
                RefreshLiveSavePath(logIfChanged: true);
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
            RefreshLiveSavePath(logIfChanged: true);
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
                {
                    try { File.Delete(LiveSavePath); }
                    catch (Exception ex)
                    { Plugin.Log.LogWarning($"[SaveSlotManager] Не удалось удалить живой сейв: {ex.Message}"); }
                }
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
        /// Работает даже если Harmony-патч SaveManager.SaveToFile не вызывается.
        /// </summary>
        public static void PollLiveSaveChanges()
        {
            RefreshLiveSavePath(logIfChanged: false);
            if (!File.Exists(LiveSavePath))
            {
                _missingLivePollTicks++;
                if (_missingLivePollTicks % 40 == 0)
                    Plugin.Log.LogInfo($"[SaveSlotManager] Живого сейва пока нет ({LiveSavePath}).");
                return;
            }

            _missingLivePollTicks = 0;

            DateTime wt;
            try { wt = File.GetLastWriteTimeUtc(LiveSavePath); }
            catch { return; }

            if (wt <= _lastCopiedLiveWriteUtc) return;

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
            if (slot < 0 || slot >= MaxSlots)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] DeleteSlot: недопустимый слот {slot}.");
                return;
            }

            bool wasActive = (slot == ActiveSlot);

            if (File.Exists(SlotSaveFile(slot))) File.Delete(SlotSaveFile(slot));
            if (File.Exists(SlotMetaFile(slot)))  File.Delete(SlotMetaFile(slot));

            if (wasActive)
            {
                // Удаляем живое сохранение, чтобы игра не загрузила удалённый слот
                if (File.Exists(LiveSavePath))
                {
                    try { File.Delete(LiveSavePath); }
                    catch (Exception ex)
                    { Plugin.Log.LogWarning($"[SaveSlotManager] Не удалось удалить живой сейв: {ex.Message}"); }
                }
                _lastCopiedLiveWriteUtc = DateTime.MinValue;
                ActiveSlot = 0;
                File.WriteAllText(ActiveSlotFile, "0");
            }

            Plugin.Log.LogInfo($"[SaveSlotManager] Слот {slot + 1} удалён{(wasActive ? " (был активным — сброс на Слот 1)." : ".")}");
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

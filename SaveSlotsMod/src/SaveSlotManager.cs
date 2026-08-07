using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO.Compression;
using System.Text.RegularExpressions;
using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        public const int    MaxSlots     = 10;
        public const string SaveFileName = "SaveFile.gwsave";

        // StoryEvent.TutorialRun3Completed = 38 (DiskCardGame.StoryEvent).
        // Третий обучающий забег завершён — Леший говорит, что больше не будет
        // тренировать игрока. Это граница между Актом 0 (обучение) и Актом 1.
        private const int StoryEvent_TutorialRun3Completed = 38;

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

        // ── Трекинг времени игры ────────────────────────────────────────────────
        // Накапливаем секунды, проведённые в игровом сценарии (не в меню).
        // При сохранении мета — сбрасываем в 0, добавив к общему времени слота.
        private static float _playTimeThisSession = 0f;

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
                    string text = ReadSaveFileText(mainPath);
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
                        ModGuids  = GetCurrentModGuids(),
                        Act       = ParseSaveAct(SlotSaveFile(0))
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

            RefreshStoredActLabels();

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

        // ── Трекинг времени игры ──────────────────────────────────────────────────
        /// <summary>
        /// Накапливает время, проведённое в игровом сценарии (не в меню).
        /// Вызывается из Plugin.Update().
        /// </summary>
        public static void AccumulatePlayTime(float deltaSeconds)
        {
            if (SaveSlotUIBehaviour.IsShowing) return;
            try
            {
                string scene = SceneManager.GetActiveScene().name;
                if (!string.IsNullOrEmpty(scene) && scene.StartsWith("Part"))
                    _playTimeThisSession += deltaSeconds;
            }
            catch { /* ignore */ }
        }

        /// <summary>Сбрасывает накопленное время сессии (при переключении слота).</summary>
        public static void ResetPlayTimeSession() => _playTimeThisSession = 0f;

        // ── Чтение прогресса из файла сейва ───────────────────────────────────────
        /// <summary>
        /// Читает .gwsave (JSON) и определяет текущий акт.
        /// Возвращает: 0=обучение, 1=Акт 1, 2=Акт 2, 3=Акт 3, 4=Акт ? (финал), -1=неизвестно.
        ///
        /// StoryState enum (DiskCardGame):
        ///   Part1       = 0   — Акт 0 (обучение) или Акт 1 (полноценный первый акт)
        ///   Part1_Boss  = 1   — Босс Акта 1
        ///   Part2       = 2   — Акт 2 (2D мир / GBC)
        ///   Part2_Boss  = 3   — Босс Акта 2
        ///   Part3       = 4   — Акт 3 (фабрика P03)
        ///   Part3_Boss  = 5   — Босс Акта 3
        ///   Finale      = 6   — Акт ? (мир удаляется)
        ///   Ascension   = 7   — Кейси-мод (не основной сюжет)
        /// </summary>
        // ── Чтение .gwsave с распаковкой GZip ─────────────────────────────────────
        /// <summary>
        /// Читает файл сохранения Inscryption (.gwsave) как текст.
        /// Файлы .gwsave — это GZip-сжатый JSON, поэтому простое ReadAllText
        /// выдаёт бинарный мусор. Здесь мы определяем GZip-заголовок (0x1F 0x8B)
        /// и распаковываем; если файл оказался обычным текстом — возвращаем как есть.
        /// </summary>
        private static string ReadSaveFileText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var ms = new MemoryStream(bytes);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public static int ParseSaveAct(string saveFilePath)
        {
            try
            {
                if (!File.Exists(saveFilePath)) return -1;
                string json = ReadSaveFileText(saveFilePath);

                // 1) Пытаемся найти storyState — целочисленное поле состояния сюжета.
                //    Inscryption (JsonUtility) сериализует enum StoryState как int.
                //    Имя поля в SaveData — "storyState". Также проверяем "story"
                //    на случай старых/модифицированных сборок.
                var storyMatch = Regex.Match(json, @"""storyState""\s*:\s*(\d+)");
                if (!storyMatch.Success)
                    storyMatch = Regex.Match(json, @"""story""\s*:\s*(\d+)");
                if (storyMatch.Success && int.TryParse(storyMatch.Groups[1].Value, out int story))
                {
                    Plugin.Log.LogInfo($"[SaveSlotManager] ParseSaveAct: storyState={story}");
                    if (story == 0) return DetectPart1Progression(json);  // Part1 — обучение или полный акт
                    if (story == 1) return 1;                              // Part1_Boss
                    if (story <= 3) return 2;                              // Part2 / Part2_Boss
                    if (story <= 5) return 3;                              // Part3 / Part3_Boss
                    if (story == 6) return 4;                              // Finale — Акт ? (мир удаляется)
                    if (story >= 7) return -1;                             // Ascension — не основной сюжет
                }

                // 2) Запасной вариант: ищем currentScene — имя сцены содержит Part1/Part2/Part3
                var sceneMatch = Regex.Match(json, @"""currentScene""\s*:\s*""([^""]+)""");
                if (sceneMatch.Success)
                {
                    string scene = sceneMatch.Groups[1].Value;
                    Plugin.Log.LogInfo($"[SaveSlotManager] ParseSaveAct: currentScene='{scene}'");
                    if (scene.Contains("Part3")) return 3;
                    if (scene.Contains("Part2") || scene.Contains("GBC")) return 2;
                    if (scene.Contains("Part1")) return DetectPart1Progression(json);
                    if (scene.Contains("Finale") || scene.Contains("End")) return 4;
                }

                // 3) Запасной вариант по секциям данных:
                //    part3Data с currency > 0 → Акт 3, gbcData с currency > 0 → Акт 2
                if (Regex.IsMatch(json, @"""part3Data""\s*:\s*\{[^}]*""currency""\s*:\s*[1-9]"))
                    return 3;
                if (Regex.IsMatch(json, @"""gbcData""\s*:\s*\{[^}]*""currency""\s*:\s*[1-9]"))
                    return 2;

                // 4) Если ничего не помогло — логируем первые 500 символов для диагностики
                Plugin.Log.LogWarning($"[SaveSlotManager] ParseSaveAct: не удалось определить акт. JSON (первые 500): {json.Substring(0, Math.Min(500, json.Length))}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] ParseSaveAct error: {ex.Message}");
            }
            return -1;
        }

        // ── Определение прогрессии в Part1 (обучение vs. полный акт) ──────────────
        /// <summary>
        /// Внутри Part1 (storyState=0) отличает обучение (Акт 0) от полноценного
        /// первого акта (Акт 1).
        ///
        /// Акт 0 — обучение: Леший тренирует игрока (обучающие забеги 1–3).
        /// Акт 1 — полноценный первый акт: Леший сказал, что больше не будет
        ///   тренировать; игрок играет полноценный забег.
        ///
        /// Граница — сюжетное событие TutorialRun3Completed (StoryEvent = 38),
        /// которое записывается в storyEvents.completedEvents сейва.
        /// </summary>
        private static int DetectPart1Progression(string json)
        {
            // completedEvents — подтверждённый игровой маркер того, что
            // обучающие забеги завершены и Леший больше не обучает игрока.
            var events = ParseCompletedEvents(json);
            if (events != null)
            {
                Plugin.Log.LogInfo($"[SaveSlotManager] DetectPart1Progression: completedEvents found, count={events.Count}, values=[{string.Join(", ", events)}]");
                if (events.Contains(StoryEvent_TutorialRun3Completed))
                {
                    Plugin.Log.LogInfo("[SaveSlotManager] DetectPart1Progression: event 38 found → Акт 1");
                    return 1;
                }
                Plugin.Log.LogInfo("[SaveSlotManager] DetectPart1Progression: событие завершения обучения не найдено.");
            }
            else
            {
                Plugin.Log.LogInfo("[SaveSlotManager] DetectPart1Progression: completedEvents не найдено.");
            }

            // Без подтверждённого события сохранение остаётся Актом 0.
            // Время, валюту и номер цикла нельзя использовать как замену
            // сюжетному событию: они не означают, что реплика Лешего уже была.
            Plugin.Log.LogInfo("[SaveSlotManager] DetectPart1Progression: событие завершения обучения не найдено → Акт 0");
            return 0;
        }

        /// <summary>
        /// Извлекает массив completedEvents (List&lt;int&gt;) из JSON сейва.
        /// Возвращает null, если поле не найдено — тогда вызывающий код использует
        /// запасные эвристики.
        /// </summary>
        private static List<int>? ParseCompletedEvents(string json)
        {
            try
            {
                var match = Regex.Match(
                    json,
                    @"""completedEvents""\s*:\s*\{.*?""\$rcontent""\s*:\s*\[([^\]]*)\]",
                    RegexOptions.Singleline);
                if (!match.Success)
                    match = Regex.Match(json, @"""completedEvents""\s*:\s*\[([^\]]*)\]");
                if (!match.Success) return null;

                var result = new List<int>();
                foreach (var part in match.Groups[1].Value.Split(','))
                {
                    if (int.TryParse(part.Trim(), out int val))
                        result.Add(val);
                }
                return result;
            }
            catch { return null; }
        }

        /// <summary>
        /// Пересчитывает метку Акта у уже существующих слотов после обновления
        /// логики определения прогресса. Остальные метаданные не изменяются.
        /// </summary>
        private static void RefreshStoredActLabels()
        {
            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (!SlotHasSave(slot)) continue;

                var existing = LoadSlotMeta(slot);
                if (existing == null) continue;

                int detectedAct = ParseSaveAct(SlotSaveFile(slot));
                if (detectedAct < 0 || detectedAct == existing.Act) continue;

                existing.Act = detectedAct;
                SaveSlotMeta(slot, existing);
                Plugin.Log.LogInfo(
                    $"[SaveSlotManager] Обновлена метка Акта для Слота {slot + 1}: {GetActLabel(detectedAct)}.");
            }
        }

        /// <summary>
        /// Возвращает текстовую метку акта для отображения в меню.
        /// </summary>
        public static string GetActLabel(int act)
        {
            switch (act)
            {
                case 0: return "Акт 0";
                case 1: return "Акт 1";
                case 2: return "Акт 2";
                case 3: return "Акт 3";
                case 4: return "Акт ?";
                default: return "Акт ?";
            }
        }

        // ── Чтение времени игры из файла сейва ─────────────────────────────────────
        /// <summary>
        /// Читает поле playTime (float) из .gwsave.
        /// Возвращает -1 если не удалось прочитать.
        /// </summary>
        public static float ParseSavePlayTime(string saveFilePath)
        {
            try
            {
                if (!File.Exists(saveFilePath)) return -1f;
                string json = ReadSaveFileText(saveFilePath);

                // playTime хранится как число с плавающей точкой на верхнем уровне JSON
                var match = Regex.Match(json, @"""playTime""\s*:\s*([0-9]+(?:\.[0-9]+)?)");
                if (match.Success && float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pt))
                    return pt;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] ParseSavePlayTime error: {ex.Message}");
            }
            return -1f;
        }

        // ── Форматирование времени ───────────────────────────────────────────────
        /// <summary>Форматирует секунды в строку «Hч MMм» или «MM:SS».</summary>
        public static string FormatPlayTime(float totalSeconds)
        {
            if (totalSeconds <= 0f) return "—";
            var ts = TimeSpan.FromSeconds(totalSeconds);
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}ч {ts.Minutes:D2}м";
            return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        // ── Построение и сохранение мета с прогрессом и временем ───────────────────
        /// <summary>
        /// Создаёт SlotMeta для слота: читает время игры из файла сейва,
        /// парсит акт, сохраняет моды.
        /// </summary>
        private static void FlushMeta(int slot)
        {
            var existing = LoadSlotMeta(slot);
            // Читаем playTime прямо из файла сейва — игра сама ведёт учёт
            float filePlayTime = ParseSavePlayTime(SlotSaveFile(slot));
            float totalPlay = filePlayTime >= 0f ? filePlayTime : (existing?.PlayTime ?? 0f) + _playTimeThisSession;
            int act = ParseSaveAct(SlotSaveFile(slot));
            SaveSlotMeta(slot, new SlotMeta
            {
                LastSaved = DateTime.UtcNow,
                ModGuids  = existing?.ModGuids ?? GetCurrentModGuids(),
                Act       = act,
                PlayTime  = totalPlay
            });
            _playTimeThisSession = 0f;
        }

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
                FlushMeta(ActiveSlot);
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
                FlushMeta(ActiveSlot);
                Plugin.Log.LogInfo($"[SaveSlotManager] Живое сохранение → Слот {ActiveSlot + 1}.");
            }
            ResetPlayTimeSession();

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

                FlushMeta(ActiveSlot);

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
            var currentSet = new HashSet<string>(current, StringComparer.Ordinal);
            var savedSet = new HashSet<string>(saved, StringComparer.Ordinal);

            var added = currentSet.Except(savedSet).OrderBy(g => g).ToList();
            var removed = savedSet.Except(currentSet).OrderBy(g => g).ToList();
            bool same = added.Count == 0 && removed.Count == 0;

            Plugin.Log.LogInfo(
                $"[SaveSlotManager] ComputeDiff: Слот {slot + 1}, сохранено модов={savedSet.Count}, сейчас={currentSet.Count}, совпадают={same}.");

            return new ModDiff
            {
                Added   = added,
                Removed = removed,
                Same    = same
            };
        }

        /// <summary>
        /// Запоминает набор модов, с которым пользователь подтвердил вход
        /// в существующий слот. До подтверждения старый набор не меняется,
        /// чтобы проверка при следующем выборе сравнивала именно последний
        /// подтверждённый вход.
        /// </summary>
        public static void RememberCurrentModsForSlot(int slot)
        {
            if (slot < 0 || slot >= MaxSlots || !SlotHasSave(slot))
                return;

            var meta = LoadSlotMeta(slot);
            if (meta == null)
                return;

            var current = GetCurrentModGuids();
            var saved = meta.ModGuids ?? new List<string>();
            if (saved.SequenceEqual(current))
                return;

            meta.ModGuids = current;
            SaveSlotMeta(slot, meta);
            Plugin.Log.LogInfo(
                $"[SaveSlotManager] Слот {slot + 1}: сохранён новый набор модов ({current.Count}) после подтверждённого входа.");
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

        // ── Импорт сейва из файла ──────────────────────────────────────────────────
        /// <summary>
        /// Копирует выбранный файл сохранения в указанный слот.
        /// Если слот активный — также обновляет живой сейв.
        /// </summary>
        public static bool ImportSaveToSlot(int slot, string sourceFilePath)
        {
            if (slot < 0 || slot >= MaxSlots)
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] ImportSaveToSlot: недопустимый слот {slot}.");
                return false;
            }
            if (!File.Exists(sourceFilePath))
            {
                Plugin.Log.LogWarning($"[SaveSlotManager] ImportSaveToSlot: файл не найден '{sourceFilePath}'.");
                return false;
            }

            try
            {
                File.Copy(sourceFilePath, SlotSaveFile(slot), overwrite: true);

                // При импорте в активный слот — обновляем и живой сейв
                if (slot == ActiveSlot)
                {
                    File.Copy(sourceFilePath, LiveSavePath, overwrite: true);
                    _lastCopiedLiveWriteUtc = File.GetLastWriteTimeUtc(LiveSavePath);
                }

                // При импорте читаем playTime и акт из нового файла
                _playTimeThisSession = 0f;
                var existing = LoadSlotMeta(slot);
                int act = ParseSaveAct(SlotSaveFile(slot));
                float filePlayTime = ParseSavePlayTime(SlotSaveFile(slot));
                SaveSlotMeta(slot, new SlotMeta
                {
                    LastSaved = DateTime.UtcNow,
                    ModGuids  = GetCurrentModGuids(),
                    Act       = act,
                    PlayTime  = filePlayTime >= 0f ? filePlayTime : (existing?.PlayTime ?? 0f)
                });

                Plugin.Log.LogInfo($"[SaveSlotManager] Импортирован сейв из '{sourceFilePath}' → Слот {slot + 1}.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[SaveSlotManager] ImportSaveToSlot failed: {ex.Message}");
                return false;
            }
        }
    }

    [DataContract]
    public class SlotMeta
    {
        [DataMember] public DateTime     LastSaved { get; set; }
        [DataMember] public List<string> ModGuids  { get; set; } = new List<string>();
        [DataMember] public int          Act       { get; set; } = -1;
        [DataMember] public float        PlayTime  { get; set; } = 0f;
    }

    public class ModDiff
    {
        public List<string> Added   { get; set; } = new List<string>();
        public List<string> Removed { get; set; } = new List<string>();
        public bool         Same    { get; set; }
    }
}

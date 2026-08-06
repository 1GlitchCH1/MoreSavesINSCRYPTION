using System;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using DiskCardGame;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SaveSlotsMod
{
    // ═════════════════════════════════════════════════════════════════════════════
    // ПАТЧИ СОХРАНЕНИЯ
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Перехват SaveManager.SaveToFile.
    /// Каждый раз когда игра сохраняет на диск — копируем файл в активный слот.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), "SaveToFile")]
    internal static class SaveManager_SaveToFile_Patch
    {
        private static void Postfix() => SaveSlotManager.OnGameSaved();
    }

    /// <summary>
    /// Перехват MenuController.Start.
    /// Срабатывает когда сцена главного меню загружается — до анимации карточки.
    /// Это самый ранний момент после возврата из игры: живое сохранение ещё
    /// гарантированно существует на диске.
    /// </summary>
    [HarmonyPatch(typeof(MenuController), "Start")]
    internal static class MenuController_Start_Patch
    {
        private static void Prefix()
        {
            // Если пикер уже открыт — ничего не делаем
            if (SaveSlotUIBehaviour.IsShowing) return;
            SaveSlotManager.BackupLiveSave();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // ПЕРЕХВАТ «ПРОДОЛЖИТЬ» / «НОВАЯ ИГРА» В ГЛАВНОМ МЕНЮ
    //
    // ИСПРАВЛЕНИЯ:
    //
    //  Bug 1 — Двойной запуск (оригинальный код):
    //    Inscryption вызывает OnStartGameCardReachedSlot для нескольких позиций
    //    карточной анимации. Второй вызов попадал в патч уже после того, как
    //    PassingThrough = false, и запускал НОВУЮ игру в слоте 0 — перетирая
    //    активный слот. Исправление: cooldown 3 сек + проверка IsShowing.
    //
    //  Bug 2 — Потеря сейва при возврате в меню:
    //    Живое сохранение не попадало в файл слота, потому что BackupLiveSave
    //    вызывался уже после того, как игра удаляла/перезаписывала SaveFile.gwsave.
    //    Исправление: патч MenuController.Start копирует файл раньше.
    // ═════════════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(MenuController), "OnStartGameCardReachedSlot")]
    internal static class MenuController_OnStartGameCardReachedSlot_Patch
    {
        // Время последнего переключения слота.
        // -100f — начальное значение, гарантирует cooldown истёк.
        private static float _lastSwitchTime = -100f;

        internal static void NotifySwitchCompleted()
        {
            _lastSwitchTime = Time.realtimeSinceStartup;
        }

        private static bool Prefix(MenuController __instance)
        {
            // 1. Мы сами вызвали метод через рефлексию — пропускаем.
            if (MenuPatches.PassingThrough) return true;

            // 2. Cooldown 3 сек после переключения — блокируем паразитные вызовы анимации.
            if (Time.realtimeSinceStartup - _lastSwitchTime < 3f)
            {
                Plugin.Log.LogInfo("[Patch] OnStartGameCardReachedSlot заблокирован (cooldown).");
                return false;
            }

            // 3. Пикер уже открыт — игнорируем.
            if (SaveSlotUIBehaviour.IsShowing)
            {
                Plugin.Log.LogInfo("[Patch] OnStartGameCardReachedSlot заблокирован (пикер открыт).");
                return false;
            }

            // 4. Штатный перехват.
            // MenuController.Start уже сделал BackupLiveSave, но продублируем на всякий случай.
            SaveSlotManager.BackupLiveSave();

            bool isNewGame = !File.Exists(SaveSlotManager.LiveSavePath);
            MenuPatches.Intercept(__instance, isNewGame: isNewGame);
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    internal static class MenuPatches
    {
        public static bool PassingThrough { get; private set; }

        private static MenuController? _menu;

        public static void Intercept(MenuController menu, bool isNewGame)
        {
            _menu = menu;
            SaveSlotUIBehaviour.Show();
        }

        // ── Вызывается когда пользователь выбрал СУЩЕСТВУЮЩИЙ слот ───────────────
        public static void ProceedWithLoad()
        {
            if (_menu == null) return;
            PassingThrough = true;
            try
            {
                SaveManager.LoadFromFile();
                AccessTools.Method(typeof(MenuController), "OnStartGameCardReachedSlot")
                           ?.Invoke(_menu, null);
            }
            finally { PassingThrough = false; }
        }

        // ── Вызывается когда пользователь выбрал ПУСТОЙ слот (новая игра) ─────────
        public static void ProceedWithNewGame()
        {
            if (_menu == null) return;
            PassingThrough = true;
            try
            {
                // Ищем метод создания нового сейва — может быть статическим
                // или экземплярным (SaveManager — обычный класс, не MonoBehaviour).
                var createMethod = AccessTools.Method(typeof(SaveManager), "CreateNewSaveFile");
                if (createMethod != null)
                {
                    Plugin.Log.LogInfo("[MenuPatches] Новая игра через SaveManager.CreateNewSaveFile()");
                    object? target = null;
                    if (!createMethod.IsStatic)
                        target = FindSaveManagerInstance();
                    createMethod.Invoke(target, null);
                    // Сразу после создания файла — копируем в слот.
                    SaveSlotManager.OnGameSaved();
                }
                else
                {
                    Plugin.Log.LogWarning("[MenuPatches] CreateNewSaveFile не найден! Методы SaveManager:");
                    foreach (var m in typeof(SaveManager).GetMethods(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.Instance))
                        Plugin.Log.LogInfo($"  SaveManager::{m.Name}");
                }

                SaveManager.LoadFromFile();
                AccessTools.Method(typeof(MenuController), "OnStartGameCardReachedSlot")
                           ?.Invoke(_menu, null);
            }
            finally { PassingThrough = false; }
        }

        /// <summary>
        /// Ищет экземпляр SaveManager через рефлексию статических полей/свойств
        /// (SaveManager — обычный класс, не MonoBehaviour, поэтому FindObjectOfType не подходит).
        /// </summary>
        private static object? FindSaveManagerInstance()
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // Поля
            foreach (var f in typeof(SaveManager).GetFields(flags))
            {
                if (typeof(SaveManager).IsAssignableFrom(f.FieldType))
                {
                    var val = f.GetValue(null);
                    if (val != null)
                    {
                        Plugin.Log.LogInfo($"[MenuPatches] SaveManager instance found via field {f.Name}");
                        return val;
                    }
                }
            }

            // Свойства
            foreach (var p in typeof(SaveManager).GetProperties(flags))
            {
                if (typeof(SaveManager).IsAssignableFrom(p.PropertyType) && p.GetMethod != null)
                {
                    try
                    {
                        var val = p.GetValue(null, null);
                        if (val != null)
                        {
                            Plugin.Log.LogInfo($"[MenuPatches] SaveManager instance found via property {p.Name}");
                            return val;
                        }
                    }
                    catch { /* ignore getter errors */ }
                }
            }

            Plugin.Log.LogWarning("[MenuPatches] Не удалось найти экземпляр SaveManager.");
            return null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // IMGUI ПИКЕР СЛОТОВ
    // ═════════════════════════════════════════════════════════════════════════════
    public class SaveSlotUIBehaviour : MonoBehaviour
    {
        private static SaveSlotUIBehaviour? _instance;

        /// <summary>True пока пикер открыт (объект ещё не уничтожен).</summary>
        public static bool IsShowing => _instance != null;

        private bool   _showWarning;
        private int    _pendingSlot    = -1;
        private bool   _pendingIsEmpty;
        private string _warningText    = "";

        // ── Диалог удаления ──────────────────────────────────────────────────────
        private bool   _showDeleteConfirm;
        private int    _deleteSlot      = -1;
        private string _deleteSlotLabel = "";

        private Texture2D? _txDark, _txRow, _txRowMain, _txBlue, _txGold, _txRed, _txGray, _txOverlay;

        // ── Курсор ───────────────────────────────────────────────────────────────
        private Texture2D? _cursorTex;
        private bool       _cursorHidden;
        private const float CURSOR_W = 28f;
        private const float CURSOR_H = 28f;

        private GUIStyle? _stTitle, _stSlotName, _stSlotNameMain, _stSlotInfo;
        private GUIStyle? _stBtnLoad, _stBtnLoadGold, _stBtnDel, _stBtnGray, _stBtnImport;
        private GUIStyle? _stBodyText, _stWarnTitle, _stHint, _stStatus;
        private bool      _stylesReady;

        // ── Статус импорта ────────────────────────────────────────────────────────
        private string _importStatus   = "";
        private float  _importStatusEnd = 0f;

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        public static void Show()
        {
            if (_instance != null) return;
            var go = new GameObject("SaveSlotUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SaveSlotUIBehaviour>();
        }

        private void Awake()     => CreateTextures();
        private void OnEnable()  { HideHardwareCursor(); }
        private void OnDisable() { RestoreHardwareCursor(); }
        private void OnDestroy() { _instance = null; RestoreHardwareCursor(); DestroyTextures(); }

        private void OnGUI()
        {
            int previousDepth = GUI.depth;
            GUI.depth = -100000;
            try
            {
                EnsureStyles();
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _txOverlay!);
                if (_showDeleteConfirm)   DrawDeleteConfirm();
                else if (_showWarning)    DrawWarning();
                else                     DrawPicker();

                DrawCustomCursor();
            }
            finally
            {
                GUI.depth = previousDepth;
            }
        }

        // ── Пикер слотов ──────────────────────────────────────────────────────────
        private void DrawPicker()
        {
            const float PW    = 580f;
            const float ROW_H = 78f, ROW_GAP = 8f;
            float rowsTotal = SaveSlotManager.MaxSlots * (ROW_H + ROW_GAP) - ROW_GAP;
            float ph = 16 + 34 + 12 + rowsTotal + 14 + 44 + 14;
            float px = (Screen.width  - PW) / 2f;
            float py = (Screen.height - ph) / 2f;

            GUI.DrawTexture(new Rect(px, py, PW, ph), _txDark!);
            GUI.Label(new Rect(px, py + 14, PW, 34), "— Выбери файл сохранения —", _stTitle!);

            float ry = py + 14 + 34 + 12;
            for (int i = 0; i < SaveSlotManager.MaxSlots; i++)
            {
                DrawSlotRow(i, px + 12, ry, PW - 24, ROW_H);
                ry += ROW_H + ROW_GAP;
            }

            float cancelW = 200f, cancelH = 40f;
            float cancelX = px + (PW - cancelW) / 2f;
            float cancelY = ry + 14;

            if (GUI.Button(new Rect(cancelX, cancelY, cancelW, cancelH), "← Назад в меню", _stBtnGray!))
                OnCancel();

            // ── Статус импорта ───────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(_importStatus) && Time.realtimeSinceStartup < _importStatusEnd)
            {
                GUI.Label(new Rect(px, py + ph + 6, PW, 24), _importStatus, _stStatus!);
            }
        }

        // ── Строка слота ──────────────────────────────────────────────────────────
        private void DrawSlotRow(int slot, float rx, float ry, float rw, float rh)
        {
            bool isMainSave = SaveSlotManager.IsMainSaveSlot(slot);
            bool hasSave    = SaveSlotManager.SlotHasSave(slot);
            var  meta       = hasSave ? SaveSlotManager.LoadSlotMeta(slot) : null;

            GUI.DrawTexture(new Rect(rx, ry, rw, rh), isMainSave ? _txRowMain! : _txRow!);

            float textX = rx + 14;

            string slotLabel = isMainSave ? "Слот 1   ★  Основное сохранение" : $"Слот {slot + 1}";
            GUI.Label(new Rect(textX, ry + 8, 320, 24), slotLabel,
                      isMainSave ? _stSlotNameMain! : _stSlotName!);

            string info;
            if (hasSave)
            {
                string dateStr = meta != null
                    ? meta.LastSaved.ToLocalTime().ToString("dd.MM.yyyy  HH:mm")
                    : "—";
                string modStr = meta != null ? $"{meta.ModGuids.Count} мод(ов)" : "—";
                info = $"{dateStr}   •   {modStr}";
            }
            else
                info = isMainSave ? "Основного сохранения нет" : "Пустой слот — Новая игра";
            GUI.Label(new Rect(textX, ry + 34, 320, 22), info, _stSlotInfo!);

            // ── Доп. информация: акт и время ────────────────────────────────────
            if (hasSave && meta != null)
            {
                string actLabel = meta.Act > 0 ? $"Акт {meta.Act}" : "Акт ?";
                string timeLabel = SaveSlotManager.FormatPlayTime(meta.PlayTime);
                string progress = $"{actLabel}   •   Время: {timeLabel}";
                GUI.Label(new Rect(textX, ry + 54, 320, 20), progress, _stSlotInfo!);
            }

            float btnRight = rx + rw - 10;
            int   cap      = slot;

            // ── Кнопка импорта — крайняя справа ───────────────────────────────────
            float iW = 44f, iH = 36f;
            float iX = btnRight - iW, iY = ry + (rh - iH) / 2f;
            if (GUI.Button(new Rect(iX, iY, iW, iH), "↑", _stBtnImport!))
                OnImportSave(cap);
            btnRight -= iW + 6;

            if (hasSave)
            {
                float dW = 32f, dH = 36f;
                float dX = btnRight - dW, dY = ry + (rh - dH) / 2f;
                if (GUI.Button(new Rect(dX, dY, dW, dH), "✕", _stBtnDel!))
                    OnDeleteSlot(cap);
                btnRight -= dW + 6;

                float lW = 106f, lH = 36f;
                float lX = btnRight - lW, lY = ry + (rh - lH) / 2f;
                if (GUI.Button(new Rect(lX, lY, lW, lH), "Играть",
                               isMainSave ? _stBtnLoadGold! : _stBtnLoad!))
                    OnSlotChosen(cap, isEmpty: false);
            }
            else
            {
                float lW = 126f, lH = 36f;
                float lX = btnRight - lW, lY = ry + (rh - lH) / 2f;
                if (GUI.Button(new Rect(lX, lY, lW, lH),
                               isMainSave ? "Начать игру" : "Новая игра",
                               isMainSave ? _stBtnLoadGold! : _stBtnLoad!))
                    OnSlotChosen(cap, isEmpty: true);
            }
        }

        // ── Диалог предупреждения о модах ─────────────────────────────────────────
        private void DrawWarning()
        {
            const float WW = 570f, WH = 370f;
            float wx = (Screen.width  - WW) / 2f;
            float wy = (Screen.height - WH) / 2f;

            GUI.DrawTexture(new Rect(wx, wy, WW, WH), _txDark!);
            GUI.Label(new Rect(wx, wy + 14, WW, 32), "⚠  Несоответствие модов", _stWarnTitle!);

            Rect box = new Rect(wx + 14, wy + 58, WW - 28, 210);
            GUI.DrawTexture(box, _txRow!);
            GUI.Label(new Rect(box.x + 8, box.y + 8, box.width - 16, box.height - 16),
                      _warningText, _stBodyText!);

            GUI.Label(new Rect(wx, wy + 280, WW, 30),
                      "Продолжение может вызвать ошибки. Убедитесь, что у вас нужные моды.", _stHint!);

            float btnY    = wy + WH - 54;
            float btnW    = 160f, btnH = 38f;
            float spacing = 16f;
            float totalW  = btnW * 2 + spacing;
            float startX  = wx + (WW - totalW) / 2f;

            if (GUI.Button(new Rect(startX, btnY, btnW, btnH), "Всё равно войти", _stBtnLoad!))
                DoSwitch(_pendingSlot, _pendingIsEmpty);

            if (GUI.Button(new Rect(startX + btnW + spacing, btnY, btnW, btnH), "Отмена", _stBtnGray!))
            {
                _showWarning    = false;
                _pendingSlot    = -1;
                _pendingIsEmpty = false;
            }
        }

        // ── Обработка выбора слота ────────────────────────────────────────────────
        private void OnSlotChosen(int slot, bool isEmpty)
        {
            if (slot == SaveSlotManager.ActiveSlot)
            {
                DoSwitch(slot, isEmpty);
                return;
            }

            if (!isEmpty)
            {
                var diff = SaveSlotManager.ComputeDiff(slot);
                if (!diff.Same)
                {
                    _warningText    = BuildDiffText(diff);
                    _pendingSlot    = slot;
                    _pendingIsEmpty = isEmpty;
                    _showWarning    = true;
                    return;
                }
            }

            DoSwitch(slot, isEmpty);
        }

        private void DoSwitch(int slot, bool isEmpty)
        {
            try { SaveSlotManager.SwitchToSlot(slot); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[SlotUI] SwitchToSlot({slot}) failed: {ex}");
                return;
            }

            // Запускаем cooldown ПЕРЕД уничтожением пикера — блокируем паразитные
            // вызовы OnStartGameCardReachedSlot от анимации меню.
            MenuController_OnStartGameCardReachedSlot_Patch.NotifySwitchCompleted();

            Destroy(gameObject);

            if (isEmpty)
                MenuPatches.ProceedWithNewGame();
            else
                MenuPatches.ProceedWithLoad();
        }

        // ── Импорт сейва из файла ────────────────────────────────────────────────────
        private void OnImportSave(int slot)
        {
            string? file = Win32OpenFileDialog.Show(
                title:  $"Выберите файл сохранения для Слота {slot + 1}",
                filter: "SaveFile.gwsave (*.gwsave)\0*.gwsave\0Все файлы (*.*)\0*.*\0");

            if (file == null)
            {
                ShowImportStatus("Импорт отменён.");
                return;
            }

            bool ok = SaveSlotManager.ImportSaveToSlot(slot, file);
            ShowImportStatus(ok
                ? $"Сейв загружен в Слот {slot + 1}."
                : $"Не удалось загрузить сейв в Слот {slot + 1}.");
        }

        private void ShowImportStatus(string msg)
        {
            _importStatus   = msg;
            _importStatusEnd = Time.realtimeSinceStartup + 4f;
            Plugin.Log.LogInfo($"[SlotUI] {msg}");
        }

        private void OnCancel()
        {
            Destroy(gameObject);
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        private void OnDeleteSlot(int slot)
        {
            _deleteSlot      = slot;
            _deleteSlotLabel = SaveSlotManager.IsMainSaveSlot(slot)
                ? "Слот 1 (Основное сохранение)"
                : $"Слот {slot + 1}";
            _showDeleteConfirm = true;
        }

        // ── Диалог подтверждения удаления ─────────────────────────────────────────
        private void DrawDeleteConfirm()
        {
            const float WW = 460f, WH = 220f;
            float wx = (Screen.width  - WW) / 2f;
            float wy = (Screen.height - WH) / 2f;

            GUI.DrawTexture(new Rect(wx, wy, WW, WH), _txDark!);
            GUI.Label(new Rect(wx, wy + 18, WW, 32), "⚠  Удалить сохранение?", _stWarnTitle!);

            string msg = $"Вы собираетесь удалить «{_deleteSlotLabel}».\nЭто действие нельзя отменить.\nВсе данные слота будут стёрты.";
            GUI.Label(new Rect(wx + 20, wy + 64, WW - 40, 70), msg, _stBodyText!);

            float btnY    = wy + WH - 54;
            float btnW    = 150f, btnH = 38f;
            float spacing = 16f;
            float totalW  = btnW * 2 + spacing;
            float startX  = wx + (WW - totalW) / 2f;

            if (GUI.Button(new Rect(startX, btnY, btnW, btnH), "Удалить", _stBtnDel!))
            {
                SaveSlotManager.DeleteSlot(_deleteSlot);
                _showDeleteConfirm = false;
                _deleteSlot        = -1;
            }

            if (GUI.Button(new Rect(startX + btnW + spacing, btnY, btnW, btnH), "Отмена", _stBtnGray!))
            {
                _showDeleteConfirm = false;
                _deleteSlot        = -1;
            }
        }

        private static string BuildDiffText(ModDiff diff)
        {
            var sb = new StringBuilder();
            if (diff.Added.Count > 0)
            {
                sb.AppendLine($"[+] Добавлены ({diff.Added.Count}):");
                foreach (var g in diff.Added) sb.AppendLine($"    + {g}");
                if (diff.Removed.Count > 0)   sb.AppendLine();
            }
            if (diff.Removed.Count > 0)
            {
                sb.AppendLine($"[-] Удалены ({diff.Removed.Count}):");
                foreach (var g in diff.Removed) sb.AppendLine($"    - {g}");
            }
            if (diff.Added.Count == 0 && diff.Removed.Count == 0)
                sb.Append("Список модов совпадает.");
            return sb.ToString().TrimEnd();
        }

        // ── Текстуры ──────────────────────────────────────────────────────────────
        private void CreateTextures()
        {
            _txDark    = MakeTex(new Color(0.08f, 0.08f, 0.08f, 0.97f));
            _txRow     = MakeTex(new Color(0.15f, 0.15f, 0.15f, 1.00f));
            _txRowMain = MakeTex(new Color(0.20f, 0.16f, 0.06f, 1.00f));
            _txBlue    = MakeTex(new Color(0.15f, 0.35f, 0.70f, 1.00f));
            _txGold    = MakeTex(new Color(0.60f, 0.45f, 0.05f, 1.00f));
            _txRed     = MakeTex(new Color(0.65f, 0.12f, 0.12f, 1.00f));
            _txGray    = MakeTex(new Color(0.30f, 0.30f, 0.30f, 1.00f));
            _txOverlay = MakeTex(new Color(0.00f, 0.00f, 0.00f, 0.60f));

            LoadCursorTexture();
        }

        private void DestroyTextures()
        {
            foreach (var tx in new[] { _txDark, _txRow, _txRowMain, _txBlue, _txGold, _txRed, _txGray, _txOverlay })
                if (tx != null) Destroy(tx);
            if (_cursorTex != null) { Destroy(_cursorTex); _cursorTex = null; }
        }

        // ── Курсор ───────────────────────────────────────────────────────────────
        private void LoadCursorTexture()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream("SaveSlotsMod.cursor.png"))
                {
                    if (stream == null)
                    {
                        Plugin.Log.LogWarning("[SlotUI] Ресурс курсора не найден.");
                        return;
                    }
                    var data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                    _cursorTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    ImageConversion.LoadImage(_cursorTex, data);
                    _cursorTex.filterMode = FilterMode.Bilinear;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[SlotUI] Не удалось загрузить курсор: {ex.Message}");
            }
        }

        private void HideHardwareCursor()
        {
            if (_cursorHidden) return;
            _cursorHidden    = true;
            Cursor.visible  = false;
        }

        private void RestoreHardwareCursor()
        {
            if (!_cursorHidden) return;
            _cursorHidden   = false;
            Cursor.visible  = true;
        }

        private void DrawCustomCursor()
        {
            if (_cursorTex == null) return;
            Vector2 mp = Input.mousePosition;
            float x = mp.x - CURSOR_W * 0.3f;
            float y = Screen.height - mp.y - CURSOR_H * 0.7f;
            GUI.DrawTexture(new Rect(x, y, CURSOR_W, CURSOR_H), _cursorTex);
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // ── Стили ─────────────────────────────────────────────────────────────────
        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _stTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.90f, 0.85f, 0.70f) }
            };
            _stSlotName = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white }
            };
            _stSlotNameMain = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1.00f, 0.85f, 0.40f) }
            };
            _stSlotInfo = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = new Color(0.68f, 0.68f, 0.68f) }
            };
            _stBtnLoad     = MakeBtnStyle(_txBlue!);
            _stBtnLoadGold = MakeBtnStyle(_txGold!);
            _stBtnDel      = MakeBtnStyle(_txRed!);
            _stBtnGray     = MakeBtnStyle(_txGray!);
            _stBtnImport   = MakeBtnStyle(MakeTex(new Color(0.18f, 0.42f, 0.20f, 1.00f))!);
            _stBodyText = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = Color.white }
            };
            _stWarnTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.85f, 0.3f) }
            };
            _stHint = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10, fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.50f, 0.50f, 0.50f) }
            };
            _stStatus = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.85f, 0.95f, 0.75f) }
            };
        }

        private static GUIStyle MakeBtnStyle(Texture2D bg) =>
            new GUIStyle(GUI.skin.button)
            {
                fontSize = 13, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal  = { textColor = Color.white, background = bg },
                hover   = { textColor = Color.white, background = bg },
                active  = { textColor = Color.white, background = bg },
                focused = { textColor = Color.white, background = bg },
                border  = new RectOffset(0, 0, 0, 0),
                padding = new RectOffset(6, 6, 4, 4)
            };
    }
}

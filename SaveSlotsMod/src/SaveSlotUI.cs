using System;
using System.Text;
using HarmonyLib;
using DiskCardGame;
using UnityEngine;

namespace SaveSlotsMod
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Перехват перед запуском корутины TransitionToGame.
    // ─────────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(MenuController), "OnStartGameCardReachedSlot")]
    internal static class MenuController_OnStartGameCardReachedSlot_Patch
    {
        private static bool Prefix(MenuController __instance)
        {
            if (MenuPatches.PassingThrough) return true;

            // Делаем резервную копию живого сохранения ДО показа пикера,
            // чтобы активный слот отображал актуальные данные.
            SaveSlotManager.BackupLiveSave();

            MenuPatches.Intercept(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveManager), "SaveToFile")]
    internal static class SaveManager_SaveToFile_Patch
    {
        private static void Postfix() => SaveSlotManager.OnGameSaved();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    internal static class MenuPatches
    {
        public static bool             PassingThrough { get; private set; }
        private static MenuController? _menu;

        public static void Intercept(MenuController menu)
        {
            _menu = menu;
            SaveSlotUIBehaviour.Show();
        }

        /// <summary>Загружает выбранный слот и запускает переход в игру.</summary>
        public static void Proceed()
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
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // IMGUI пикер слотов — без Canvas, без EventSystem.
    // ─────────────────────────────────────────────────────────────────────────────
    public class SaveSlotUIBehaviour : MonoBehaviour
    {
        private static SaveSlotUIBehaviour? _instance;

        private bool     _showWarning;
        private int      _pendingSlot = -1;
        private string   _warningText  = "";

        // Текстуры для фонов / кнопок
        private Texture2D? _txDark;
        private Texture2D? _txRow;
        private Texture2D? _txRowMain; // Особый фон для Слота 1 (основного)
        private Texture2D? _txBlue;
        private Texture2D? _txGold;   // Цвет кнопки для основного сохранения
        private Texture2D? _txRed;
        private Texture2D? _txGray;
        private Texture2D? _txOverlay;

        // GUIStyles — создаются один раз
        private GUIStyle? _stTitle;
        private GUIStyle? _stSlotName;
        private GUIStyle? _stSlotNameMain; // Жирный золотой для основного слота
        private GUIStyle? _stSlotInfo;
        private GUIStyle? _stBtnLoad;
        private GUIStyle? _stBtnLoadGold; // Кнопка «Загрузить» для основного слота
        private GUIStyle? _stBtnDel;
        private GUIStyle? _stBtnGray;
        private GUIStyle? _stBodyText;
        private GUIStyle? _stWarnTitle;
        private GUIStyle? _stHint;

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        public static void Show()
        {
            if (_instance != null) return;
            var go = new GameObject("SaveSlotUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SaveSlotUIBehaviour>();
        }

        private void Awake()    => CreateTextures();
        private void OnDestroy(){ _instance = null; DestroyTextures(); }

        // ── OnGUI ─────────────────────────────────────────────────────────────────
        private void OnGUI()
        {
            EnsureStyles();

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _txOverlay!);

            if (_showWarning) DrawWarning();
            else              DrawPicker();
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
            if (GUI.Button(new Rect(cancelX, cancelY, cancelW, cancelH),
                           "← Назад в меню", _stBtnGray!))
                OnCancel();
        }

        // ── Строка слота ──────────────────────────────────────────────────────────
        private void DrawSlotRow(int slot, float rx, float ry, float rw, float rh)
        {
            bool isMainSave = SaveSlotManager.IsMainSaveSlot(slot);
            bool hasSave    = SaveSlotManager.SlotHasSave(slot);
            var  meta       = hasSave ? SaveSlotManager.LoadSlotMeta(slot) : null;

            // Фон строки: немного другой для основного сохранения
            GUI.DrawTexture(new Rect(rx, ry, rw, rh), isMainSave ? _txRowMain! : _txRow!);

            float textX = rx + 14;

            // ── Название слота ────────────────────────────────────────────────────
            string slotLabel = isMainSave
                ? "Слот 1   ★  Основное сохранение"
                : $"Слот {slot + 1}";
            GUI.Label(
                new Rect(textX, ry + 8, 320, 24),
                slotLabel,
                isMainSave ? _stSlotNameMain! : _stSlotName!);

            // ── Информация о сохранении ───────────────────────────────────────────
            string info;
            if (hasSave)
                info = meta != null
                    ? $"{meta.LastSaved.ToLocalTime():dd.MM.yyyy  HH:mm}   •   {meta.ModGuids.Count} мод(ов)"
                    : "Сохранение (нет данных о модах)";
            else
                info = isMainSave ? "Основного сохранения нет" : "Пустой слот — Новая игра";
            GUI.Label(new Rect(textX, ry + 34, 320, 22), info, _stSlotInfo!);

            // ── Кнопки ────────────────────────────────────────────────────────────
            float btnRight = rx + rw - 10;
            int   cap      = slot;

            if (hasSave)
            {
                // Кнопка удаления — НЕ показываем для основного сохранения (Слот 0)
                if (!isMainSave)
                {
                    float dW = 32f, dH = 36f;
                    float dX = btnRight - dW, dY = ry + (rh - dH) / 2f;
                    if (GUI.Button(new Rect(dX, dY, dW, dH), "✕", _stBtnDel!))
                        OnDeleteSlot(cap);
                    btnRight -= dW + 6;
                }

                float lW = 106f, lH = 36f;
                float lX = btnRight - lW, lY = ry + (rh - lH) / 2f;
                GUIStyle loadStyle = isMainSave ? _stBtnLoadGold! : _stBtnLoad!;
                if (GUI.Button(new Rect(lX, lY, lW, lH), "Загрузить", loadStyle))
                    OnSlotChosen(cap);
            }
            else
            {
                string btnText = isMainSave ? "Начать игру" : "Новая игра";
                float  lW = 126f, lH = 36f;
                float  lX = btnRight - lW, lY = ry + (rh - lH) / 2f;
                GUIStyle newStyle = isMainSave ? _stBtnLoadGold! : _stBtnLoad!;
                if (GUI.Button(new Rect(lX, lY, lW, lH), btnText, newStyle))
                    OnSlotChosen(cap);
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
            GUI.Label(
                new Rect(box.x + 10, box.y + 8, box.width - 20, box.height - 16),
                _warningText, _stBodyText!);

            GUI.Label(
                new Rect(wx, wy + WH - 84, WW, 22),
                "Вход может привести к нестабильности из-за изменений в модах.", _stHint!);

            float btnY = wy + WH - 54;
            float half = WW / 2f;
            if (GUI.Button(new Rect(wx + half - 8 - 164, btnY, 164, 42), "Войти  →", _stBtnLoad!))
            {
                _showWarning = false;
                if (_pendingSlot >= 0) DoSwitch(_pendingSlot);
            }
            if (GUI.Button(new Rect(wx + half + 8, btnY, 164, 42), "Отмена", _stBtnGray!))
            {
                _showWarning = false;
                _pendingSlot = -1;
            }
        }

        // ── Логика ───────────────────────────────────────────────────────────────
        private void OnSlotChosen(int slot)
        {
            if (!SaveSlotManager.SlotHasSave(slot)) { DoSwitch(slot); return; }
            var diff = SaveSlotManager.ComputeDiff(slot);
            if (diff.Same) { DoSwitch(slot); return; }
            _pendingSlot = slot;
            _warningText = BuildDiffText(diff);
            _showWarning = true;
        }

        private void DoSwitch(int slot)
        {
            try   { SaveSlotManager.SwitchToSlot(slot); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[SlotUI] SwitchToSlot({slot}) failed: {ex}");
                return;
            }
            Destroy(gameObject); // очищает _instance через OnDestroy
            MenuPatches.Proceed();
        }

        private void OnCancel()
        {
            Destroy(gameObject);
            MenuPatches.Proceed();
        }

        private void OnDeleteSlot(int slot)
        {
            if (SaveSlotManager.IsMainSaveSlot(slot)) return; // Защита
            SaveSlotManager.DeleteSlot(slot);
            // OnGUI перерисовывает каждый кадр — обновление произойдёт автоматически
        }

        // ── Текст разницы модов ───────────────────────────────────────────────────
        private static string BuildDiffText(ModDiff diff)
        {
            var sb = new StringBuilder();
            if (diff.Added.Count > 0)
            {
                sb.AppendLine($"[+] Добавлены ({diff.Added.Count}):");
                foreach (var g in diff.Added)   sb.AppendLine($"    + {g}");
                if (diff.Removed.Count > 0)     sb.AppendLine();
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
            _txDark    = MakeTex(new Color(0.07f, 0.07f, 0.10f, 0.97f));
            _txRow     = MakeTex(new Color(0.14f, 0.14f, 0.17f, 0.96f));
            _txRowMain = MakeTex(new Color(0.16f, 0.13f, 0.06f, 0.98f)); // тёплый тон для основного
            _txBlue    = MakeTex(new Color(0.18f, 0.52f, 0.88f, 1.00f));
            _txGold    = MakeTex(new Color(0.72f, 0.55f, 0.10f, 1.00f)); // золотой для основного
            _txRed     = MakeTex(new Color(0.68f, 0.16f, 0.16f, 1.00f));
            _txGray    = MakeTex(new Color(0.28f, 0.28f, 0.28f, 1.00f));
            _txOverlay = MakeTex(new Color(0.00f, 0.00f, 0.00f, 0.80f));
        }

        private void DestroyTextures()
        {
            Destroy(_txDark);    Destroy(_txRow);   Destroy(_txRowMain);
            Destroy(_txBlue);    Destroy(_txGold);  Destroy(_txRed);
            Destroy(_txGray);    Destroy(_txOverlay);
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // ── GUIStyles ─────────────────────────────────────────────────────────────
        private void EnsureStyles()
        {
            if (_stTitle != null) return;

            _stTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 17,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white }
            };
            _stSlotName = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 14,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white }
            };
            _stSlotNameMain = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 14,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(1.00f, 0.85f, 0.35f) } // золотистый
            };
            _stSlotInfo = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.68f, 0.68f, 0.68f) }
            };
            _stBtnLoad     = MakeBtnStyle(_txBlue!);
            _stBtnLoadGold = MakeBtnStyle(_txGold!);
            _stBtnDel      = MakeBtnStyle(_txRed!);
            _stBtnGray     = MakeBtnStyle(_txGray!);
            _stBodyText = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                wordWrap  = true,
                alignment = TextAnchor.UpperLeft,
                normal    = { textColor = Color.white }
            };
            _stWarnTitle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(1f, 0.85f, 0.3f) }
            };
            _stHint = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 10,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(0.50f, 0.50f, 0.50f) }
            };
        }

        private static GUIStyle MakeBtnStyle(Texture2D bg)
        {
            return new GUIStyle(GUI.skin.button)
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = Color.white, background = bg },
                hover     = { textColor = Color.white, background = bg },
                active    = { textColor = Color.white, background = bg },
                focused   = { textColor = Color.white, background = bg },
                border    = new RectOffset(0, 0, 0, 0),
                padding   = new RectOffset(6, 6, 4, 4)
            };
        }
    }
}

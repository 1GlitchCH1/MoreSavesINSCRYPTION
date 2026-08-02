using System;
using System.IO;
using System.Text;
using HarmonyLib;
using DiskCardGame;
using UnityEngine;

namespace SaveSlotsMod
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Перехват «Продолжить» / «Новая игра» в главном меню.
    //
    // ПРИМЕЧАНИЕ: В данной версии Inscryption метода OnNewGameCardReachedSlot
    // НЕТ — игра вызывает только OnStartGameCardReachedSlot в обоих случаях.
    // Поэтому перехватываем только его.
    // ─────────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(MenuController), "OnStartGameCardReachedSlot")]
    internal static class MenuController_OnStartGameCardReachedSlot_Patch
    {
        private static bool Prefix(MenuController __instance)
        {
            if (MenuPatches.PassingThrough) return true;

            SaveSlotManager.BackupLiveSave();

            // Если сейв уже существует — это «Продолжить», иначе — «Новая игра»
            bool isNewGame = !File.Exists(SaveSlotManager.LiveSavePath);
            MenuPatches.Intercept(__instance, isNewGame: isNewGame);
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
        public static bool PassingThrough { get; private set; }

        private static MenuController? _menu;
        private static bool            _menuWasNewGame;

        public static void Intercept(MenuController menu, bool isNewGame)
        {
            _menu           = menu;
            _menuWasNewGame = isNewGame;
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
                // ── ИСПРАВЛЕНИЕ Bug 2 ─────────────────────────────────────────────
                // CreateNewSaveFile — нативный метод игры, создаёт чистый SaveFile.gwsave.
                // После этого LoadFromFile грузит его в память, и OnStartGameCardReachedSlot
                // запускает игру с чистым состоянием.
                var createMethod = AccessTools.Method(typeof(SaveManager), "CreateNewSaveFile");
                if (createMethod != null)
                {
                    Plugin.Log.LogInfo("[MenuPatches] Новая игра через SaveManager.CreateNewSaveFile()");
                    createMethod.Invoke(null, null);
                }
                else
                {
                    Plugin.Log.LogWarning("[MenuPatches] CreateNewSaveFile не найден! Логируем методы SaveManager:");
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
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // IMGUI пикер слотов.
    // ─────────────────────────────────────────────────────────────────────────────
    public class SaveSlotUIBehaviour : MonoBehaviour
    {
        private static SaveSlotUIBehaviour? _instance;

        private bool   _showWarning;
        private int    _pendingSlot    = -1;
        private bool   _pendingIsEmpty;
        private string _warningText    = "";

        private Texture2D? _txDark, _txRow, _txRowMain, _txBlue, _txGold, _txRed, _txGray, _txOverlay;

        private GUIStyle? _stTitle, _stSlotName, _stSlotNameMain, _stSlotInfo;
        private GUIStyle? _stBtnLoad, _stBtnLoadGold, _stBtnDel, _stBtnGray;
        private GUIStyle? _stBodyText, _stWarnTitle, _stHint;
        private bool      _stylesReady;

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        public static void Show()
        {
            if (_instance != null) return;
            var go = new GameObject("SaveSlotUI");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<SaveSlotUIBehaviour>();
        }

        private void Awake()     => CreateTextures();
        private void OnDestroy() { _instance = null; DestroyTextures(); }

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

            // ИСПРАВЛЕНИЕ Bug 3: просто закрываем UI, не запускаем игру.
            if (GUI.Button(new Rect(cancelX, cancelY, cancelW, cancelH), "← Назад в меню", _stBtnGray!))
                OnCancel();
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
                info = meta != null
                    ? $"{meta.LastSaved.ToLocalTime():dd.MM.yyyy  HH:mm}   •   {meta.ModGuids.Count} мод(ов)"
                    : "Сохранение (нет данных о модах)";
            else
                info = isMainSave ? "Основного сохранения нет" : "Пустой слот — Новая игра";
            GUI.Label(new Rect(textX, ry + 34, 320, 22), info, _stSlotInfo!);

            float btnRight = rx + rw - 10;
            int   cap      = slot;

            if (hasSave)
            {
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
                if (GUI.Button(new Rect(lX, lY, lW, lH), "Загрузить",
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
            try   { SaveSlotManager.SwitchToSlot(slot); }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[SlotUI] SwitchToSlot({slot}) failed: {ex}");
                return;
            }

            Destroy(gameObject);

            if (isEmpty)
                MenuPatches.ProceedWithNewGame();
            else
                MenuPatches.ProceedWithLoad();
        }

        // ИСПРАВЛЕНИЕ Bug 3: просто закрываем UI, не запускаем игру.
        private void OnCancel()
        {
            Destroy(gameObject);
        }

        private void OnDeleteSlot(int slot)
        {
            if (SaveSlotManager.IsMainSaveSlot(slot)) return;
            SaveSlotManager.DeleteSlot(slot);
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
        }

        private void DestroyTextures()
        {
            foreach (var tx in new[] { _txDark, _txRow, _txRowMain, _txBlue, _txGold, _txRed, _txGray, _txOverlay })
                if (tx != null) Destroy(tx);
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

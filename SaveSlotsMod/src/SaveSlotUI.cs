using System;
using System.Text;
using HarmonyLib;
using DiskCardGame;
using UnityEngine;

namespace SaveSlotsMod
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Intercept right before TransitionToGame coroutine is started.
    // Patching TransitionToGame itself caused StartCoroutine(null) crash.
    // ─────────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(MenuController), "OnStartGameCardReachedSlot")]
    internal static class MenuController_OnStartGameCardReachedSlot_Patch
    {
        private static bool Prefix(MenuController __instance)
        {
            if (MenuPatches.PassingThrough) return true;

            // Backup current live save BEFORE showing the picker,
            // so the active slot shows up-to-date data.
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

        /// <summary>Load chosen slot and trigger the game transition.</summary>
        public static void Proceed()
        {
            if (_menu == null) return;
            PassingThrough = true;
            try
            {
                SaveManager.LoadFromFile();
                // With PassingThrough=true the prefix returns true → original runs →
                // StartCoroutine(TransitionToGame()) works normally.
                AccessTools.Method(typeof(MenuController), "OnStartGameCardReachedSlot")
                           ?.Invoke(_menu, null);
            }
            finally { PassingThrough = false; }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // IMGUI slot picker — no Canvas, no EventSystem, no RectTransform headaches.
    // OnGUI renders above all world-space objects automatically.
    // ─────────────────────────────────────────────────────────────────────────────
    public class SaveSlotUIBehaviour : MonoBehaviour
    {
        private static SaveSlotUIBehaviour? _instance;

        private bool     _showWarning;
        private int      _pendingSlot = -1;
        private string   _warningText  = "";

        // Solid-colour textures for backgrounds/buttons
        private Texture2D? _txDark;
        private Texture2D? _txRow;
        private Texture2D? _txBlue;
        private Texture2D? _txRed;
        private Texture2D? _txGray;
        private Texture2D? _txOverlay;

        // GUIStyles — built once in first OnGUI call
        private GUIStyle? _stTitle;
        private GUIStyle? _stSlotName;
        private GUIStyle? _stSlotInfo;
        private GUIStyle? _stBtnLoad;
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

            // Dim the whole screen
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _txOverlay!);

            if (_showWarning) DrawWarning();
            else              DrawPicker();
        }

        // ── Slot picker ───────────────────────────────────────────────────────────
        private void DrawPicker()
        {
            const float PW = 560f;
            const float ROW_H = 78f, ROW_GAP = 8f;
            float rowsTotal = SaveSlotManager.MaxSlots * (ROW_H + ROW_GAP) - ROW_GAP;
            // Title + padding + rows + cancel button row + bottom padding
            float ph = 16 + 34 + 12 + rowsTotal + 14 + 44 + 14;
            float px = (Screen.width  - PW) / 2f;
            float py = (Screen.height - ph) / 2f;

            GUI.DrawTexture(new Rect(px, py, PW, ph), _txDark!);

            // ── Title ──────────────────────────────────────────────────────────────
            GUI.Label(new Rect(px, py + 14, PW, 34), "— Выбери файл сохранения —", _stTitle!);

            // ── Slot rows ──────────────────────────────────────────────────────────
            float ry = py + 14 + 34 + 12;
            for (int i = 0; i < SaveSlotManager.MaxSlots; i++)
            {
                DrawSlotRow(i, px + 12, ry, PW - 24, ROW_H);
                ry += ROW_H + ROW_GAP;
            }

            // ── Cancel button ──────────────────────────────────────────────────────
            float cancelY = ry + 14;
            float cancelW = 200f, cancelH = 40f;
            float cancelX = px + (PW - cancelW) / 2f;
            if (GUI.Button(new Rect(cancelX, cancelY, cancelW, cancelH),
                           "← Назад в меню", _stBtnGray!))
            {
                OnCancel();
            }
        }

        private void DrawSlotRow(int slot, float rx, float ry, float rw, float rh)
        {
            GUI.DrawTexture(new Rect(rx, ry, rw, rh), _txRow!);

            bool hasSave = SaveSlotManager.SlotHasSave(slot);
            var  meta    = hasSave ? SaveSlotManager.LoadSlotMeta(slot) : null;

            // Labels
            float textX = rx + 14;
            GUI.Label(new Rect(textX, ry + 8,  240, 24), $"Слот {slot + 1}", _stSlotName!);

            string info = hasSave
                ? (meta != null
                    ? $"{meta.LastSaved.ToLocalTime():dd.MM.yyyy  HH:mm}   •   {meta.ModGuids.Count} мод(ов)"
                    : "Сохранение (нет данных о модах)")
                : "Пустой слот";
            GUI.Label(new Rect(textX, ry + 34, 300, 22), info, _stSlotInfo!);

            // Buttons on right side
            float btnRight = rx + rw - 10;
            int   cap      = slot;

            if (hasSave)
            {
                // ✕ delete (narrow)
                float dW = 32f, dH = 36f;
                float dX = btnRight - dW, dY = ry + (rh - dH) / 2f;
                if (GUI.Button(new Rect(dX, dY, dW, dH), "✕", _stBtnDel!))
                    OnDeleteSlot(cap);
                btnRight -= dW + 6;

                // Load
                float lW = 106f, lH = 36f;
                float lX = btnRight - lW, lY = ry + (rh - lH) / 2f;
                if (GUI.Button(new Rect(lX, lY, lW, lH), "Загрузить", _stBtnLoad!))
                    OnSlotChosen(cap);
            }
            else
            {
                float lW = 126f, lH = 36f;
                float lX = btnRight - lW, lY = ry + (rh - lH) / 2f;
                if (GUI.Button(new Rect(lX, lY, lW, lH), "Новая игра", _stBtnLoad!))
                    OnSlotChosen(cap);
            }
        }

        // ── Warning dialog ────────────────────────────────────────────────────────
        private void DrawWarning()
        {
            const float WW = 570f, WH = 370f;
            float wx = (Screen.width  - WW) / 2f;
            float wy = (Screen.height - WH) / 2f;

            GUI.DrawTexture(new Rect(wx, wy, WW, WH), _txDark!);
            GUI.Label(new Rect(wx, wy + 14, WW, 32), "⚠  Несоответствие модов", _stWarnTitle!);

            // Diff text area
            Rect box = new Rect(wx + 14, wy + 58, WW - 28, 210);
            GUI.DrawTexture(box, _txRow!);
            GUI.Label(new Rect(box.x + 10, box.y + 8, box.width - 20, box.height - 16),
                _warningText, _stBodyText!);

            GUI.Label(new Rect(wx, wy + WH - 84, WW, 22),
                "Вход может привести к нестабильности из-за изменений в модах.", _stHint!);

            float btnY = wy + WH - 54;
            float half = WW / 2f;
            if (GUI.Button(new Rect(wx + half - 8 - 164, btnY, 164, 42), "Войти  →", _stBtnLoad!))
            { _showWarning = false; if (_pendingSlot >= 0) DoSwitch(_pendingSlot); }

            if (GUI.Button(new Rect(wx + half + 8, btnY, 164, 42), "Отмена", _stBtnGray!))
            { _showWarning = false; _pendingSlot = -1; }
        }

        // ── Logic ─────────────────────────────────────────────────────────────────
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
            Destroy(gameObject);   // clears _instance via OnDestroy
            MenuPatches.Proceed();
        }

        private void OnCancel()
        {
            // Don't switch slots — just proceed with whatever is currently loaded.
            Destroy(gameObject);
            MenuPatches.Proceed();
        }

        private void OnDeleteSlot(int slot)
        {
            SaveSlotManager.DeleteSlot(slot);
            // No rebuild needed: OnGUI redraws every frame and reads fresh state.
        }

        // ── Diff text ─────────────────────────────────────────────────────────────
        private static string BuildDiffText(ModDiff diff)
        {
            var sb = new StringBuilder();
            if (diff.Added.Count > 0)
            {
                sb.AppendLine($"[+] Добавлены ({diff.Added.Count}):");
                foreach (var g in diff.Added) sb.AppendLine($"    + {g}");
                if (diff.Removed.Count > 0) sb.AppendLine();
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

        // ── Textures ──────────────────────────────────────────────────────────────
        private void CreateTextures()
        {
            _txDark    = MakeTex(new Color(0.07f, 0.07f, 0.10f, 0.97f));
            _txRow     = MakeTex(new Color(0.14f, 0.14f, 0.17f, 0.96f));
            _txBlue    = MakeTex(new Color(0.18f, 0.52f, 0.88f, 1.00f));
            _txRed     = MakeTex(new Color(0.68f, 0.16f, 0.16f, 1.00f));
            _txGray    = MakeTex(new Color(0.28f, 0.28f, 0.28f, 1.00f));
            _txOverlay = MakeTex(new Color(0.00f, 0.00f, 0.00f, 0.80f));
        }

        private void DestroyTextures()
        {
            Destroy(_txDark); Destroy(_txRow);  Destroy(_txBlue);
            Destroy(_txRed);  Destroy(_txGray); Destroy(_txOverlay);
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
            _stSlotInfo = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.68f, 0.68f, 0.68f) }
            };
            _stBtnLoad = MakeBtnStyle(_txBlue!);
            _stBtnDel  = MakeBtnStyle(_txRed!);
            _stBtnGray = MakeBtnStyle(_txGray!);
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

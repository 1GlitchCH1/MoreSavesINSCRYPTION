using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SaveSlotsMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("cyantist.inscryption.api", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.saveslotsmod.inscryption";
        public const string PluginName    = "Save Slots Mod";
        public const string PluginVersion = "1.0.7";

        internal static ManualLogSource Log = null!;

        // Страховка на случай, если Harmony-патчи не вызываются (например, из-за MonoMod/APIPatcher):
        // часто проверяем, изменился ли файл сейва на диске, и сразу сохраняем в активный слот.
        //
        // Важно: интервал маленький. Иначе игрок может успеть выйти в меню,
        // а живой сейв уже будет удалён/перезаписан.
        private float _pollTimer = 0f;
        private const float POLL_INTERVAL = 0.25f;

        // ── Горячая клавиша Shift+K+M ──────────────────────────────────────────
        // В оригинале запускает Kaycee's Mod DLC.
        // Если уже в KCM — отключает его из сейва.
        private float _kcmKeyCooldownTime = -100f;
        private const float KCM_KEY_COOLDOWN = 1f;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            SaveSlotManager.Initialize();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            Log.LogInfo($"{PluginName} loaded successfully. Found {SaveSlotManager.SlotCount} save slot(s).");
        }

        private void Update()
        {
            CheckKayceesModHotkey();

            // Не трогаем файлы, пока открыт пикер слотов (там идёт SwitchToSlot)
            if (SaveSlotUIBehaviour.IsShowing) return;

            _pollTimer += Time.deltaTime;
            if (_pollTimer < POLL_INTERVAL) return;
            _pollTimer = 0f;

            SaveSlotManager.AccumulatePlayTime(Time.deltaTime);
            SaveSlotManager.PollLiveSaveChanges();
        }

        /// <summary>
        /// Обработка Shift+K+M: если игрок уже в Kaycee's Mod — отключает KCM из сейва.
        /// Если KCM не активен — ничего не делаем (оригинальная игра сама запустит DLC).
        /// </summary>
        private void CheckKayceesModHotkey()
        {
            if (SaveSlotUIBehaviour.IsShowing) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool k = Input.GetKey(KeyCode.K);
            bool m = Input.GetKey(KeyCode.M);

            if (shift && k && m && (Input.GetKeyDown(KeyCode.K) || Input.GetKeyDown(KeyCode.M)))
            {
                if (Time.realtimeSinceStartup - _kcmKeyCooldownTime < KCM_KEY_COOLDOWN) return;
                _kcmKeyCooldownTime = Time.realtimeSinceStartup;

                if (SaveSlotManager.IsKayceesModActive())
                {
                    Log.LogInfo("[Plugin] Shift+K+M: отключаем Kaycee's Mod из сейва.");
                    SaveSlotManager.DisableKayceesMod();
                }
            }
        }
    }
}

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
        public const string PluginVersion = "1.0.5";

        internal static ManualLogSource Log = null!;

        // Страховка на случай, если Harmony-патчи не вызываются (например, из-за MonoMod/APIPatcher):
        // часто проверяем, изменился ли файл сейва на диске, и сразу сохраняем в активный слот.
        //
        // Важно: интервал маленький. Иначе игрок может успеть выйти в меню,
        // а живой сейв уже будет удалён/перезаписан.
        private float _pollTimer = 0f;
        private const float POLL_INTERVAL = 0.25f;

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
            // Не трогаем файлы, пока открыт пикер слотов (там идёт SwitchToSlot)
            if (SaveSlotUIBehaviour.IsShowing) return;

            _pollTimer += Time.deltaTime;
            if (_pollTimer < POLL_INTERVAL) return;
            _pollTimer = 0f;

            SaveSlotManager.AccumulatePlayTime(Time.deltaTime);
            SaveSlotManager.PollLiveSaveChanges();
        }
    }
}

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
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log = null!;

        // Периодический бэкап: раз в 60 секунд копируем живое сохранение в слот-файл.
        // Страховка на случай, если патч SaveToFile не сработал.
        private float _backupTimer = 0f;
        private const float BACKUP_INTERVAL = 60f;

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
            // Не делаем бэкап пока открыт пикер слотов
            if (SaveSlotUIBehaviour.IsShowing) return;
            // Не делаем бэкап в главном меню (нет активной игры)
            if (!File.Exists(SaveSlotManager.LiveSavePath)) return;

            _backupTimer += Time.deltaTime;
            if (_backupTimer >= BACKUP_INTERVAL)
            {
                _backupTimer = 0f;
                SaveSlotManager.OnGameSaved();
                Log.LogInfo($"[Plugin] Периодический бэкап → Слот {SaveSlotManager.ActiveSlot}.");
            }
        }
    }
}

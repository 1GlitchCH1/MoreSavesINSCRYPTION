using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace SaveSlotsMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("cyantist.inscryption.api", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.saveslotsmod.inscryption";
        public const string PluginName    = "Save Slots Mod";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log = null!;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            SaveSlotManager.Initialize();

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            Log.LogInfo($"{PluginName} loaded successfully. Found {SaveSlotManager.SlotCount} save slot(s).");
        }
    }
}

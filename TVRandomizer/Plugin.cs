using BepInEx;

namespace TVRandomizer
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            // Plugin startup logic
            Logger.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} is loaded!");
        }
    }
    
    [BepInPlugin("Lesnomievi.TVRandomizer", "TVRandomizer", "1.0.0")]
    public class TVRandomizerPlugin : BaseUnityPlugin
    {
        private const string MyGUID = "Lesnomievi.TVRandomizer";

        private const string PluginName = "TVRandomizer";

        private const string VersionString = "1.0.0";

        private static readonly Harmony Harmony = new Harmony("Lesnomievi.TVRandomizer");

        public static ManualLogSource Log = new ManualLogSource("TVRandomizer");

        private void Awake()
        {
            Log = ((BaseUnityPlugin)this).Logger;
            Harmony.PatchAll();
            VideoManager.Load();
            ((BaseUnityPlugin)this).Logger.LogInfo((object)string.Format("PluginName: {0}, VersionString: {1} is loaded. Video Count: {2}", "TVLoader", "1.1.1", VideoManager.Videos.Count));
        }
    }
}

namespace TVRandomizer.Patches
{
    [HarmonyPatch]
    internal class TVPatch {
        private static Type _tvManagerType;
        
        public static MethodBase TargetMethod() {
            var tvLoaderPlugin = typeof(TVLoaderPlugin);
            var assembly = tvLoaderPlugin.Assembly;
            var types = assembly.GetTypes();
            var tvManagerType = types.First(t => t.Name == "VideoManager");
            _tvManagerType = tvManagerType;
            return AccessTools.FirstMethod(tvManagerType, x => x.Name.Contains("Load"));
        }

        public static void Postfix() {
            var videosField = AccessTools.Field(_tvManagerType, "Videos");
            var videos = (List<string>) videosField.GetValue(null);

            if (videos.Count == 0) {
                Plugin.Log.LogInfo("No videos found");
                return;
            }
            
            // shuffle
            var random = new Random();
            videos = videos.OrderBy(x => random.Next()).ToList();
            videosField.SetValue(null, videos);
            Plugin.Log.LogInfo($"Shuffled {videos.Count} videos");
        }
    }
}
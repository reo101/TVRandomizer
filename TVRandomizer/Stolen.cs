using BepInEx;
using System.Collections.Generic;
using System.IO;

using HarmonyLib;
using System.Reflection;
using UnityEngine;
using UnityEngine.Video;
using TVRandomizer;
using TVRandomizer.Utils;
using TVRandomizer.Patches;

using BepInEx.Logging;

#nullable disable
namespace TVRandomizer
{
  [BepInPlugin("Lesnomievi.TVRandomizer", "TVRandomizer", "1.0.0")]
  // [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
  public class TVRandomizerPlugin : BaseUnityPlugin
  {
    private const string MyGUID = "Lesnomievi.TVRandomizer";
    private const string PluginName = "TVRandomizer";
    private const string VersionString = "1.0.0";
    private static readonly Harmony Harmony = new Harmony("Lesnomievi.TVRandomizer");
    public static ManualLogSource Log = new ManualLogSource("TVRandomizer");

    private void Awake()
    {
      TVRandomizerPlugin.Log = this.Logger;
      TVRandomizerPlugin.Harmony.PatchAll();
      VideoManager.Load();
      this.Logger.LogInfo((object) string.Format("PluginName: {0}, VersionString: {1} is loaded. Video Count: {2}", (object) "TVRandomizer", (object) "1.1.1", (object) VideoManager.Videos.Count));
    }
  }
}


#nullable disable
namespace TVRandomizer.Patches
{
  [HarmonyPatch(typeof (TVScript))]
  internal class TVScriptPatches
  {
    private static FieldInfo currentClipProperty = typeof (TVScript).GetField("currentClip", BindingFlags.Instance | BindingFlags.NonPublic);
    private static FieldInfo currentTimeProperty = typeof (TVScript).GetField("currentClipTime", BindingFlags.Instance | BindingFlags.NonPublic);
    private static FieldInfo wasTvOnLastFrameProp = typeof (TVScript).GetField("wasTvOnLastFrame", BindingFlags.Instance | BindingFlags.NonPublic);
    private static FieldInfo timeSinceTurningOffTVProp = typeof (TVScript).GetField("timeSinceTurningOffTV", BindingFlags.Instance | BindingFlags.NonPublic);
    private static MethodInfo setMatMethod = typeof (TVScript).GetMethod("SetTVScreenMaterial", BindingFlags.Instance | BindingFlags.NonPublic);
    private static MethodInfo onEnableMethod = typeof (TVScript).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic);
    private static bool tvHasPlayedBefore = false;
    private static RenderTexture renderTexture;
    private static VideoPlayer currentVideoPlayer;
    private static VideoPlayer nextVideoPlayer;

    [HarmonyPrefix]
    [HarmonyPatch("Update")]
    public static bool Update(TVScript __instance)
    {
      if (TVScriptPatches.currentVideoPlayer == null)
      {
        TVScriptPatches.currentVideoPlayer = ((Component) __instance).GetComponent<VideoPlayer>();
        TVScriptPatches.renderTexture = TVScriptPatches.currentVideoPlayer.targetTexture;
        if (VideoManager.Videos.Count > 0)
          TVScriptPatches.PrepareVideo(__instance, 0);
      }
      return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("TurnTVOnOff")]
    public static bool TurnTVOnOff(TVScript __instance, bool on)
    {
      TVRandomizerPlugin.Log.LogInfo((object) string.Format("TVOnOff: on: {0}", (object) on));
      TVRandomizerPlugin.Log.LogInfo((object) string.Format("TVOnOff: VideoManager.Videos.Count {0}", (object) VideoManager.Videos.Count));
      if (VideoManager.Videos.Count == 0)
        return false;
      int num1 = (int) TVScriptPatches.currentClipProperty.GetValue((object) __instance);
      if (on && TVScriptPatches.tvHasPlayedBefore)
      {
        int num2 = (num1 + 1) % VideoManager.Videos.Count;
        TVScriptPatches.currentClipProperty.SetValue((object) __instance, (object) num2);
      }
      __instance.tvOn = on;
      if (on)
      {
        TVScriptPatches.PlayVideo(__instance);
        __instance.tvSFX.PlayOneShot(__instance.switchTVOn);
        WalkieTalkie.TransmitOneShotAudio(__instance.tvSFX, __instance.switchTVOn, 1f);
      }
      else
      {
        __instance.video.Stop();
        __instance.tvSFX.PlayOneShot(__instance.switchTVOff);
        WalkieTalkie.TransmitOneShotAudio(__instance.tvSFX, __instance.switchTVOff, 1f);
      }
      TVScriptPatches.setMatMethod.Invoke((object) __instance, new object[1]
      {
        (object) on
      });
      return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch("TVFinishedClip")]
    public static bool TVFinishedClip(TVScript __instance, VideoPlayer source)
    {
      if (!__instance.tvOn || GameNetworkManager.Instance.localPlayerController.isInsideFactory)
        return false;
      TVRandomizerPlugin.Log.LogInfo((object) nameof (TVFinishedClip));
      int num = (int) TVScriptPatches.currentClipProperty.GetValue((object) __instance);
      if (VideoManager.Videos.Count > 0)
        num = (num + 1) % VideoManager.Videos.Count;
      TVScriptPatches.currentTimeProperty.SetValue((object) __instance, (object) 0.0f);
      TVScriptPatches.currentClipProperty.SetValue((object) __instance, (object) num);
      TVScriptPatches.PlayVideo(__instance);
      return false;
    }

    private static void PrepareVideo(TVScript instance, int index = -1)
    {
      if (index == -1)
        index = (int) TVScriptPatches.currentClipProperty.GetValue((object) instance) + 1;
      if ((TVScriptPatches.nextVideoPlayer != null) && ((Component) TVScriptPatches.nextVideoPlayer).gameObject.activeInHierarchy)
        Object.Destroy((Object) TVScriptPatches.nextVideoPlayer);
      TVScriptPatches.nextVideoPlayer = ((Component) instance).gameObject.AddComponent<VideoPlayer>();
      TVScriptPatches.nextVideoPlayer.playOnAwake = false;
      TVScriptPatches.nextVideoPlayer.isLooping = false;
      TVScriptPatches.nextVideoPlayer.source = (VideoSource) 1;
      TVScriptPatches.nextVideoPlayer.controlledAudioTrackCount = (ushort) 1;
      TVScriptPatches.nextVideoPlayer.audioOutputMode = (VideoAudioOutputMode) 1;
      TVScriptPatches.nextVideoPlayer.SetTargetAudioSource((ushort) 0, instance.tvSFX);
      TVScriptPatches.nextVideoPlayer.url = "file://" + VideoManager.Videos[index % VideoManager.Videos.Count];
      TVScriptPatches.nextVideoPlayer.Prepare();
      TVScriptPatches.nextVideoPlayer.prepareCompleted += (VideoPlayer.EventHandler) (source => TVRandomizerPlugin.Log.LogInfo((object) "Prepared next video!"));
    }

    private static void PlayVideo(TVScript instance)
    {
      TVScriptPatches.tvHasPlayedBefore = true;
      if (VideoManager.Videos.Count == 0)
        return;
      if (TVScriptPatches.nextVideoPlayer != null)
      {
        VideoPlayer currentVideoPlayer = TVScriptPatches.currentVideoPlayer;
        instance.video = TVScriptPatches.currentVideoPlayer = TVScriptPatches.nextVideoPlayer;
        TVScriptPatches.nextVideoPlayer = (VideoPlayer) null;
        TVRandomizerPlugin.Log.LogInfo((object) string.Format("Destroy {0}", (object) currentVideoPlayer));
        Object.Destroy((Object) currentVideoPlayer);
        TVScriptPatches.onEnableMethod.Invoke((object) instance, new object[0]);
      }
      TVScriptPatches.currentTimeProperty.SetValue((object) instance, (object) 0.0f);
      instance.video.targetTexture = TVScriptPatches.renderTexture;
      instance.video.Play();
      TVScriptPatches.PrepareVideo(instance);
    }
  }
}


#nullable disable
namespace TVRandomizer.Utils
{
  internal static class VideoManager
  {
    public static List<string> Videos = new List<string>();

    public static void Load()
    {
      foreach (string directory in Directory.GetDirectories(Paths.PluginPath))
      {
        string path = Path.Combine(Paths.PluginPath, directory, "Television Videos");
        if (Directory.Exists(path))
        {
          string[] files = Directory.GetFiles(path, "*.mp4");
          VideoManager.Videos.AddRange((IEnumerable<string>) files);
          TVRandomizerPlugin.Log.LogInfo((object) string.Format("{0} has {1} videos.", (object) directory, (object) files.Length));
        }
      }
      string path1 = Path.Combine(Paths.PluginPath, "Television Videos");
      if (!Directory.Exists(path1))
        Directory.CreateDirectory(path1);
      string[] files1 = Directory.GetFiles(path1, "*.mp4");
      VideoManager.Videos.AddRange((IEnumerable<string>) files1);
      TVRandomizerPlugin.Log.LogInfo((object) string.Format("Global has {0} videos.", (object) files1.Length));
      TVRandomizerPlugin.Log.LogInfo((object) string.Format("Loaded {0} total.", (object) VideoManager.Videos.Count));
    }
  }
}
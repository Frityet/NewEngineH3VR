using System;
using System.IO;
using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using NativeH3VR.Platform;
using RenderHeads.Media.AVProVideo;
public sealed class PlatformProbe : MonoBehaviour {
    [Serializable]
    public class Report {
        public string unity, gpu, architecture;
        public bool metal, steamInit, steamOwned, steamRestart, steamCallback, videoPrepared,
            videoTexture, videoOrientation, videoSeek, videoPaused;
        public int banks, events, fmodTimeline;
        public float fmodPeak, fmodRms;
        public string error;
    }
    Report result = new Report();
    UnityMediaPlayer video;
    FMOD.Studio.System studio;
    void Need(bool condition, string operation) {
        if (!condition)
            throw new Exception(operation);
    }
    void Check(FMOD.RESULT status, string operation) {
        if (status != FMOD.RESULT.OK)
            throw new Exception(operation + ": " + status);
    }
    IEnumerator Start() {
        result.unity = Application.unityVersion;
        result.gpu = SystemInfo.graphicsDeviceName;
        result.architecture = "arm64";
        result.metal = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;
        try {
            TestSteam();
            TestFMOD();
            var owner = new GameObject("Original AVPro Factory").AddComponent<MediaPlayer>();
            owner.enabled = false;
            video = owner.CreateMediaPlayer() as UnityMediaPlayer;
            Need(video != null, "original AVPro factory still uses Windows backend");
            video.MuteAudio(true);
            Need(video.OpenMedia(Path.Combine(Application.streamingAssetsPath, "probe.mp4"), 0,
                                 null, default(MediaHints)),
                 "video.OpenMedia");
        } catch (Exception e) {
            Finish(e);
            yield break;
        }
        var deadline = Time.realtimeSinceStartup + 20;
        while (!video.CanPlay() && Time.realtimeSinceStartup < deadline) {
            video.Update();
            yield return null;
        }
        try {
            result.videoPrepared = video.CanPlay();
            Need(result.videoPrepared, "video prepare timeout");
            video.Play();
        } catch (Exception e) {
            Finish(e);
            yield break;
        }
        while (video.GetTextureFrameCount() < 2 && Time.realtimeSinceStartup < deadline) {
            video.Update();
            yield return null;
        }
        yield return new WaitForEndOfFrame();
        try {
            var texture = video.GetTexture();
            result.videoTexture = texture != null && texture.width == 128 && texture.height == 64;
            Need(result.videoTexture, "decoded texture missing");
            var rt = RenderTexture.GetTemporary(128, 64, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(texture, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var pixels = new Texture2D(128, 64, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, 128, 64), 0, 0);
            pixels.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var bottom = pixels.GetPixel(20, 10);
            var top = pixels.GetPixel(20, 54);
            result.videoOrientation =
                top.r > 0.7f && top.b < 0.2f && bottom.b > 0.7f && bottom.r < 0.2f;
            var folder = Path.GetDirectoryName(
                Environment.GetEnvironmentVariable("NATIVEH3VR_PROBE_RESULT"));
            File.WriteAllBytes(Path.Combine(folder, "video-frame.png"), pixels.EncodeToPNG());
            UnityEngine.Object.Destroy(pixels);
            Need(result.videoOrientation,
                 "video texture orientation/color mismatch: " + top + " / " + bottom);
            video.Seek(1.5);
            deadline = Time.realtimeSinceStartup + 10;
        } catch (Exception e) {
            Finish(e);
            yield break;
        }
        while (video.IsSeeking() && Time.realtimeSinceStartup < deadline) {
            video.Update();
            yield return null;
        }
        try {
            result.videoSeek = video.GetCurrentTime() >= 1.4 && video.GetCurrentTime() < 2.5;
            Need(result.videoSeek, "video seek failed " + video.GetCurrentTime());
            video.Pause();
            result.videoPaused = video.IsPaused() && !video.IsPlaying();
            Need(result.videoPaused, "video pause failed");
            Finish(null);
        } catch (Exception e) {
            Finish(e);
        }
    }
    void TestSteam() {
        Need(Steamworks.Packsize.Test(), "Steam packing");
        result.steamRestart =
            Steamworks.SteamAPI.RestartAppIfNecessary(new Steamworks.AppId_t(450540));
        Need(!result.steamRestart, "unexpected Steam restart");
        result.steamInit = Steamworks.SteamAPI.Init();
        Need(result.steamInit, "Steam Init");
        result.steamOwned = Steamworks.SteamApps.BIsSubscribedApp(new Steamworks.AppId_t(450540));
        Need(result.steamOwned, "Steam ownership");
        using (var callback = Steamworks.Callback<Steamworks.UserStatsReceived_t>.Create(
                   data => result.steamCallback = (int)data.m_eResult == 1)) {
            Steamworks.SteamUserStats.RequestCurrentStats();
            for (int i = 0; i < 100 && !result.steamCallback; i++) {
                Steamworks.SteamAPI.RunCallbacks();
                System.Threading.Thread.Sleep(20);
            }
        }
        Need(result.steamCallback, "Steam callback");
        Steamworks.SteamAPI.Shutdown();
    }
    void TestFMOD() {
        Check(FMOD.Studio.System.create(out studio), "FMOD create");
        Check(studio.getLowLevelSystem(out var core), "core");
        Check(core.setOutput(FMOD.OUTPUTTYPE.NOSOUND_NRT), "FMOD non-real-time output");
        var advanced = new FMOD.ADVANCEDSETTINGS { randomSeed = 1234 };
        Check(core.setAdvancedSettings(ref advanced), "advanced settings");
        Check(studio.initialize(128, FMOD.Studio.INITFLAGS.SYNCHRONOUS_UPDATE,
                                FMOD.INITFLAGS.NORMAL, IntPtr.Zero),
              "initialize");
        var folder = Environment.GetEnvironmentVariable("NATIVEH3VR_BANKS");
        foreach (var path in Directory.GetFiles(folder, "*.bank")) {
            Check(studio.loadBankFile(path, FMOD.Studio.LOAD_BANK_FLAGS.NORMAL, out var bank),
                  "load " + path);
            result.banks++;
            Check(bank.getEventCount(out var count), "event count");
            result.events += count;
        }
        Check(studio.getEvent("event:/MX/TAH2/mx_TAH2_Take", out var description), "music event");
        Check(description.loadSampleData(), "load samples");
        Check(studio.flushSampleLoading(), "flush samples");
        Check(description.createInstance(out var instance), "instance");
        Check(instance.getParameter("TAH2 Intensity", out var parameter), "parameter");
        Check(parameter.setValue(1), "set parameter");
        Check(core.getMasterChannelGroup(out var master), "master");
        Check(master.getDSP(0, out var dsp), "meter DSP");
        Check(dsp.setMeteringEnabled(false, true), "meter");
        Check(instance.start(), "start music");
        for (int i = 0; i < 500; i++) {
            Check(studio.update(), "update");
            Check(dsp.getMeteringInfo(IntPtr.Zero, out var meter), "get meter");
            if (meter.peaklevel != null)
                foreach (var f in meter.peaklevel)
                    result.fmodPeak = Math.Max(result.fmodPeak, f);
            if (meter.rmslevel != null)
                foreach (var f in meter.rmslevel)
                    result.fmodRms = Math.Max(result.fmodRms, f);
        }
        Check(instance.getTimelinePosition(out result.fmodTimeline), "timeline");
        Need(result.fmodPeak > 0 && result.fmodRms > 0 && result.fmodTimeline > 0,
             "music mixer silent");
        Check(instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE), "stop");
        Check(instance.release(), "release event");
        Check(studio.release(), "release FMOD");
        studio.clearHandle();
    }
    void Finish(Exception e) {
        if (video != null) {
            video.Dispose();
            video = null;
        }
        if (studio.hasHandle()) {
            studio.release();
            studio.clearHandle();
        }
        result.error = e == null ? null : e.ToString();
        var text = JsonUtility.ToJson(result, true);
        File.WriteAllText(Environment.GetEnvironmentVariable("NATIVEH3VR_PROBE_RESULT"), text);
        Debug.Log("PLATFORM_PROBE " + text);
        Application.Quit(e == null ? 0 : 1);
    }
}

using System;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Audio;
using UnityEngine.Experimental.Video;
using UnityEngine.Video;
using RenderHeads.Media.AVProVideo;

namespace NativeH3VR.Platform {
// Retains the original AVPro MediaPlayer/event/material API while decoding via
// Unity's native macOS AVFoundation/Metal video backend.
public sealed class UnityMediaPlayer : BaseMediaPlayer {
    readonly GameObject host;
    readonly VideoPlayer video;
    readonly object audioLock = new object();
    AudioSampleProvider audioProvider;
    bool hasAudioProvider;
    NativeArray<float> audioSampleBuffer;
    float[] audioRing;
    int audioProviderChannels, audioProviderRate, audioOutputSampleRate, audioRingRead,
        audioRingWrite, audioRingSamples, audioRingCapacityFrames;
    double audioResamplePhase;
    volatile bool audioPlaying;
    volatile bool hasAudio;
    volatile int audioChannelCount;
    bool opening, wantPlay, paused, finished, seeking, seekFrameReady, muted, disposed;
    volatile bool audioRateRefreshPending;
    readonly bool[] changedTracks = new bool[3];
    int frames, activeAudio;
    float volume = 1f, balance;
    double? pendingSeek;
    double seekTarget;
    bool seekAcknowledged, resumeAfterSeek;
    float desiredPlaybackRate = 1f;
    string temporaryFile;
    public UnityMediaPlayer() : this(null) {}
    public UnityMediaPlayer(MediaPlayer mediaOwner) {
        _playerDescription = "Native macOS Unity VideoPlayer (AVFoundation / Metal)";
        host = new GameObject("NativeH3VR Video Decoder");
        UnityEngine.Object.DontDestroyOnLoad(host);
        video = host.AddComponent<VideoPlayer>();
        video.playOnAwake = false;
        video.source = VideoSource.Url;
        video.renderMode = VideoRenderMode.APIOnly;
        // APIOnly is the only Unity VideoPlayer output that exposes decoded
        // PCM for AVPro's existing AudioOutput/GrabAudio consumer.
        video.audioOutputMode = VideoAudioOutputMode.APIOnly;
        video.controlledAudioTrackCount = 1;
        video.waitForFirstFrame = true;
        video.sendFrameReadyEvents = true;
        video.skipOnDrop = true;
        video.prepareCompleted += OnPrepared;
        video.seekCompleted += OnSeekCompleted;
        video.frameReady += OnFrame;
        video.loopPointReached += OnEnd;
        video.errorReceived += OnError;
        audioOutputSampleRate = AudioSettings.outputSampleRate;
        AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
    }
    void OnAudioConfigurationChanged(bool deviceWasChanged) { audioRateRefreshPending = true; }
    void OnPrepared(VideoPlayer sender) {
        opening = false;
        paused = true;
        video.controlledAudioTrackCount =
            (ushort)Math.Min(video.audioTrackCount, VideoPlayer.controlledAudioTrackMaxCount);
        for (ushort i = 0; i < video.controlledAudioTrackCount; i++)
            video.EnableAudioTrack(i, i == activeAudio);
        hasAudio = video.audioTrackCount > 0;
        audioChannelCount = hasAudio ? video.GetAudioChannelCount((ushort)activeAudio) : 0;
        ResetAudioProvider();
        for (int i = 0; i < changedTracks.Length; i++)
            changedTracks[i] = true;
        _seekableTimes = new TimeRanges(new[] { new TimeRange(0, video.length) });
        // Unity does not expose buffered ranges. Do not claim the entire network stream is cached.
        _bufferedTimes = new TimeRanges();
        if (pendingSeek.HasValue) {
            var time = pendingSeek.Value;
            pendingSeek = null;
            Seek(time);
        }
        if (video.canSetPlaybackSpeed)
            video.playbackSpeed = desiredPlaybackRate;
        if (wantPlay)
            Play();
    }
    void OnSeekCompleted(VideoPlayer sender) { seekAcknowledged = true; }
    void OnFrame(VideoPlayer sender, long index) {
        frames++;
        if (seeking)
            seekFrameReady = true;
    }
    void OnEnd(VideoPlayer sender) {
        if (!video.isLooping) {
            finished = true;
            wantPlay = false;
            SetAudioPlaying(false);
            paused = false;
        }
    }
    void OnError(VideoPlayer sender, string message) {
        _lastError = opening ? ErrorCode.LoadFailed : ErrorCode.DecodeFailed;
        opening = false;
        wantPlay = false;
        SetAudioPlaying(false);
        Debug.LogError("[NativeH3VR Video] " + message);
    }
    bool Fail(string message) {
        _lastError = ErrorCode.LoadFailed;
        SetAudioPlaying(false);
        Debug.LogError("[NativeH3VR Video] " + message);
        return false;
    }
    void SetAudioPlaying(bool value) { lock (audioLock) audioPlaying = value; }
    public override string GetVersion() => "NativeH3VR.UnityVideo/1";
    public override string GetExpectedVersion() => "NativeH3VR.UnityVideo/1";
    public override bool OpenMedia(string path, long offset, string customHttpHeaders,
                                   MediaHints hints, int forceFileFormat = 0,
                                   bool startWithHighestBitrate = false) {
        CloseMedia();
        if (disposed)
            return Fail("Decoder already disposed.");
        if (offset != 0)
            return Fail(
                "Byte-offset media requires a container-aware source; this file was not opened.");
        if (!string.IsNullOrEmpty(customHttpHeaders))
            return Fail("Custom HTTP headers are not exposed by Unity VideoPlayer; this URL was " +
                        "not opened.");
        if (string.IsNullOrEmpty(path))
            return Fail("Empty media path.");
        if (!path.Contains("://") && !File.Exists(path))
            return Fail("Media file does not exist: " + path);
        _mediaHints = hints;
        opening = true;
        activeAudio = 0;
        video.url = path;
        try {
            video.Prepare();
            return true;
        } catch (Exception e) {
            opening = false;
            return Fail(e.Message);
        }
    }
    public override bool OpenMediaFromBuffer(byte[] buffer) {
        if (buffer == null || buffer.Length == 0)
            return Fail("Empty media buffer.");
        string path = Path.Combine(Application.temporaryCachePath,
                                   "nativeh3vr-video-" + Guid.NewGuid().ToString("N") + ".mp4");
        try {
            File.WriteAllBytes(path, buffer);
            if (!OpenMedia(path, 0, null, default(MediaHints))) {
                File.Delete(path);
                return false;
            }
            temporaryFile = path;
            return true;
        } catch (Exception e) {
            return Fail(e.Message);
        }
    }
    public override void CloseMedia() {
        SetAudioPlaying(false);
        ReleaseAudioProvider(true);
        if (!disposed)
            video.Stop();
        opening = wantPlay = paused = finished = seeking = resumeAfterSeek = false;
        pendingSeek = null;
        frames = 0;
        _seekableTimes = new TimeRanges();
        _bufferedTimes = new TimeRanges();
        if (temporaryFile != null) {
            try {
                File.Delete(temporaryFile);
            } catch (IOException) {
            }
            temporaryFile = null;
        }
        base.CloseMedia();
    }
    public override void SetLooping(bool looping) { video.isLooping = looping; }
    public override bool IsLooping() => video.isLooping;
    public override bool HasMetaData() => video.isPrepared;
    public override bool CanPlay() => video.isPrepared && _lastError == ErrorCode.None;
    public override void Play() {
        wantPlay = true;
        finished = false;
        if (video.isPrepared) {
            video.Play();
            paused = false;
            SetAudioPlaying(true);
        }
    }
    public override void Pause() {
        wantPlay = false;
        SetAudioPlaying(false);
        video.Pause();
        FlushAudioProvider();
        paused = true;
    }
    public override void Stop() {
        Pause();
        finished = false;
        Seek(0);
    }
    public override void Seek(double time) {
        finished = false;
        time = Math.Max(0, time);
        if (!video.isPrepared) {
            pendingSeek = time;
            return;
        }
        if (video.canSetTime) {
            resumeAfterSeek = audioPlaying && wantPlay;
            SetAudioPlaying(false);
            if (resumeAfterSeek)
                video.Pause();
            FlushAudioProvider();
            seeking = true;
            seekFrameReady = false;
            seekAcknowledged = false;
            seekTarget = Math.Min(time, video.length);
            video.Play();
            video.time = seekTarget;
        }
    }
    public override void SeekFast(double time) => Seek(time);
    public override double GetCurrentTime() => video.time;
    public override float GetPlaybackRate() => video.playbackSpeed;
    public override void SetPlaybackRate(float rate) {
        desiredPlaybackRate = rate;
        if (video.canSetPlaybackSpeed)
            video.playbackSpeed = rate;
        else if (rate != 1)
            Debug.LogWarning(
                "[NativeH3VR Video] Decoder cannot change playback speed for this media.");
    }
    public override double GetDuration() => video.length;
    public override int GetVideoWidth() => (int)video.width;
    public override int GetVideoHeight() => (int)video.height;
    public override float GetVideoFrameRate() => video.frameRate;
    public override bool HasAudio() => hasAudio;
    public override bool HasVideo() => video.isPrepared && video.width > 0 && video.height > 0;
    public override bool IsSeeking() => seeking;
    // AVPro calls IsPlaying from AudioOutput.OnAudioFilterRead. Reading Unity's
    // VideoPlayer state there throws; Update and the control methods publish it.
    public override bool IsPlaying() => audioPlaying;
    public override bool IsPaused() => paused;
    public override bool IsFinished() => finished;
    public override bool IsBuffering() => opening || (wantPlay && !video.isPlaying && !finished &&
                                                      _lastError == ErrorCode.None);
    public override Texture GetTexture(int index = 0) => index == 0 ? video.texture : null;
    public override int GetTextureFrameCount() => frames;
    public override long GetTextureTimeStamp() => (long)(video.time * 10000000.0);
    public override bool RequiresVerticalFlip() => false;
    internal override StereoPacking InternalGetTextureStereoPacking() => StereoPacking.Unknown;
    void ResetAudioProvider() {
        ReleaseAudioProvider(false);
        if (!hasAudio || video.audioOutputMode != VideoAudioOutputMode.APIOnly)
            return;
        try {
            var provider = video.GetAudioSampleProvider((ushort)activeAudio);
            if (!provider.valid)
                return;
            lock (audioLock) {
                int channels = provider.channelCount, rate = (int)provider.sampleRate;
                if (channels <= 0 || channels > 32 || rate <= 0 || rate > 384000) {
                    provider.Dispose();
                    return;
                }
                audioProvider = provider;
                hasAudioProvider = true;
                audioProviderChannels = channels;
                audioProviderRate = rate;
                audioChannelCount = audioProviderChannels;
                provider.enableSilencePadding = false;
                int dspFrames, dspBuffers;
                AudioSettings.GetDSPBufferSize(out dspFrames, out dspBuffers);
                dspFrames = Math.Max(64, Math.Min(8192, dspFrames));
                audioRingCapacityFrames = Math.Max(8192, Math.Min(65536, dspFrames * 8));
                audioRing = new float[checked(audioRingCapacityFrames * audioProviderChannels)];
                int nativeFrames =
                    Math.Max(1024, Math.Min(audioRingCapacityFrames / 4, dspFrames * 4));
                audioSampleBuffer = new NativeArray<float>(
                    checked(nativeFrames * audioProviderChannels), Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                audioRingRead = audioRingWrite = audioRingSamples = 0;
                audioResamplePhase = 0;
            }
        } catch (Exception e) {
            Debug.LogError("[NativeH3VR Video] Could not attach Unity audio sample provider: " +
                           e.Message);
        }
    }
    void ReleaseAudioProvider(bool clearTrackMetadata) {
        lock (audioLock) {
            if (hasAudioProvider)
                audioProvider.Dispose();
            audioProvider = default(AudioSampleProvider);
            hasAudioProvider = false;
            if (audioSampleBuffer.IsCreated)
                audioSampleBuffer.Dispose();
            audioRing = null;
            audioProviderChannels = audioProviderRate = audioRingRead = audioRingWrite =
                audioRingSamples = audioRingCapacityFrames = 0;
            audioResamplePhase = 0;
            if (clearTrackMetadata) {
                audioChannelCount = 0;
                hasAudio = false;
            }
        }
    }
    void FlushAudioProvider() {
        lock (audioLock) {
            audioRingRead = audioRingWrite = audioRingSamples = 0;
            audioResamplePhase = 0;
            if (!hasAudioProvider || !audioSampleBuffer.IsCreated)
                return;
            int capacityFrames = audioSampleBuffer.Length / Math.Max(1, audioProviderChannels);
            for (int n = 0; n < 256; n++) {
                int available = (int)audioProvider.availableSampleFrameCount;
                if (available <= 0)
                    break;
                int frames = Math.Min(available, capacityFrames);
                if (frames <= 0)
                    break;
                audioProvider.ConsumeSampleFrames(audioSampleBuffer);
            }
        }
    }
    float InterpolatedSample(int baseIndex, int channel, double fraction, int sourceChannels) {
        int index = (baseIndex + channel) % audioRing.Length;
        int next = (index + sourceChannels) % audioRing.Length;
        return audioRing[index] + (audioRing[next] - audioRing[index]) * (float)fraction;
    }
    public override int GrabAudio(float[] data, int floatCount, int channelCount) {
        if (data == null || floatCount <= 0 || channelCount <= 0)
            return 0;
        int requestedFloats = Math.Min(floatCount, data.Length);
        int requestedFrames = requestedFloats / channelCount;
        if (requestedFrames <= 0)
            return 0;
        lock (audioLock) {
            if (!audioPlaying || !hasAudioProvider || !audioSampleBuffer.IsCreated ||
                audioProviderChannels <= 0 || audioRing == null || audioProviderRate <= 0)
                return 0;
            int sourceChannels = audioProviderChannels;
            int ringFrames = audioRingSamples / sourceChannels;
            double rate = (double)audioProviderRate / Math.Max(1, audioOutputSampleRate);
            if (rate <= 0 || rate > 16 || requestedFrames > 65536)
                return 0;
            int requiredFrames =
                (int)Math.Ceiling(audioResamplePhase + (requestedFrames - 1) * rate) + 2;
            if (requiredFrames > audioRingCapacityFrames)
                return 0;
            int nativeFrames = audioSampleBuffer.Length / sourceChannels;
            while (ringFrames < requiredFrames && ringFrames < audioRingCapacityFrames) {
                int freeFrames = audioRingCapacityFrames - ringFrames;
                int available = (int)audioProvider.availableSampleFrameCount;
                int consumeFrames = Math.Min(Math.Min(freeFrames, nativeFrames), available);
                if (consumeFrames < nativeFrames)
                    break;
                if (consumeFrames <= 0)
                    break;
                int got = (int)audioProvider.ConsumeSampleFrames(audioSampleBuffer);
                if (got <= 0)
                    break;
                got = Math.Min(got, consumeFrames);
                int samples = Math.Min(got * sourceChannels, freeFrames * sourceChannels);
                for (int i = 0; i < samples; i++) {
                    audioRing[audioRingWrite] = audioSampleBuffer[i];
                    audioRingWrite = (audioRingWrite + 1) % audioRing.Length;
                }
                audioRingSamples += samples;
                ringFrames = audioRingSamples / sourceChannels;
            }
            if (ringFrames < requiredFrames)
                return 0;
            for (int frame = 0; frame < requestedFrames; frame++) {
                double fraction = audioResamplePhase + frame * rate;
                int whole = (int)fraction;
                fraction -= whole;
                int baseIndex = (audioRingRead + whole * sourceChannels) % audioRing.Length;
                float left = InterpolatedSample(baseIndex, 0, fraction, sourceChannels);
                float right = sourceChannels > 1
                                  ? InterpolatedSample(baseIndex, 1, fraction, sourceChannels)
                                  : left;
                for (int c = 0; c < channelCount; c++) {
                    float sample;
                    if (channelCount == 1) {
                        sample = 0;
                        for (int sc = 0; sc < sourceChannels; sc++)
                            sample += InterpolatedSample(baseIndex, sc, fraction, sourceChannels);
                        sample /= sourceChannels;
                    } else if (sourceChannels == 1)
                        sample = left;
                    else
                        sample = InterpolatedSample(baseIndex, Math.Min(c, sourceChannels - 1),
                                                    fraction, sourceChannels);
                    if (channelCount >= 2 && c == 0 && balance > 0)
                        sample *= 1f - balance;
                    else if (channelCount >= 2 && c == 1 && balance < 0)
                        sample *= 1f + balance;
                    data[frame * channelCount + c] = sample;
                }
            }
            double advanced = audioResamplePhase + requestedFrames * rate;
            int discard = (int)advanced;
            audioResamplePhase = advanced - discard;
            int discarded = Math.Min(discard, audioRingSamples / sourceChannels);
            audioRingRead = (audioRingRead + discarded * sourceChannels) % audioRing.Length;
            audioRingSamples -= discarded * sourceChannels;
            return requestedFrames * channelCount;
        }
    }
    public override int GetAudioBufferedSampleCount() {
        lock (audioLock) return hasAudioProvider
            ? (int)audioProvider.availableSampleFrameCount +
                  audioRingSamples / Math.Max(1, audioProviderChannels)
            : 0;
    }
    public override void MuteAudio(bool value) { muted = value; }
    public override bool IsMuted() => muted;
    public override void SetVolume(float value) { volume = Mathf.Clamp01(value); }
    public override float GetVolume() => volume;
    public override void SetBalance(float value) { balance = Mathf.Clamp(value, -1, 1); }
    public override float GetBalance() => balance;
    // AVPro queries this from its audio mixer callback, so publish it from
    // OnPrepared/track changes instead of reaching back into VideoPlayer.
    public override int GetAudioChannelCount() => audioChannelCount;
    public override void Update() {
        if (disposed)
            return;
        if (audioRateRefreshPending) {
            audioRateRefreshPending = false;
            int rate = AudioSettings.outputSampleRate;
            if (rate > 0)
                lock (audioLock) audioOutputSampleRate = rate;
        }
        if (seeking && seekAcknowledged && seekFrameReady &&
            Math.Abs(video.time - seekTarget) <=
                Math.Max(0.1, 2.0 / Math.Max(1, video.frameRate))) {
            seeking = false;
            if (resumeAfterSeek && wantPlay) {
                resumeAfterSeek = false;
            } else
                video.Pause();
        }
        SetAudioPlaying(video.isPlaying && !seeking);
        UpdateDisplayFrameRate();
        UpdateTracks();
        UpdateSubtitles();
        ApplyTextureProperties(video.texture);
    }
    public override void Render() { /* VideoPlayer owns decode and Metal texture submission. */ }
    public override void Dispose() {
        if (disposed)
            return;
        CloseMedia();
        disposed = true;
        AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
        video.prepareCompleted -= OnPrepared;
        video.seekCompleted -= OnSeekCompleted;
        video.frameReady -= OnFrame;
        video.loopPointReached -= OnEnd;
        video.errorReceived -= OnError;
        UnityEngine.Object.Destroy(host);
    }
    internal override bool
    InternalIsChangedTextCue() => false; // Sideloaded SRT remains implemented by BaseMediaPlayer.
    internal override string InternalGetCurrentTextCue() => null;
    internal override bool InternalIsChangedTracks(TrackType type) {
        int i = (int)type;
        bool changed = changedTracks[i];
        changedTracks[i] = false;
        return changed;
    }
    internal override int
    InternalGetTrackCount(TrackType type) => type == TrackType.Video ? (HasVideo() ? 1 : 0)
                                             : type == TrackType.Audio
                                                 ? (HasAudio() ? video.audioTrackCount : 0)
                                                 : 0;
    internal override bool InternalSetActiveTrack(TrackType type, int uid) {
        if (type == TrackType.Video)
            return uid == 0;
        if (type != TrackType.Audio || uid < 0 || uid >= video.controlledAudioTrackCount)
            return false;
        SetAudioPlaying(false);
        activeAudio = uid;
        for (ushort i = 0; i < video.controlledAudioTrackCount; i++)
            video.EnableAudioTrack(i, i == uid);
        audioChannelCount = video.GetAudioChannelCount((ushort)activeAudio);
        ResetAudioProvider();
        if (wantPlay)
            SetAudioPlaying(true);
        return true;
    }
    internal override TrackBase InternalGetTrackInfo(TrackType type, int index, ref bool active) {
        active = type == TrackType.Video ? index == 0 : index == activeAudio;
        if (type == TrackType.Video && index == 0)
            return new VideoTrack(0, "Video", "", true);
        if (type == TrackType.Audio && index < video.audioTrackCount)
            return new AudioTrack(index, "Audio " + (index + 1),
                                  video.GetAudioLanguageCode((ushort)index), index == 0);
        return null;
    }
}
}

// FMOD 1.10 managed ABI -> official FMOD 2.03 native Apple Silicon runtime.
// All audio, scheduling, bank interpretation and parameter evaluation use FMOD.
#define FMOD_Studio_System_Release F203_System_Release
#define FMOD_Studio_System_GetSoundInfo F203_System_GetSoundInfo
#define FMOD_Studio_System_Create F203_System_Create
#define FMOD_Studio_System_SetAdvancedSettings F203_System_SetAdvancedSettings
#define FMOD_Studio_System_GetAdvancedSettings F203_System_GetAdvancedSettings
#define FMOD_Studio_System_SetListenerAttributes F203_System_SetListenerAttributes
#define FMOD_Studio_System_GetListenerAttributes F203_System_GetListenerAttributes
#define FMOD_Studio_System_GetCPUUsage F203_System_GetCPUUsage
#define FMOD_Studio_EventInstance_Release F203_EventInstance_Release
#include "fmod.h"
#include "fmod_studio.h"
#undef FMOD_Studio_System_Release
#undef FMOD_Studio_System_GetSoundInfo
#undef FMOD_Studio_System_Create
#undef FMOD_Studio_System_SetAdvancedSettings
#undef FMOD_Studio_System_GetAdvancedSettings
#undef FMOD_Studio_System_SetListenerAttributes
#undef FMOD_Studio_System_GetListenerAttributes
#undef FMOD_Studio_System_GetCPUUsage
#undef FMOD_Studio_EventInstance_Release
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#define EXPORT extern "C" __attribute__((visibility("default")))
template <class T> T studio(const char *symbol) {
    static void *library = []() {
        Dl_info info = {};
        dladdr((const void *)&studio<T>, &info);
        std::string path = info.dli_fname;
        path = path.substr(0, path.find_last_of('/') + 1) + "libfmodstudio.dylib";
        return dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    }();
    auto function = reinterpret_cast<T>(dlsym(library, symbol));
    if (!function) {
        std::fprintf(stderr, "NativeH3VR FMOD: missing native symbol %s\n", symbol);
        std::abort();
    }
    return function;
}
EXPORT FMOD_RESULT FMOD_Studio_System_Create(FMOD_STUDIO_SYSTEM **system, unsigned int) {
    return studio<decltype(&F203_System_Create)>("FMOD_Studio_System_Create")(system, FMOD_VERSION);
}
EXPORT FMOD_RESULT FMOD5_System_Create(FMOD_SYSTEM **system) {
    return FMOD_System_Create(system, FMOD_VERSION);
}
EXPORT FMOD_RESULT FMOD5_System_GetVersion(FMOD_SYSTEM *system, unsigned int *version) {
    return FMOD_System_GetVersion(system, version, nullptr);
}
EXPORT FMOD_RESULT FMOD_Studio_System_GetLowLevelSystem(FMOD_STUDIO_SYSTEM *system,
                                                        FMOD_SYSTEM **core) {
    return FMOD_Studio_System_GetCoreSystem(system, core);
}
struct LegacyCoreAdvanced {
    int cbSize, maxMPEGCodecs, maxADPCMCodecs, maxXMACodecs, maxVorbisCodecs, maxAT9Codecs,
        maxFADPCMCodecs, maxPCMCodecs, ASIONumChannels;
    char **ASIOChannelList;
    FMOD_SPEAKER *ASIOSpeakerList;
    float HRTFMinAngle, HRTFMaxAngle, HRTFFreq, vol0virtualvol;
    unsigned defaultDecodeBufferSize;
    unsigned short profilePort;
    unsigned geometryMaxFadeTime;
    float distanceFilterCenterFreq;
    int reverb3Dinstance, DSPBufferPoolSize;
    unsigned stackSizeStream, stackSizeNonBlocking, stackSizeMixer;
    FMOD_DSP_RESAMPLER resamplerMethod;
    unsigned commandQueueSize, randomSeed;
};
#define COPY_ADVANCED(dst, src)                                                                    \
    do {                                                                                           \
        dst.maxMPEGCodecs = src.maxMPEGCodecs;                                                     \
        dst.maxADPCMCodecs = src.maxADPCMCodecs;                                                   \
        dst.maxXMACodecs = src.maxXMACodecs;                                                       \
        dst.maxVorbisCodecs = src.maxVorbisCodecs;                                                 \
        dst.maxAT9Codecs = src.maxAT9Codecs;                                                       \
        dst.maxFADPCMCodecs = src.maxFADPCMCodecs;                                                 \
        dst.ASIONumChannels = src.ASIONumChannels;                                                 \
        dst.ASIOChannelList = src.ASIOChannelList;                                                 \
        dst.ASIOSpeakerList = src.ASIOSpeakerList;                                                 \
        dst.vol0virtualvol = src.vol0virtualvol;                                                   \
        dst.defaultDecodeBufferSize = src.defaultDecodeBufferSize;                                 \
        dst.profilePort = src.profilePort;                                                         \
        dst.geometryMaxFadeTime = src.geometryMaxFadeTime;                                         \
        dst.distanceFilterCenterFreq = src.distanceFilterCenterFreq;                               \
        dst.reverb3Dinstance = src.reverb3Dinstance;                                               \
        dst.DSPBufferPoolSize = src.DSPBufferPoolSize;                                             \
        dst.resamplerMethod = src.resamplerMethod;                                                 \
        dst.randomSeed = src.randomSeed;                                                           \
    } while (0)
EXPORT FMOD_RESULT FMOD5_System_SetAdvancedSettings(FMOD_SYSTEM *system,
                                                    const LegacyCoreAdvanced *old) {
    if (!old)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_ADVANCEDSETTINGS current = {};
    current.cbSize = sizeof(current);
    COPY_ADVANCED(current, (*old));
    return FMOD_System_SetAdvancedSettings(system, &current);
}
EXPORT FMOD_RESULT FMOD5_System_GetAdvancedSettings(FMOD_SYSTEM *system, LegacyCoreAdvanced *old) {
    if (!old)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_ADVANCEDSETTINGS current = {};
    current.cbSize = sizeof(current);
    auto result = FMOD_System_GetAdvancedSettings(system, &current);
    if (result == FMOD_OK) {
        *old = {};
        old->cbSize = sizeof(*old);
        COPY_ADVANCED((*old), current);
    }
    return result;
}
struct LegacyStudioAdvanced {
    int cbsize;
    unsigned commandqueuesize, handleinitialsize;
    int studioupdateperiod, idlesampledatapoolsize;
};
EXPORT FMOD_RESULT FMOD_Studio_System_SetAdvancedSettings(FMOD_STUDIO_SYSTEM *system,
                                                          const LegacyStudioAdvanced *old) {
    if (!old)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_STUDIO_ADVANCEDSETTINGS current = {};
    std::memcpy(&current, old, sizeof(*old));
    current.cbsize = sizeof(current);
    return studio<decltype(&F203_System_SetAdvancedSettings)>(
        "FMOD_Studio_System_SetAdvancedSettings")(system, &current);
}
EXPORT FMOD_RESULT FMOD_Studio_System_GetAdvancedSettings(FMOD_STUDIO_SYSTEM *system,
                                                          LegacyStudioAdvanced *old) {
    if (!old)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_STUDIO_ADVANCEDSETTINGS current = {};
    current.cbsize = sizeof(current);
    auto result = studio<decltype(&F203_System_GetAdvancedSettings)>(
        "FMOD_Studio_System_GetAdvancedSettings")(system, &current);
    if (result == FMOD_OK) {
        std::memcpy(old, &current, sizeof(*old));
        old->cbsize = sizeof(*old);
    }
    return result;
}
EXPORT FMOD_RESULT FMOD_Studio_System_SetListenerAttributes(FMOD_STUDIO_SYSTEM *system, int index,
                                                            const FMOD_3D_ATTRIBUTES *attributes) {
    return studio<decltype(&F203_System_SetListenerAttributes)>(
        "FMOD_Studio_System_SetListenerAttributes")(system, index, attributes, nullptr);
}
EXPORT FMOD_RESULT FMOD_Studio_System_GetListenerAttributes(FMOD_STUDIO_SYSTEM *system, int index,
                                                            FMOD_3D_ATTRIBUTES *attributes) {
    return studio<decltype(&F203_System_GetListenerAttributes)>(
        "FMOD_Studio_System_GetListenerAttributes")(system, index, attributes, nullptr);
}
struct LegacyStudioCPU {
    float dspusage, streamusage, geometryusage, updateusage, studiousage;
};
EXPORT FMOD_RESULT FMOD_Studio_System_GetCPUUsage(FMOD_STUDIO_SYSTEM *system,
                                                  LegacyStudioCPU *usage) {
    if (!usage)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_STUDIO_CPU_USAGE s = {};
    FMOD_CPU_USAGE c = {};
    auto result = studio<decltype(&F203_System_GetCPUUsage)>("FMOD_Studio_System_GetCPUUsage")(
        system, &s, &c);
    if (result == FMOD_OK)
        *usage = {c.dsp, c.stream, c.geometry, c.update, s.update};
    return result;
}
EXPORT FMOD_RESULT FMOD5_System_GetCPUUsage(FMOD_SYSTEM *system, float *dsp, float *stream,
                                            float *geometry, float *update, float *total) {
    FMOD_CPU_USAGE c = {};
    auto result = FMOD_System_GetCPUUsage(system, &c);
    if (result == FMOD_OK) {
        if (dsp)
            *dsp = c.dsp;
        if (stream)
            *stream = c.stream;
        if (geometry)
            *geometry = c.geometry;
        if (update)
            *update = c.update;
        if (total)
            *total = c.dsp + c.stream + c.geometry + c.update + c.convolution1 + c.convolution2;
    }
    return result;
}
EXPORT FMOD_RESULT FMOD5_System_GetSoundRAM(FMOD_SYSTEM *, int *, int *, int *) {
    return FMOD_ERR_UNSUPPORTED;
}
// Legacy alternative pan positions were removed; retain the real position when queried.
EXPORT FMOD_RESULT FMOD5_Channel_Get3DAttributes(FMOD_CHANNEL *c, FMOD_VECTOR *p, FMOD_VECTOR *v,
                                                 FMOD_VECTOR *a) {
    auto r = FMOD_Channel_Get3DAttributes(c, p, v);
    if (r == FMOD_OK && a && p)
        *a = *p;
    return r;
}
EXPORT FMOD_RESULT FMOD5_ChannelGroup_Get3DAttributes(FMOD_CHANNELGROUP *c, FMOD_VECTOR *p,
                                                      FMOD_VECTOR *v, FMOD_VECTOR *a) {
    auto r = FMOD_ChannelGroup_Get3DAttributes(c, p, v);
    if (r == FMOD_OK && a && p)
        *a = *p;
    return r;
}
// ParameterInstance was removed in FMOD2. Handles below retain event+parameter identity;
// they are invalidated when the underlying event releases and never report fake success.
struct Parameter {
    FMOD_STUDIO_EVENTINSTANCE *event;
    FMOD_STUDIO_PARAMETER_ID id;
    int index;
};
static std::recursive_mutex parameterMutex;
static std::unordered_map<uintptr_t, Parameter> parameters;
static uintptr_t nextParameter = 1;
struct LegacyParameterDescription {
    const char *name;
    int index;
    float minimum, maximum, defaultvalue;
    FMOD_STUDIO_PARAMETER_TYPE type;
};
static void copyParameter(const FMOD_STUDIO_PARAMETER_DESCRIPTION &from, int index,
                          LegacyParameterDescription *to) {
    *to = {from.name, index, from.minimum, from.maximum, from.defaultvalue, from.type};
}
static FMOD_RESULT description(FMOD_STUDIO_EVENTINSTANCE *event,
                               FMOD_STUDIO_EVENTDESCRIPTION **desc) {
    return FMOD_Studio_EventInstance_GetDescription(event, desc);
}
EXPORT FMOD_RESULT
FMOD_Studio_EventDescription_GetParameterCount(FMOD_STUDIO_EVENTDESCRIPTION *desc, int *count) {
    return FMOD_Studio_EventDescription_GetParameterDescriptionCount(desc, count);
}
EXPORT FMOD_RESULT FMOD_Studio_EventDescription_GetParameterByIndex(
    FMOD_STUDIO_EVENTDESCRIPTION *desc, int index, LegacyParameterDescription *parameter) {
    FMOD_STUDIO_PARAMETER_DESCRIPTION current = {};
    auto result =
        FMOD_Studio_EventDescription_GetParameterDescriptionByIndex(desc, index, &current);
    if (result == FMOD_OK)
        copyParameter(current, index, parameter);
    return result;
}
static int parameterIndex(FMOD_STUDIO_EVENTDESCRIPTION *desc, FMOD_STUDIO_PARAMETER_ID id) {
    int count = 0;
    FMOD_Studio_EventDescription_GetParameterDescriptionCount(desc, &count);
    for (int i = 0; i < count; i++) {
        FMOD_STUDIO_PARAMETER_DESCRIPTION p = {};
        if (FMOD_Studio_EventDescription_GetParameterDescriptionByIndex(desc, i, &p) == FMOD_OK &&
            p.id.data1 == id.data1 && p.id.data2 == id.data2)
            return i;
    }
    return -1;
}
EXPORT FMOD_RESULT FMOD_Studio_EventDescription_GetParameter(
    FMOD_STUDIO_EVENTDESCRIPTION *desc, const char *name, LegacyParameterDescription *parameter) {
    FMOD_STUDIO_PARAMETER_DESCRIPTION current = {};
    auto result = FMOD_Studio_EventDescription_GetParameterDescriptionByName(desc, name, &current);
    if (result == FMOD_OK)
        copyParameter(current, parameterIndex(desc, current.id), parameter);
    return result;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_GetParameterCount(FMOD_STUDIO_EVENTINSTANCE *event,
                                                               int *count) {
    FMOD_STUDIO_EVENTDESCRIPTION *desc = nullptr;
    auto r = description(event, &desc);
    return r == FMOD_OK ? FMOD_Studio_EventDescription_GetParameterDescriptionCount(desc, count)
                        : r;
}
static FMOD_RESULT remember(FMOD_STUDIO_EVENTINSTANCE *event, FMOD_STUDIO_PARAMETER_DESCRIPTION p,
                            int index, void **handle) {
    if (!handle)
        return FMOD_ERR_INVALID_PARAM;
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    for (auto &entry : parameters)
        if (entry.second.event == event && entry.second.id.data1 == p.id.data1 &&
            entry.second.id.data2 == p.id.data2) {
            *handle = (void *)entry.first;
            return FMOD_OK;
        }
    auto key = nextParameter++;
    parameters.emplace(key, Parameter{event, p.id, index});
    *handle = (void *)key;
    return FMOD_OK;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_GetParameter(FMOD_STUDIO_EVENTINSTANCE *event,
                                                          const char *name, void **handle) {
    FMOD_STUDIO_EVENTDESCRIPTION *desc = nullptr;
    auto r = description(event, &desc);
    if (r != FMOD_OK)
        return r;
    FMOD_STUDIO_PARAMETER_DESCRIPTION p = {};
    r = FMOD_Studio_EventDescription_GetParameterDescriptionByName(desc, name, &p);
    return r == FMOD_OK ? remember(event, p, parameterIndex(desc, p.id), handle) : r;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_GetParameterByIndex(FMOD_STUDIO_EVENTINSTANCE *event,
                                                                 int index, void **handle) {
    FMOD_STUDIO_EVENTDESCRIPTION *desc = nullptr;
    auto r = description(event, &desc);
    if (r != FMOD_OK)
        return r;
    FMOD_STUDIO_PARAMETER_DESCRIPTION p = {};
    r = FMOD_Studio_EventDescription_GetParameterDescriptionByIndex(desc, index, &p);
    return r == FMOD_OK ? remember(event, p, index, handle) : r;
}
EXPORT bool FMOD_Studio_ParameterInstance_IsValid(void *handle) {
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    auto it = parameters.find((uintptr_t)handle);
    return it != parameters.end() && FMOD_Studio_EventInstance_IsValid(it->second.event);
}
EXPORT FMOD_RESULT FMOD_Studio_ParameterInstance_GetValue(void *handle, float *value) {
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    auto it = parameters.find((uintptr_t)handle);
    return it == parameters.end() ? FMOD_ERR_INVALID_HANDLE
                                  : FMOD_Studio_EventInstance_GetParameterByID(
                                        it->second.event, it->second.id, value, nullptr);
}
EXPORT FMOD_RESULT FMOD_Studio_ParameterInstance_SetValue(void *handle, float value) {
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    auto it = parameters.find((uintptr_t)handle);
    return it == parameters.end() ? FMOD_ERR_INVALID_HANDLE
                                  : FMOD_Studio_EventInstance_SetParameterByID(
                                        it->second.event, it->second.id, value, false);
}
EXPORT FMOD_RESULT
FMOD_Studio_ParameterInstance_GetDescription(void *handle, LegacyParameterDescription *parameter) {
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    auto it = parameters.find((uintptr_t)handle);
    if (it == parameters.end())
        return FMOD_ERR_INVALID_HANDLE;
    FMOD_STUDIO_EVENTDESCRIPTION *desc = nullptr;
    auto r = description(it->second.event, &desc);
    if (r != FMOD_OK)
        return r;
    FMOD_STUDIO_PARAMETER_DESCRIPTION p = {};
    r = FMOD_Studio_EventDescription_GetParameterDescriptionByID(desc, it->second.id, &p);
    if (r == FMOD_OK)
        copyParameter(p, it->second.index, parameter);
    return r;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_GetParameterValue(FMOD_STUDIO_EVENTINSTANCE *event,
                                                               const char *name, float *value,
                                                               float *finalvalue) {
    return FMOD_Studio_EventInstance_GetParameterByName(event, name, value, finalvalue);
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_SetParameterValue(FMOD_STUDIO_EVENTINSTANCE *event,
                                                               const char *name, float value) {
    return FMOD_Studio_EventInstance_SetParameterByName(event, name, value, false);
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_GetParameterValueByIndex(
    FMOD_STUDIO_EVENTINSTANCE *event, int index, float *value, float *finalvalue) {
    FMOD_STUDIO_EVENTDESCRIPTION *desc = nullptr;
    auto r = description(event, &desc);
    if (r != FMOD_OK)
        return r;
    FMOD_STUDIO_PARAMETER_DESCRIPTION p = {};
    r = FMOD_Studio_EventDescription_GetParameterDescriptionByIndex(desc, index, &p);
    return r == FMOD_OK ? FMOD_Studio_EventInstance_GetParameterByID(event, p.id, value, finalvalue)
                        : r;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_SetParameterValueByIndex(
    FMOD_STUDIO_EVENTINSTANCE *event, int index, float value) {
    void *handle = nullptr;
    auto r = FMOD_Studio_EventInstance_GetParameterByIndex(event, index, &handle);
    return r == FMOD_OK ? FMOD_Studio_ParameterInstance_SetValue(handle, value) : r;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_SetParameterValuesByIndices(
    FMOD_STUDIO_EVENTINSTANCE *event, int *indices, float *values, int count) {
    if (count < 0 || (!indices && count) || (!values && count))
        return FMOD_ERR_INVALID_PARAM;
    for (int i = 0; i < count; i++) {
        auto r = FMOD_Studio_EventInstance_SetParameterValueByIndex(event, indices[i], values[i]);
        if (r != FMOD_OK)
            return r;
    }
    return FMOD_OK;
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_TriggerCue(FMOD_STUDIO_EVENTINSTANCE *event) {
    return FMOD_Studio_EventInstance_KeyOff(event);
}
EXPORT FMOD_RESULT FMOD_Studio_EventDescription_HasCue(FMOD_STUDIO_EVENTDESCRIPTION *desc,
                                                       FMOD_BOOL *has) {
    return FMOD_Studio_EventDescription_HasSustainPoint(desc, has);
}
EXPORT FMOD_RESULT
FMOD_Studio_EventDescription_GetMinimumDistance(FMOD_STUDIO_EVENTDESCRIPTION *desc, float *value) {
    return FMOD_Studio_EventDescription_GetMinMaxDistance(desc, value, nullptr);
}
EXPORT FMOD_RESULT
FMOD_Studio_EventDescription_GetMaximumDistance(FMOD_STUDIO_EVENTDESCRIPTION *desc, float *value) {
    return FMOD_Studio_EventDescription_GetMinMaxDistance(desc, nullptr, value);
}
EXPORT FMOD_RESULT FMOD_Studio_EventInstance_Release(FMOD_STUDIO_EVENTINSTANCE *event) {
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    auto r =
        studio<decltype(&F203_EventInstance_Release)>("FMOD_Studio_EventInstance_Release")(event);
    if (r == FMOD_OK) {
        for (auto it = parameters.begin(); it != parameters.end();)
            if (it->second.event == event)
                it = parameters.erase(it);
            else
                ++it;
    }
    return r;
}

struct LegacyCreateSoundExInfo {
    int cbsize;
    unsigned int length;
    unsigned int fileoffset;
    int numchannels;
    int defaultfrequency;
    FMOD_SOUND_FORMAT format;
    unsigned int decodebuffersize;
    int initialsubsound;
    int numsubsounds;
    int *inclusionlist;
    int inclusionlistnum;
    FMOD_SOUND_PCMREAD_CALLBACK pcmreadcallback;
    FMOD_SOUND_PCMSETPOS_CALLBACK pcmsetposcallback;
    FMOD_SOUND_NONBLOCK_CALLBACK nonblockcallback;
    const char *dlsname;
    const char *encryptionkey;
    int maxpolyphony;
    void *userdata;
    FMOD_SOUND_TYPE suggestedsoundtype;
    FMOD_FILE_OPEN_CALLBACK fileuseropen;
    FMOD_FILE_CLOSE_CALLBACK fileuserclose;
    FMOD_FILE_READ_CALLBACK fileuserread;
    FMOD_FILE_SEEK_CALLBACK fileuserseek;
    FMOD_FILE_ASYNCREAD_CALLBACK fileuserasyncread;
    FMOD_FILE_ASYNCCANCEL_CALLBACK fileuserasynccancel;
    void *fileuserdata;
    int filebuffersize;
    FMOD_CHANNELORDER channelorder;
    FMOD_CHANNELMASK channelmask;
    FMOD_SOUNDGROUP *initialsoundgroup;
    unsigned int initialseekposition;
    FMOD_TIMEUNIT initialseekpostype;
    int ignoresetfilesystem;
    unsigned int audioqueuepolicy;
    unsigned int minmidigranularity;
    int nonblockthreadid;
    FMOD_GUID *fsbguid;
};
template <class D, class S> static void copySoundInfo(D &dst, const S &src) {
    dst.length = src.length;
    dst.fileoffset = src.fileoffset;
    dst.numchannels = src.numchannels;
    dst.defaultfrequency = src.defaultfrequency;
    dst.format = src.format;
    dst.decodebuffersize = src.decodebuffersize;
    dst.initialsubsound = src.initialsubsound;
    dst.numsubsounds = src.numsubsounds;
    dst.inclusionlist = src.inclusionlist;
    dst.inclusionlistnum = src.inclusionlistnum;
    dst.pcmreadcallback = src.pcmreadcallback;
    dst.pcmsetposcallback = src.pcmsetposcallback;
    dst.nonblockcallback = src.nonblockcallback;
    dst.dlsname = src.dlsname;
    dst.encryptionkey = src.encryptionkey;
    dst.maxpolyphony = src.maxpolyphony;
    dst.userdata = src.userdata;
    dst.suggestedsoundtype = src.suggestedsoundtype;
    dst.fileuseropen = src.fileuseropen;
    dst.fileuserclose = src.fileuserclose;
    dst.fileuserread = src.fileuserread;
    dst.fileuserseek = src.fileuserseek;
    dst.fileuserasyncread = src.fileuserasyncread;
    dst.fileuserasynccancel = src.fileuserasynccancel;
    dst.fileuserdata = src.fileuserdata;
    dst.filebuffersize = src.filebuffersize;
    dst.channelorder = src.channelorder;
    dst.initialsoundgroup = src.initialsoundgroup;
    dst.initialseekposition = src.initialseekposition;
    dst.initialseekpostype = src.initialseekpostype;
    dst.ignoresetfilesystem = src.ignoresetfilesystem;
    dst.audioqueuepolicy = src.audioqueuepolicy;
    dst.minmidigranularity = src.minmidigranularity;
    dst.nonblockthreadid = src.nonblockthreadid;
    dst.fsbguid = src.fsbguid;
    dst.cbsize = sizeof(dst);
}

static FMOD_RESULT createLegacySound(FMOD_SYSTEM *system, const char *name, FMOD_MODE mode,
                                     LegacyCreateSoundExInfo *old, FMOD_SOUND **sound,
                                     bool stream) {
    FMOD_CREATESOUNDEXINFO current = {};
    if (old) {
        // The old async callback has an additional handle argument; never invoke it with the new
        // ABI.
        if (old->fileuserasyncread || old->fileuserasynccancel || old->channelmask)
            return FMOD_ERR_UNSUPPORTED;
        copySoundInfo(current, *old);
    }
    return stream ? FMOD_System_CreateStream(system, name, mode, old ? &current : nullptr, sound)
                  : FMOD_System_CreateSound(system, name, mode, old ? &current : nullptr, sound);
}
EXPORT FMOD_RESULT FMOD5_System_CreateSound(FMOD_SYSTEM *system, const char *name, FMOD_MODE mode,
                                            LegacyCreateSoundExInfo *old, FMOD_SOUND **sound) {
    return createLegacySound(system, name, mode, old, sound, false);
}
EXPORT FMOD_RESULT FMOD5_System_CreateStream(FMOD_SYSTEM *system, const char *name, FMOD_MODE mode,
                                             LegacyCreateSoundExInfo *old, FMOD_SOUND **sound) {
    return createLegacySound(system, name, mode, old, sound, true);
}
struct LegacyStudioSoundInfo {
    const char *name_or_data;
    FMOD_MODE mode;
    LegacyCreateSoundExInfo exinfo;
    int subsoundindex;
};
EXPORT FMOD_RESULT FMOD_Studio_System_GetSoundInfo(FMOD_STUDIO_SYSTEM *system, const char *key,
                                                   LegacyStudioSoundInfo *old) {
    if (!old)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_STUDIO_SOUND_INFO current = {};
    auto r = studio<decltype(&F203_System_GetSoundInfo)>("FMOD_Studio_System_GetSoundInfo")(
        system, key, &current);
    if (r == FMOD_OK) {
        old->name_or_data = current.name_or_data;
        old->mode = current.mode;
        old->exinfo = {};
        copySoundInfo(old->exinfo, current.exinfo);
        old->subsoundindex = current.subsoundindex;
    }
    return r;
}
EXPORT FMOD_RESULT FMOD5_DSP_GetParameterInfo(FMOD_DSP *dsp, int index,
                                              FMOD_DSP_PARAMETER_DESC *desc) {
    if (!desc)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_DSP_PARAMETER_DESC *native = nullptr;
    auto r = FMOD_DSP_GetParameterInfo(dsp, index, &native);
    if (r == FMOD_OK && native)
        *desc = *native;
    return r;
}
EXPORT FMOD_RESULT FMOD_Studio_System_Release(FMOD_STUDIO_SYSTEM *system) {
    std::lock_guard<std::recursive_mutex> lock(parameterMutex);
    std::vector<uintptr_t> dead;
    for (auto &entry : parameters) {
        FMOD_STUDIO_SYSTEM *owner = nullptr;
        if (FMOD_Studio_EventInstance_GetSystem(entry.second.event, &owner) != FMOD_OK ||
            owner == system)
            dead.push_back(entry.first);
    }
    auto r = studio<decltype(&F203_System_Release)>("FMOD_Studio_System_Release")(system);
    if (r == FMOD_OK)
        for (auto id : dead)
            parameters.erase(id);
    return r;
}
EXPORT FMOD_RESULT FMOD5_System_SetFileSystem(FMOD_SYSTEM *system, FMOD_FILE_OPEN_CALLBACK open,
                                              FMOD_FILE_CLOSE_CALLBACK close,
                                              FMOD_FILE_READ_CALLBACK read,
                                              FMOD_FILE_SEEK_CALLBACK seek, void *asyncRead,
                                              void *asyncCancel, int align) {
    if (asyncRead || asyncCancel)
        return FMOD_ERR_UNSUPPORTED;
    return FMOD_System_SetFileSystem(system, open, close, read, seek, nullptr, nullptr, align);
}

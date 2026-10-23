// FMOD 1.10 OUTPUTTYPE numeric values differ from FMOD 2.03 after value5.
// Preserve original managed enum semantics and pass every operation to real FMOD.
#include "fmod.h"
#define EXPORT extern "C" __attribute__((visibility("default")))
EXPORT FMOD_RESULT FMOD5_System_SetOutput(FMOD_SYSTEM *system, int legacy) {
    FMOD_OUTPUTTYPE out;
    if (legacy >= 0 && legacy <= 5)
        out = (FMOD_OUTPUTTYPE)legacy;
    else
        switch (legacy) {
        case 8:
            out = FMOD_OUTPUTTYPE_WASAPI;
            break;
        case 9:
            out = FMOD_OUTPUTTYPE_ASIO;
            break;
        case 10:
            out = FMOD_OUTPUTTYPE_PULSEAUDIO;
            break;
        case 11:
            out = FMOD_OUTPUTTYPE_ALSA;
            break;
        case 12:
            out = FMOD_OUTPUTTYPE_COREAUDIO;
            break;
        case 15:
            out = FMOD_OUTPUTTYPE_AUDIOTRACK;
            break;
        case 16:
            out = FMOD_OUTPUTTYPE_OPENSL;
            break;
        case 18:
            out = FMOD_OUTPUTTYPE_AUDIOOUT;
            break;
        case 19:
            out = FMOD_OUTPUTTYPE_AUDIO3D;
            break;
        case 21:
            out = FMOD_OUTPUTTYPE_WEBAUDIO;
            break;
        case 22:
            out = FMOD_OUTPUTTYPE_NNAUDIO;
            break;
        case 23:
            out = FMOD_OUTPUTTYPE_WINSONIC;
            break;
        case 6:
        case 7:
        case 13:
        case 14:
        case 17:
        case 20:
            return FMOD_ERR_UNSUPPORTED;
        default:
            return FMOD_ERR_INVALID_PARAM;
        }
    return FMOD_System_SetOutput(system, out);
}
EXPORT FMOD_RESULT FMOD5_System_GetOutput(FMOD_SYSTEM *system, int *legacy) {
    if (!legacy)
        return FMOD_ERR_INVALID_PARAM;
    FMOD_OUTPUTTYPE out;
    auto result = FMOD_System_GetOutput(system, &out);
    if (result != FMOD_OK)
        return result;
    if (out <= FMOD_OUTPUTTYPE_WAVWRITER_NRT)
        *legacy = (int)out;
    else
        switch (out) {
        case FMOD_OUTPUTTYPE_WASAPI:
            *legacy = 8;
            break;
        case FMOD_OUTPUTTYPE_ASIO:
            *legacy = 9;
            break;
        case FMOD_OUTPUTTYPE_PULSEAUDIO:
            *legacy = 10;
            break;
        case FMOD_OUTPUTTYPE_ALSA:
            *legacy = 11;
            break;
        case FMOD_OUTPUTTYPE_COREAUDIO:
            *legacy = 12;
            break;
        case FMOD_OUTPUTTYPE_AUDIOTRACK:
            *legacy = 15;
            break;
        case FMOD_OUTPUTTYPE_OPENSL:
            *legacy = 16;
            break;
        case FMOD_OUTPUTTYPE_AUDIOOUT:
            *legacy = 18;
            break;
        case FMOD_OUTPUTTYPE_AUDIO3D:
            *legacy = 19;
            break;
        case FMOD_OUTPUTTYPE_WEBAUDIO:
            *legacy = 21;
            break;
        case FMOD_OUTPUTTYPE_NNAUDIO:
            *legacy = 22;
            break;
        case FMOD_OUTPUTTYPE_WINSONIC:
            *legacy = 23;
            break;
        default:
            *legacy = 1;
            break; // UNKNOWN: modern output without an equivalent old enum.
        }
    return result;
}

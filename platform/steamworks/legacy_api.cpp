// Bridge SDK 1.41 entry points to Valve's installed native arm64 Steam API.
// Every success value is returned by Steam. No ownership/authentication emulation.
#include "CSteamworks.h"
#include <cstdio>
#include <cstdlib>
#include <dlfcn.h>
extern "C" int SteamInternal_SteamAPI_Init(const char *, char *);
extern "C" int SteamInternal_GameServer_Init_V2(uint32, uint16, uint16, EServerMode, const char *,
                                                const char *, char *);
static const char clientVersions[] = STEAMUTILS_INTERFACE_VERSION
    "\0" STEAMAPPS_INTERFACE_VERSION "\0" STEAMCONTROLLER_INTERFACE_VERSION
    "\0" STEAMFRIENDS_INTERFACE_VERSION "\0" STEAMHTMLSURFACE_INTERFACE_VERSION
    "\0" STEAMHTTP_INTERFACE_VERSION "\0" STEAMINVENTORY_INTERFACE_VERSION
    "\0" STEAMMATCHMAKINGSERVERS_INTERFACE_VERSION "\0" STEAMMATCHMAKING_INTERFACE_VERSION
    "\0" STEAMMUSIC_INTERFACE_VERSION "\0" STEAMNETWORKING_INTERFACE_VERSION
    "\0" STEAMREMOTESTORAGE_INTERFACE_VERSION "\0" STEAMSCREENSHOTS_INTERFACE_VERSION
    "\0" STEAMUGC_INTERFACE_VERSION "\0" STEAMUSERSTATS_INTERFACE_VERSION
    "\0" STEAMUSER_INTERFACE_VERSION "\0" STEAMVIDEO_INTERFACE_VERSION "\0\0";
extern "C" bool SteamAPI_Init() {
    char error[1024] = {};
    int result = SteamInternal_SteamAPI_Init(clientVersions, error);
    if (result != 0)
        std::fprintf(stderr, "NativeH3VR Steam init failed (%d): %s\n", result, error);
    return result == 0;
}
extern "C" bool SteamInternal_GameServer_Init(uint32 ip, uint16 /* obsoleteSteamPort */,
                                              uint16 gamePort, uint16 queryPort, EServerMode mode,
                                              const char *version) {
    const char versions[] = STEAMUTILS_INTERFACE_VERSION
        "\0" STEAMGAMESERVER_INTERFACE_VERSION "\0" STEAMGAMESERVERSTATS_INTERFACE_VERSION
        "\0" STEAMHTTP_INTERFACE_VERSION "\0" STEAMINVENTORY_INTERFACE_VERSION
        "\0" STEAMNETWORKING_INTERFACE_VERSION "\0" STEAMUGC_INTERFACE_VERSION "\0\0";
    char error[1024] = {};
    int result =
        SteamInternal_GameServer_Init_V2(ip, gamePort, queryPort, mode, version, versions, error);
    if (result != 0)
        std::fprintf(stderr, "NativeH3VR Steam server init failed (%d): %s\n", result, error);
    return result == 0;
}
extern "C" HSteamUser Steam_GetHSteamUserCurrent() { return SteamAPI_GetHSteamUser(); }
extern "C" void Steam_RunCallbacks(HSteamPipe pipe, bool server) {
    HSteamPipe expected = server ? SteamGameServer_GetHSteamPipe() : SteamAPI_GetHSteamPipe();
    if (pipe != expected) {
        std::fprintf(stderr,
                     "NativeH3VR unsupported private Steam callback pipe %d (expected %d)\n", pipe,
                     expected);
        std::abort();
    }
    if (server)
        SteamGameServer_RunCallbacks();
    else
        SteamAPI_RunCallbacks();
}
extern "C" void Steam_RegisterInterfaceFuncs(void *module) {
    // Obsolete private SDK API: dynamically forward when the module provides it.
    // No recovered managed caller invokes this import. Do not silently pretend it works.
    auto function =
        reinterpret_cast<void (*)(void *)>(dlsym(module, "Steam_RegisterInterfaceFuncs"));
    if (!function || function == &Steam_RegisterInterfaceFuncs) {
        std::fprintf(stderr, "NativeH3VR: obsolete private Steam_RegisterInterfaceFuncs is "
                             "unavailable in this native Steam runtime\n");
        std::abort();
    }
    function(module);
}

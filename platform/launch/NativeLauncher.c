// Native app entry point. Establish the official Steam development AppID before
// executing Unity so RestartAppIfNecessary does not launch a different platform build.
#include <limits.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#ifndef NATIVEH3VR_PLAYER_EXECUTABLE
#define NATIVEH3VR_PLAYER_EXECUTABLE "NewEngineH3VRPlayer"
#endif
int main(int argc, char **argv) {
    char executable[PATH_MAX], resolved[PATH_MAX], player[PATH_MAX], resources[PATH_MAX];
    uint32_t size = sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0 || !realpath(executable, resolved)) {
        perror("NewEngineH3VR launcher path");
        return 1;
    }
    char *slash = strrchr(resolved, '/');
    if (!slash)
        return 1;
    *slash = 0;
    if (snprintf(player, sizeof(player), "%s/%s", resolved, NATIVEH3VR_PLAYER_EXECUTABLE) >=
        sizeof(player))
        return 1;
    if (snprintf(resources, sizeof(resources), "%s/../Resources", resolved) >= sizeof(resources))
        return 1;
    const char *mode = getenv("NATIVEH3VR_OPENXR");
    if (!mode || !*mode) {
        if (setenv("NATIVEH3VR_OPENXR", "live", 1)) {
            perror("NewEngineH3VR runtime environment");
            return 1;
        }
    } else if (strcmp(mode, "live") && strcmp(mode, "synthetic")) {
        fprintf(stderr, "NewEngineH3VR runtime mode must be live or synthetic.\n");
        return 1;
    }
    if (setenv("SteamAppId", "450540", 1) || setenv("SteamGameId", "450540", 1)) {
        perror("NewEngineH3VR Steam environment");
        return 1;
    }
    if (chdir(resources)) {
        perror("NewEngineH3VR resource directory");
        return 1;
    }
    argv[0] = player;
    execv(player, argv);
    perror("NewEngineH3VR Unity player");
    return 1;
}

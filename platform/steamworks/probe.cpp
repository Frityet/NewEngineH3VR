#include "CSteamworks.h"
#include <cstdio>
#include <unistd.h>
extern "C" bool Init();
extern "C" bool RestartAppIfNecessary(uint32);
extern "C" void Shutdown();
extern "C" bool ISteamUser_BLoggedOn();
extern "C" bool ISteamApps_BIsSubscribed();
extern "C" bool ISteamApps_BIsSubscribedApp(AppId_t);
extern "C" AppId_t ISteamUtils_GetAppID();
extern "C" bool ISteamUserStats_RequestCurrentStats();
static int callbacks = 0;
class StatsCallback : public CCallbackBase {
  public:
    void Run(void *data) override {
        auto *stats = static_cast<UserStatsReceived_t *>(data);
        ++callbacks;
        std::printf("stats_callback_result=%d app=%llu\n", static_cast<int>(stats->m_eResult),
                    stats->m_nGameID);
    }
    void Run(void *data, bool failure, SteamAPICall_t) override {
        if (!failure)
            Run(data);
    }
    int GetCallbackSizeBytes() override { return sizeof(UserStatsReceived_t); }
};
int main() {
    std::printf("architecture=arm64 pack_sentinel=%zu subscribed_files_size=%zu\n",
                sizeof(ValvePackingSentinel_t),
                sizeof(RemoteStorageEnumerateUserSubscribedFilesResult_t));
    bool restart = RestartAppIfNecessary(450540);
    std::printf("restart_required=%s\n", restart ? "true" : "false");
    if (restart)
        return 5;
    bool init = Init();
    std::printf("initialized=%s\n", init ? "true" : "false");
    if (!init)
        return 2;
    bool logged = ISteamUser_BLoggedOn();
    bool subscribed = ISteamApps_BIsSubscribed();
    bool owns = ISteamApps_BIsSubscribedApp(450540);
    auto app = ISteamUtils_GetAppID();
    std::printf("logged_on=%s subscribed=%s owns_450540=%s app_id=%u\n", logged ? "true" : "false",
                subscribed ? "true" : "false", owns ? "true" : "false", app);
    StatsCallback callback;
    SteamAPI_RegisterCallback(&callback, UserStatsReceived_t::k_iCallback);
    bool requested = ISteamUserStats_RequestCurrentStats();
    std::printf("stats_requested=%s\n", requested ? "true" : "false");
    for (int i = 0; i < 100 && !callbacks; i++) {
        SteamAPI_RunCallbacks();
        usleep(100000);
    }
    SteamAPI_UnregisterCallback(&callback);
    Shutdown();
    std::printf("callbacks_received=%d\n", callbacks);
    return logged && subscribed && owns && app == 450540 ? 0 : 3;
}

// Source-injected unit test: exercises the actual bridge query/status functions
// without creating an OpenXR instance, Unity process, or Metal device.
#include <cassert>
#include <cstdio>
#include <cstring>
#ifndef NH3VR_XR_BRIDGE_SOURCE
#define NH3VR_XR_BRIDGE_SOURCE "../src/xr_bridge.mm"
#endif
#include NH3VR_XR_BRIDGE_SOURCE

static XrResult locateResult;
static XrViewStateFlags locateFlags;
static uint32_t locateCount, locateCalls;
static constexpr auto bothValid =
    XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT;

static XrResult XRAPI_PTR locateFixture(XrSession queriedSession, const XrViewLocateInfo *info,
                                        XrViewState *state, uint32_t capacity, uint32_t *count,
                                        XrView *output) {
    ++locateCalls;
    assert(queriedSession == session && capacity == 2);
    assert(info->type == XR_TYPE_VIEW_LOCATE_INFO);
    assert(info->viewConfigurationType == XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO);
    assert(info->space == viewSpace && info->displayTime == frameState.predictedDisplayTime);
    assert(state->type == XR_TYPE_VIEW_STATE);
    state->viewStateFlags = locateFlags;
    *count = locateCount;
    for (uint32_t i = 0; i < 2; i++) {
        assert(output[i].type == XR_TYPE_VIEW);
        output[i].pose.position = {float(i) + 1, 2, 3};
        output[i].pose.orientation = {.1f, .2f, .3f, .9f};
        output[i].fov = {-.6f, .7f, .8f, -.9f};
    }
    return locateResult;
}
static void resetFixture() {
    session = reinterpret_cast<XrSession>(uintptr_t(1));
    viewSpace = reinterpret_cast<XrSpace>(uintptr_t(2));
    pLocateViews = locateFixture;
    frameState = {};
    frameState.type = XR_TYPE_FRAME_STATE;
    frameState.predictedDisplayTime = 123456789;
    frameState.shouldRender = XR_FALSE;
    locateResult = XR_SUCCESS;
    locateFlags = bothValid;
    locateCount = 2;
    locateCalls = 0;
    lastStatus = 0;
    error = "retained diagnostic";
    pending = false;
    active = false;
    running = true;
    sessionState = XR_SESSION_STATE_FOCUSED;
    completed = 17;
}
static void untouched(const nh3vr_xr_view &left, const nh3vr_xr_view &right,
                      const nh3vr_xr_view &sentinel) {
    assert(std::memcmp(&left, &sentinel, sizeof(left)) == 0);
    assert(std::memcmp(&right, &sentinel, sizeof(right)) == 0);
}
static void preservedState(int32_t expectedStatus, const char *expectedError,
                           bool wasPending = false) {
    uint64_t count = 0;
    assert(lastStatus == expectedStatus && error == expectedError);
    assert(nh3vr_xr_status(&count) == (wasPending ? NH3VR_XR_BUSY : expectedStatus));
    assert(count == 17 && pending == wasPending && !active);
    assert(frameState.predictedDisplayTime == 123456789 && !frameState.shouldRender);
    assert(nh3vr_xr_focused() == 1);
}
int main() {
    nh3vr_xr_view sentinel;
    std::memset(&sentinel, 0x5a, sizeof(sentinel));
    // Validity is independent of tracked bits: orientation AND position must
    // both be valid; temporarily untracked but valid calibration is accepted.
    const XrViewStateFlags invalidFlags[] = {
        0, XR_VIEW_STATE_ORIENTATION_VALID_BIT, XR_VIEW_STATE_POSITION_VALID_BIT,
        XR_VIEW_STATE_ORIENTATION_TRACKED_BIT | XR_VIEW_STATE_POSITION_TRACKED_BIT};
    for (auto flags : invalidFlags)
        for (bool priorFatal : {false, true})
            for (bool busy : {false, true}) {
                resetFixture();
                locateFlags = flags;
                pending = busy;
                if (priorFatal)
                    fail("prior GPU failure", -1777);
                const int32_t oldStatus = lastStatus;
                const std::string oldError = error;
                nh3vr_xr_view left = sentinel, right = sentinel;
                assert(nh3vr_xr_eye_views(&left, &right) == -1017);
                assert(locateCalls == 1);
                untouched(left, right, sentinel);
                preservedState(oldStatus, oldError.c_str(), busy);
                // A subsequent valid sample recovers eye data, while preserving any
                // independent fatal submission status for the real status consumer.
                locateFlags = bothValid;
                assert(nh3vr_xr_eye_views(&left, &right) == 0 && locateCalls == 2);
                for (const auto *eye : {&left, &right}) {
                    assert(eye->pose.position[1] == 2 && eye->pose.position[2] == -3);
                    assert(eye->pose.rotation_xyzw[0] == -.1f &&
                           eye->pose.rotation_xyzw[1] == -.2f);
                    assert(eye->pose.rotation_xyzw[2] == .3f && eye->pose.rotation_xyzw[3] == .9f);
                    assert(eye->fov_left == -.6f && eye->fov_right == .7f && eye->fov_up == .8f &&
                           eye->fov_down == -.9f);
                }
                assert(left.pose.position[0] == 1 && right.pose.position[0] == 2);
                preservedState(oldStatus, oldError.c_str(), busy);
            }
    // A malformed stereo result is an invariant violation, not tracking loss.
    for (uint32_t count : {0u, 1u, 3u}) {
        resetFixture();
        locateCount = count;
        nh3vr_xr_view left = sentinel, right = sentinel;
        assert(nh3vr_xr_eye_views(&left, &right) == -1017);
        assert(nh3vr_xr_status(nullptr) == -1017);
        assert(error == "eye view count invalid (-1017)");
        untouched(left, right, sentinel);
    }
    // Real OpenXR failures still latch the original API result, even when its
    // otherwise unspecified outputs look like transient tracking loss.
    resetFixture();
    locateResult = XR_ERROR_SESSION_LOST;
    locateFlags = 0;
    locateCount = 0;
    nh3vr_xr_view left = sentinel, right = sentinel;
    assert(nh3vr_xr_eye_views(&left, &right) == XR_ERROR_SESSION_LOST);
    assert(nh3vr_xr_status(nullptr) == XR_ERROR_SESSION_LOST);
    assert(error.find("pLocateViews") != std::string::npos);
    untouched(left, right, sentinel);
    // Reject invalid initialization and output pointers before calling OpenXR.
    for (int invalid = 0; invalid < 6; invalid++) {
        resetFixture();
        left = right = sentinel;
        if (invalid == 0)
            session = XR_NULL_HANDLE;
        if (invalid == 1)
            viewSpace = XR_NULL_HANDLE;
        if (invalid == 2)
            frameState.predictedDisplayTime = 0;
        if (invalid == 3)
            pLocateViews = nullptr;
        assert(nh3vr_xr_eye_views(invalid == 4 ? nullptr : &left,
                                  invalid == 5 ? nullptr : &right) == -1016);
        assert(locateCalls == 0 && nh3vr_xr_status(nullptr) == -1016);
        assert(error == "eye views unavailable (-1016)");
        untouched(left, right, sentinel);
    }
    std::puts("Eye query regression passed: 16 transient/recovery combinations; 3 invalid counts; "
              "API failure; 6 invalid initializations. No OpenXR/Metal session created.");
}

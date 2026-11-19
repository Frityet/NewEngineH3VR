// NativeH3VR Unity Metal/OpenXR client ABI. Poses use OpenXR coordinates.
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    float x, y, z;
} NhxrVector;

typedef struct {
    float x, y, z, w;
} NhxrRotation;

typedef struct {
    NhxrVector position;
    NhxrRotation rotation;
} NhxrPose;

typedef struct {
    NhxrPose pose;
    float left, right, up, down;
} NhxrView;

typedef struct {
    uint64_t sequence;
    int64_t displayTime, displayPeriod;
    uint32_t width, height, focused, reserved;
    NhxrPose head;
    NhxrView left, right;
} NhxrFrame;

enum {
    NHXR_CONNECTED = 1, NHXR_POSE_VALID = 2, NHXR_POSE_TRACKED = 4,
    NHXR_VELOCITY_VALID = 8, NHXR_INPUT_CURRENT = 16
};

enum {
    NHXR_PROFILE_UNKNOWN = 0, NHXR_PROFILE_VALVE_INDEX = 1
};

typedef struct {
    uint32_t flags, buttons, touches, profile;
    NhxrPose grip, aim;
    NhxrVector velocity, angularVelocity;
    float trigger, squeeze, squeezeForce, trackpadForce;
    float trackpadX, trackpadY, thumbstickX, thumbstickY;
} NhxrHand;

typedef struct {
    uint64_t completed, skipped;
    uint64_t completedSequence, unityGpuNs, endFrameNs;
    uint32_t active, focused, width, height;
} NhxrStatus;

typedef struct {
    float foreground[4], background[4], grid;
} NhxrOverlay;

uint32_t nhxr_frame_size(void);
uint32_t nhxr_hand_size(void);
const char *nhxr_last_error(void);
const char *nhxr_system_name(void);
int nhxr_open(const char *loaderPath, uint32_t requestedWidth, uint32_t requestedHeight);
// 0 requires two eye renders; 1 means retry, 2 a session that has exited, negatives are errors.
int nhxr_begin(NhxrFrame *frame);
// The next event records the two copies on Unity's current Metal command buffer.
int nhxr_queue(uint64_t sequence, void *left, void *right, const NhxrOverlay *overlay);
void *nhxr_render_event(void);
int nhxr_cancel(void);
int nhxr_status(NhxrStatus *status);
int nhxr_hand(uint32_t hand, NhxrHand *value);
int nhxr_stage_bounds(float *width, float *depth);
int nhxr_haptic(uint32_t hand, float amplitude, float frequency, float seconds);
// 1 means the render callback or its command buffer still owns the frame.
int nhxr_close(void);

#ifdef __cplusplus
}
#endif

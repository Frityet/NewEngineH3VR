#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

#define NH3VR_TRACKING_ABI 1u
#define NH3VR_MAX_DEVICES 16u
#if defined(__GNUC__)
#define NH3VR_API __attribute__((visibility("default")))
#else
#define NH3VR_API
#endif

enum nh3vr_result {
    NH3VR_OK = 0,
    NH3VR_INVALID_ARGUMENT = -1,
    NH3VR_BROKER_UNAVAILABLE = -2,
    NH3VR_PROTOCOL_ERROR = -3,
    NH3VR_TRANSPORT_ERROR = -4,
    NH3VR_INVALID_SAMPLE = -5
};
enum nh3vr_device_role { NH3VR_OTHER = 0, NH3VR_HEAD = 1, NH3VR_LEFT = 2, NH3VR_RIGHT = 3 };
enum nh3vr_device_flags {
    NH3VR_CONNECTED = 1,
    NH3VR_POSE_VALID = 2,
    NH3VR_POSE_TRACKED = 4,
    NH3VR_VELOCITY_VALID = 8,
    NH3VR_INPUT_CURRENT = 16,
    NH3VR_OPTICAL_CURRENT = 32,
    NH3VR_CONTROLLER_OFFSETS = 64
};
enum nh3vr_floor_state {
    NH3VR_FLOOR_UNSET = 0,
    NH3VR_FLOOR_PENDING = 1,
    NH3VR_FLOOR_READY = 2,
    NH3VR_FLOOR_MISMATCH = 3
};
enum nh3vr_snapshot_flags { NH3VR_RUNNING = 1, NH3VR_SIMULATION = 2, NH3VR_PLAYBACK = 4 };

/* Unity convention: metres, Y up, Z forward; quaternion x,y,z,w.
 * With an active native XR session, poses/velocities use its STAGE reference
 * space and frame sample time, matching the rendered eyes. Outside a session,
 * no prediction or recenter is applied: floor_y is subtracted when READY,
 * otherwise positions retain the tracking origin.
 * tracking_pose is the device tracking origin, not necessarily eye/head centre.
 * grip_pose/aim_pose are available only with CONTROLLER_OFFSETS. */
typedef struct {
    float position[3];
    float rotation_xyzw[4];
} nh3vr_pose;
typedef struct {
    uint32_t role, flags;
    char name[16], serial[32];
    nh3vr_pose tracking_pose, grip_pose, aim_pose;
    float linear_velocity[3], angular_velocity[3];
    double pose_age_seconds, optical_age_seconds;
    uint32_t buttons, touches;
    float axes[16];
} nh3vr_device;
typedef struct {
    uint32_t abi_version, struct_size, flags, device_count;
    uint32_t floor_state, reserved;
    double monotonic_seconds, floor_y;
    nh3vr_device devices[NH3VR_MAX_DEVICES];
} nh3vr_tracking_snapshot;

NH3VR_API uint32_t nh3vr_tracking_abi(void);
NH3VR_API uint32_t nh3vr_tracking_snapshot_size(void);
/* Reads the running Virtual Reality broker only; never opens USB, starts a
 * driver, modifies calibration, emits haptics, or acquires headset output.
 * socket_path=NULL selects VR_TRACKING_SOCKET or the runtime's per-user path.
 * On any failure out is cleared (when its exact size is supplied). */
NH3VR_API int32_t nh3vr_read_tracking(const char *socket_path, nh3vr_tracking_snapshot *out,
                                      uint32_t out_size);
NH3VR_API const char *nh3vr_result_string(int32_t result);

#ifdef __cplusplus
}
#endif

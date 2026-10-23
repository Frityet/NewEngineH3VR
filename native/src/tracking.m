#import "IVRoomCalibration.h"
#include "broker_protocol.h"
#include "nativeh3vr_tracking.h"
#include "nativeh3vr_xr.h"
#include "openxr/IVXRControllerPoses.h"
#import <Foundation/Foundation.h>
#include <errno.h>
#include <math.h>
#include <poll.h>
#include <simd/simd.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

_Static_assert(IDX_ABI_VERSION == 2, "Reaudit the upstream tracking ABI");
_Static_assert(IDX_MAX_DEVICES == NH3VR_MAX_DEVICES, "Device capacity mismatch");

static double now_seconds(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}
static BOOL transfer(int fd, void *buffer, size_t size, BOOL sending, double deadline) {
    unsigned char *p = buffer;
    while (size) {
        double remain = deadline - now_seconds();
        if (remain <= 0)
            return NO;
        struct pollfd wait = {.fd = fd, .events = sending ? POLLOUT : POLLIN};
        int ready = poll(&wait, 1, (int)ceil(remain * 1000));
        if (ready < 0 && errno == EINTR)
            continue;
        if (ready <= 0)
            return NO;
        ssize_t n = sending ? send(fd, p, size, MSG_DONTWAIT) : recv(fd, p, size, MSG_DONTWAIT);
        if (n < 0 && (errno == EINTR || errno == EAGAIN))
            continue;
        if (n <= 0)
            return NO;
        p += n;
        size -= (size_t)n;
    }
    return YES;
}
static int read_broker(const char *path, nh3vr_ipc_reply *reply) {
    struct sockaddr_un addr = {.sun_family = AF_UNIX};
    if (!path || !*path || strlen(path) >= sizeof(addr.sun_path))
        return NH3VR_INVALID_ARGUMENT;
    strlcpy(addr.sun_path, path, sizeof(addr.sun_path));
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return NH3VR_TRANSPORT_ERROR;
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof(one));
    struct timeval timeout = {.tv_sec = 0, .tv_usec = 200000};
    setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));
    int result = NH3VR_BROKER_UNAVAILABLE;
    if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) == 0) {
        uid_t uid;
        gid_t gid;
        result = NH3VR_PROTOCOL_ERROR;
        if (getpeereid(fd, &uid, &gid) == 0 && uid == getuid()) {
            nh3vr_ipc_request request = {.magic = NH3VR_IPC_MAGIC,
                                         .version = NH3VR_IPC_VERSION,
                                         .operation = 1,
                                         .size = sizeof(request)};
            double deadline = now_seconds() + .2;
            result = NH3VR_TRANSPORT_ERROR;
            if (transfer(fd, &request, sizeof(request), YES, deadline) &&
                transfer(fd, reply, sizeof(*reply), NO, deadline))
                result = NH3VR_OK;
        }
    }
    close(fd);
    return result;
}
static double sample_age(double now, double time) {
    return isfinite(time) && time > 0 && time <= now ? now - time : INFINITY;
}
static BOOL finite_vector(const double *p, int n) {
    for (int i = 0; i < n; i++)
        if (!isfinite(p[i]))
            return NO;
    return YES;
}
static nh3vr_pose unity_pose(simd_float3 position, simd_quatf rotation) {
    return (nh3vr_pose){.position = {position.x, position.y, -position.z},
                        .rotation_xyzw = {-rotation.vector.x, -rotation.vector.y, rotation.vector.z,
                                          rotation.vector.w}};
}
static nh3vr_pose controller_pose(simd_float3 position, simd_quatf rotation, BOOL right, BOOL aim) {
    XrPosef offset = IVXRIndexControllerOffset(right, aim);
    simd_float3 op = {offset.position.x, offset.position.y, offset.position.z};
    simd_quatf oq = simd_quaternion((simd_float4){offset.orientation.x, offset.orientation.y,
                                                  offset.orientation.z, offset.orientation.w});
    return unity_pose(position + simd_act(rotation, op), simd_mul(rotation, oq));
}
uint32_t nh3vr_tracking_abi(void) { return NH3VR_TRACKING_ABI; }
uint32_t nh3vr_tracking_snapshot_size(void) { return sizeof(nh3vr_tracking_snapshot); }
const char *nh3vr_result_string(int32_t result) {
    switch (result) {
    case NH3VR_OK:
        return "Tracking snapshot received; inspect per-device validity and floor state";
    case NH3VR_INVALID_ARGUMENT:
        return "Invalid argument or snapshot ABI size";
    case NH3VR_BROKER_UNAVAILABLE:
        return "Virtual Reality tracking broker unavailable; this plugin never starts it";
    case NH3VR_PROTOCOL_ERROR:
        return "Tracking broker protocol, peer identity, or snapshot ABI mismatch";
    case NH3VR_TRANSPORT_ERROR:
        return "Tracking broker transfer failed or exceeded 200ms deadline";
    case NH3VR_INVALID_SAMPLE:
        return "Tracking broker returned invalid data";
    default:
        return "Unknown NativeH3VR tracking error";
    }
}
int32_t nh3vr_read_tracking(const char *socket_path, nh3vr_tracking_snapshot *out,
                            uint32_t out_size) {
    if (!out || out_size != sizeof(*out))
        return NH3VR_INVALID_ARGUMENT;
    memset(out, 0, sizeof(*out));
    @autoreleasepool {
        NSString *defaultPath = nil;
        if (!socket_path) {
            const char *override = getenv("VR_TRACKING_SOCKET");
            defaultPath =
                override && *override
                    ? [NSString stringWithUTF8String:override]
                    : [NSTemporaryDirectory()
                          stringByAppendingPathComponent:
                              [NSString stringWithFormat:@"virtual-reality-%u.sock", getuid()]];
            socket_path = defaultPath.fileSystemRepresentation;
        }
        nh3vr_ipc_reply reply = {0};
        int result = read_broker(socket_path, &reply);
        if (result)
            return result;
        idx_snapshot *source = &reply.snapshot;
        if (reply.magic != NH3VR_IPC_MAGIC || reply.version != NH3VR_IPC_VERSION ||
            reply.size != sizeof(reply) || reply.result || source->abi_version != IDX_ABI_VERSION ||
            !memchr(reply.state_directory, 0, sizeof(reply.state_directory)))
            return NH3VR_PROTOCOL_ERROR;
        NSString *directory = [NSString stringWithUTF8String:reply.state_directory];
        if (!directory.isAbsolutePath)
            return NH3VR_PROTOCOL_ERROR;
        if (source->device_count < 0 || source->device_count > IDX_MAX_DEVICES ||
            source->lighthouse_count < 0 || source->lighthouse_count > IDX_MAX_LIGHTHOUSES ||
            !isfinite(source->now))
            return NH3VR_INVALID_SAMPLE;
        out->abi_version = NH3VR_TRACKING_ABI;
        out->struct_size = sizeof(*out);
        out->monotonic_seconds = now_seconds();
        out->device_count = source->device_count;
        out->flags = (source->running ? NH3VR_RUNNING : 0) |
                     (source->simulation ? NH3VR_SIMULATION : 0) |
                     (source->playback ? NH3VR_PLAYBACK : 0);
        double floor = 0;
        out->floor_state =
            (uint32_t)IVResolveRoomFloor(IVReadRoomCalibration(directory), source, &floor);
        out->floor_y = out->floor_state == NH3VR_FLOOR_READY ? floor : 0;
        for (uint32_t i = 0; i < out->device_count; i++) {
            const idx_device *d = &source->devices[i];
            nh3vr_device *dest = &out->devices[i];
            dest->role = d->type == 1                      ? NH3VR_HEAD
                         : d->type == 2 && d->subtype == 5 ? NH3VR_LEFT
                         : d->type == 2 && d->subtype == 4 ? NH3VR_RIGHT
                                                           : NH3VR_OTHER;
            memcpy(dest->name, d->name, sizeof(dest->name) - 1);
            memcpy(dest->serial, d->serial, sizeof(dest->serial) - 1);
            dest->flags = d->connected ? NH3VR_CONNECTED : 0;
            dest->pose_age_seconds = sample_age(out->monotonic_seconds, d->last_pose);
            dest->optical_age_seconds = sample_age(out->monotonic_seconds, d->last_optical);
            if (source->running && d->connected && dest->optical_age_seconds <= .25)
                dest->flags |= NH3VR_OPTICAL_CURRENT;
            if (source->running && d->connected && d->has_pose && dest->pose_age_seconds <= .25 &&
                finite_vector(d->position, 3) && finite_vector(d->rotation, 4)) {
                simd_quatf q = simd_quaternion(
                    (simd_float4){d->rotation[1], d->rotation[2], d->rotation[3], d->rotation[0]});
                float norm = simd_length(q.vector);
                if (norm > .99f && norm < 1.01f) {
                    q = simd_normalize(
                        simd_mul(simd_quaternion(-M_PI_2, (simd_float3){1, 0, 0}), q));
                    simd_float3 p = {d->position[0], d->position[2] - out->floor_y,
                                     -d->position[1]};
                    dest->tracking_pose = unity_pose(p, q);
                    dest->flags |= NH3VR_POSE_VALID;
                    if (dest->pose_age_seconds < .1)
                        dest->flags |= NH3VR_POSE_TRACKED;
                    if (dest->role == NH3VR_LEFT || dest->role == NH3VR_RIGHT) {
                        dest->grip_pose = controller_pose(p, q, dest->role == NH3VR_RIGHT, NO);
                        dest->aim_pose = controller_pose(p, q, dest->role == NH3VR_RIGHT, YES);
                        dest->flags |= NH3VR_CONTROLLER_OFFSETS;
                    }
                }
            }
            if ((dest->flags & NH3VR_POSE_VALID) && d->has_velocity &&
                sample_age(out->monotonic_seconds, d->last_velocity) <= .25 &&
                finite_vector(d->linear_velocity, 3) && finite_vector(d->angular_velocity, 3)) {
                /* Axial angular velocity changes sign under the Unity reflection. */
                float v[] = {d->linear_velocity[0], d->linear_velocity[2], d->linear_velocity[1]};
                float w[] = {-d->angular_velocity[0], -d->angular_velocity[2],
                             -d->angular_velocity[1]};
                memcpy(dest->linear_velocity, v, sizeof(v));
                memcpy(dest->angular_velocity, w, sizeof(w));
                dest->flags |= NH3VR_VELOCITY_VALID;
            }
            if (source->running && d->connected && d->input_events &&
                sample_age(out->monotonic_seconds, d->last_imu) <= .25 &&
                finite_vector(d->axes, 16)) {
                dest->flags |= NH3VR_INPUT_CURRENT;
                dest->buttons = d->buttons;
                dest->touches = d->touches;
                for (int j = 0; j < 16; j++)
                    dest->axes[j] = (float)d->axes[j];
            }
            // Eye rendering uses the active session's STAGE, including its
            // initial horizontal position and heading. Broker poses alone only
            // have a floor offset and cannot be mixed with those eye poses.
            if (dest->role >= NH3VR_HEAD && dest->role <= NH3VR_RIGHT &&
                (dest->flags & NH3VR_POSE_VALID)) {
                int located = nh3vr_xr_locate_device(dest);
                if (located < 0)
                    dest->flags &= ~(NH3VR_POSE_VALID | NH3VR_POSE_TRACKED | NH3VR_VELOCITY_VALID |
                                     NH3VR_CONTROLLER_OFFSETS);
            }
        }
        return NH3VR_OK;
    }
}

#include "broker_protocol.h"
#include "nativeh3vr_tracking.h"
#import <Foundation/Foundation.h>
#include <assert.h>
#include <math.h>
#include <pthread.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

static int server;
static char state_directory[1024];
static double monotonic_now(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}
static void full_transfer(int fd, void *data, size_t size, int writing) {
    char *p = data;
    while (size) {
        ssize_t n = writing ? write(fd, p, size) : read(fd, p, size);
        assert(n > 0);
        p += n;
        size -= n;
    }
}
static void *serve(void *unused) {
    (void)unused;
    for (int scenario = 0; scenario < 4; scenario++) {
        int fd = accept(server, NULL, NULL);
        assert(fd >= 0);
        nh3vr_ipc_request req = {0};
        full_transfer(fd, &req, sizeof(req), 0);
        assert(req.operation == 1 && req.version == NH3VR_IPC_VERSION &&
               req.magic == NH3VR_IPC_MAGIC);
        nh3vr_ipc_reply reply = {
            .magic = NH3VR_IPC_MAGIC, .version = NH3VR_IPC_VERSION, .size = sizeof(reply)};
        strlcpy(reply.state_directory, state_directory, sizeof(reply.state_directory));
        idx_snapshot *s = &reply.snapshot;
        s->abi_version = IDX_ABI_VERSION;
        s->running = true;
        s->now = monotonic_now();
        s->device_count = 2;
        s->lighthouse_count = 1;
        s->lighthouses[0] =
            (idx_lighthouse){.serial = 123, .calibrated = true, .rotation = {1, 0, 0, 0}};
        for (int i = 0; i < 2; i++) {
            idx_device *d = &s->devices[i];
            strlcpy(d->name, i ? "left" : "head", sizeof(d->name));
            d->type = i ? 2 : 1;
            d->subtype = i ? 5 : 0;
            d->connected = true;
            d->has_pose = true;
            d->has_velocity = true;
            d->last_pose = d->last_velocity = d->last_imu = d->last_optical = s->now;
            d->position[0] = 1;
            d->position[1] = 2;
            d->position[2] = 3;
            d->rotation[0] = d->rotation[1] = M_SQRT1_2;
            d->linear_velocity[0] = 4;
            d->linear_velocity[1] = 5;
            d->linear_velocity[2] = 6;
            d->angular_velocity[0] = 7;
            d->angular_velocity[1] = 8;
            d->angular_velocity[2] = 9;
            d->input_events = 1;
            d->buttons = 5;
            d->axes[1] = .7;
        }
        if (scenario == 1)
            for (int i = 0; i < 2; i++) {
                s->devices[i].last_pose -= 1;
                s->devices[i].last_imu -= 1;
                s->devices[i].last_velocity -= 1;
                s->devices[i].last_optical -= 1;
            }
        if (scenario == 2)
            s->lighthouses[0].position[0] = 10;
        if (scenario == 3)
            reply.version = 999;
        full_transfer(fd, &reply, sizeof(reply), 1);
        close(fd);
    }
    return NULL;
}
static void near(float a, float b) { assert(fabsf(a - b) < .0001f); }
int main(int argc, char **argv) {
    @autoreleasepool {
        assert(argc == 2);
        assert(nh3vr_tracking_abi() == NH3VR_TRACKING_ABI);
        assert(nh3vr_tracking_snapshot_size() == sizeof(nh3vr_tracking_snapshot));
        nh3vr_tracking_snapshot out;
        assert(nh3vr_read_tracking(NULL, NULL, sizeof(out)) == NH3VR_INVALID_ARGUMENT);
        assert(nh3vr_read_tracking(NULL, &out, 0) == NH3VR_INVALID_ARGUMENT);
        char temp[1024];
        assert(snprintf(temp, sizeof(temp), "%s/tracking-test-XXXXXX", argv[1]) <
               (int)sizeof(temp));
        assert(mkdtemp(temp));
        strlcpy(state_directory, temp, sizeof(state_directory));
        NSString *room = @"{\"floor_y\":0.5,\"anchors\":[{\"serial\":123,\"position\":[0,0,0],"
                         @"\"rotation_wxyz\":[1,0,0,0]}]}";
        NSString *roomPath = [@(temp) stringByAppendingPathComponent:@"openxr-room.json"];
        assert([room writeToFile:roomPath atomically:YES encoding:NSUTF8StringEncoding error:nil]);
        char path[104];
        snprintf(path, sizeof(path), "%s/broker.sock", temp);
        struct sockaddr_un addr = {.sun_family = AF_UNIX};
        strlcpy(addr.sun_path, path, sizeof(addr.sun_path));
        server = socket(AF_UNIX, SOCK_STREAM, 0);
        assert(server >= 0);
        assert(bind(server, (struct sockaddr *)&addr, sizeof(addr)) == 0);
        assert(listen(server, 4) == 0);
        pthread_t worker;
        assert(pthread_create(&worker, NULL, serve, NULL) == 0);
        assert(nh3vr_read_tracking(path, &out, sizeof(out)) == NH3VR_OK);
        assert(out.device_count == 2 && out.floor_state == NH3VR_FLOOR_READY);
        nh3vr_device *d = &out.devices[0];
        assert(d->role == NH3VR_HEAD);
        assert((d->flags & (NH3VR_POSE_TRACKED | NH3VR_VELOCITY_VALID | NH3VR_INPUT_CURRENT)) ==
               (NH3VR_POSE_TRACKED | NH3VR_VELOCITY_VALID | NH3VR_INPUT_CURRENT));
        near(d->tracking_pose.position[0], 1);
        near(d->tracking_pose.position[1], 2.5);
        near(d->tracking_pose.position[2], 2);
        near(d->tracking_pose.rotation_xyzw[0], 0);
        near(d->tracking_pose.rotation_xyzw[3], 1);
        near(d->linear_velocity[0], 4);
        near(d->linear_velocity[1], 6);
        near(d->linear_velocity[2], 5);
        near(d->angular_velocity[0], -7);
        near(d->angular_velocity[1], -9);
        near(d->angular_velocity[2], -8);
        assert(d->buttons == 5);
        near(d->axes[1], .7);
        d = &out.devices[1];
        assert(d->role == NH3VR_LEFT && (d->flags & NH3VR_CONTROLLER_OFFSETS));
        near(d->grip_pose.position[1], 2.485);
        near(d->grip_pose.position[2], 1.87);
        assert(nh3vr_read_tracking(path, &out, sizeof(out)) == NH3VR_OK);
        assert(out.devices[0].flags == NH3VR_CONNECTED);
        assert(out.devices[0].buttons == 0);
        assert(nh3vr_read_tracking(path, &out, sizeof(out)) == NH3VR_OK);
        assert(out.floor_state == NH3VR_FLOOR_MISMATCH);
        near(out.devices[0].tracking_pose.position[1], 3);
        assert(nh3vr_read_tracking(path, &out, sizeof(out)) == NH3VR_PROTOCOL_ERROR);
        assert(out.abi_version == 0 && out.device_count == 0);
        pthread_join(worker, NULL);
        close(server);
        unlink(path);
        unlink(roomPath.fileSystemRepresentation);
        rmdir(temp);
        assert(nh3vr_read_tracking(path, &out, sizeof(out)) == NH3VR_BROKER_UNAVAILABLE);
        puts("PASS: arm64 C ABI, live/stale samples, Unity coordinates, velocity handedness, "
             "controller offsets, room-anchor mismatch, protocol mismatch, unavailable broker");
        return 0;
    }
}

#include "IUnityGraphicsMetal.h"
#include "nativeh3vr_xr.h"
#import <Foundation/Foundation.h>
#import <ImageIO/ImageIO.h>
#import <Metal/Metal.h>
#include <assert.h>
#include <cmath>
#include <fcntl.h>
#include <stdio.h>
#include <sys/file.h>
#include <unistd.h>
static id<MTLCommandBuffer> unityBuffer;
static IUnityGraphicsMetalV1 metalInterface;
static IUnityInterface *getInterface(UnityInterfaceGUID guid) {
    return guid == GetUnityInterfaceGUID<IUnityGraphicsMetalV1>() ? &metalInterface : nullptr;
}
static id<MTLCommandBuffer> getBuffer() { return unityBuffer; }
static void endEncoder() {}
static void check(int r) {
    if (r) {
        fprintf(stderr, "XR bridge error %d: %s\n", r, nh3vr_xr_error());
        abort();
    }
}
static void near(float actual, float expected) { assert(fabsf(actual - expected) < .002f); }
static nh3vr_device locationProbe(uint32_t role) {
    nh3vr_device device = {};
    device.role = role;
    device.flags = NH3VR_CONNECTED | NH3VR_INPUT_CURRENT | NH3VR_OPTICAL_CURRENT;
    device.buttons = 0x5a;
    device.touches = 0xa5;
    for (int i = 0; i < 16; i++)
        device.axes[i] = (float)i + .25f;
    device.pose_age_seconds = .012;
    device.optical_age_seconds = .034;
    return device;
}
static void assertProbeIOPreserved(const nh3vr_device &device, uint32_t role) {
    assert(device.role == role);
    assert(device.buttons == 0x5a && device.touches == 0xa5);
    for (int i = 0; i < 16; i++)
        near(device.axes[i], (float)i + .25f);
    assert(device.pose_age_seconds == .012 && device.optical_age_seconds == .034);
    assert(device.flags & NH3VR_CONNECTED);
    assert(device.flags & NH3VR_INPUT_CURRENT);
    assert(device.flags & NH3VR_OPTICAL_CURRENT);
}
static void assertControllerAt(const nh3vr_device &device, uint32_t role, float x, float y,
                               float z) {
    assert(device.role == role);
    assert(device.flags & NH3VR_POSE_VALID);
    assert(device.flags & NH3VR_POSE_TRACKED);
    near(device.tracking_pose.position[0], x);
    near(device.tracking_pose.position[1], y);
    near(device.tracking_pose.position[2], z);
}
static void assertPoseAt(const nh3vr_pose &pose, float x, float y, float z) {
    near(pose.position[0], x);
    near(pose.position[1], y);
    near(pose.position[2], z);
    near(pose.rotation_xyzw[0], 0);
    near(pose.rotation_xyzw[1], 0);
    near(pose.rotation_xyzw[2], 0);
    near(pose.rotation_xyzw[3], 1);
}
int main(int argc, char **argv) {
    if (argc != 4)
        return 2;
    setenv("VR_OPENXR_CAPTURE", argv[3], 1);
    unlink(argv[3]);
    @autoreleasepool {
        assert(nh3vr_xr_frame_size() == sizeof(nh3vr_xr_frame));
        setenv("VI_HOME_CONTROL_DIR", "/tmp/NativeH3VR-test-parent-must-not-be-used", 1);
        check(nh3vr_xr_start(argv[1], argv[2], 1, 128, 128));
        const char *isolated = getenv("VI_HOME_CONTROL_DIR");
        assert(isolated && strstr(isolated, "NativeH3VR-headless-") &&
               !strstr(isolated, "parent-must-not-be-used"));
        id<MTLDevice> device = MTLCreateSystemDefaultDevice();
        MTLTextureDescriptor *descriptor =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm_sRGB
                                                               width:128
                                                              height:128
                                                           mipmapped:NO];
        descriptor.storageMode = MTLStorageModeShared;
        id<MTLTexture> left = [device newTextureWithDescriptor:descriptor];
        id<MTLTexture> right = [device newTextureWithDescriptor:descriptor];
        uint32_t pixels[128 * 128];
        for (int i = 0; i < 128 * 128; i++)
            pixels[i] = 0xff0000ff;
        [left replaceRegion:MTLRegionMake2D(0, 0, 128, 128)
                mipmapLevel:0
                  withBytes:pixels
                bytesPerRow:128 * 4];
        for (int i = 0; i < 128 * 128; i++)
            pixels[i] = 0xff00ff00;
        [right replaceRegion:MTLRegionMake2D(0, 0, 128, 128)
                 mipmapLevel:0
                   withBytes:pixels
                 bytesPerRow:128 * 4];
        int rendered = 0;
        int64_t previous = 0;
        for (int i = 0; i < 5; i++) {
            nh3vr_xr_frame frame;
            check(nh3vr_xr_begin(&frame, sizeof(frame)));
            assert(frame.width == 128 && frame.height == 128 && frame.display_time_ns > previous);
            previous = frame.display_time_ns;
            if (!frame.should_render)
                continue;
            assert(frame.views[0].pose.position[0] < frame.views[1].pose.position[0]);
            assert(frame.views[0].fov_left < 0 && frame.views[0].fov_right > 0);
            assert(nh3vr_xr_begin(&frame, sizeof(frame)) == NH3VR_XR_BUSY);
            check(nh3vr_xr_set_textures((__bridge void *)left, (__bridge void *)right));
            check(nh3vr_xr_submit_standalone());
            rendered++;
        }
        nh3vr_xr_view eyeLeft, eyeRight;
        check(nh3vr_xr_eye_views(&eyeLeft, &eyeRight));
        assert(eyeLeft.pose.position[0] < 0 && eyeRight.pose.position[0] > 0);
        nh3vr_pose head;
        check(nh3vr_xr_head_pose(&head));
        assert(head.rotation_xyzw[3] > .99f);
        // Session-backed controller poses share the same predicted frame and
        // STAGE origin as the HMD. Synthetic runtime grip actions are already
        // raw; the bridge still supplies calibrated auxiliary grip/aim fields.
        nh3vr_xr_frame poseFrame;
        check(nh3vr_xr_begin(&poseFrame, sizeof(poseFrame)));
        nh3vr_device leftProbe = locationProbe(NH3VR_LEFT), rightProbe = locationProbe(NH3VR_RIGHT);
        check(nh3vr_xr_locate_device(&leftProbe));
        check(nh3vr_xr_locate_device(&rightProbe));
        assertControllerAt(leftProbe, NH3VR_LEFT, -.25f, 1.2f, .4f);
        assertControllerAt(rightProbe, NH3VR_RIGHT, .25f, 1.2f, .4f);
        assertPoseAt(leftProbe.tracking_pose, -.25f, 1.2f, .4f);
        assertPoseAt(rightProbe.tracking_pose, .25f, 1.2f, .4f);
        near(leftProbe.grip_pose.position[1], 1.185f);
        near(leftProbe.grip_pose.position[2], .27f);
        near(rightProbe.grip_pose.position[1], 1.185f);
        near(rightProbe.grip_pose.position[2], .27f);
        near(leftProbe.aim_pose.position[0], -.244f);
        near(rightProbe.aim_pose.position[0], .244f);
        near(leftProbe.aim_pose.position[2], .38f);
        near(rightProbe.aim_pose.position[2], .38f);
        assertProbeIOPreserved(leftProbe, NH3VR_LEFT);
        assertProbeIOPreserved(rightProbe, NH3VR_RIGHT);
        check(nh3vr_xr_cancel());
        assert(nh3vr_xr_focused() == 1);
        float boundsWidth, boundsDepth;
        check(nh3vr_xr_stage_bounds(&boundsWidth, &boundsDepth));
        assert(boundsWidth == 2 && boundsDepth == 2);
        const char *targetHz = getenv("VR_OPENXR_TARGET_HZ");
        float expectedHz = targetHz ? strtof(targetHz, nullptr) : 90.f;
        assert(fabsf(nh3vr_xr_display_frequency() - expectedHz) < .001f);
        assert(nh3vr_xr_haptic(1, 0.1f, 100, 0.01f) < 0);
        check(nh3vr_xr_haptic(
            2, 0.1f, 100, 0.01f)); // Synthetic command acceptance only; no physical tactile test.
        uint64_t completed = 0;
        check(nh3vr_xr_status(&completed));
        assert(completed == (uint64_t)rendered && rendered >= 3);
        // Exercise the exported Unity rendering callback with a real Metal buffer.
        // The narrow host mock supplies the exact installed Unity API contract.
        IUnityInterfaces interfaces = {};
        interfaces.GetInterface = getInterface;
        metalInterface.CurrentCommandBuffer = getBuffer;
        metalInterface.EndCurrentCommandEncoder = endEncoder;
        UnityPluginLoad(&interfaces);
        nh3vr_xr_frame unityFrame;
        check(nh3vr_xr_begin(&unityFrame, sizeof(unityFrame)));
        assert(unityFrame.should_render);
        check(nh3vr_xr_set_textures((__bridge void *)left, (__bridge void *)right));
        id<MTLCommandQueue> unityQueue = [device newCommandQueue];
        unityBuffer = [unityQueue commandBuffer];
        ((void (*)(int))nh3vr_xr_render_event())(1);
        assert(nh3vr_xr_status(&completed) == NH3VR_XR_BUSY && completed == (uint64_t)rendered);
        assert(nh3vr_xr_stop() == NH3VR_XR_BUSY);
        [unityBuffer commit];
        [unityBuffer waitUntilCompleted];
        for (int i = 0; i < 5000 && nh3vr_xr_status(&completed) == NH3VR_XR_BUSY; i++)
            usleep(1000);
        check(nh3vr_xr_status(&completed));
        assert(completed == (uint64_t)rendered + 1);
        unityBuffer = nil;
        // Simulate only Home's ownership lock inside the bridge-created private
        // directory. The unchanged runtime emits real VISIBLE/FOCUSED events;
        // no physical Home endpoint, USB or display owner is contacted.
        NSString *focusLock = [[NSString stringWithUTF8String:getenv("VI_HOME_CONTROL_DIR")]
            stringByAppendingPathComponent:@"display-owner.lock"];
        int focusFD = open(focusLock.fileSystemRepresentation, O_CREAT | O_RDWR | O_CLOEXEC, 0600);
        assert(focusFD >= 0 && flock(focusFD, LOCK_EX | LOCK_NB) == 0);
        auto awaitFocus = [&](uint32_t expected) {
            for (int i = 0; i < 90 && nh3vr_xr_focused() != expected; i++) {
                nh3vr_xr_frame f;
                check(nh3vr_xr_begin(&f, sizeof(f)));
                check(nh3vr_xr_cancel());
                usleep(10000);
            }
            assert(nh3vr_xr_focused() == expected);
        };
        awaitFocus(0);
        check(nh3vr_xr_head_pose(&head)); // Visibility/focus loss does not erase tracking.
        assert(nh3vr_xr_haptic(2, .1f, 100, .01f) != 0);
        assert(flock(focusFD, LOCK_UN) == 0);
        close(focusFD);
        awaitFocus(1);
        check(nh3vr_xr_haptic(2, .1f, 100, .01f));
        printf("PASS actual synthetic OpenXR FOCUSED -> VISIBLE -> FOCUSED events; poses retained; "
               "unfocused haptics rejected; private Home lock only.\n");
        check(nh3vr_xr_stop());
        check(nh3vr_xr_stop());
        assert(nh3vr_xr_focused() == 0);
        UnityPluginUnload();
        assert(
            !strcmp(getenv("VI_HOME_CONTROL_DIR"), "/tmp/NativeH3VR-test-parent-must-not-be-used"));
        // Validate the runtime's actual composited output, not just API success.
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:argv[3]]];
        CGImageSourceRef source = CGImageSourceCreateWithURL((__bridge CFURLRef)url, NULL);
        assert(source);
        CGImageRef capture = CGImageSourceCreateImageAtIndex(source, 0, NULL);
        assert(capture);
        size_t cw = CGImageGetWidth(capture), ch = CGImageGetHeight(capture);
        assert(cw == 256 && ch == 128);
        uint8_t *rgba = (uint8_t *)calloc(cw * ch, 4);
        CGColorSpaceRef colors = CGColorSpaceCreateDeviceRGB();
        CGContextRef context =
            CGBitmapContextCreate(rgba, cw, ch, 8, cw * 4, colors,
                                  kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big);
        assert(context);
        CGContextDrawImage(context, CGRectMake(0, 0, cw, ch), capture);
        uint8_t *l = rgba + ((ch / 2) * cw + cw / 4) * 4,
                *r = rgba + ((ch / 2) * cw + 3 * cw / 4) * 4;
        assert(l[0] > 250 && l[1] < 5 && l[2] < 5 && r[0] < 5 && r[1] > 250 && r[2] < 5);
        CGContextRelease(context);
        CGColorSpaceRelease(colors);
        CGImageRelease(capture);
        CFRelease(source);
        free(rgba);
        check(nh3vr_xr_start(argv[1], argv[2], 1, 64, 64));
        check(nh3vr_xr_stop());
        nh3vr_device noSession = locationProbe(NH3VR_LEFT);
        assert(nh3vr_xr_locate_device(&noSession) == NH3VR_XR_NOT_RUNNING);
        assertProbeIOPreserved(noSession, NH3VR_LEFT);
        // Delayed synthetic headset acquisition changes the runtime LOCAL/STAGE
        // origin to the first real head pose. Both controllers must follow that
        // horizontal shift; a floor-only transform would leave their raw X/Z.
        setenv("VR_OPENXR_TEST_DELAYED_POSE", "1", 1);
        check(nh3vr_xr_start(argv[1], argv[2], 1, 64, 64));
        // The delayed first HMD sample establishes LOCAL during WaitFrame;
        // the runtime publishes FOCUSED on a later event poll. Let the real
        // session event sequence settle before locating action-space poses.
        for (int i = 0; i < 90 && !nh3vr_xr_focused(); i++) {
            nh3vr_xr_frame focusFrame;
            check(nh3vr_xr_begin(&focusFrame, sizeof(focusFrame)));
            check(nh3vr_xr_cancel());
            usleep(10000);
        }
        assert(nh3vr_xr_focused() == 1);
        nh3vr_xr_frame delayedFrame;
        check(nh3vr_xr_begin(&delayedFrame, sizeof(delayedFrame)));
        check(nh3vr_xr_head_pose(&head));
        near(head.position[0], 0);
        near(head.position[2], 0);
        leftProbe = locationProbe(NH3VR_LEFT);
        rightProbe = locationProbe(NH3VR_RIGHT);
        check(nh3vr_xr_locate_device(&leftProbe));
        check(nh3vr_xr_locate_device(&rightProbe));
        assertControllerAt(leftProbe, NH3VR_LEFT, -1.05f, 1.2f, -.1f);
        assertControllerAt(rightProbe, NH3VR_RIGHT, -.55f, 1.2f, -.1f);
        assertProbeIOPreserved(leftProbe, NH3VR_LEFT);
        assertProbeIOPreserved(rightProbe, NH3VR_RIGHT);
        check(nh3vr_xr_cancel());
        check(nh3vr_xr_stop());
        unsetenv("VR_OPENXR_TEST_DELAYED_POSE");
        printf("PASS genuine Metal/OpenXR synthetic headless stereo: %d GPU-completed projection "
               "frames, calibrated view/FOV, call order, restart, actual compositor pixel checks, "
               "Unity callback GPU ordering (mock host). No physical HMD output.\n",
               rendered);
    }
}

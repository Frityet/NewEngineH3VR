#include "nativeh3vr_xr.h"
#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <assert.h>
#include <stdio.h>
#include <unistd.h>
static void check(int value) {
    if (value) {
        fprintf(stderr, "fade probe: %d %s\n", value, nh3vr_xr_error());
        abort();
    }
}
int main(int argc, char **argv) {
    if (argc != 4)
        return 2;
    @autoreleasepool {
        check(nh3vr_xr_start(argv[1], argv[2], 1, 128, 128));
        setenv("VR_OPENXR_CAPTURE_EVERY", "1", 1);
        id<MTLDevice> device = MTLCreateSystemDefaultDevice();
        auto desc =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm_sRGB
                                                               width:128
                                                              height:128
                                                           mipmapped:NO];
        desc.storageMode = MTLStorageModeShared;
        id<MTLTexture> texture = [device newTextureWithDescriptor:desc];
        uint32_t pixels[128 * 128];
        for (auto &p : pixels)
            p = 0xffffffff;
        [texture replaceRegion:MTLRegionMake2D(0, 0, 128, 128)
                   mipmapLevel:0
                     withBytes:pixels
                   bytesPerRow:128 * 4];
        auto frame = [&](const char *name) {
            NSString *path = [[NSString stringWithUTF8String:argv[3]]
                stringByAppendingPathComponent:[NSString stringWithUTF8String:name]];
            setenv("VR_OPENXR_CAPTURE", path.fileSystemRepresentation, 1);
            for (int i = 0; i < 3; i++) {
                nh3vr_xr_frame f;
                check(nh3vr_xr_begin(&f, sizeof(f)));
                if (f.should_render) {
                    check(
                        nh3vr_xr_set_textures((__bridge void *)texture, (__bridge void *)texture));
                    check(nh3vr_xr_submit_standalone());
                }
            }
        };
        float r, g, b, a, grid;
        check(nh3vr_xr_fade_state(0, &r, &g, &b, &a, &grid));
        assert(a == 0 && grid == 0);
        frame("baseline.png");
        check(nh3vr_xr_fade_color(0, 0, 0, 0, 1, 0));
        frame("black.png");
        check(nh3vr_xr_fade_color(0, 0, 0, 0, .5, 0));
        frame("half-black.png");
        check(nh3vr_xr_fade_color(0, 1, 0, 0, 1, 0));
        frame("red.png");
        check(nh3vr_xr_fade_color(0, 0, 0, 0, 0, 0));
        check(nh3vr_xr_fade_grid(0, 1));
        frame("grid.png");
        check(nh3vr_xr_fade_color(0, 0, 0, 1, 1, 1));
        frame("blue-grid.png");
        check(nh3vr_xr_fade_grid(0, 0));
        frame("restored.png");
        check(nh3vr_xr_fade_color(.2, 0, 0, 0, 1, 0));
        usleep(50000);
        check(nh3vr_xr_fade_state(0, &r, &g, &b, &a, &grid));
        assert(a > .15 && a < .9);
        check(nh3vr_xr_fade_color(.2, 0, 0, 0, 0, 0));
        float start = a;
        usleep(50000);
        check(nh3vr_xr_fade_state(0, &r, &g, &b, &a, &grid));
        assert(a > 0 && a < start);
        usleep(180000);
        check(nh3vr_xr_fade_state(0, &r, &g, &b, &a, &grid));
        assert(a == 0);
        check(nh3vr_xr_stop());
        printf("NATIVE_FADE_PROBE_PASS actual GPU captures, timed fades, interrupted fades, "
               "calibrated world grid\n");
    }
}

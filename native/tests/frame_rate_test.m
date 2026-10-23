#import "HeadsetFrameRate.h"
#include <assert.h>
#include <math.h>
#include <string.h>

static NSDictionary *Calibration(void) {
    return @{@"direct_mode_edid_vid" : @"VLV", @"direct_mode_edid_pid" : @"91a8"};
}
static NSMutableDictionary *Display(double hz, BOOL native) {
    return [@{
        @"vendor" : @0x5996,
        @"model" : @0x91a8,
        @"hz" : @(hz),
        @"native_hmd" : @(native),
        @"attached" : @YES
    } mutableCopy];
}
int main(int argc, char **argv) {
    @autoreleasepool {
        if (argc == 2 && !strcmp(argv[1], "--read-current")) {
            return NH3VRHeadsetFramePeriod(NULL, NO, Calibration()) > 0 ? 0 : 1;
        }
        const char *source;
        for (NSNumber *rate in @[ @80, @90, @120, @144, @119.88 ]) {
            double hz = rate.doubleValue;
            int64_t period =
                NH3VRChooseFramePeriod(NULL, NO, @[ Display(hz, YES) ], Calibration(), &source);
            assert(fabs(1e9 / period - hz) < .0001);
            assert(!strcmp(source, "headset-current-mode"));
        }
        NSArray *both = @[ Display(90, NO), Display(144, YES) ];
        assert(NH3VRChooseFramePeriod(NULL, NO, both, Calibration(), &source) == 6944444);
        assert(NH3VRChooseFramePeriod(NULL, YES, both, Calibration(), &source) == 11111111);
        assert(!strcmp(source, "synthetic-default"));
        for (NSString *rate in @[ @"80", @"90", @"120", @"144" ]) {
            int64_t period =
                NH3VRChooseFramePeriod(rate.UTF8String, YES, both, Calibration(), &source);
            assert(fabs(1e9 / period - rate.doubleValue) < .0001);
            assert(!strcmp(source, "explicit-override"));
        }
        for (NSString *bad in @[ @"", @"0", @"-1", @"NaN", @"60", @"144junk", @"144.0", @" 144" ]) {
            assert(!NH3VRChooseFramePeriod(bad.UTF8String, NO, both, Calibration(), &source));
        }
        for (NSNumber *bad in @[ @0, @-1, @(NAN), @(INFINITY), @300, @YES ]) {
            NSMutableDictionary *d = Display(144, YES);
            d[@"hz"] = bad;
            assert(NH3VRChooseFramePeriod(NULL, NO, @[ Display(120, NO), d ], Calibration(),
                                          &source) == 11111111);
            assert(!strcmp(source, "headset-rate-unavailable-fallback"));
        }
        NSMutableDictionary *d = Display(144, YES);
        d[@"attached"] = @NO;
        assert(NH3VRChooseFramePeriod(NULL, NO, @[ d ], Calibration(), &source) == 11111111);
        d = Display(144, YES);
        d[@"vendor"] = @123;
        assert(NH3VRChooseFramePeriod(NULL, NO, @[ d ], Calibration(), &source) == 11111111);
        d = Display(120, NO);
        d[@"mirrored"] = @YES;
        assert(NH3VRChooseFramePeriod(NULL, NO, @[ d ], Calibration(), &source) == 11111111);
        assert(NH3VRChooseFramePeriod(NULL, NO, @[ Display(120, NO) ], Calibration(), &source) ==
               8333333);
        assert(NH3VRChooseFramePeriod(NULL, NO, @[], Calibration(), &source) == 11111111);
        assert(NH3VRChooseFramePeriod(NULL, NO, both, @{}, &source) == 11111111);
        puts("PASS refresh selection: active native/extended modes, fractional rates, overrides, "
             "synthetic isolation and unavailable/malformed/wrong-display fallbacks");
        return 0;
    }
}

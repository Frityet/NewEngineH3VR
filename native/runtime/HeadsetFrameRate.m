#import "HeadsetFrameRate.h"
#import "IVHMDDisplay.h"
#import "IVSupport.h"
#import "openxr/IVXRFrameTrace.h"
#include <math.h>

static int64_t DisplayPeriod(NSDictionary *display, NSDictionary *calibration) {
    if ([display[@"builtin"] boolValue] ||
        !IVDisplayMatchesCalibration([display[@"vendor"] unsignedIntValue],
                                     [display[@"model"] unsignedIntValue], calibration))
        return 0;
    id value = display[@"hz"];
    if (![value isKindOfClass:NSNumber.class] ||
        CFGetTypeID((__bridge CFTypeRef)value) == CFBooleanGetTypeID())
        return 0;
    double hz = [value doubleValue];
    // Reject unavailable/invalid telemetry. Preserve fractional refresh rates.
    if (!isfinite(hz) || hz < 60 || hz > 240)
        return 0;
    return (int64_t)llround(1e9 / hz);
}

int64_t NH3VRChooseFramePeriod(const char *override, BOOL synthetic,
                               NSArray<NSDictionary *> *displays, NSDictionary *calibration,
                               const char **source) {
    if (override) {
        *source = "explicit-override";
        return IVXRFramePeriodForTarget(override);
    }
    if (synthetic) {
        *source = "synthetic-default";
        return 11111111;
    }
    // Match the output backend: an attached native HMD takes precedence over
    // extended displays. Do not select a desktop monitor if its mode is unknown.
    for (NSDictionary *display in displays) {
        if (![display[@"native_hmd"] boolValue] || ![display[@"attached"] boolValue] ||
            !IVDisplayMatchesCalibration([display[@"vendor"] unsignedIntValue],
                                         [display[@"model"] unsignedIntValue], calibration))
            continue;
        int64_t period = DisplayPeriod(display, calibration);
        *source = period ? "headset-current-mode" : "headset-rate-unavailable-fallback";
        return period ? period : 11111111;
    }
    for (NSDictionary *display in displays) {
        if ([display[@"native_hmd"] boolValue] || [display[@"mirrored"] boolValue])
            continue;
        int64_t period = DisplayPeriod(display, calibration);
        if (period) {
            *source = "headset-current-mode";
            return period;
        }
    }
    *source = "headset-rate-unavailable-fallback";
    return 11111111;
}

int64_t NH3VRHeadsetFramePeriod(const char *override, BOOL synthetic, NSDictionary *calibration) {
    // One read at session startup. No mode transactions, session acquisition,
    // display reconfiguration or private mode queries in the frame loop.
    NSMutableArray *displays = NSMutableArray.new;
    if (!override && !synthetic) {
        [displays addObjectsFromArray:IVHMDDisplayModeInventory()];
        [displays addObjectsFromArray:IVDisplays()];
    }
    const char *source;
    int64_t period = NH3VRChooseFramePeriod(override, synthetic, displays, calibration, &source);
    fprintf(stderr, "NATIVEH3VR_FRAME_PACING target_hz=%.6f period_ns=%lld source=%s\n",
            period ? 1e9 / period : 0, (long long)period, source);
    return period;
}

#pragma once
#import <Foundation/Foundation.h>
#include <stdint.h>

// Selection is separate from discovery so missing/disconnected/wrong displays
// can be exercised without touching the user's headset or display mode.
int64_t NH3VRChooseFramePeriod(const char *override, BOOL synthetic,
                               NSArray<NSDictionary *> *displays, NSDictionary *calibration,
                               const char **source);
int64_t NH3VRHeadsetFramePeriod(const char *override, BOOL synthetic, NSDictionary *calibration);

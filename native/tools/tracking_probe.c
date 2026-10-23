#include "nativeh3vr_tracking.h"
#include <stdio.h>
int main(int argc, char **argv) {
    nh3vr_tracking_snapshot snapshot;
    int result = nh3vr_read_tracking(argc > 1 ? argv[1] : NULL, &snapshot, sizeof(snapshot));
    if (result) {
        fprintf(stderr, "%s (%d)\n", nh3vr_result_string(result), result);
        return 1;
    }
    printf("ABI=%u devices=%u running=%u simulation=%u playback=%u floor_state=%u floor_y=%.3f\n",
           snapshot.abi_version, snapshot.device_count, !!(snapshot.flags & NH3VR_RUNNING),
           !!(snapshot.flags & NH3VR_SIMULATION), !!(snapshot.flags & NH3VR_PLAYBACK),
           snapshot.floor_state, snapshot.floor_y);
    for (uint32_t i = 0; i < snapshot.device_count; i++) {
        const nh3vr_device *d = &snapshot.devices[i];
        printf("%s role=%u flags=0x%x pose_age=%.3f optical_age=%.3f position=(%.3f,%.3f,%.3f)\n",
               d->name, d->role, d->flags, d->pose_age_seconds, d->optical_age_seconds,
               d->tracking_pose.position[0], d->tracking_pose.position[1],
               d->tracking_pose.position[2]);
        printf("  buttons=0x%x touches=0x%x axes=", d->buttons, d->touches);
        for (uint32_t axis = 0; axis < 16; axis++)
            printf("%s%.3f", axis ? "," : "", d->axes[axis]);
        putchar('\n');
    }
    return 0;
}

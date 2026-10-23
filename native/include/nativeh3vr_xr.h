#pragma once
#include "nativeh3vr_tracking.h"
#ifdef __cplusplus
extern "C" {
#endif
/* One instance per process. All APIs serialized internally. No physical output
 * is acquired when synthetic_headless=1. Paths must be absolute. */
typedef struct {
    nh3vr_pose pose;
    float fov_left, fov_right, fov_up, fov_down;
} nh3vr_xr_view;
typedef struct {
    uint32_t struct_size, width, height, should_render;
    int64_t display_time_ns;
    uint64_t sequence, completed_frames;
    nh3vr_xr_view views[2];
} nh3vr_xr_frame;
enum { NH3VR_XR_BUSY = 1, NH3VR_XR_NOT_RUNNING = 2 };
NH3VR_API int32_t nh3vr_xr_start(const char *loader, const char *runtime_json,
                                 uint32_t synthetic_headless, uint32_t width, uint32_t height);
NH3VR_API int32_t nh3vr_xr_begin(nh3vr_xr_frame *frame, uint32_t size);
/* Retains two non-MSAA RGBA8Unorm[_sRGB] 2D MTLTextures until submission finishes.
 * Set only after begin succeeds. Plugin event 1 must follow both Unity camera
 * renders. Unity textures are flipped vertically and sampled into sRGB on
 * Unity's active command buffer; xrEndFrame
 * runs only after that buffer completed, so texture ownership is synchronized. */
/* Linear RGB compositor fades and calibrated STAGE loading grid. */
NH3VR_API int32_t nh3vr_xr_fade_color(float seconds, float r, float g, float b, float a,
                                      uint32_t background);
NH3VR_API int32_t nh3vr_xr_fade_grid(float seconds, uint32_t fade_in);
NH3VR_API int32_t nh3vr_xr_fade_state(uint32_t background, float *r, float *g, float *b, float *a,
                                      float *grid);
NH3VR_API int32_t nh3vr_xr_set_textures(void *left, void *right);
NH3VR_API void *nh3vr_xr_render_event(void);
/* Standalone textures have top-left origin. Samples on its own queue and waits
 * for completion, converting linear UNORM to the sRGB swapchain if necessary. */
NH3VR_API int32_t nh3vr_xr_submit_standalone(void);
NH3VR_API int32_t nh3vr_xr_stop(void);
NH3VR_API int32_t nh3vr_xr_cancel(void);
/* Calibrated eye poses relative to the OpenXR VIEW reference space. */
NH3VR_API int32_t nh3vr_xr_haptic(uint32_t role, float amplitude, float frequency_hz,
                                  float duration_seconds);
NH3VR_API float nh3vr_xr_display_frequency(void);
NH3VR_API int32_t nh3vr_xr_stage_bounds(float *width, float *depth);
NH3VR_API int32_t nh3vr_xr_head_pose(nh3vr_pose *pose);
/* Locate the device's raw tracking origin in the same STAGE and at the same
 * display time as the eyes. Updates pose/velocity fields and their flags only;
 * preserves broker identity and inputs. NOT_RUNNING leaves the device untouched. */
NH3VR_API int32_t nh3vr_xr_locate_device(nh3vr_device *device);
NH3VR_API int32_t nh3vr_xr_eye_views(nh3vr_xr_view *left, nh3vr_xr_view *right);
NH3VR_API int32_t nh3vr_xr_status(uint64_t *completed_frames);
/* Actual FOCUSED session state, not merely running/visible. Zero before/after session. */
NH3VR_API uint32_t nh3vr_xr_focused(void);
NH3VR_API const char *nh3vr_xr_error(void);
/* Actual xrGetSystemProperties identity; empty outside an initialized session. */
NH3VR_API const char *nh3vr_xr_system_name(void);
NH3VR_API uint32_t nh3vr_xr_frame_size(void);
#ifdef __cplusplus
}
#endif

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <algorithm>
#include <cmath>
#include <dlfcn.h>
#include <mutex>
#include <simd/simd.h>
#include <string>
#include <vector>
#define XR_USE_GRAPHICS_API_METAL
#include "IUnityGraphicsMetal.h"
#include "nativeh3vr_xr.h"
#include "openxr/IVXRControllerPoses.h"
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
// clang-format off
#define XR_FUNCTIONS(X) \
    X(CreateInstance) \
    X(DestroyInstance) \
    X(GetSystem) \
    X(GetSystemProperties) \
    X(CreateSession) \
    X(DestroySession) \
    X(PollEvent) \
    X(BeginSession) \
    X(EndSession) \
    X(RequestExitSession) \
    X(CreateReferenceSpace) \
    X(CreateActionSpace) \
    X(DestroySpace) \
    X(EnumerateViewConfigurationViews) \
    X(EnumerateSwapchainFormats) \
    X(CreateSwapchain) \
    X(DestroySwapchain) \
    X(EnumerateSwapchainImages) \
    X(AcquireSwapchainImage) \
    X(WaitSwapchainImage) \
    X(ReleaseSwapchainImage) \
    X(WaitFrame) \
    X(BeginFrame) \
    X(EndFrame) \
    X(LocateViews) \
    X(LocateSpace) \
    X(StringToPath) \
    X(CreateActionSet) \
    X(DestroyActionSet) \
    X(CreateAction) \
    X(SuggestInteractionProfileBindings) \
    X(AttachSessionActionSets) \
    X(SyncActions) \
    X(ApplyHapticFeedback) \
    X(GetReferenceSpaceBoundsRect)
// clang-format on
#define DECL(name) static PFN_xr##name p##name;
XR_FUNCTIONS(DECL)
static std::recursive_mutex mutex;
static dispatch_queue_t submissionQueue;
static NSString *isolatedHome;
static std::string priorHome;
static bool hadPriorHome;
static void *loaderHandle;
static XrInstance instance;
static XrSession session;
static XrSpace space, viewSpace;
static XrSpace controllerSpaces[2];
static XrActionSet hapticSet;
static XrAction hapticAction, controllerPoseAction;
static XrPath handPaths[2];
static XrSwapchain chains[2];
static std::vector<XrSwapchainImageMetalKHR> images[2];
static XrView views[2];
static XrFrameState frameState;
static id<MTLCommandQueue> queue;
static id<MTLTexture> sources[2];
static id<MTLRenderPipelineState> eyeCopyPipeline;
static bool loggedTextureFormats;
struct TimedColor {
    simd_float4 from = {}, to = {};
    double begin = 0, duration = 0;
    simd_float4 value(double now) const {
        float t = duration > 0 ? (float)std::clamp((now - begin) / duration, 0.0, 1.0) : 1;
        return from + (to - from) * t;
    }
    void set(simd_float4 target, double seconds, double now) {
        from = value(now);
        to = target;
        begin = now;
        duration = seconds;
    }
};
static TimedColor foregroundFade, backgroundFade, gridFade;
struct EyeUniforms {
    simd_uint4 flags;
    simd_float4 foreground, background, position, rotation, fov, options;
};

static IUnityGraphicsMetalV1 *unityMetal;
static IUnityGraphicsMetal *unityLegacy;
static uint32_t width, height;
static bool running, active, pending;
static XrSessionState sessionState = XR_SESSION_STATE_UNKNOWN;
static uint64_t sequence, completed;
static int32_t lastStatus;
static std::string error;
static std::string systemName;
static int32_t fail(const char *where, int32_t code) {
    error = std::string(where) + " (" + std::to_string(code) + ")";
    lastStatus = code;
    return code;
}
#define CHECK(call)                                                                                \
    do {                                                                                           \
        XrResult r = (call);                                                                       \
        if (XR_FAILED(r))                                                                          \
            return fail(#call, r);                                                                 \
    } while (0)
static int32_t finish(bool layers) {
    XrCompositionLayerProjectionView projections[2] = {};
    for (int i = 0; i < 2; i++) {
        projections[i].type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
        projections[i].pose = views[i].pose;
        projections[i].fov = views[i].fov;
        projections[i].subImage.swapchain = chains[i];
        projections[i].subImage.imageRect.extent = {(int32_t)width, (int32_t)height};
    }
    XrCompositionLayerProjection layer = {};
    layer.type = XR_TYPE_COMPOSITION_LAYER_PROJECTION;
    layer.space = space;
    layer.viewCount = 2;
    layer.views = projections;
    const XrCompositionLayerBaseHeader *base = (const XrCompositionLayerBaseHeader *)&layer;
    XrFrameEndInfo end = {};
    end.type = XR_TYPE_FRAME_END_INFO;
    end.displayTime = frameState.predictedDisplayTime;
    end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    end.layerCount = layers ? 1 : 0;
    end.layers = layers ? &base : nullptr;
    XrResult result = pEndFrame(session, &end);
    active = false;
    pending = false;
    sources[0] = nil;
    sources[1] = nil;
    if (XR_FAILED(result))
        return fail("xrEndFrame", result);
    if (layers)
        completed++;
    lastStatus = 0;
    return 0;
}
static int32_t poll() {
    XrEventDataBuffer event = {};
    event.type = XR_TYPE_EVENT_DATA_BUFFER;
    XrResult result;
    while ((result = pPollEvent(instance, &event)) == XR_SUCCESS) {
        if (event.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto changed = (XrEventDataSessionStateChanged *)&event;
            if (changed->session != session) {
                event = {};
                event.type = XR_TYPE_EVENT_DATA_BUFFER;
                continue;
            }
            auto state = changed->state;
            sessionState = state;
            if (state == XR_SESSION_STATE_READY && !running) {
                XrSessionBeginInfo begin = {};
                begin.type = XR_TYPE_SESSION_BEGIN_INFO;
                begin.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                CHECK(pBeginSession(session, &begin));
                running = true;
            } else if (state == XR_SESSION_STATE_STOPPING) {
                CHECK(pEndSession(session));
                running = false;
            } else if (state == XR_SESSION_STATE_EXITING || state == XR_SESSION_STATE_LOSS_PENDING)
                running = false;
        }
        event = {};
        event.type = XR_TYPE_EVENT_DATA_BUFFER;
    }
    if (XR_FAILED(result))
        return fail("xrPollEvent", result);
    return 0;
}
const char *nh3vr_xr_system_name(void) {
    static thread_local std::string snapshot;
    std::lock_guard<std::recursive_mutex> guard(mutex);
    snapshot = systemName;
    return snapshot.c_str();
}
uint32_t nh3vr_xr_frame_size(void) { return sizeof(nh3vr_xr_frame); }
const char *nh3vr_xr_error(void) {
    static thread_local std::string snapshot;
    std::lock_guard<std::recursive_mutex> guard(mutex);
    snapshot = error;
    return snapshot.c_str();
}
int32_t nh3vr_xr_status(uint64_t *n) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (n)
        *n = completed;
    return pending ? NH3VR_XR_BUSY : lastStatus;
}
uint32_t nh3vr_xr_focused(void) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    return running && sessionState == XR_SESSION_STATE_FOCUSED;
}
int32_t nh3vr_xr_stop(void) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (pending)
        return NH3VR_XR_BUSY;
    if (active)
        finish(false);
    if (running) {
        pRequestExitSession(session);
        poll();
        running = false;
    }
    for (int i = 0; i < 2; i++) {
        if (chains[i])
            pDestroySwapchain(chains[i]);
        chains[i] = XR_NULL_HANDLE;
        images[i].clear();
        sources[i] = nil;
    }
    if (viewSpace)
        pDestroySpace(viewSpace);
    viewSpace = XR_NULL_HANDLE;
    for (auto &controller : controllerSpaces) {
        if (controller)
            pDestroySpace(controller);
        controller = XR_NULL_HANDLE;
    }
    if (space)
        pDestroySpace(space);
    space = XR_NULL_HANDLE;
    if (session)
        pDestroySession(session);
    session = XR_NULL_HANDLE;
    if (hapticSet)
        pDestroyActionSet(hapticSet);
    hapticSet = XR_NULL_HANDLE;
    hapticAction = controllerPoseAction = XR_NULL_HANDLE;
    if (instance)
        pDestroyInstance(instance);
    instance = XR_NULL_HANDLE;
    sessionState = XR_SESSION_STATE_UNKNOWN;
    running = false;
    eyeCopyPipeline = nil;
    queue = nil;
    systemName.clear();
    if (isolatedHome) {
        [NSFileManager.defaultManager removeItemAtPath:isolatedHome error:nil];
        isolatedHome = nil;
        if (hadPriorHome)
            setenv("VI_HOME_CONTROL_DIR", priorHome.c_str(), 1);
        else
            unsetenv("VI_HOME_CONTROL_DIR");
    }
    // OpenXR loader keeps process-global dispatch state. Retain its image for
    // process lifetime rather than invalidating pointers on destroy/restart.
    foregroundFade = {};
    backgroundFade = {};
    gridFade = {};
    active = false;
    return 0;
}
int32_t nh3vr_xr_start(const char *loader, const char *manifest, uint32_t synthetic, uint32_t w,
                       uint32_t h) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    @autoreleasepool {
        if (instance || pending)
            return fail("already initialized", -1000);
        if (!loader || loader[0] != '/' || !manifest || manifest[0] != '/' || synthetic > 1 ||
            w > 4096 || h > 4096)
            return fail("invalid configuration", -1001);
        error.clear();
        lastStatus = 0;
        sequence = completed = 0;
        loggedTextureFormats = false;
        sessionState = XR_SESSION_STATE_UNKNOWN;
        if (!submissionQueue)
            submissionQueue =
                dispatch_queue_create("org.nativeh3vr.openxr-submit", DISPATCH_QUEUE_SERIAL);
        setenv("XR_RUNTIME_JSON", manifest, 1);
        setenv("VR_OPENXR_SIMULATE", synthetic ? "1" : "0", 1);
        setenv("VR_OPENXR_HEADLESS", synthetic ? "1" : "0", 1);
        // H3VR is always a scene client, never the resident system Home owner.
        setenv("VI_OPENXR_HOME", "0", 1);
        // A headless session can still stream into the runtime Home compositor.
        // Isolate its control directory too, preventing synthetic frames from ever
        // becoming the current physical Home application's incoming scene.
        if (synthetic) {
            const char *home = getenv("VI_HOME_CONTROL_DIR");
            hadPriorHome = home != nullptr;
            priorHome = home ? home : "";
            isolatedHome = [NSTemporaryDirectory()
                stringByAppendingPathComponent:[@"NativeH3VR-headless-"
                                                   stringByAppendingString:NSUUID.UUID.UUIDString]];
            if (![NSFileManager.defaultManager createDirectoryAtPath:isolatedHome
                                         withIntermediateDirectories:NO
                                                          attributes:@{
                                                              NSFilePosixPermissions : @0700
                                                          }
                                                               error:nil])
                return fail("headless control directory", -1020);
            setenv("VI_HOME_CONTROL_DIR", isolatedHome.fileSystemRepresentation, 1);
        }
        if (!loaderHandle)
            loaderHandle = dlopen(loader, RTLD_NOW | RTLD_LOCAL);
        if (!loaderHandle)
            return fail(dlerror(), -1002);
        auto getProc = (PFN_xrGetInstanceProcAddr)dlsym(loaderHandle, "xrGetInstanceProcAddr");
        if (!getProc)
            return fail("xrGetInstanceProcAddr unavailable", -1003);
        CHECK(getProc(XR_NULL_HANDLE, "xrCreateInstance", (PFN_xrVoidFunction *)&pCreateInstance));
        const char *extension = XR_KHR_METAL_ENABLE_EXTENSION_NAME;
        XrInstanceCreateInfo create = {};
        create.type = XR_TYPE_INSTANCE_CREATE_INFO;
        strcpy(create.applicationInfo.applicationName, "Native H3VR Metal");
        create.applicationInfo.apiVersion = XR_API_VERSION_1_1;
        create.enabledExtensionCount = 1;
        create.enabledExtensionNames = &extension;
        CHECK(pCreateInstance(&create, &instance));
#define LOAD(name) CHECK(getProc(instance, "xr" #name, (PFN_xrVoidFunction *)&p##name));
        XR_FUNCTIONS(LOAD)
        XrSystemGetInfo get = {};
        get.type = XR_TYPE_SYSTEM_GET_INFO;
        get.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        XrSystemId system;
        CHECK(pGetSystem(instance, &get, &system));
        XrSystemProperties properties = {};
        properties.type = XR_TYPE_SYSTEM_PROPERTIES;
        CHECK(pGetSystemProperties(instance, system, &properties));
        systemName = properties.systemName;
        PFN_xrGetMetalGraphicsRequirementsKHR getMetal;
        CHECK(getProc(instance, "xrGetMetalGraphicsRequirementsKHR",
                      (PFN_xrVoidFunction *)&getMetal));
        XrGraphicsRequirementsMetalKHR requirements = {};
        requirements.type = XR_TYPE_GRAPHICS_REQUIREMENTS_METAL_KHR;
        CHECK(getMetal(instance, system, &requirements));
        queue = [(__bridge id<MTLDevice>)requirements.metalDevice newCommandQueue];
        if (!queue)
            return fail("Metal queue allocation", -1004);
        // Sampling observes the source's real Metal format (sRGB decode where
        // applicable); rendering into the sRGB swapchain encodes exactly once.
        // A raw blit cannot convert linear UNORM sources or Unity's texture origin.
        NSString *shader = @R"metal(
#include <metal_stdlib>
using namespace metal;
vertex float4 eye_vertex(uint i [[vertex_id]]) {
    return float4(i==1?3.0f:-1.0f, i==2?3.0f:-1.0f, 0.0f, 1.0f);
}
struct EyeUniforms {uint4 flags;float4 foreground,background,position,rotation,fov,options;};
fragment float4 eye_fragment(float4 p [[position]], texture2d<float> image [[texture(0)]], constant EyeUniforms &u [[buffer(0)]]) {
    uint2 pixel=uint2(p.xy);
    if(u.flags.x)pixel.y=image.get_height()-1-pixel.y;
    float4 c=image.read(pixel);
    if(u.options.x>0) {
        float2 uv=p.xy/float2(image.get_width(),image.get_height());
        float3 ray=normalize(float3(mix(u.fov.x,u.fov.y,uv.x),mix(u.fov.z,u.fov.w,uv.y),-1));
        ray+=2*cross(u.rotation.xyz,cross(u.rotation.xyz,ray)+u.rotation.w*ray);
        float3 grid=mix(float3(0.008),u.background.rgb,u.background.a);
        if(ray.y < -0.0001 && u.position.y>0) {
            float distance=-u.position.y/ray.y;
            float3 hit=u.position.xyz+ray*distance;
            float2 spacing=abs(fract(hit.xz+0.5)-0.5);
            float aa=max(max(fwidth(hit.x),fwidth(hit.z)),0.001);
            float line=1-smoothstep(0.012,0.012+aa,min(spacing.x,spacing.y));
            grid+=float3(0.10)*line*exp(-distance*0.05);
        }
        c.rgb=mix(c.rgb,grid,u.options.x);
    }
    c.rgb=mix(c.rgb,u.foreground.rgb,u.foreground.a);
    return float4(c.rgb,1);
}
)metal";
        NSError *metalError = nil;
        id<MTLLibrary> library = [queue.device newLibraryWithSource:shader
                                                            options:nil
                                                              error:&metalError];
        if (!library)
            return fail(metalError.localizedDescription.UTF8String, -1021);
        MTLRenderPipelineDescriptor *pipeline = [MTLRenderPipelineDescriptor new];
        pipeline.vertexFunction = [library newFunctionWithName:@"eye_vertex"];
        pipeline.fragmentFunction = [library newFunctionWithName:@"eye_fragment"];
        pipeline.colorAttachments[0].pixelFormat = MTLPixelFormatRGBA8Unorm_sRGB;
        eyeCopyPipeline = [queue.device newRenderPipelineStateWithDescriptor:pipeline
                                                                       error:&metalError];
        if (!eyeCopyPipeline)
            return fail(metalError.localizedDescription.UTF8String, -1022);
        XrGraphicsBindingMetalKHR binding = {};
        binding.type = XR_TYPE_GRAPHICS_BINDING_METAL_KHR;
        binding.commandQueue = (__bridge void *)queue;
        XrSessionCreateInfo sessionInfo = {};
        sessionInfo.type = XR_TYPE_SESSION_CREATE_INFO;
        sessionInfo.next = &binding;
        sessionInfo.systemId = system;
        CHECK(pCreateSession(instance, &sessionInfo, &session));
        XrReferenceSpaceCreateInfo reference = {};
        reference.type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO;
        reference.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
        reference.poseInReferenceSpace.orientation.w = 1;
        CHECK(pCreateReferenceSpace(session, &reference, &space));
        reference.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
        CHECK(pCreateReferenceSpace(session, &reference, &viewSpace));
        XrViewConfigurationView configurations[2] = {};
        for (auto &v : configurations)
            v.type = XR_TYPE_VIEW_CONFIGURATION_VIEW;
        uint32_t count = 0;
        CHECK(pEnumerateViewConfigurationViews(instance, system,
                                               XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 2, &count,
                                               configurations));
        if (count != 2)
            return fail("stereo view count", -1005);
        width = w ? w : configurations[0].recommendedImageRectWidth;
        height = h ? h : configurations[0].recommendedImageRectHeight;
        uint32_t formatCount = 0;
        CHECK(pEnumerateSwapchainFormats(session, 0, &formatCount, nullptr));
        std::vector<int64_t> formats(formatCount);
        CHECK(pEnumerateSwapchainFormats(session, formatCount, &formatCount, formats.data()));
        bool found = false;
        for (auto f : formats)
            if (f == MTLPixelFormatRGBA8Unorm_sRGB)
                found = true;
        if (!found)
            return fail("RGBA8 sRGB swapchain unavailable", -1006);
        for (int eye = 0; eye < 2; eye++) {
            XrSwapchainCreateInfo info = {};
            info.type = XR_TYPE_SWAPCHAIN_CREATE_INFO;
            info.usageFlags =
                XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
            info.format = MTLPixelFormatRGBA8Unorm_sRGB;
            info.sampleCount = 1;
            info.width = width;
            info.height = height;
            info.faceCount = 1;
            info.arraySize = 1;
            info.mipCount = 1;
            CHECK(pCreateSwapchain(session, &info, &chains[eye]));
            uint32_t n = 0;
            CHECK(pEnumerateSwapchainImages(chains[eye], 0, &n, nullptr));
            images[eye].resize(n);
            for (auto &image : images[eye])
                image.type = XR_TYPE_SWAPCHAIN_IMAGE_METAL_KHR;
            CHECK(pEnumerateSwapchainImages(chains[eye], n, &n,
                                            (XrSwapchainImageBaseHeader *)images[eye].data()));
        }
        XrActionSetCreateInfo actionSet = {};
        actionSet.type = XR_TYPE_ACTION_SET_CREATE_INFO;
        strcpy(actionSet.actionSetName, "native_h3vr");
        strcpy(actionSet.localizedActionSetName, "Native H3VR");
        CHECK(pCreateActionSet(instance, &actionSet, &hapticSet));
        CHECK(pStringToPath(instance, "/user/hand/left", &handPaths[0]));
        CHECK(pStringToPath(instance, "/user/hand/right", &handPaths[1]));
        XrActionCreateInfo action = {};
        action.type = XR_TYPE_ACTION_CREATE_INFO;
        action.actionType = XR_ACTION_TYPE_VIBRATION_OUTPUT;
        strcpy(action.actionName, "haptic");
        strcpy(action.localizedActionName, "Haptic feedback");
        action.countSubactionPaths = 2;
        action.subactionPaths = handPaths;
        CHECK(pCreateAction(hapticSet, &action, &hapticAction));
        action.actionType = XR_ACTION_TYPE_POSE_INPUT;
        strcpy(action.actionName, "controller_pose");
        strcpy(action.localizedActionName, "Controller tracking pose");
        CHECK(pCreateAction(hapticSet, &action, &controllerPoseAction));
        XrPath profile, haptics[2], grips[2];
        CHECK(pStringToPath(instance, "/interaction_profiles/valve/index_controller", &profile));
        CHECK(pStringToPath(instance, "/user/hand/left/output/haptic", &haptics[0]));
        CHECK(pStringToPath(instance, "/user/hand/right/output/haptic", &haptics[1]));
        CHECK(pStringToPath(instance, "/user/hand/left/input/grip/pose", &grips[0]));
        CHECK(pStringToPath(instance, "/user/hand/right/input/grip/pose", &grips[1]));
        XrActionSuggestedBinding bindings[4] = {{hapticAction, haptics[0]},
                                                {hapticAction, haptics[1]},
                                                {controllerPoseAction, grips[0]},
                                                {controllerPoseAction, grips[1]}};
        XrInteractionProfileSuggestedBinding suggested = {};
        suggested.type = XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING;
        suggested.interactionProfile = profile;
        suggested.countSuggestedBindings = 4;
        suggested.suggestedBindings = bindings;
        CHECK(pSuggestInteractionProfileBindings(instance, &suggested));
        XrSessionActionSetsAttachInfo attach = {};
        attach.type = XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO;
        attach.countActionSets = 1;
        attach.actionSets = &hapticSet;
        CHECK(pAttachSessionActionSets(session, &attach));
        for (int hand = 0; hand < 2; hand++) {
            XrActionSpaceCreateInfo controller = {};
            controller.type = XR_TYPE_ACTION_SPACE_CREATE_INFO;
            controller.action = controllerPoseAction;
            controller.subactionPath = handPaths[hand];
            controller.poseInActionSpace.orientation.w = 1;
            // H3VR binds pose/raw. Undo the runtime's calibrated IMU-to-grip
            // offset, while retaining OpenXR's session origin and sample time.
            // The runtime's explicitly synthetic grip poses have no IMU offset.
            if (!synthetic) {
                XrPosef offset = IVXRIndexControllerOffset(hand == 1, false);
                simd_quatf inverse = simd_inverse(
                    simd_quaternion((simd_float4){offset.orientation.x, offset.orientation.y,
                                                  offset.orientation.z, offset.orientation.w}));
                simd_float3 p =
                    simd_act(inverse, -(simd_float3){offset.position.x, offset.position.y,
                                                     offset.position.z});
                controller.poseInActionSpace = {
                    {inverse.vector.x, inverse.vector.y, inverse.vector.z, inverse.vector.w},
                    {p.x, p.y, p.z}};
            }
            CHECK(pCreateActionSpace(session, &controller, &controllerSpaces[hand]));
        }
        return poll();
    }
}
int32_t nh3vr_xr_begin(nh3vr_xr_frame *out, uint32_t size) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    @autoreleasepool {
        if (!out || size != sizeof(*out))
            return fail("frame ABI mismatch", -1007);
        memset(out, 0, size);
        out->struct_size = size;
        if (!session)
            return NH3VR_XR_NOT_RUNNING;
        if (active || pending)
            return NH3VR_XR_BUSY;
        int result = poll();
        if (result)
            return result;
        if (!running)
            return NH3VR_XR_NOT_RUNNING;
        frameState = {};
        frameState.type = XR_TYPE_FRAME_STATE;
        CHECK(pWaitFrame(session, nullptr, &frameState));
        CHECK(pBeginFrame(session, nullptr));
        active = true;
        XrActiveActionSet activeSet = {hapticSet, XR_NULL_PATH};
        XrActionsSyncInfo sync = {};
        sync.type = XR_TYPE_ACTIONS_SYNC_INFO;
        sync.countActiveActionSets = 1;
        sync.activeActionSets = &activeSet;
        XrResult synced = pSyncActions(session, &sync);
        if (XR_FAILED(synced)) {
            finish(false);
            return fail("xrSyncActions", synced);
        }
        out->width = width;
        out->height = height;
        out->display_time_ns = frameState.predictedDisplayTime;
        out->sequence = ++sequence;
        out->completed_frames = completed;
        if (!frameState.shouldRender)
            return finish(false);
        XrViewLocateInfo locate = {};
        locate.type = XR_TYPE_VIEW_LOCATE_INFO;
        locate.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
        locate.displayTime = frameState.predictedDisplayTime;
        locate.space = space;
        XrViewState state = {};
        state.type = XR_TYPE_VIEW_STATE;
        uint32_t count = 0;
        for (auto &v : views) {
            v = {};
            v.type = XR_TYPE_VIEW;
        }
        XrResult located = pLocateViews(session, &locate, &state, 2, &count, views);
        if (XR_FAILED(located)) {
            finish(false);
            return fail("xrLocateViews", located);
        }
        if (count != 2 ||
            (state.viewStateFlags &
             (XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT)) !=
                (XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT))
            return finish(false);
        for (int i = 0; i < 2; i++) {
            auto &v = views[i];
            auto &dest = out->views[i];
            dest.pose.position[0] = v.pose.position.x;
            dest.pose.position[1] = v.pose.position.y;
            dest.pose.position[2] = -v.pose.position.z;
            dest.pose.rotation_xyzw[0] = -v.pose.orientation.x;
            dest.pose.rotation_xyzw[1] = -v.pose.orientation.y;
            dest.pose.rotation_xyzw[2] = v.pose.orientation.z;
            dest.pose.rotation_xyzw[3] = v.pose.orientation.w;
            dest.fov_left = v.fov.angleLeft;
            dest.fov_right = v.fov.angleRight;
            dest.fov_up = v.fov.angleUp;
            dest.fov_down = v.fov.angleDown;
        }
        out->should_render = 1;
        return 0;
    }
}
int32_t nh3vr_xr_fade_color(float seconds, float r, float g, float b, float a,
                            uint32_t background) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!session)
        return NH3VR_XR_NOT_RUNNING;
    if (!std::isfinite(seconds) || seconds < 0 || background > 1)
        return fail("invalid fade duration/background", -1023);
    for (float v : {r, g, b, a})
        if (!std::isfinite(v) || v < 0 || v > 1)
            return fail("invalid fade color", -1023);
    (background ? backgroundFade : foregroundFade)
        .set({r, g, b, a}, seconds, NSProcessInfo.processInfo.systemUptime);
    return 0;
}
int32_t nh3vr_xr_fade_grid(float seconds, uint32_t fadeIn) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!session)
        return NH3VR_XR_NOT_RUNNING;
    if (!std::isfinite(seconds) || seconds < 0 || fadeIn > 1)
        return fail("invalid grid fade", -1023);
    gridFade.set({fadeIn ? 1.f : 0.f, 0, 0, 0}, seconds, NSProcessInfo.processInfo.systemUptime);
    return 0;
}
int32_t nh3vr_xr_fade_state(uint32_t background, float *r, float *g, float *b, float *a,
                            float *grid) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!session)
        return NH3VR_XR_NOT_RUNNING;
    if (background > 1 || !r || !g || !b || !a || !grid)
        return fail("invalid fade output", -1023);
    double now = NSProcessInfo.processInfo.systemUptime;
    auto c = (background ? backgroundFade : foregroundFade).value(now);
    *r = c.x;
    *g = c.y;
    *b = c.z;
    *a = c.w;
    *grid = gridFade.value(now).x;
    return 0;
}
int32_t nh3vr_xr_set_textures(void *left, void *right) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!active || pending)
        return fail("set textures outside prepared frame", -1008);
    id<MTLTexture> textures[2] = {(__bridge id<MTLTexture>)left, (__bridge id<MTLTexture>)right};
    for (auto texture : textures)
        if (!texture || texture.textureType != MTLTextureType2D || texture.sampleCount != 1 ||
            texture.width != width || texture.height != height ||
            texture.device.registryID != queue.device.registryID ||
            (texture.pixelFormat != MTLPixelFormatRGBA8Unorm_sRGB &&
             texture.pixelFormat != MTLPixelFormatRGBA8Unorm))
            return fail("incompatible eye texture", -1009);
    sources[0] = textures[0];
    sources[1] = textures[1];
    return 0;
}
static int32_t submit(id<MTLCommandBuffer> buffer, bool own) {
    if (!active || pending || !sources[0] || !sources[1])
        return fail("submit without prepared stereo textures", -1010);
    if (!buffer || buffer.device.registryID != queue.device.registryID)
        return fail("invalid Metal command buffer/device", -1011);
    uint32_t indices[2] = {};
    bool acquired[2] = {};
    for (int eye = 0; eye < 2; eye++) {
        XrSwapchainImageAcquireInfo acquire = {};
        acquire.type = XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO;
        XrResult result = pAcquireSwapchainImage(chains[eye], &acquire, &indices[eye]);
        if (XR_SUCCEEDED(result)) {
            acquired[eye] = true;
            XrSwapchainImageWaitInfo wait = {};
            wait.type = XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO;
            wait.timeout = XR_INFINITE_DURATION;
            result = pWaitSwapchainImage(chains[eye], &wait);
        }
        if (XR_FAILED(result)) {
            for (int i = 0; i < 2; i++)
                if (acquired[i])
                    pReleaseSwapchainImage(chains[i], nullptr);
            finish(false);
            return fail("acquire/wait swapchain", result);
        }
    }
    if (!loggedTextureFormats) {
        NSLog(@"NativeH3VR eye submission source Metal formats=%lu,%lu destination=sRGB "
              @"UnityFlipY=%d",
              (unsigned long)sources[0].pixelFormat, (unsigned long)sources[1].pixelFormat, !own);
        loggedTextureFormats = true;
    }
    for (int eye = 0; eye < 2; eye++) {
        MTLRenderPassDescriptor *pass = [MTLRenderPassDescriptor renderPassDescriptor];
        pass.colorAttachments[0].texture =
            (__bridge id<MTLTexture>)images[eye][indices[eye]].texture;
        pass.colorAttachments[0].loadAction = MTLLoadActionDontCare;
        pass.colorAttachments[0].storeAction = MTLStoreActionStore;
        id<MTLRenderCommandEncoder> encoder = [buffer renderCommandEncoderWithDescriptor:pass];
        if (!encoder) {
            for (auto chain : chains)
                pReleaseSwapchainImage(chain, nullptr);
            finish(false);
            return fail("Metal eye render encoder", -1012);
        }
        double now = NSProcessInfo.processInfo.systemUptime;
        const auto &view = views[eye];
        EyeUniforms uniforms = {};
        uniforms.flags.x = own ? 0 : 1;
        uniforms.foreground = foregroundFade.value(now);
        uniforms.background = backgroundFade.value(now);
        uniforms.position = {view.pose.position.x, view.pose.position.y, view.pose.position.z, 1};
        uniforms.rotation = {view.pose.orientation.x, view.pose.orientation.y,
                             view.pose.orientation.z, view.pose.orientation.w};
        uniforms.fov = {tanf(view.fov.angleLeft), tanf(view.fov.angleRight), tanf(view.fov.angleUp),
                        tanf(view.fov.angleDown)};
        uniforms.options.x = gridFade.value(now).x;
        [encoder setRenderPipelineState:eyeCopyPipeline];
        [encoder setFragmentTexture:sources[eye] atIndex:0];
        [encoder setFragmentBytes:&uniforms length:sizeof(uniforms) atIndex:0];
        [encoder drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
        [encoder endEncoding];
    }
    pending = true;
    [buffer addCompletedHandler:^(id<MTLCommandBuffer> done) {
      dispatch_async(submissionQueue, ^{
        @autoreleasepool {
            std::lock_guard<std::recursive_mutex> guard(mutex);
            bool success = done.status == MTLCommandBufferStatusCompleted;
            XrResult releaseError = XR_SUCCESS;
            for (auto chain : chains) {
                XrResult r = pReleaseSwapchainImage(chain, nullptr);
                if (XR_FAILED(r))
                    releaseError = r;
            }
            finish(success && XR_SUCCEEDED(releaseError));
            if (!success)
                fail("Metal GPU command failed", -1013);
            else if (XR_FAILED(releaseError))
                fail("release swapchain", releaseError);
        }
      });
    }];
    if (own)
        [buffer commit];
    return 0;
}
int32_t nh3vr_xr_submit_standalone(void) {
    id<MTLCommandBuffer> buffer;
    {
        std::lock_guard<std::recursive_mutex> guard(mutex);
        buffer = [queue commandBuffer];
        int result = submit(buffer, true);
        if (result)
            return result;
    }
    [buffer waitUntilCompleted];
    // The completion handler owns the release/end-frame operation. Completion
    // status can become visible just before the handler has returned.
    for (int i = 0; i < 5000; i++) {
        int result = nh3vr_xr_status(nullptr);
        if (result != NH3VR_XR_BUSY)
            return result;
        usleep(1000);
    }
    return fail("completion handler timeout", -1014);
}
static void UNITY_INTERFACE_API renderEvent(int event) {
    if (event != 1)
        return;
    std::lock_guard<std::recursive_mutex> guard(mutex);
    @autoreleasepool {
        if (!unityMetal && !unityLegacy) {
            fail("Unity Metal interface unavailable", -1015);
            return;
        }
        if (unityMetal)
            unityMetal->EndCurrentCommandEncoder();
        else
            unityLegacy->EndCurrentCommandEncoder();
        submit(unityMetal ? unityMetal->CurrentCommandBuffer()
                          : unityLegacy->CurrentCommandBuffer(),
               false);
    }
}
void *nh3vr_xr_render_event(void) { return (void *)renderEvent; }
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces *interfaces) {
    unityMetal = interfaces->Get<IUnityGraphicsMetalV1>();
    unityLegacy = interfaces->Get<IUnityGraphicsMetal>();
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload(void) {
    // Host must drain plugin events before unloading. Never destroy a session
    // underneath an outstanding GPU completion callback.
    nh3vr_xr_stop();
    unityMetal = nullptr;
    unityLegacy = nullptr;
}

int32_t nh3vr_xr_cancel(void) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (pending)
        return NH3VR_XR_BUSY;
    return active ? finish(false) : 0;
}
int32_t nh3vr_xr_eye_views(nh3vr_xr_view *left, nh3vr_xr_view *right) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!left || !right || !session || !viewSpace || !pLocateViews ||
        !frameState.predictedDisplayTime)
        return fail("eye views unavailable", -1016);
    XrViewLocateInfo locate = {};
    locate.type = XR_TYPE_VIEW_LOCATE_INFO;
    locate.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    locate.displayTime = frameState.predictedDisplayTime;
    locate.space = viewSpace;
    XrViewState state = {};
    state.type = XR_TYPE_VIEW_STATE;
    XrView local[2] = {};
    for (auto &v : local)
        v.type = XR_TYPE_VIEW;
    uint32_t count = 0;
    CHECK(pLocateViews(session, &locate, &state, 2, &count, local));
    if (count != 2)
        return fail("eye view count invalid", -1017);
    // A successful locate can temporarily lack valid tracking. The managed
    // caller retains its calibrated eyes on this query failure; do not poison
    // asynchronous submission status, or clear an earlier fatal error.
    const auto valid = XR_VIEW_STATE_ORIENTATION_VALID_BIT | XR_VIEW_STATE_POSITION_VALID_BIT;
    if ((state.viewStateFlags & valid) != valid)
        return -1017;
    nh3vr_xr_view *destinations[2] = {left, right};
    for (int i = 0; i < 2; i++) {
        auto &v = local[i];
        auto &dest = *destinations[i];
        dest.pose.position[0] = v.pose.position.x;
        dest.pose.position[1] = v.pose.position.y;
        dest.pose.position[2] = -v.pose.position.z;
        dest.pose.rotation_xyzw[0] = -v.pose.orientation.x;
        dest.pose.rotation_xyzw[1] = -v.pose.orientation.y;
        dest.pose.rotation_xyzw[2] = v.pose.orientation.z;
        dest.pose.rotation_xyzw[3] = v.pose.orientation.w;
        dest.fov_left = v.fov.angleLeft;
        dest.fov_right = v.fov.angleRight;
        dest.fov_up = v.fov.angleUp;
        dest.fov_down = v.fov.angleDown;
    }
    return 0;
}

int32_t nh3vr_xr_head_pose(nh3vr_pose *out) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!out || !viewSpace || !frameState.predictedDisplayTime)
        return fail("head pose unavailable", -1018);
    memset(out, 0, sizeof(*out));
    XrSpaceLocation location = {};
    location.type = XR_TYPE_SPACE_LOCATION;
    CHECK(pLocateSpace(viewSpace, space, frameState.predictedDisplayTime, &location));
    if ((location.locationFlags &
         (XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT)) !=
        (XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT))
        return NH3VR_XR_NOT_RUNNING;
    auto &v = location.pose;
    out->position[0] = v.position.x;
    out->position[1] = v.position.y;
    out->position[2] = -v.position.z;
    out->rotation_xyzw[0] = -v.orientation.x;
    out->rotation_xyzw[1] = -v.orientation.y;
    out->rotation_xyzw[2] = v.orientation.z;
    out->rotation_xyzw[3] = v.orientation.w;
    return 0;
}

static nh3vr_pose unityPose(XrPosef pose) {
    return {{pose.position.x, pose.position.y, -pose.position.z},
            {-pose.orientation.x, -pose.orientation.y, pose.orientation.z, pose.orientation.w}};
}
static XrPosef withControllerOffset(XrPosef pose, bool right, bool aim) {
    XrPosef offset = IVXRIndexControllerOffset(right, aim);
    simd_quatf rotation = simd_quaternion((simd_float4){pose.orientation.x, pose.orientation.y,
                                                        pose.orientation.z, pose.orientation.w});
    simd_quatf relative = simd_quaternion((simd_float4){
        offset.orientation.x, offset.orientation.y, offset.orientation.z, offset.orientation.w});
    simd_float3 p =
        (simd_float3){pose.position.x, pose.position.y, pose.position.z} +
        simd_act(rotation, (simd_float3){offset.position.x, offset.position.y, offset.position.z});
    simd_quatf q = simd_mul(rotation, relative);
    return {{q.vector.x, q.vector.y, q.vector.z, q.vector.w}, {p.x, p.y, p.z}};
}
int32_t nh3vr_xr_locate_device(nh3vr_device *device) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!device || device->role < NH3VR_HEAD || device->role > NH3VR_RIGHT)
        return NH3VR_INVALID_ARGUMENT;
    if (!session)
        return NH3VR_XR_NOT_RUNNING;
    device->flags &=
        ~(NH3VR_POSE_VALID | NH3VR_POSE_TRACKED | NH3VR_VELOCITY_VALID | NH3VR_CONTROLLER_OFFSETS);
    device->tracking_pose = {};
    device->grip_pose = {};
    device->aim_pose = {};
    memset(device->linear_velocity, 0, sizeof(device->linear_velocity));
    memset(device->angular_velocity, 0, sizeof(device->angular_velocity));
    if (!running || !frameState.predictedDisplayTime)
        return 0;
    XrSpace source =
        device->role == NH3VR_HEAD ? viewSpace : controllerSpaces[device->role - NH3VR_LEFT];
    XrSpaceVelocity velocity = {};
    velocity.type = XR_TYPE_SPACE_VELOCITY;
    XrSpaceLocation location = {};
    location.type = XR_TYPE_SPACE_LOCATION;
    location.next = &velocity;
    CHECK(pLocateSpace(source, space, frameState.predictedDisplayTime, &location));
    const XrSpaceLocationFlags valid =
        XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    if ((location.locationFlags & valid) != valid)
        return 0;
    device->tracking_pose = unityPose(location.pose);
    device->flags |= NH3VR_POSE_VALID;
    const XrSpaceLocationFlags tracked =
        XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
    if ((location.locationFlags & tracked) == tracked)
        device->flags |= NH3VR_POSE_TRACKED;
    if (device->role != NH3VR_HEAD) {
        device->grip_pose =
            unityPose(withControllerOffset(location.pose, device->role == NH3VR_RIGHT, false));
        device->aim_pose =
            unityPose(withControllerOffset(location.pose, device->role == NH3VR_RIGHT, true));
        device->flags |= NH3VR_CONTROLLER_OFFSETS;
    }
    const XrSpaceVelocityFlags velocities =
        XR_SPACE_VELOCITY_LINEAR_VALID_BIT | XR_SPACE_VELOCITY_ANGULAR_VALID_BIT;
    if ((velocity.velocityFlags & velocities) == velocities) {
        device->linear_velocity[0] = velocity.linearVelocity.x;
        device->linear_velocity[1] = velocity.linearVelocity.y;
        device->linear_velocity[2] = -velocity.linearVelocity.z;
        device->angular_velocity[0] = -velocity.angularVelocity.x;
        device->angular_velocity[1] = -velocity.angularVelocity.y;
        device->angular_velocity[2] = velocity.angularVelocity.z;
        device->flags |= NH3VR_VELOCITY_VALID;
    }
    return 0;
}

float nh3vr_xr_display_frequency(void) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    return frameState.predictedDisplayPeriod > 0 ? 1e9f / frameState.predictedDisplayPeriod : 0;
}
int32_t nh3vr_xr_haptic(uint32_t role, float amplitude, float frequency, float duration) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!running)
        return NH3VR_XR_NOT_RUNNING;
    if (sessionState != XR_SESSION_STATE_FOCUSED)
        return XR_SESSION_NOT_FOCUSED;
    if ((role != 2 && role != 3) || !std::isfinite(amplitude) || amplitude < 0 || amplitude > 1 ||
        !std::isfinite(frequency) || frequency < 0 || !std::isfinite(duration) || duration <= 0 ||
        duration > 10)
        return -1019;
    XrActiveActionSet activeSet = {hapticSet, XR_NULL_PATH};
    XrActionsSyncInfo sync = {};
    sync.type = XR_TYPE_ACTIONS_SYNC_INFO;
    sync.countActiveActionSets = 1;
    sync.activeActionSets = &activeSet;
    XrResult result = pSyncActions(session, &sync);
    if (result != XR_SUCCESS)
        return result;
    XrHapticActionInfo info = {};
    info.type = XR_TYPE_HAPTIC_ACTION_INFO;
    info.action = hapticAction;
    info.subactionPath = handPaths[role - 2];
    XrHapticVibration vibration = {};
    vibration.type = XR_TYPE_HAPTIC_VIBRATION;
    vibration.amplitude = amplitude;
    vibration.frequency = frequency;
    vibration.duration = (XrDuration)(duration * 1e9);
    return pApplyHapticFeedback(session, &info, (const XrHapticBaseHeader *)&vibration);
}

int32_t nh3vr_xr_stage_bounds(float *outWidth, float *outDepth) {
    std::lock_guard<std::recursive_mutex> guard(mutex);
    if (!outWidth || !outDepth)
        return -1001;
    *outWidth = *outDepth = 0;
    if (!session)
        return NH3VR_XR_NOT_RUNNING;
    XrExtent2Df bounds = {};
    XrResult result = pGetReferenceSpaceBoundsRect(session, XR_REFERENCE_SPACE_TYPE_STAGE, &bounds);
    if (result == XR_SUCCESS) {
        *outWidth = bounds.width;
        *outDepth = bounds.height;
    }
    return result;
}

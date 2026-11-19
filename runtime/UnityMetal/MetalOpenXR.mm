#define XR_NO_PROTOTYPES
#define XR_USE_GRAPHICS_API_METAL
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"
#include "MetalOpenXR.h"

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <dlfcn.h>
#include <mutex>
#include <simd/simd.h>
#include <string>
#include <vector>

namespace {

constexpr auto stereo = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
constexpr auto validPose = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
constexpr auto trackedPose = XR_SPACE_LOCATION_POSITION_TRACKED_BIT | XR_SPACE_LOCATION_ORIENTATION_TRACKED_BIT;
constexpr std::array<const char *, 2> hands = {"/user/hand/left", "/user/hand/right"};

#define XR_CALLS(X) \
    X(DestroyInstance) X(GetInstanceProperties) X(GetSystem) X(GetSystemProperties) \
    X(EnumerateViewConfigurationViews) X(EnumerateEnvironmentBlendModes) \
    X(GetMetalGraphicsRequirementsKHR) X(CreateSession) X(DestroySession) X(BeginSession) \
    X(EndSession) X(PollEvent) X(CreateReferenceSpace) X(GetReferenceSpaceBoundsRect) X(DestroySpace) X(LocateSpace) \
    X(LocateViews) X(EnumerateSwapchainFormats) X(CreateSwapchain) X(DestroySwapchain) \
    X(EnumerateSwapchainImages) X(AcquireSwapchainImage) X(WaitSwapchainImage) X(ReleaseSwapchainImage) \
    X(WaitFrame) X(BeginFrame) X(EndFrame) X(StringToPath) X(CreateActionSet) X(DestroyActionSet) \
    X(CreateAction) X(SuggestInteractionProfileBindings) X(AttachSessionActionSets) X(GetCurrentInteractionProfile) \
    X(CreateActionSpace) X(SyncActions) X(GetActionStatePose) X(GetActionStateFloat) \
    X(GetActionStateVector2f) X(GetActionStateBoolean) X(ApplyHapticFeedback)

struct Dispatch {
    PFN_xrGetInstanceProcAddr GetInstanceProcAddr = nullptr;
#define DECLARE(name) PFN_xr##name name = nullptr;
    XR_CALLS(DECLARE)
#undef DECLARE

    template<class Func>
    XrResult load(XrInstance instance, const char *name, Func &target) {
        PFN_xrVoidFunction address = nullptr;
        XrResult result = GetInstanceProcAddr(instance, name, &address);
        target = reinterpret_cast<Func>(address);
        return XR_SUCCEEDED(result) && !address ? XR_ERROR_FUNCTION_UNSUPPORTED : result;
    }
};

enum ActionSlot {
    GripPose, AimPose, Haptic, TriggerValue, SqueezeValue, SqueezeForce, TrackpadForce,
    Trackpad, Thumbstick, TriggerClick, ThumbstickClick, PrimaryClick, SecondaryClick,
    SystemClick, TriggerTouch, ThumbstickTouch, TrackpadTouch, PrimaryTouch, SecondaryTouch,
    ActionCount
};

struct Binding {
    const char *name;
    const char *component;
    XrActionType type;
};

constexpr std::array<Binding, ActionCount> indexBindings = {{
    {"grip_pose", "input/grip/pose", XR_ACTION_TYPE_POSE_INPUT},
    {"aim_pose", "input/aim/pose", XR_ACTION_TYPE_POSE_INPUT},
    {"haptic", "output/haptic", XR_ACTION_TYPE_VIBRATION_OUTPUT},
    {"trigger", "input/trigger/value", XR_ACTION_TYPE_FLOAT_INPUT},
    {"squeeze", "input/squeeze/value", XR_ACTION_TYPE_FLOAT_INPUT},
    {"squeeze_force", "input/squeeze/force", XR_ACTION_TYPE_FLOAT_INPUT},
    {"trackpad_force", "input/trackpad/force", XR_ACTION_TYPE_FLOAT_INPUT},
    {"trackpad", "input/trackpad", XR_ACTION_TYPE_VECTOR2F_INPUT},
    {"thumbstick", "input/thumbstick", XR_ACTION_TYPE_VECTOR2F_INPUT},
    {"trigger_click", "input/trigger/click", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"thumbstick_click", "input/thumbstick/click", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"primary_click", "input/a/click", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"secondary_click", "input/b/click", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"system_click", "input/system/click", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"trigger_touch", "input/trigger/touch", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"thumbstick_touch", "input/thumbstick/touch", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"trackpad_touch", "input/trackpad/touch", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"primary_touch", "input/a/touch", XR_ACTION_TYPE_BOOLEAN_INPUT},
    {"secondary_touch", "input/b/touch", XR_ACTION_TYPE_BOOLEAN_INPUT}
}};

constexpr NSString *copyShader = @R"metal(
#include <metal_stdlib>
using namespace metal;
struct EyeCopy {
    float4 foreground, background, position, rotation, fov;
};
vertex float4 copyVertex(uint i [[vertex_id]]) {
    return float4(i == 1 ? 3.0 : -1.0, i == 2 ? 3.0 : -1.0, 0.0, 1.0);
}
fragment float4 copyFragment(float4 p [[position]], texture2d<float> source [[texture(0)]],
                             constant EyeCopy &u [[buffer(0)]]) {
    uint2 pixel = uint2(p.xy);
    // Camera.Render writes Unity render textures with the opposite row order
    // to OpenXR Metal images. Keep this conversion even for an sRGB source.
    pixel.y = source.get_height() - 1 - pixel.y;
    float3 color = source.read(pixel).rgb;
    if (u.position.w > 0.0) {
        float2 uv = p.xy / float2(source.get_width(), source.get_height());
        float3 ray = normalize(float3(mix(u.fov.x, u.fov.y, uv.x), mix(u.fov.z, u.fov.w, uv.y), -1.0));
        ray += 2.0 * cross(u.rotation.xyz, cross(u.rotation.xyz, ray) + u.rotation.w * ray);
        float3 grid = mix(float3(0.008), u.background.rgb, u.background.a);
        if (ray.y < -0.0001 && u.position.y > 0.0) {
            float distanceToFloor = -u.position.y / ray.y;
            float2 floorPoint = u.position.xz + ray.xz * distanceToFloor;
            float2 spacing = abs(fract(floorPoint + 0.5) - 0.5);
            float width = max(max(fwidth(floorPoint.x), fwidth(floorPoint.y)), 0.001);
            grid += (1.0 - smoothstep(0.012, 0.012 + width, min(spacing.x, spacing.y))) *
                    0.1 * exp(-distanceToFloor * 0.05);
        }
        color = mix(color, grid, u.position.w);
    }
    return float4(mix(color, u.foreground.rgb, u.foreground.a), 1.0);
}
)metal";

struct EyeCopy {
    simd_float4 foreground, background, position, rotation, fov;
};

NhxrVector vector(XrVector3f v) { return {v.x, v.y, v.z}; }
NhxrPose pose(XrPosef p) {
    return {vector(p.position), {p.orientation.x, p.orientation.y, p.orientation.z, p.orientation.w}};
}
NhxrView view(XrView v) {
    return {pose(v.pose), v.fov.angleLeft, v.fov.angleRight, v.fov.angleUp, v.fov.angleDown};
}

struct Eye {
    XrSwapchain swapchain = XR_NULL_HANDLE;
    std::vector<XrSwapchainImageMetalKHR> images;
    uint32_t index = 0;
    bool ready = false;
};

enum class Phase { Idle, Begun, Queued, Drawing };

class Session {
public:
    Dispatch xr;
    XrInstance instance = XR_NULL_HANDLE;
    XrSession session = XR_NULL_HANDLE;
    XrSpace stage = XR_NULL_HANDLE;
    XrSpace head = XR_NULL_HANDLE;
    XrActionSet actionSet = XR_NULL_HANDLE;
    std::array<XrAction, ActionCount> actions{};
    std::array<XrPath, 2> handPaths{};
    XrPath indexProfile = XR_NULL_PATH;
    std::array<XrPath, hands.size()> profiles{};
    std::array<XrSpace, 2> gripSpaces{}, aimSpaces{};
    std::array<NhxrHand, 2> samples{};
    std::array<XrView, 2> views{};
    std::array<Eye, 2> eyes;
    XrSessionState state = XR_SESSION_STATE_UNKNOWN;
    XrFrameState timing{};
    bool running = false;
    bool exiting = false;
    bool failure = false;
    bool closeRequested = false;
    uint32_t width = 0, height = 0;
    NhxrOverlay overlay{};
    uint64_t sequence = 0, completed = 0, skipped = 0;
    uint64_t completedSequence = 0, unityGpuNs = 0, endFrameNs = 0;
    Phase phase = Phase::Idle;
    id<MTLCommandQueue> queue = nil;
    id<MTLRenderPipelineState> copyPipeline = nil;
    id<MTLTexture> sources[2] = {nil, nil};
    std::string error, systemName;

    // The Khronos loader owns process-global dispatch data. Retain it after
    // closing a session and require the same image if the player starts again.
    void *loader = nullptr;
    std::string loaderPath;

    int fail(std::string message) {
        if (!failure) {
            error = std::move(message);
            std::fprintf(stderr, "NATIVEH3VR_OPENXR_ERROR %s\n", error.c_str());
        }
        failure = true;
        return -1;
    }

    bool check(XrResult result, const char *operation) {
        if (XR_SUCCEEDED(result)) return true;
        fail(std::string(operation) + " returned " + std::to_string(result));
        return false;
    }

#define CHECK(call, ...) if (!check(xr.call(__VA_ARGS__), "xr" #call)) return false

    bool path(const char *name, XrPath &value) {
        CHECK(StringToPath, instance, name, &value);
        return true;
    }

    bool createActionSpace(ActionSlot slot, unsigned hand, XrSpace &result) {
        XrActionSpaceCreateInfo create{};
        create.type = XR_TYPE_ACTION_SPACE_CREATE_INFO;
        create.action = actions[slot];
        create.subactionPath = handPaths[hand];
        create.poseInActionSpace.orientation.w = 1;
        CHECK(CreateActionSpace, session, &create, &result);
        return true;
    }

    bool setupActions() {
        XrActionSetCreateInfo set{};
        set.type = XR_TYPE_ACTION_SET_CREATE_INFO;
        std::strcpy(set.actionSetName, "player");
        std::strcpy(set.localizedActionSetName, "Player");
        CHECK(CreateActionSet, instance, &set, &actionSet);
        for (unsigned hand = 0; hand < hands.size(); hand++)
            if (!path(hands[hand], handPaths[hand])) return false;

        std::vector<XrActionSuggestedBinding> suggested;
        for (unsigned i = 0; i < ActionCount; i++) {
            const auto &binding = indexBindings[i];
            XrActionCreateInfo create{};
            create.type = XR_TYPE_ACTION_CREATE_INFO;
            create.actionType = binding.type;
            std::strcpy(create.actionName, binding.name);
            std::strcpy(create.localizedActionName, binding.name);
            create.countSubactionPaths = handPaths.size();
            create.subactionPaths = handPaths.data();
            CHECK(CreateAction, actionSet, &create, &actions[i]);
            for (const char *hand : hands) {
                const std::string source = std::string(hand) + "/" + binding.component;
                XrPath component;
                if (!path(source.c_str(), component)) return false;
                suggested.push_back({actions[i], component});
            }
        }
        XrInteractionProfileSuggestedBinding profile{};
        profile.type = XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING;
        if (!path("/interaction_profiles/valve/index_controller", indexProfile))
            return false;
        profile.interactionProfile = indexProfile;
        profile.countSuggestedBindings = suggested.size();
        profile.suggestedBindings = suggested.data();
        CHECK(SuggestInteractionProfileBindings, instance, &profile);
        XrSessionActionSetsAttachInfo attach{};
        attach.type = XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO;
        attach.countActionSets = 1;
        attach.actionSets = &actionSet;
        CHECK(AttachSessionActionSets, session, &attach);
        for (unsigned i = 0; i < hands.size(); i++)
            if (!createActionSpace(GripPose, i, gripSpaces[i]) || !createActionSpace(AimPose, i, aimSpaces[i]))
                return false;
        return true;
    }

    bool setupGraphics(XrSystemId system, uint32_t requestedWidth, uint32_t requestedHeight) {
        XrGraphicsRequirementsMetalKHR requirements{};
        requirements.type = XR_TYPE_GRAPHICS_REQUIREMENTS_METAL_KHR;
        CHECK(GetMetalGraphicsRequirementsKHR, instance, system, &requirements);
        id<MTLDevice> device = (__bridge id<MTLDevice>)requirements.metalDevice;
        queue = [device newCommandQueue];
        if (!queue) return fail("OpenXR Metal command queue creation failed") == 0;

        NSError *metalError = nil;
        id<MTLLibrary> library = [device newLibraryWithSource:copyShader options:nil error:&metalError];
        if (!library) return fail(metalError.localizedDescription.UTF8String) == 0;
        MTLRenderPipelineDescriptor *pipeline = [MTLRenderPipelineDescriptor new];
        pipeline.vertexFunction = [library newFunctionWithName:@"copyVertex"];
        pipeline.fragmentFunction = [library newFunctionWithName:@"copyFragment"];
        pipeline.colorAttachments[0].pixelFormat = MTLPixelFormatRGBA8Unorm_sRGB;
        copyPipeline = [device newRenderPipelineStateWithDescriptor:pipeline error:&metalError];
        if (!copyPipeline) return fail(metalError.localizedDescription.UTF8String) == 0;

        XrGraphicsBindingMetalKHR binding{};
        binding.type = XR_TYPE_GRAPHICS_BINDING_METAL_KHR;
        binding.commandQueue = (__bridge void *)queue;
        XrSessionCreateInfo create{};
        create.type = XR_TYPE_SESSION_CREATE_INFO;
        create.next = &binding;
        create.systemId = system;
        CHECK(CreateSession, instance, &create, &session);

        std::array<XrViewConfigurationView, 2> config{};
        for (auto &entry : config) entry.type = XR_TYPE_VIEW_CONFIGURATION_VIEW;
        uint32_t count = 0;
        CHECK(EnumerateViewConfigurationViews, instance, system, stereo, config.size(), &count, config.data());
        if (count != config.size()) return fail("OpenXR system has no primary stereo view configuration") == 0;
        width = requestedWidth ? requestedWidth : config[0].recommendedImageRectWidth;
        height = requestedHeight ? requestedHeight : config[0].recommendedImageRectHeight;
        for (const auto &v : config)
            if (!width || !height || width > v.maxImageRectWidth || height > v.maxImageRectHeight)
                return fail("Requested eye size exceeds OpenXR stereo view limits") == 0;

        CHECK(EnumerateSwapchainFormats, session, 0, &count, nullptr);
        std::vector<int64_t> formats(count);
        CHECK(EnumerateSwapchainFormats, session, count, &count, formats.data());
        if (std::find(formats.begin(), formats.end(), MTLPixelFormatRGBA8Unorm_sRGB) == formats.end())
            return fail("OpenXR runtime did not expose Metal RGBA8 sRGB") == 0;
        for (auto &eye : eyes) {
            XrSwapchainCreateInfo desired{};
            desired.type = XR_TYPE_SWAPCHAIN_CREATE_INFO;
            desired.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT;
            desired.format = MTLPixelFormatRGBA8Unorm_sRGB;
            desired.sampleCount = 1;
            desired.width = width;
            desired.height = height;
            desired.faceCount = desired.arraySize = desired.mipCount = 1;
            CHECK(CreateSwapchain, session, &desired, &eye.swapchain);
            CHECK(EnumerateSwapchainImages, eye.swapchain, 0, &count, nullptr);
            if (!count) return fail("OpenXR runtime returned an empty swapchain") == 0;
            eye.images.assign(count, {XR_TYPE_SWAPCHAIN_IMAGE_METAL_KHR, nullptr, nullptr});
            CHECK(EnumerateSwapchainImages, eye.swapchain, count, &count,
                  reinterpret_cast<XrSwapchainImageBaseHeader *>(eye.images.data()));
        }
        return true;
    }

    bool setup(const char *pathToLoader, uint32_t eyeWidth, uint32_t eyeHeight) {
        const char *runtime = std::getenv("XR_RUNTIME_JSON");
        if (!pathToLoader || pathToLoader[0] != '/' || !runtime || runtime[0] != '/')
            return fail("Supply absolute loader and process-scoped XR_RUNTIME_JSON paths") == 0;
        if (loader && loaderPath != pathToLoader)
            return fail("Cannot change the OpenXR loader inside a Unity process") == 0;
        if (!loader) {
            loader = dlopen(pathToLoader, RTLD_NOW | RTLD_LOCAL);
            if (!loader) return fail(dlerror()) == 0;
            loaderPath = pathToLoader;
        }
        xr.GetInstanceProcAddr = reinterpret_cast<PFN_xrGetInstanceProcAddr>(dlsym(loader, "xrGetInstanceProcAddr"));
        if (!xr.GetInstanceProcAddr) return fail("Invalid OpenXR loader image") == 0;
        PFN_xrCreateInstance createInstance = nullptr;
        if (!check(xr.load(XR_NULL_HANDLE, "xrCreateInstance", createInstance), "resolve xrCreateInstance"))
            return false;
        const char *extension = XR_KHR_METAL_ENABLE_EXTENSION_NAME;
        XrInstanceCreateInfo create{};
        create.type = XR_TYPE_INSTANCE_CREATE_INFO;
        std::strcpy(create.applicationInfo.applicationName, "NativeH3VR Unity");
        std::strcpy(create.applicationInfo.engineName, "Unity");
        create.applicationInfo.apiVersion = XR_API_VERSION_1_1;
        create.enabledExtensionCount = 1;
        create.enabledExtensionNames = &extension;
        if (!check(createInstance(&create, &instance), "xrCreateInstance")) return false;
#define LOAD(name) if (!check(xr.load(instance, "xr" #name, xr.name), "resolve xr" #name)) return false;
        XR_CALLS(LOAD)
#undef LOAD
        XrSystemGetInfo request{};
        request.type = XR_TYPE_SYSTEM_GET_INFO;
        request.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
        XrSystemId system = XR_NULL_SYSTEM_ID;
        CHECK(GetSystem, instance, &request, &system);
        XrSystemProperties properties{};
        properties.type = XR_TYPE_SYSTEM_PROPERTIES;
        CHECK(GetSystemProperties, instance, system, &properties);
        systemName = properties.systemName;
        uint32_t blendCount = 0;
        CHECK(EnumerateEnvironmentBlendModes, instance, system, stereo, 0, &blendCount, nullptr);
        std::vector<XrEnvironmentBlendMode> modes(blendCount);
        CHECK(EnumerateEnvironmentBlendModes, instance, system, stereo, blendCount, &blendCount, modes.data());
        if (std::find(modes.begin(), modes.end(), XR_ENVIRONMENT_BLEND_MODE_OPAQUE) == modes.end())
            return fail("OpenXR runtime does not offer opaque stereo composition") == 0;
        if (!setupGraphics(system, eyeWidth, eyeHeight)) return false;
        XrReferenceSpaceCreateInfo space{};
        space.type = XR_TYPE_REFERENCE_SPACE_CREATE_INFO;
        space.poseInReferenceSpace.orientation.w = 1;
        space.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
        CHECK(CreateReferenceSpace, session, &space, &stage);
        space.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
        CHECK(CreateReferenceSpace, session, &space, &head);
        if (!setupActions()) return false;

        XrInstanceProperties runtimeProperties{};
        runtimeProperties.type = XR_TYPE_INSTANCE_PROPERTIES;
        CHECK(GetInstanceProperties, instance, &runtimeProperties);
        std::fprintf(stdout, "NATIVEH3VR_OPENXR runtime=%s runtimeVersion=%u.%u.%u device=%s eye=%ux%u\n",
                     runtimeProperties.runtimeName, unsigned(XR_VERSION_MAJOR(runtimeProperties.runtimeVersion)),
                     unsigned(XR_VERSION_MINOR(runtimeProperties.runtimeVersion)),
                     unsigned(XR_VERSION_PATCH(runtimeProperties.runtimeVersion)), queue.device.name.UTF8String,
                     width, height);
        std::fflush(stdout);
        return true;
    }

    bool poll() {
        XrEventDataBuffer data{};
        for (;;) {
            data.type = XR_TYPE_EVENT_DATA_BUFFER;
            data.next = nullptr;
            XrResult result = xr.PollEvent(instance, &data);
            if (result == XR_EVENT_UNAVAILABLE) return true;
            if (!check(result, "xrPollEvent")) return false;
            if (data.type == XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) exiting = true;
            if (data.type == XR_TYPE_EVENT_DATA_INTERACTION_PROFILE_CHANGED) {
                const auto &change = reinterpret_cast<const XrEventDataInteractionProfileChanged &>(data);
                if (change.session == session) profiles = {};
            }
            if (data.type == XR_TYPE_EVENT_DATA_EVENTS_LOST) profiles = {};
            if (data.type != XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) continue;
            const auto &change = reinterpret_cast<const XrEventDataSessionStateChanged &>(data);
            if (change.session != session) continue;
            state = change.state;
            if (state == XR_SESSION_STATE_READY && !running) {
                XrSessionBeginInfo begin{};
                begin.type = XR_TYPE_SESSION_BEGIN_INFO;
                begin.primaryViewConfigurationType = stereo;
                CHECK(BeginSession, session, &begin);
                running = true;
            }
            if (state == XR_SESSION_STATE_STOPPING && running) {
                CHECK(EndSession, session);
                running = false;
            }
            if (state == XR_SESSION_STATE_EXITING || state == XR_SESSION_STATE_LOSS_PENDING)
                exiting = true;
        }
    }

    XrActionStateGetInfo readAction(ActionSlot slot, unsigned hand) const {
        XrActionStateGetInfo read{};
        read.type = XR_TYPE_ACTION_STATE_GET_INFO;
        read.action = actions[slot];
        read.subactionPath = handPaths[hand];
        return read;
    }

    float readFloat(ActionSlot slot, unsigned hand) {
        auto read = readAction(slot, hand);
        XrActionStateFloat value{};
        value.type = XR_TYPE_ACTION_STATE_FLOAT;
        if (!check(xr.GetActionStateFloat(session, &read, &value), "xrGetActionStateFloat")) return 0;
        return value.isActive ? value.currentState : 0;
    }

    bool readBit(ActionSlot slot, unsigned hand) {
        auto read = readAction(slot, hand);
        XrActionStateBoolean value{};
        value.type = XR_TYPE_ACTION_STATE_BOOLEAN;
        if (!check(xr.GetActionStateBoolean(session, &read, &value), "xrGetActionStateBoolean")) return false;
        return value.isActive && value.currentState;
    }

    XrVector2f readVector(ActionSlot slot, unsigned hand) {
        auto read = readAction(slot, hand);
        XrActionStateVector2f value{};
        value.type = XR_TYPE_ACTION_STATE_VECTOR2F;
        if (!check(xr.GetActionStateVector2f(session, &read, &value), "xrGetActionStateVector2f")) return {};
        return value.isActive ? value.currentState : XrVector2f{};
    }

    bool sampleHands() {
        XrActiveActionSet active{actionSet, XR_NULL_PATH};
        XrActionsSyncInfo sync{};
        sync.type = XR_TYPE_ACTIONS_SYNC_INFO;
        sync.countActiveActionSets = 1;
        sync.activeActionSets = &active;
        const XrResult result = xr.SyncActions(session, &sync);
        samples = {};
        if (result == XR_SESSION_NOT_FOCUSED || state != XR_SESSION_STATE_FOCUSED) return true;
        if (!check(result, "xrSyncActions")) return false;
        for (unsigned i = 0; i < hands.size(); i++) {
            auto read = readAction(GripPose, i);
            XrActionStatePose activePose{};
            activePose.type = XR_TYPE_ACTION_STATE_POSE;
            CHECK(GetActionStatePose, session, &read, &activePose);
            if (!activePose.isActive) continue;
            XrSpaceVelocity velocity{};
            velocity.type = XR_TYPE_SPACE_VELOCITY;
            XrSpaceLocation grip{};
            grip.type = XR_TYPE_SPACE_LOCATION;
            grip.next = &velocity;
            CHECK(LocateSpace, gripSpaces[i], stage, timing.predictedDisplayTime, &grip);
            XrSpaceLocation aim{};
            aim.type = XR_TYPE_SPACE_LOCATION;
            CHECK(LocateSpace, aimSpaces[i], stage, timing.predictedDisplayTime, &aim);
            NhxrHand &hand = samples[i];
            hand.flags = NHXR_CONNECTED | NHXR_INPUT_CURRENT;
            if (profiles[i] == XR_NULL_PATH) {
                XrInteractionProfileState profile{};
                profile.type = XR_TYPE_INTERACTION_PROFILE_STATE;
                if (XR_SUCCEEDED(xr.GetCurrentInteractionProfile(session, handPaths[i], &profile)))
                    profiles[i] = profile.interactionProfile;
            }
            if (profiles[i] == indexProfile)
                hand.profile = NHXR_PROFILE_VALVE_INDEX;
            if ((grip.locationFlags & validPose) == validPose) {
                hand.grip = pose(grip.pose);
                hand.aim = (aim.locationFlags & validPose) == validPose ? pose(aim.pose) : hand.grip;
                hand.flags |= NHXR_POSE_VALID;
            }
            if ((grip.locationFlags & trackedPose) == trackedPose) hand.flags |= NHXR_POSE_TRACKED;
            if (velocity.velocityFlags & XR_SPACE_VELOCITY_LINEAR_VALID_BIT) {
                hand.velocity = vector(velocity.linearVelocity);
                hand.flags |= NHXR_VELOCITY_VALID;
            }
            if (velocity.velocityFlags & XR_SPACE_VELOCITY_ANGULAR_VALID_BIT) {
                hand.angularVelocity = vector(velocity.angularVelocity);
                hand.flags |= NHXR_VELOCITY_VALID;
            }
            hand.trigger = readFloat(TriggerValue, i);
            hand.squeeze = readFloat(SqueezeValue, i);
            hand.squeezeForce = readFloat(SqueezeForce, i);
            hand.trackpadForce = readFloat(TrackpadForce, i);
            const auto pad = readVector(Trackpad, i), stick = readVector(Thumbstick, i);
            hand.trackpadX = pad.x;
            hand.trackpadY = pad.y;
            hand.thumbstickX = stick.x;
            hand.thumbstickY = stick.y;
            hand.buttons = (readBit(TriggerClick, i) ? 1u : 0) |
                           (hand.trackpadForce > .5f ? 2u : 0) |
                           (readBit(ThumbstickClick, i) ? 4u : 0) |
                           (readBit(PrimaryClick, i) ? 16u : 0) |
                           (readBit(SecondaryClick, i) ? 32u : 0) |
                           (hand.squeezeForce > .5f ? 128u : 0) |
                           (readBit(SystemClick, i) ? 256u : 0);
            hand.touches = (readBit(TriggerTouch, i) ? 1u : 0) |
                           (readBit(ThumbstickTouch, i) ? 4u : 0) |
                           (readBit(TrackpadTouch, i) ? 2u : 0) |
                           (readBit(PrimaryTouch, i) ? 16u : 0) |
                           (readBit(SecondaryTouch, i) ? 32u : 0);
        }
        return !failure;
    }

    bool locate(NhxrFrame &output) {
        XrViewLocateInfo locateInfo{};
        locateInfo.type = XR_TYPE_VIEW_LOCATE_INFO;
        locateInfo.viewConfigurationType = stereo;
        locateInfo.displayTime = timing.predictedDisplayTime;
        locateInfo.space = stage;
        XrViewState stateOut{};
        stateOut.type = XR_TYPE_VIEW_STATE;
        uint32_t count = 0;
        for (auto &v : views) v.type = XR_TYPE_VIEW;
        CHECK(LocateViews, session, &locateInfo, &stateOut, views.size(), &count, views.data());
        constexpr auto validViews = XR_VIEW_STATE_POSITION_VALID_BIT | XR_VIEW_STATE_ORIENTATION_VALID_BIT;
        if (count != views.size() || (stateOut.viewStateFlags & validViews) != validViews) return false;
        XrSpaceLocation headLocation{};
        headLocation.type = XR_TYPE_SPACE_LOCATION;
        CHECK(LocateSpace, head, stage, timing.predictedDisplayTime, &headLocation);
        if ((headLocation.locationFlags & validPose) != validPose) return false;
        output = {sequence, timing.predictedDisplayTime, timing.predictedDisplayPeriod,
                  width, height, uint32_t(state == XR_SESSION_STATE_FOCUSED), 0,
                  pose(headLocation.pose), view(views[0]), view(views[1])};
        return true;
    }

    bool acquire() {
        for (auto &eye : eyes) {
            XrSwapchainImageAcquireInfo acquireInfo{};
            acquireInfo.type = XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO;
            CHECK(AcquireSwapchainImage, eye.swapchain, &acquireInfo, &eye.index);
            XrSwapchainImageWaitInfo wait{};
            wait.type = XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO;
            wait.timeout = XR_INFINITE_DURATION;
            CHECK(WaitSwapchainImage, eye.swapchain, &wait);
            eye.ready = true;
            if (eye.index >= eye.images.size())
                return fail("Runtime returned an out-of-range swapchain image index") == 0;
        }
        return true;
    }

    void finish(bool rendered) {
        for (auto &eye : eyes) {
            if (!eye.ready) continue;
            XrSwapchainImageReleaseInfo release{};
            release.type = XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO;
            rendered &= check(xr.ReleaseSwapchainImage(eye.swapchain, &release), "xrReleaseSwapchainImage");
            eye.ready = false;
        }
        std::array<XrCompositionLayerProjectionView, 2> projected{};
        for (unsigned i = 0; i < projected.size(); i++) {
            auto &p = projected[i];
            p.type = XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW;
            p.pose = views[i].pose;
            p.fov = views[i].fov;
            p.subImage.swapchain = eyes[i].swapchain;
            p.subImage.imageRect.extent = {int32_t(width), int32_t(height)};
        }
        XrCompositionLayerProjection layer{};
        layer.type = XR_TYPE_COMPOSITION_LAYER_PROJECTION;
        layer.space = stage;
        layer.viewCount = projected.size();
        layer.views = projected.data();
        const XrCompositionLayerBaseHeader *layers[]{reinterpret_cast<const XrCompositionLayerBaseHeader *>(&layer)};
        XrFrameEndInfo end{};
        end.type = XR_TYPE_FRAME_END_INFO;
        end.displayTime = timing.predictedDisplayTime;
        end.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
        end.layerCount = rendered ? 1 : 0;
        end.layers = rendered ? layers : nullptr;
        const auto entered = std::chrono::steady_clock::now();
        const bool ended = check(xr.EndFrame(session, &end), "xrEndFrame");
        if (ended && rendered) {
            completed++;
            completedSequence = sequence;
            endFrameNs = std::chrono::duration_cast<std::chrono::nanoseconds>(
                std::chrono::steady_clock::now() - entered).count();
        } else {
            skipped++;
        }
        sources[0] = sources[1] = nil;
        phase = Phase::Idle;
    }

    void destroy() {
        for (auto &eye : eyes) {
            if (eye.swapchain) xr.DestroySwapchain(eye.swapchain);
            eye = {};
        }
        for (XrSpace space : gripSpaces) if (space) xr.DestroySpace(space);
        for (XrSpace space : aimSpaces) if (space) xr.DestroySpace(space);
        if (head) xr.DestroySpace(head);
        if (stage) xr.DestroySpace(stage);
        if (session) xr.DestroySession(session);
        if (actionSet) xr.DestroyActionSet(actionSet);
        if (instance && xr.DestroyInstance) xr.DestroyInstance(instance);
        head = stage = XR_NULL_HANDLE;
        gripSpaces = aimSpaces = {};
        actions = {};
        actionSet = XR_NULL_HANDLE;
        session = XR_NULL_HANDLE;
        instance = XR_NULL_HANDLE;
        queue = nil;
        copyPipeline = nil;
        sources[0] = sources[1] = nil;
        samples = {};
        profiles = {};
        state = XR_SESSION_STATE_UNKNOWN;
        phase = Phase::Idle;
        running = exiting = closeRequested = false;
    }
#undef CHECK
};

Session bridge;
std::mutex mutex;
IUnityGraphicsMetalV1 *unityMetal = nullptr;
dispatch_queue_t releases = nil;

void UNITY_INTERFACE_API draw(int eventId) {
    std::lock_guard<std::mutex> guard(mutex);
    if (eventId != 1 || bridge.phase != Phase::Queued) return;
    @autoreleasepool {
        id<MTLCommandBuffer> commands = unityMetal ? unityMetal->CurrentCommandBuffer() : nil;
        if (!commands || commands.device != bridge.queue.device) {
            bridge.fail("Unity and OpenXR are not using the same Metal device");
            bridge.finish(false);
            return;
        }
        unityMetal->EndCurrentCommandEncoder();
        bridge.phase = Phase::Drawing;
        bool encoded = true;
        for (unsigned i = 0; i < bridge.eyes.size(); i++) {
            const auto &eye = bridge.eyes[i];
            id<MTLTexture> target = (__bridge id<MTLTexture>)eye.images[eye.index].texture;
            MTLRenderPassDescriptor *pass = [MTLRenderPassDescriptor renderPassDescriptor];
            pass.colorAttachments[0].texture = target;
            pass.colorAttachments[0].loadAction = MTLLoadActionDontCare;
            pass.colorAttachments[0].storeAction = MTLStoreActionStore;
            id<MTLRenderCommandEncoder> encoder = [commands renderCommandEncoderWithDescriptor:pass];
            if (!encoder) {
                bridge.fail("Metal eye copy encoder creation failed");
                encoded = false;
                break;
            }
            encoder.label = i == 0 ? @"OpenXR left eye copy" : @"OpenXR right eye copy";
            [encoder setRenderPipelineState:bridge.copyPipeline];
            [encoder setFragmentTexture:bridge.sources[i] atIndex:0];
            const auto &view = bridge.views[i];
            const auto &overlay = bridge.overlay;
            const EyeCopy uniforms{
                {overlay.foreground[0], overlay.foreground[1], overlay.foreground[2], overlay.foreground[3]},
                {overlay.background[0], overlay.background[1], overlay.background[2], overlay.background[3]},
                {view.pose.position.x, view.pose.position.y, view.pose.position.z, overlay.grid},
                {view.pose.orientation.x, view.pose.orientation.y, view.pose.orientation.z, view.pose.orientation.w},
                {std::tan(view.fov.angleLeft), std::tan(view.fov.angleRight), std::tan(view.fov.angleUp), std::tan(view.fov.angleDown)},
            };
            [encoder setFragmentBytes:&uniforms length:sizeof(uniforms) atIndex:0];
            [encoder drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
            [encoder endEncoding];
        }
        const bool copiedBoth = encoded;
        [commands addCompletedHandler:^(id<MTLCommandBuffer> done) {
            // Never block Unity's driver completion thread on xrEndFrame.
            dispatch_async(releases, ^{
                std::lock_guard<std::mutex> finishGuard(mutex);
                const bool complete = done.status == MTLCommandBufferStatusCompleted;
                if (!complete) bridge.fail("Unity Metal command buffer did not complete");
                const double seconds = done.GPUEndTime - done.GPUStartTime;
                bridge.unityGpuNs = seconds > 0 && seconds < 10 ? uint64_t(seconds * 1e9) : 0;
                bridge.finish(complete && copiedBoth);
                if (bridge.closeRequested) bridge.destroy();
            });
        }];
    }
}

} // namespace

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces *interfaces) {
    std::lock_guard<std::mutex> guard(mutex);
    unityMetal = interfaces->Get<IUnityGraphicsMetalV1>();
    if (!releases) releases = dispatch_queue_create("org.nativeh3vr.xr.release", DISPATCH_QUEUE_SERIAL);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    std::lock_guard<std::mutex> guard(mutex);
    unityMetal = nullptr;
    // Unity has stopped scheduling plugin events. A submitted GPU callback can
    // still own swapchain images, so let the completion handler clean them up.
    bridge.closeRequested = true;
    if (bridge.phase == Phase::Drawing) return;
    if (bridge.phase != Phase::Idle) bridge.finish(false);
    bridge.destroy();
}

uint32_t nhxr_frame_size() { return sizeof(NhxrFrame); }
uint32_t nhxr_hand_size() { return sizeof(NhxrHand); }

const char *nhxr_last_error() {
    std::lock_guard<std::mutex> guard(mutex);
    thread_local std::string error;
    error = bridge.error;
    return error.c_str();
}

const char *nhxr_system_name() {
    std::lock_guard<std::mutex> guard(mutex);
    thread_local std::string name;
    name = bridge.systemName;
    return name.c_str();
}

int nhxr_open(const char *path, uint32_t width, uint32_t height) {
    std::lock_guard<std::mutex> guard(mutex);
    @autoreleasepool {
        if (bridge.instance) return -1;
        bridge.error.clear();
        bridge.failure = false;
        bridge.sequence = bridge.completed = bridge.skipped = 0;
        bridge.completedSequence = bridge.unityGpuNs = bridge.endFrameNs = 0;
        if (bridge.setup(path, width, height) && bridge.poll()) return 0;
        bridge.destroy();
        return -1;
    }
}

int nhxr_begin(NhxrFrame *frame) {
    std::lock_guard<std::mutex> guard(mutex);
    @autoreleasepool {
        if (!frame || !bridge.instance || bridge.failure) return -1;
        if (bridge.phase != Phase::Idle) return 1;
        if (!bridge.poll()) return -1;
        if (bridge.exiting || bridge.closeRequested) return 2;
        if (!bridge.running) return 1;
        XrFrameWaitInfo wait{};
        wait.type = XR_TYPE_FRAME_WAIT_INFO;
        bridge.timing = {};
        bridge.timing.type = XR_TYPE_FRAME_STATE;
        if (!bridge.check(bridge.xr.WaitFrame(bridge.session, &wait, &bridge.timing), "xrWaitFrame")) return -1;
        XrFrameBeginInfo begin{};
        begin.type = XR_TYPE_FRAME_BEGIN_INFO;
        XrResult result = bridge.xr.BeginFrame(bridge.session, &begin);
        if (!bridge.check(result, "xrBeginFrame")) return -1;
        bridge.phase = Phase::Begun;
        bridge.sequence++;
        if (!bridge.sampleHands() || result == XR_FRAME_DISCARDED || !bridge.timing.shouldRender ||
            !bridge.locate(*frame) || !bridge.acquire()) {
            bridge.finish(false);
            return bridge.failure ? -1 : 1;
        }
        return 0;
    }
}

int nhxr_queue(uint64_t sequence, void *left, void *right, const NhxrOverlay *overlay) {
    std::lock_guard<std::mutex> guard(mutex);
    if (bridge.phase != Phase::Begun || bridge.sequence != sequence ||
        !left || !right || !overlay || !unityMetal) return bridge.fail("Invalid Unity eye submission");
    bridge.sources[0] = (__bridge id<MTLTexture>)left;
    bridge.sources[1] = (__bridge id<MTLTexture>)right;
    for (id<MTLTexture> source : bridge.sources)
        if (source.device != bridge.queue.device || source.width != bridge.width ||
            source.height != bridge.height || source.textureType != MTLTextureType2D)
            return bridge.fail("Unity eye texture does not match the runtime's Metal device or eye dimensions");
    bridge.overlay = *overlay;
    bridge.phase = Phase::Queued;
    return 0;
}

void *nhxr_render_event() { return reinterpret_cast<void *>(draw); }

int nhxr_cancel() {
    std::lock_guard<std::mutex> guard(mutex);
    if (bridge.phase == Phase::Begun) bridge.finish(false);
    return bridge.failure ? -1 : 0;
}

int nhxr_status(NhxrStatus *status) {
    std::lock_guard<std::mutex> guard(mutex);
    if (!status) return -1;
    *status = {bridge.completed, bridge.skipped, bridge.completedSequence,
               bridge.unityGpuNs, bridge.endFrameNs, uint32_t(bridge.running),
               uint32_t(bridge.running && bridge.state == XR_SESSION_STATE_FOCUSED),
               bridge.width, bridge.height};
    return bridge.failure ? -1 : 0;
}

int nhxr_hand(uint32_t hand, NhxrHand *value) {
    std::lock_guard<std::mutex> guard(mutex);
    if (!value || hand >= bridge.samples.size()) return -1;
    *value = bridge.samples[hand];
    return 0;
}

int nhxr_stage_bounds(float *width, float *depth) {
    std::lock_guard<std::mutex> guard(mutex);
    if (!width || !depth || !bridge.running) return -1;
    XrExtent2Df bounds{};
    const auto result = bridge.xr.GetReferenceSpaceBoundsRect(bridge.session, XR_REFERENCE_SPACE_TYPE_STAGE, &bounds);
    if (result != XR_SUCCESS || bounds.width <= 0 || bounds.height <= 0) return -1;
    *width = bounds.width;
    *depth = bounds.height;
    return 0;
}

int nhxr_haptic(uint32_t hand, float amplitude, float frequency, float seconds) {
    std::lock_guard<std::mutex> guard(mutex);
    if (hand >= hands.size() || !bridge.running || bridge.state != XR_SESSION_STATE_FOCUSED ||
        !std::isfinite(amplitude) || !std::isfinite(seconds) || !std::isfinite(frequency) ||
        amplitude < 0 || amplitude > 1 || seconds < 0 || seconds > 10 || frequency < 0)
        return -1;
    XrHapticActionInfo action{};
    action.type = XR_TYPE_HAPTIC_ACTION_INFO;
    action.action = bridge.actions[Haptic];
    action.subactionPath = bridge.handPaths[hand];
    XrHapticVibration vibration{};
    vibration.type = XR_TYPE_HAPTIC_VIBRATION;
    vibration.amplitude = amplitude;
    vibration.frequency = frequency;
    vibration.duration = seconds == 0 ? XR_MIN_HAPTIC_DURATION : XrDuration(seconds * 1e9);
    const auto result = bridge.xr.ApplyHapticFeedback(
        bridge.session, &action, reinterpret_cast<const XrHapticBaseHeader *>(&vibration));
    return result == XR_SUCCESS ? 0 : -1;
}

int nhxr_close() {
    std::lock_guard<std::mutex> guard(mutex);
    bridge.closeRequested = true;
    if (bridge.phase == Phase::Drawing || bridge.phase == Phase::Queued) return 1;
    if (bridge.phase == Phase::Begun) bridge.finish(false);
    bridge.destroy();
    return 0;
}

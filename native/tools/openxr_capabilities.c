/* No system/session/swapchain is created: this cannot take USB or HMD output. */
#include <dlfcn.h>
#include <openxr/openxr.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int main(int argc, char **argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: %s loader.dylib runtime.json\n", argv[0]);
        return 2;
    }
    setenv("XR_RUNTIME_JSON", argv[2], 1);
    void *lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!lib) {
        fprintf(stderr, "%s\n", dlerror());
        return 1;
    }
    PFN_xrEnumerateInstanceExtensionProperties enumerate =
        dlsym(lib, "xrEnumerateInstanceExtensionProperties");
    if (!enumerate) {
        fprintf(stderr, "Missing xrEnumerateInstanceExtensionProperties\n");
        return 1;
    }
    uint32_t count = 0;
    XrResult result = enumerate(NULL, 0, &count, NULL);
    if (XR_FAILED(result)) {
        fprintf(stderr, "extension enumeration: %d\n", result);
        return 1;
    }
    XrExtensionProperties *ext = calloc(count, sizeof(*ext));
    if (!ext)
        return 1;
    for (uint32_t i = 0; i < count; i++)
        ext[i].type = XR_TYPE_EXTENSION_PROPERTIES;
    result = enumerate(NULL, count, &count, ext);
    if (XR_FAILED(result)) {
        fprintf(stderr, "extension data: %d\n", result);
        free(ext);
        return 1;
    }
    int metal = 0;
    puts("Native OpenXR loader/runtime loaded; no XR instance, system, or session created.");
    for (uint32_t i = 0; i < count; i++) {
        printf("%s %u\n", ext[i].extensionName, ext[i].extensionVersion);
        if (!strcmp(ext[i].extensionName, "XR_KHR_metal_enable"))
            metal = 1;
    }
    free(ext);
    dlclose(lib);
    return metal ? 0 : 1;
}

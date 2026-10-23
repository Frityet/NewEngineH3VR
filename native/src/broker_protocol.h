#pragma once
#include "index_core.h"
/* Mirrors ValveIndexMacOS/native/IVTrackingIPC.m, version 3. The upstream
 * snapshot ABI and all message sizes are validated before accepting data. */
#define NH3VR_IPC_MAGIC 0x56524950u
#define NH3VR_IPC_VERSION 3u
typedef struct {
    uint32_t magic, version, operation, size;
    char name[16];
    double amplitude, duration, frequency;
} nh3vr_ipc_request;
typedef struct {
    uint32_t magic, version, size;
    int32_t result;
    idx_snapshot snapshot;
    char state_directory[1024];
} nh3vr_ipc_reply;

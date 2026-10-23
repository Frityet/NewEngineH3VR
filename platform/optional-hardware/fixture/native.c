#include <stdint.h>
#include <stdlib.h>
static int count;
void InitAsync(void) { count++; }
void SetActive(uint8_t a) {
    if (!a)
        abort();
    count++;
}
void Kick(uint8_t p) {
    if (p != 11)
        abort();
    count++;
}
void Rumble(uint8_t p, float t) {
    if (p != 12 || t != .5f)
        abort();
    count++;
}
void Shot(uint8_t k, uint8_t r, float t) {
    if (k != 13 || r != 14 || t != .6f)
        abort();
    count++;
}
uint8_t TempoToKickPower(float t) {
    if (t != 2)
        abort();
    count++;
    return 17;
}
uint8_t GetBatteryLevel(void) {
    count++;
    return 42;
}
int GetCount(void) { return count; }

/* ============================================================================================================
 * Gameplay cheats behind the launcher's mods (included once from stubs.cpp; everything is inert unless an env
 * switch below is set).
 *
 * RE (EU EBOOT, BCES00220): the hero's progress is one static object at 0x007D5F80 (vtable 0x00727080) holding
 * the same fields the save's STATE file starts with (save offset = object offset - 0x10):
 *   +0x18 float  karma         < 0 evil, > 0 good. The rank lookup covers about +-1000: +-750 reads as Hero /
 *                               Infamous, -990 and beyond reads as good again.
 *   +0x1C u32    XP available   the Powers menu's "Available XP"
 *   +0x20 u32    XP earned in total
 *   +0x24 u32 x16 unlock level of each Powers-menu power: 0 = locked ("?"), 3 = the power and all its upgrade
 *                  tiers unlocked by the story (tiers are still bought with XP and the karma tiers need the rank)
 *   +0x64 u32    blast shards collected
 * Found by scanning live guest memory for a save's values and checked in the Powers menu (XP, karma rank and every
 * power reacting to writes there).
 *
 * Env (the launcher's mods set them):
 *   CHEAT_XP=<n>                      keep available XP at n or more: spending is topped up again (infinite XP)
 *   CHEAT_KARMA=hero|infamous|<v>     hold karma at +750 / -750 / v (max good or evil rank)
 *   CHEAT_ALL_POWERS=1                every Powers-menu power unlocked with all its upgrade tiers
 *   PAD_LIGHTBAR=karma                the DualSense / DualShock 4 light bar follows the karma: white when neutral,
 *                                     blue towards Hero, red towards Infamous (cellPad's pad_sdl3.c applies it)
 * A host thread re-applies them every 250 ms: aligned 32-bit writes, so nothing tears, and a value the game
 * changes in between is simply set again. Saving keeps them -- they are part of that save from then on.
 * ============================================================================================================ */
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <thread>

/* Light bar request read by cellPad's pad_sdl3.c (0 = none, 0x01RRGGBB). selectany: pad_sdl3.c defines it too, and a
 * build without that file still links. */
extern "C" { __declspec(selectany) volatile uint32_t g_pad_lightbar_rgb = 0; }

namespace infamous_cheats {
static const uint32_t OBJ = 0x007D5F80u, VTABLE = 0x00727080u;
static const uint32_t KARMA = OBJ + 0x18u, XP = OBJ + 0x1Cu, POWERS = OBJ + 0x24u, NPOWERS = 16u, POWER_MAX = 3u;
static const float KARMA_HERO = 750.0f, KARMA_INFAMOUS = -750.0f;

static uint32_t s_xp = 0;
static int s_karma_on = 0, s_powers = 0, s_karma_led = 0;
static float s_karma = 0.0f;

static bool configure()
{
    const char* e;
    if ((e = std::getenv("CHEAT_XP")) && *e) {
        const long v = std::strtol(e, nullptr, 10);
        if (v > 0) s_xp = (uint32_t)(v > 9999999 ? 9999999 : v);
    }
    if ((e = std::getenv("CHEAT_KARMA")) && *e) {
        if (!std::strcmp(e, "hero") || !std::strcmp(e, "good")) { s_karma = KARMA_HERO; s_karma_on = 1; }
        else if (!std::strcmp(e, "infamous") || !std::strcmp(e, "evil")) { s_karma = KARMA_INFAMOUS; s_karma_on = 1; }
        else {
            char* end = nullptr; const float v = std::strtof(e, &end);
            if (end != e && v >= -900.0f && v <= 900.0f) { s_karma = v; s_karma_on = 1; }
            else std::fprintf(stderr, "[CHEATS] CHEAT_KARMA=%s ignored (hero, infamous or -900..900)\n", e);
        }
    }
    if ((e = std::getenv("CHEAT_ALL_POWERS")) && *e == '1') s_powers = 1;
    if ((e = std::getenv("PAD_LIGHTBAR")) && !std::strcmp(e, "karma")) s_karma_led = 1;
    return s_xp || s_karma_on || s_powers || s_karma_led;
}

/* karma -> light bar: white when neutral, towards blue (Hero) or red (Infamous) */
static void karma_led()
{
    const uint32_t u = vm_read32(KARMA);
    float k; std::memcpy(&k, &u, 4);
    if (!(k == k)) return;
    float t = k / KARMA_HERO;
    if (t > 1.0f) t = 1.0f;
    if (t < -1.0f) t = -1.0f;
    static const float neutral[3] = { 170.0f, 170.0f, 170.0f }, good[3] = { 0.0f, 110.0f, 255.0f }, evil[3] = { 255.0f, 10.0f, 0.0f };
    const float* side = t >= 0.0f ? good : evil;
    const float a = t >= 0.0f ? t : -t;
    uint32_t rgb = 0;
    for (int c = 0; c < 3; c++) rgb = rgb << 8 | (uint32_t)(neutral[c] + a * (side[c] - neutral[c]) + 0.5f);
    g_pad_lightbar_rgb = 0x01000000u | rgb;
}

static void apply()
{
    if (s_xp && vm_read32(XP) < s_xp) vm_write32(XP, s_xp);
    if (s_karma_on) {
        uint32_t want; std::memcpy(&want, &s_karma, 4);
        if (vm_read32(KARMA) != want) vm_write32(KARMA, want);
    }
    if (s_powers)
        for (uint32_t i = 0; i < NPOWERS; i++)
            if (vm_read32(POWERS + 4u * i) < POWER_MAX) vm_write32(POWERS + 4u * i, POWER_MAX);
}

static void run()
{
    using namespace std::chrono;
    const auto t0 = steady_clock::now();
    bool found = false, gave_up = false;
    for (;;) {
        std::this_thread::sleep_for(milliseconds(250));
        if (!vm_base) continue;
        if (vm_read32(OBJ) != VTABLE) {   /* not this EBOOT, or the object is not constructed yet */
            if (!found && !gave_up && steady_clock::now() - t0 > seconds(180)) {
                gave_up = true;
                std::fprintf(stderr, "[CHEATS] hero progress object not found at 0x%08X (vtable 0x%08X, want 0x%08X): this "
                                     "build of the game is not the one the cheats were made for -> cheats off\n",
                             OBJ, vm_read32(OBJ), VTABLE);
            }
            continue;
        }
        if (s_karma_led) karma_led();
        if (!(s_xp || s_karma_on || s_powers)) continue;
        if (!found) {
            found = true;
            char k[48] = "";
            if (s_karma_on) std::snprintf(k, sizeof k, " karma held at %+.0f", s_karma);
            std::fprintf(stderr, "[CHEATS] on (progress object 0x%08X):%s%s%s\n", OBJ, s_xp ? " infinite XP" : "", k,
                         s_powers ? " all powers" : "");
        }
        apply();
    }
}

static struct Starter {
    Starter() { if (configure()) std::thread(run).detach(); }
} s_starter;
} // namespace infamous_cheats

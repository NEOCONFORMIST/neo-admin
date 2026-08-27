#pragma once

#include <string>

struct NeoAdminCompatibilityReport
{
    bool core_engine = false;
    bool game_ban_cleanup = false;
};

// Refresh after address resolution. Optional engine features remain disabled
// when their signatures are absent instead of preventing NEO ADMIN startup.
void NeoAdminCompatibility_Refresh(bool core_engine_ready);
const NeoAdminCompatibilityReport& NeoAdminCompatibility_Get();
bool NeoAdminCompatibility_CanCleanGameBans();
std::string NeoAdminCompatibility_Describe();

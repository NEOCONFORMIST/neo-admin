#include "neo_admin_compatibility.h"

#include "addresses.h"

namespace
{
NeoAdminCompatibilityReport g_report{};
}

void NeoAdminCompatibility_Refresh(bool core_engine_ready)
{
    g_report.core_engine = core_engine_ready;
    g_report.game_ban_cleanup =
        core_engine_ready && addresses::sm_mapGcBanInformation != nullptr;
}

const NeoAdminCompatibilityReport& NeoAdminCompatibility_Get()
{
    return g_report;
}

bool NeoAdminCompatibility_CanCleanGameBans()
{
    return g_report.game_ban_cleanup;
}

std::string NeoAdminCompatibility_Describe()
{
    return std::string("core_engine=") +
        (g_report.core_engine ? "ready" : "unavailable") +
        ", game_ban_cleanup=" +
        (g_report.game_ban_cleanup ? "ready" : "disabled");
}

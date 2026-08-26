#pragma once

#include <array>
#include <string_view>

namespace neo_admin
{
struct GiveItemDefinition
{
    std::string_view entity_class;
    std::string_view display_name;
};

inline constexpr std::array kGiveItems{
    GiveItemDefinition{"weapon_cz75a", "CZ75-Auto"},
    GiveItemDefinition{"weapon_deagle", "Desert Eagle"},
    GiveItemDefinition{"weapon_elite", "Dual Berettas"},
    GiveItemDefinition{"weapon_fiveseven", "Five-SeveN"},
    GiveItemDefinition{"weapon_glock", "Glock-18"},
    GiveItemDefinition{"weapon_hkp2000", "P2000"},
    GiveItemDefinition{"weapon_p250", "P250"},
    GiveItemDefinition{"weapon_revolver", "R8 Revolver"},
    GiveItemDefinition{"weapon_tec9", "Tec-9"},
    GiveItemDefinition{"weapon_usp_silencer", "USP-S"},

    GiveItemDefinition{"weapon_mac10", "MAC-10"},
    GiveItemDefinition{"weapon_mp5sd", "MP5-SD"},
    GiveItemDefinition{"weapon_mp7", "MP7"},
    GiveItemDefinition{"weapon_mp9", "MP9"},
    GiveItemDefinition{"weapon_p90", "P90"},
    GiveItemDefinition{"weapon_bizon", "PP-Bizon"},
    GiveItemDefinition{"weapon_ump45", "UMP-45"},

    GiveItemDefinition{"weapon_ak47", "AK-47"},
    GiveItemDefinition{"weapon_aug", "AUG"},
    GiveItemDefinition{"weapon_awp", "AWP"},
    GiveItemDefinition{"weapon_famas", "FAMAS"},
    GiveItemDefinition{"weapon_g3sg1", "G3SG1"},
    GiveItemDefinition{"weapon_galilar", "Galil AR"},
    GiveItemDefinition{"weapon_m4a1_silencer", "M4A1-S"},
    GiveItemDefinition{"weapon_m4a1", "M4A4"},
    GiveItemDefinition{"weapon_scar20", "SCAR-20"},
    GiveItemDefinition{"weapon_sg556", "SG 553"},
    GiveItemDefinition{"weapon_ssg08", "SSG 08"},

    GiveItemDefinition{"weapon_m249", "M249"},
    GiveItemDefinition{"weapon_mag7", "MAG-7"},
    GiveItemDefinition{"weapon_negev", "Negev"},
    GiveItemDefinition{"weapon_nova", "Nova"},
    GiveItemDefinition{"weapon_sawedoff", "Sawed-Off"},
    GiveItemDefinition{"weapon_xm1014", "XM1014"},

    GiveItemDefinition{"weapon_decoy", "Decoy Grenade"},
    GiveItemDefinition{"weapon_flashbang", "Flashbang"},
    GiveItemDefinition{"weapon_hegrenade", "HE Grenade"},
    GiveItemDefinition{"weapon_incgrenade", "Incendiary Grenade"},
    GiveItemDefinition{"weapon_molotov", "Molotov"},
    GiveItemDefinition{"weapon_smokegrenade", "Smoke Grenade"},

    GiveItemDefinition{"item_defuser", "Defuse Kit"},
    GiveItemDefinition{"item_kevlar", "Kevlar Vest"},
    GiveItemDefinition{"item_assaultsuit", "Kevlar + Helmet"},
    GiveItemDefinition{"weapon_taser", "Zeus x27"},
};

constexpr const GiveItemDefinition* FindGiveItem(
    std::string_view entity_class)
{
    for (const GiveItemDefinition& item : kGiveItems)
    {
        if (item.entity_class == entity_class)
            return &item;
    }

    return nullptr;
}
} // namespace neo_admin

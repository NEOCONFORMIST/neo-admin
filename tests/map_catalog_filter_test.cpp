#include "voicebridge_map_catalog.h"

#include <iostream>
#include <string_view>
#include <utility>

int main()
{
    constexpr std::pair<std::string_view, bool> cases[] = {
        {"de_dust2", true},
        {"ar_baggage", true},
        {"my_custom_map", true},
        {"workshop/3274104006/de_thera", true},
        {"workshop/3484400725/zm_lila_panic_371", true},
        {"workshop\\3274104006\\de_thera", true},
        {"de_dust2_vanity", false},
        {"graphics_settings", false},
        {"lobby_mapveto", false},
        {"workshop_preview_dust2", false},
        {"editor/toolscene_lighting_de_dust_day", false},
        {"prefabs/de_dust2/de_dust2_skybox", false},
        {"templates/env_sun_entity_template", false},
        {"ui/buy_menu", false},
        {"workshop/not-a-number/de_map", false},
        {"workshop/123/prefabs/de_map", false},
        {"workshop/123/de_map_vanity", false},
        {"/de_dust2", false},
        {"", false},
    };

    for (const auto& [token, expected] : cases)
    {
        const bool actual = voicebridge::IsGameplayMapToken(token);

        if (actual != expected)
        {
            std::cerr << "unexpected catalog result for " << token
                      << ": expected " << expected
                      << ", got " << actual << '\n';
            return 1;
        }
    }

    std::cout << "Map catalog filter self-test passed.\n";
    return 0;
}

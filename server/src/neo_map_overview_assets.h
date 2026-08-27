#pragma once

#include <cstdint>
#include <string_view>
#include <vector>

bool NeoAdmin_GetEmbeddedMapOverview(
    std::string_view map_name,
    std::vector<std::uint8_t>& definition,
    std::vector<std::uint8_t>& image);

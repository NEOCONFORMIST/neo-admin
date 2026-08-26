#pragma once

#include <algorithm>
#include <cctype>
#include <string>
#include <string_view>

namespace voicebridge
{
inline bool IsGameplayMapToken(std::string_view value)
{
    std::string token(value);
    std::replace(token.begin(), token.end(), '\\', '/');
    std::transform(
        token.begin(),
        token.end(),
        token.begin(),
        [](unsigned char character)
        {
            return static_cast<char>(std::tolower(character));
        });

    if (token.empty() || token.front() == '/' || token.back() == '/')
        return false;

    std::string_view map_name = token;
    const std::size_t first_separator = token.find('/');

    if (first_separator != std::string::npos)
    {
        constexpr std::string_view workshop_prefix = "workshop/";

        if (!token.starts_with(workshop_prefix))
            return false;

        const std::size_t id_begin = workshop_prefix.size();
        const std::size_t id_end = token.find('/', id_begin);

        if (id_end == std::string::npos || id_end == id_begin)
            return false;

        if (!std::all_of(
                token.begin() + static_cast<std::ptrdiff_t>(id_begin),
                token.begin() + static_cast<std::ptrdiff_t>(id_end),
                [](unsigned char character)
                {
                    return std::isdigit(character) != 0;
                }))
        {
            return false;
        }

        map_name = std::string_view(token).substr(id_end + 1);

        if (map_name.empty() || map_name.find('/') != std::string_view::npos)
            return false;
    }

    if (map_name.ends_with("_vanity") ||
        map_name.starts_with("workshop_preview_") ||
        map_name == "graphics_settings" ||
        map_name == "lobby_mapveto")
    {
        return false;
    }

    return true;
}
}

#include "neo_admin_give_items.h"

#include <cstddef>
#include <string_view>

int main()
{
    using neo_admin::FindGiveItem;
    using neo_admin::kGiveItems;

    const auto* ak47 = FindGiveItem("weapon_ak47");
    if (!ak47 || ak47->display_name != "AK-47")
        return 1;

    const auto* armor = FindGiveItem("item_assaultsuit");
    if (!armor || armor->display_name != "Kevlar + Helmet")
        return 2;

    constexpr std::string_view blocked[]{
        "weapon_c4",
        "weapon_knife",
        "item_heavyassaultsuit",
        "point_servercommand",
        "weapon_ak47\nquit",
        "",
    };

    for (std::string_view entity_class : blocked)
    {
        if (FindGiveItem(entity_class))
            return 3;
    }

    for (std::size_t left = 0; left < kGiveItems.size(); ++left)
    {
        if (kGiveItems[left].entity_class.empty() ||
            kGiveItems[left].display_name.empty())
        {
            return 4;
        }

        for (std::size_t right = left + 1;
             right < kGiveItems.size();
             ++right)
        {
            if (kGiveItems[left].entity_class ==
                kGiveItems[right].entity_class)
            {
                return 5;
            }
        }
    }

    return 0;
}

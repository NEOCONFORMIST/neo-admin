#pragma once

#include <cstdint>
#include <string_view>

class CCSPlayerController;
class CPointWorldText;

bool NeoAdminMenu_TryOpenChatCommand(
    CCSPlayerController* player,
    std::string_view message);
bool NeoAdminMenu_HandleChatInput(
    CCSPlayerController* player,
    std::string_view message);
bool NeoAdminMenu_HandleSelection(
    CCSPlayerController* player,
    int selection);
bool NeoAdminMenu_HandleButtons(
    CCSPlayerController* player,
    std::uint64_t buttons,
    std::uint64_t pressed_buttons);
CPointWorldText* NeoAdminMenu_GetDisplay(int slot);
void NeoAdminMenu_OnClientDisconnect(int slot);

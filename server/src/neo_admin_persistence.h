#pragma once

#include <string>
#include <string_view>

namespace neo_admin
{
bool ConfigureDatabase(const std::string& path, std::string& error);
void CloseDatabase();
bool DatabaseConfigured();
std::string DatabasePath();

bool ReadJsonDocument(
    std::string_view name,
    const std::string& legacy_path,
    std::string& content,
    bool& found,
    std::string& error);

bool WriteJsonDocument(
    std::string_view name,
    const std::string& legacy_path,
    std::string_view content,
    std::string& error);
} // namespace neo_admin

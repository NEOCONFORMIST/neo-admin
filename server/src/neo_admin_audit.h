#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace neo_admin
{
struct AuditEvent
{
    std::uint64_t id = 0;
    std::string created_utc;
    std::string account_id;
    std::string action;
    std::string target;
    bool success = false;
    std::string details;
};

class AuditStore
{
public:
    bool Load(const std::string& path, std::string& error);
    bool Append(
        std::string_view account_id,
        std::string_view action,
        std::string_view target,
        bool success,
        std::string_view details,
        std::string& error);
    std::string BuildCatalogJson(std::size_t limit = 500) const;
    std::size_t Size() const { return events_.size(); }

private:
    bool Save(std::string& error) const;

    std::string path_;
    std::vector<AuditEvent> events_;
    std::uint64_t next_id_ = 1;
};
} // namespace neo_admin

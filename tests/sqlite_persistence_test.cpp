#include "neo_admin_persistence.h"
#include "sqlite3.h"

#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>

#if defined(__linux__)
#include <sys/stat.h>
#endif

namespace
{
bool Check(bool condition, const char* message)
{
    if (!condition)
        std::cerr << message << '\n';
    return condition;
}

#if defined(__linux__)
bool IsPrivateFile(const std::filesystem::path& path)
{
    struct stat details {};
    return ::stat(path.c_str(), &details) == 0 &&
        (details.st_mode & (S_IRWXG | S_IRWXO)) == 0;
}
#endif
}

int main()
{
    const std::filesystem::path directory =
        std::filesystem::temp_directory_path() / "neo-admin-sqlite-test";
    std::error_code filesystem_error;
    std::filesystem::remove_all(directory, filesystem_error);
    std::filesystem::create_directories(directory, filesystem_error);
    if (!Check(!filesystem_error, "Could not create the test directory."))
        return 1;

    const std::filesystem::path database = directory / "neo_admin.sqlite3";
    const std::filesystem::path legacy = directory / "neo_admin_audit.json";
    {
        std::ofstream output(legacy);
        output << "{\"version\":1,\"events\":[]}";
    }

    std::string error;
    if (!Check(
            neo_admin::ConfigureDatabase(database.string(), error),
            error.c_str()))
        return 1;

    std::string content;
    bool found = false;
    if (!Check(
            neo_admin::ReadJsonDocument(
                "audit", legacy.string(), content, found, error),
            error.c_str()) ||
        !Check(found, "Legacy audit data was not imported.") ||
        !Check(
            content == "{\"version\":1,\"events\":[]}",
            "Imported audit content changed.") ||
        !Check(
            !std::filesystem::exists(legacy),
            "Legacy file was not retired after import.") ||
        !Check(
            std::filesystem::exists(legacy.string() + ".migrated-to-sqlite.bak"),
            "Legacy migration backup was not retained."))
    {
        return 1;
    }

    const std::string updated =
        "{\"version\":1,\"events\":[{\"id\":1}]}";
    if (!Check(
            neo_admin::WriteJsonDocument(
                "audit", legacy.string(), updated, error),
            error.c_str()))
        return 1;

#if defined(__linux__)
    if (!Check(IsPrivateFile(database), "SQLite database permissions are not private.") ||
        !Check(
            IsPrivateFile(database.string() + "-wal"),
            "SQLite WAL permissions are not private.") ||
        !Check(
            IsPrivateFile(database.string() + "-shm"),
            "SQLite shared-memory permissions are not private.") ||
        !Check(
            IsPrivateFile(legacy.string() + ".migrated-to-sqlite.bak"),
            "Legacy backup permissions are not private."))
    {
        return 1;
    }
#endif

    error.clear();
    if (!Check(
            !neo_admin::WriteJsonDocument(
                "invalid", legacy.string(), "not-json", error),
            "SQLite accepted invalid JSON."))
        return 1;

    neo_admin::CloseDatabase();
    error.clear();
    if (!Check(
            neo_admin::ConfigureDatabase(database.string(), error),
            error.c_str()))
        return 1;
    content.clear();
    found = false;
    if (!Check(
            neo_admin::ReadJsonDocument(
                "audit", legacy.string(), content, found, error),
            error.c_str()) ||
        !Check(found && content == updated, "SQLite update was not durable."))
    {
        return 1;
    }
    neo_admin::CloseDatabase();

    sqlite3* inspection = nullptr;
    if (!Check(
            sqlite3_open_v2(
                database.string().c_str(), &inspection, SQLITE_OPEN_READONLY, nullptr) ==
                SQLITE_OK,
            "Could not inspect the generated SQLite database."))
        return 1;
    sqlite3_stmt* statement = nullptr;
    const bool schema_ok =
        sqlite3_prepare_v2(
            inspection,
            "SELECT value FROM neo_meta WHERE key='schema_version'",
            -1,
            &statement,
            nullptr) == SQLITE_OK &&
        sqlite3_step(statement) == SQLITE_ROW &&
        std::string(reinterpret_cast<const char*>(sqlite3_column_text(statement, 0))) ==
            "1";
    sqlite3_finalize(statement);
    sqlite3_close(inspection);
    if (!Check(schema_ok, "SQLite schema version metadata is missing."))
        return 1;

    std::filesystem::remove_all(directory, filesystem_error);
    std::cout << "SQLite persistence and legacy migration self-test passed.\n";
    return 0;
}

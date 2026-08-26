#include "neo_admin_persistence.h"

#include "sqlite3.h"

#include <chrono>
#include <filesystem>
#include <fstream>
#include <sstream>

#if defined(__linux__)
#include <sys/stat.h>
#endif

namespace neo_admin
{
namespace
{
sqlite3* g_database = nullptr;
std::string g_database_path;

void HardenDatabasePermissions()
{
#if defined(__linux__)
    if (g_database_path.empty())
        return;

    constexpr mode_t private_mode = S_IRUSR | S_IWUSR;
    (void)::chmod(g_database_path.c_str(), private_mode);
    (void)::chmod((g_database_path + "-wal").c_str(), private_mode);
    (void)::chmod((g_database_path + "-shm").c_str(), private_mode);
#endif
}

void SetSqliteError(std::string_view operation, std::string& error)
{
    error.assign(operation);
    error.append(": ");
    error.append(g_database ? sqlite3_errmsg(g_database) : "database is not open");
}

bool Execute(std::string_view sql, std::string& error)
{
    char* message = nullptr;
    const int result = sqlite3_exec(
        g_database, std::string(sql).c_str(), nullptr, nullptr, &message);
    if (result == SQLITE_OK)
        return true;
    error = message ? message : sqlite3_errmsg(g_database);
    sqlite3_free(message);
    return false;
}

bool ReadFile(
    const std::string& path,
    std::string& content,
    bool& found,
    std::string& error)
{
    found = false;
    content.clear();
    std::error_code filesystem_error;
    if (!std::filesystem::exists(path, filesystem_error))
    {
        if (filesystem_error)
        {
            error = "Could not inspect the legacy storage file.";
            return false;
        }
        return true;
    }

    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        error = "Could not open the legacy storage file.";
        return false;
    }
    std::ostringstream buffer;
    buffer << input.rdbuf();
    if (!input.good() && !input.eof())
    {
        error = "Could not read the legacy storage file.";
        return false;
    }
    content = buffer.str();
    found = true;
    return true;
}

bool WriteFile(
    const std::string& path,
    std::string_view content,
    std::string& error)
{
    const std::filesystem::path target(path);
    std::error_code filesystem_error;
    if (target.has_parent_path())
    {
        std::filesystem::create_directories(target.parent_path(), filesystem_error);
        if (filesystem_error)
        {
            error = "Could not create the storage directory.";
            return false;
        }
    }

    const std::filesystem::path temporary = target.string() + ".new";
    {
        std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
        if (!output)
        {
            error = "Could not write the storage file.";
            return false;
        }
        output.write(content.data(), static_cast<std::streamsize>(content.size()));
        output.flush();
        if (!output)
        {
            error = "Could not finish writing the storage file.";
            return false;
        }
    }
#if defined(__linux__)
    (void)::chmod(temporary.c_str(), S_IRUSR | S_IWUSR);
#endif
    std::filesystem::rename(temporary, target, filesystem_error);
    if (filesystem_error)
    {
        error = "Could not replace the storage file.";
        return false;
    }
#if defined(__linux__)
    (void)::chmod(target.c_str(), S_IRUSR | S_IWUSR);
#endif
    return true;
}

bool WriteDatabaseDocument(
    std::string_view name,
    std::string_view content,
    std::string& error)
{
    if (!Execute("BEGIN IMMEDIATE", error))
        return false;

    constexpr const char* sql =
        "INSERT INTO neo_documents(name, schema_version, json, updated_utc) "
        "VALUES(?1, 1, ?2, strftime('%Y-%m-%dT%H:%M:%SZ','now')) "
        "ON CONFLICT(name) DO UPDATE SET "
        "schema_version=excluded.schema_version, json=excluded.json, "
        "updated_utc=excluded.updated_utc";
    sqlite3_stmt* statement = nullptr;
    int result = sqlite3_prepare_v2(g_database, sql, -1, &statement, nullptr);
    if (result == SQLITE_OK)
    {
        result = sqlite3_bind_text(
            statement, 1, name.data(), static_cast<int>(name.size()), SQLITE_TRANSIENT);
    }
    if (result == SQLITE_OK)
    {
        result = sqlite3_bind_text(
            statement, 2, content.data(), static_cast<int>(content.size()), SQLITE_TRANSIENT);
    }
    if (result == SQLITE_OK)
        result = sqlite3_step(statement);
    sqlite3_finalize(statement);

    if (result != SQLITE_DONE)
    {
        SetSqliteError("Could not store the SQLite document", error);
        std::string ignored;
        (void)Execute("ROLLBACK", ignored);
        return false;
    }
    if (!Execute("COMMIT", error))
    {
        std::string ignored;
        (void)Execute("ROLLBACK", ignored);
        return false;
    }
    HardenDatabasePermissions();
    return true;
}

void RetainLegacyBackup(const std::string& path)
{
    std::error_code filesystem_error;
    if (!std::filesystem::exists(path, filesystem_error) || filesystem_error)
        return;

    std::filesystem::path backup = path + ".migrated-to-sqlite.bak";
    if (std::filesystem::exists(backup, filesystem_error) && !filesystem_error)
    {
        const auto now = std::chrono::system_clock::now().time_since_epoch();
        backup += "." + std::to_string(
            std::chrono::duration_cast<std::chrono::seconds>(now).count());
    }
    filesystem_error.clear();
    std::filesystem::rename(path, backup, filesystem_error);
#if defined(__linux__)
    if (!filesystem_error)
        (void)::chmod(backup.c_str(), S_IRUSR | S_IWUSR);
#endif
}
} // namespace

bool ConfigureDatabase(const std::string& path, std::string& error)
{
    CloseDatabase();
    if (path.empty())
    {
        error = "SQLite database path is invalid.";
        return false;
    }

    const std::filesystem::path target(path);
    std::error_code filesystem_error;
    if (target.has_parent_path())
    {
        std::filesystem::create_directories(target.parent_path(), filesystem_error);
        if (filesystem_error)
        {
            error = "Could not create the SQLite database directory.";
            return false;
        }
    }

    const int result = sqlite3_open_v2(
        path.c_str(), &g_database,
        SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX,
        nullptr);
    if (result != SQLITE_OK)
    {
        SetSqliteError("Could not open the SQLite database", error);
        CloseDatabase();
        return false;
    }
    g_database_path = path;
    (void)sqlite3_busy_timeout(g_database, 5000);

    constexpr std::string_view schema =
        "PRAGMA journal_mode=WAL;"
        "PRAGMA synchronous=FULL;"
        "PRAGMA foreign_keys=ON;"
        "CREATE TABLE IF NOT EXISTS neo_meta("
        "key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);"
        "INSERT OR IGNORE INTO neo_meta(key,value) VALUES('schema_version','1');"
        "CREATE TABLE IF NOT EXISTS neo_documents("
        "name TEXT PRIMARY KEY NOT NULL,"
        "schema_version INTEGER NOT NULL,"
        "json TEXT NOT NULL CHECK(json_valid(json)),"
        "updated_utc TEXT NOT NULL);"
        "PRAGMA user_version=1;";
    if (!Execute(schema, error))
    {
        error = "Could not initialize the SQLite schema: " + error;
        CloseDatabase();
        return false;
    }
    HardenDatabasePermissions();
    return true;
}

void CloseDatabase()
{
    if (g_database)
    {
        (void)sqlite3_close_v2(g_database);
        g_database = nullptr;
    }
    g_database_path.clear();
}

bool DatabaseConfigured()
{
    return g_database != nullptr;
}

std::string DatabasePath()
{
    return g_database_path;
}

bool ReadJsonDocument(
    std::string_view name,
    const std::string& legacy_path,
    std::string& content,
    bool& found,
    std::string& error)
{
    if (!DatabaseConfigured())
        return ReadFile(legacy_path, content, found, error);

    constexpr const char* sql = "SELECT json FROM neo_documents WHERE name=?1";
    sqlite3_stmt* statement = nullptr;
    int result = sqlite3_prepare_v2(g_database, sql, -1, &statement, nullptr);
    if (result == SQLITE_OK)
    {
        result = sqlite3_bind_text(
            statement, 1, name.data(), static_cast<int>(name.size()), SQLITE_TRANSIENT);
    }
    if (result == SQLITE_OK)
        result = sqlite3_step(statement);

    if (result == SQLITE_ROW)
    {
        const auto* value = sqlite3_column_text(statement, 0);
        const int length = sqlite3_column_bytes(statement, 0);
        content.assign(reinterpret_cast<const char*>(value), static_cast<std::size_t>(length));
        found = true;
        sqlite3_finalize(statement);
        return true;
    }
    sqlite3_finalize(statement);
    if (result != SQLITE_DONE)
    {
        SetSqliteError("Could not read the SQLite document", error);
        return false;
    }

    if (!ReadFile(legacy_path, content, found, error) || !found)
        return error.empty();
    if (!WriteDatabaseDocument(name, content, error))
        return false;
    RetainLegacyBackup(legacy_path);
    return true;
}

bool WriteJsonDocument(
    std::string_view name,
    const std::string& legacy_path,
    std::string_view content,
    std::string& error)
{
    if (!DatabaseConfigured())
        return WriteFile(legacy_path, content, error);
    return WriteDatabaseDocument(name, content, error);
}
} // namespace neo_admin

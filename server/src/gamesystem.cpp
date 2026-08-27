/**
 * =============================================================================
 * CS2Fixes
 * Copyright (C) 2023-2026 Source2ZE
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 */

#include "detours.h"
#include "gamesystem.h"
#include "entity/ccsplayercontroller.h"
#include "serversideclient.h"
#include "iserver.h"
#include "voicebridge.h"
#include "voicebridge_map_catalog.h"
#include "neo_map_overview_assets.h"
#include "neo_admin_permissions.h"
#include "neo_admin_give_items.h"
#include "neo_ptt.h"
#include "recipientfilters.h"
#include "netmessages.pb.h"
#include "bspflags.h"
#include "gametrace.h"

#include <cmath>
#include <charconv>
#include <string>

extern VoiceBridge g_VoiceBridge;
extern const char* PLUGIN_FULL_VERSION;

void NeoAdmin_ChangeStoredMap(const std::string& map)
{
    constexpr std::string_view prefix = "workshop/";
    if (map.starts_with(prefix))
    {
        const std::size_t slash = map.find('/', prefix.size());
        const std::string_view id_text(map.data() + prefix.size(),
            (slash == std::string::npos ? map.size() : slash) - prefix.size());
        std::uint64_t workshop_id = 0;
        const auto result = std::from_chars(
            id_text.data(), id_text.data() + id_text.size(), workshop_id);
        if (result.ec == std::errc{} && result.ptr == id_text.data() + id_text.size() &&
            workshop_id != 0)
        {
            const std::string command = "host_workshop_map " + std::to_string(workshop_id);
            g_pEngineServer2->ServerCommand(command.c_str());
            return;
        }
    }
    g_pEngineServer2->ChangeLevel(map.c_str(), nullptr);
}
#include "addresses.h"
#include "adminsystem.h"
#include "common.h"
#include "commands.h"
#include "customio.h"
#include "entities.h"
#include "entity/cgamerules.h"
#include "gameconfig.h"
#include "idlemanager.h"
#include "leader.h"
#include "map_votes.h"

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <limits>
#include "playermanager.h"
#include "tier0/vprof.h"
#include "zombiereborn.h"

#include "tier0/memdbgon.h"

CGameSystem g_GameSystem;
CBaseGameSystemFactory** CBaseGameSystemFactory::sm_pFirst = nullptr;
CGameSystemStaticCustomFactory<CGameSystem>* CGameSystem::sm_Factory = nullptr;

namespace
{
constexpr std::string_view kZombieSurvivalMapToken =
	"workshop/3484400725/zm_lila_panic_371";
constexpr std::uint64_t kZombieSurvivalWorkshopId = 3484400725ULL;

std::string NeoAdmin_LowerAscii(std::string value)
{
	std::transform(value.begin(), value.end(), value.begin(),
		[](unsigned char character)
		{
			return static_cast<char>(std::tolower(character));
		});
	return value;
}

std::uint64_t NeoAdmin_ParseWorkshopId(const std::string& token)
{
	constexpr std::string_view prefix = "workshop/";
	if (!token.starts_with(prefix))
		return 0;

	const std::size_t id_begin = prefix.size();
	const std::size_t id_end = token.find('/', id_begin);
	if (id_end == std::string::npos || id_end == id_begin)
		return 0;

	std::uint64_t value = 0;
	const auto result = std::from_chars(
		token.data() + id_begin, token.data() + id_end, value);
	if (result.ec != std::errc{} || result.ptr != token.data() + id_end)
		return 0;
	return value;
}

enum class NeoAdminVoiceRelayKind
{
	None,
	SourceTv,
};

NeoAdminVoiceRelayKind NeoAdmin_GetVoiceRelayKind(
	CCSPlayerController* controller)
{
	if (!controller || !controller->IsConnected())
		return NeoAdminVoiceRelayKind::None;
	if (controller->m_bIsHLTV())
		return NeoAdminVoiceRelayKind::SourceTv;
	return NeoAdminVoiceRelayKind::None;
}

void NeoAdmin_ProtectNativeSourceTv()
{
	static std::uint32_t check_counter = 0;
	if ((++check_counter % 32) != 1 || !g_VoiceBridge.IsConfigured())
		return;

	static ConVarRefAbstract tv_enable("tv_enable", true);
	static ConVarRefAbstract bot_auto_vacate("bot_auto_vacate", true);
	static ConVarRefAbstract bot_quota_mode("bot_quota_mode", true);
	if (!tv_enable.IsValidRef() || !bot_auto_vacate.IsValidRef() ||
		!bot_quota_mode.IsValidRef() || tv_enable.GetInt() == 0)
	{
		return;
	}

	// The client disconnect hook blocks CS2 from counting native SourceTV as
	// the bot to vacate, so the normal fill behavior can remain enabled.
	bool changed = false;
	if (bot_auto_vacate.GetInt() != 1)
	{
		bot_auto_vacate.SetString(CUtlString("1"));
		changed = true;
	}
	if (V_stricmp(bot_quota_mode.GetString(), "fill") != 0)
	{
		bot_quota_mode.SetString(CUtlString("fill"));
		changed = true;
	}
	if (changed)
	{
		Message(
			"[NEO PTT] Protected native SourceTV: "
			"bot_auto_vacate=1 bot_quota_mode=fill.\n");
	}
}

struct NeoAdminMapOverviewPackage
{
	std::string map_name;
	std::vector<std::uint8_t> bytes;
	std::uint32_t hash = 0;
	std::uint32_t definition_length = 0;
};

bool NeoAdmin_NormalizeOverviewMapName(
	std::string_view requested,
	std::string& normalized)
{
	normalized.assign(requested);
	std::replace(normalized.begin(), normalized.end(), '\\', '/');
	const std::size_t separator = normalized.find_last_of('/');
	if (separator != std::string::npos)
		normalized.erase(0, separator + 1);
	normalized = NeoAdmin_LowerAscii(std::move(normalized));

	if (normalized.empty() || normalized.size() > 96)
		return false;

	return std::all_of(
		normalized.begin(),
		normalized.end(),
		[](unsigned char character)
		{
			return std::isalnum(character) != 0 ||
				character == '_' || character == '-';
		});
}

bool NeoAdmin_ReadOverviewFile(
	const std::filesystem::path& path,
	std::size_t maximum_bytes,
	std::vector<std::uint8_t>& bytes)
{
	std::ifstream file(path, std::ios::binary | std::ios::ate);
	if (!file)
		return false;

	const std::streampos end = file.tellg();
	if (end <= 0 ||
		static_cast<std::uint64_t>(end) > maximum_bytes)
	{
		return false;
	}

	bytes.resize(static_cast<std::size_t>(end));
	file.seekg(0, std::ios::beg);
	file.read(
		reinterpret_cast<char*>(bytes.data()),
		static_cast<std::streamsize>(bytes.size()));
	return file.good();
}

const NeoAdminMapOverviewPackage* NeoAdmin_GetMapOverviewPackage(
	std::string_view requested,
	std::string& error_message)
{
	static NeoAdminMapOverviewPackage cached;
	std::string map_name;
	if (!NeoAdmin_NormalizeOverviewMapName(requested, map_name))
	{
		error_message = "Invalid map overview name.";
		return nullptr;
	}

	if (cached.map_name == map_name && !cached.bytes.empty())
		return &cached;

	const std::filesystem::path root =
		std::filesystem::path(Plat_GetGameDirectory()) /
		"csgo" / "addons" / "cs2fixes" / "overviews";
	std::vector<std::uint8_t> definition;
	std::vector<std::uint8_t> image;
	const bool read_from_disk = NeoAdmin_ReadOverviewFile(
			root / (map_name + ".json"),
			64U * 1024U,
			definition) &&
		NeoAdmin_ReadOverviewFile(
			root / (map_name + ".png"),
			2U * 1024U * 1024U,
			image);
	if (!read_from_disk &&
		!NeoAdmin_GetEmbeddedMapOverview(map_name, definition, image))
	{
		error_message = "No server-hosted overview is available for this map.";
		return nullptr;
	}

	// JSON parsers consume the definition bytes directly from the package.
	// Normalize UTF-8 files saved with a BOM before publishing them.
	if (definition.size() >= 3U &&
		definition[0] == 0xEFU &&
		definition[1] == 0xBBU &&
		definition[2] == 0xBFU)
	{
		definition.erase(definition.begin(), definition.begin() + 3);
	}

	if (definition.size() + image.size() + 4U >
		2U * 1024U * 1024U)
	{
		error_message = "The server map overview package is too large.";
		return nullptr;
	}

	NeoAdminMapOverviewPackage loaded;
	loaded.map_name = map_name;
	loaded.definition_length =
		static_cast<std::uint32_t>(definition.size());
	loaded.bytes.reserve(definition.size() + image.size() + 4U);
	for (int shift = 0; shift < 32; shift += 8)
	{
		loaded.bytes.push_back(static_cast<std::uint8_t>(
			loaded.definition_length >> shift));
	}
	loaded.bytes.insert(
		loaded.bytes.end(), definition.begin(), definition.end());
	loaded.bytes.insert(
		loaded.bytes.end(), image.begin(), image.end());

	std::uint32_t hash = 2166136261U;
	for (const std::uint8_t byte : loaded.bytes)
	{
		hash ^= byte;
		hash *= 16777619U;
	}
	loaded.hash = hash;
	cached = std::move(loaded);
	return &cached;
}
}

std::vector<NeoFilesystemMapEntry> NeoAdmin_ScanFilesystemMaps()
{
	std::vector<NeoFilesystemMapEntry> discovered;
	if (kZombieSurvivalImplemented)
	{
		discovered.push_back(
			{std::string(kZombieSurvivalMapToken), kZombieSurvivalWorkshopId});
	}
	const std::filesystem::path maps_root =
		std::filesystem::path(Plat_GetGameDirectory()) / "csgo" / "maps";

	std::error_code error;
	if (!std::filesystem::exists(maps_root, error) || error)
		return discovered;
	error.clear();
	if (!std::filesystem::is_directory(maps_root, error) || error)
		return discovered;

	std::filesystem::recursive_directory_iterator iterator(
		maps_root,
		std::filesystem::directory_options::skip_permission_denied,
		error);
	const std::filesystem::recursive_directory_iterator end;

	while (iterator != end)
	{
		if (error)
		{
			error.clear();
			iterator.increment(error);
			continue;
		}

		std::error_code entry_error;
		if (!iterator->is_regular_file(entry_error) || entry_error)
		{
			iterator.increment(error);
			continue;
		}

		const std::filesystem::path file_path = iterator->path();
		const std::string extension =
			NeoAdmin_LowerAscii(file_path.extension().string());
		if (extension != ".vpk" && extension != ".bsp" &&
			extension != ".vmap_c")
		{
			iterator.increment(error);
			continue;
		}

		std::error_code relative_error;
		std::filesystem::path relative = std::filesystem::relative(
			file_path, maps_root, relative_error);
		if (relative_error || relative.empty())
		{
			iterator.increment(error);
			continue;
		}

		relative.replace_extension();
		std::string token = relative.generic_string();
		const std::string lowered_token = NeoAdmin_LowerAscii(token);
		if (token.empty() || !voicebridge::IsGameplayMapToken(token) ||
			(lowered_token.size() >= 4 && lowered_token.ends_with("_dir")))
		{
			iterator.increment(error);
			continue;
		}

		discovered.push_back({
			std::move(token),
			NeoAdmin_ParseWorkshopId(lowered_token)});
		iterator.increment(error);
	}

	std::sort(discovered.begin(), discovered.end(),
		[](const auto& left, const auto& right)
		{
			return NeoAdmin_LowerAscii(left.token) <
				NeoAdmin_LowerAscii(right.token);
		});
	discovered.erase(
		std::unique(discovered.begin(), discovered.end(),
			[](const auto& left, const auto& right)
			{
				return NeoAdmin_LowerAscii(left.token) ==
					NeoAdmin_LowerAscii(right.token);
			}),
		discovered.end());
	return discovered;
}

const NeoFilesystemMapEntry* NeoAdmin_FindFilesystemMap(
	const std::vector<NeoFilesystemMapEntry>& maps,
	std::string requested)
{
	std::replace(requested.begin(), requested.end(), '\\', '/');
	while (!requested.empty() && requested.front() == '/')
		requested.erase(requested.begin());

	std::string requested_lower = NeoAdmin_LowerAscii(requested);
	for (const std::string_view extension : {".vpk", ".bsp", ".vmap_c"})
	{
		if (requested_lower.size() > extension.size() &&
			requested_lower.ends_with(extension))
		{
			requested.resize(requested.size() - extension.size());
			requested_lower.resize(requested_lower.size() - extension.size());
			break;
		}
	}

	for (const auto& map : maps)
	{
		if (NeoAdmin_LowerAscii(map.token) == requested_lower)
			return &map;
	}

	const NeoFilesystemMapEntry* basename_match = nullptr;
	for (const auto& map : maps)
	{
		const std::size_t slash = map.token.find_last_of('/');
		const std::string basename = slash == std::string::npos
			? map.token : map.token.substr(slash + 1);
		if (NeoAdmin_LowerAscii(basename) != requested_lower)
			continue;
		if (basename_match)
			return nullptr;
		basename_match = &map;
	}
	return basename_match;
}

bool NeoAdmin_IsZombieSurvivalMap(const NeoFilesystemMapEntry& map)
{
	return map.workshop_id == kZombieSurvivalWorkshopId &&
		NeoAdmin_LowerAscii(map.token) == kZombieSurvivalMapToken;
}

bool NeoAdmin_PrepareMapProfile(
	const NeoFilesystemMapEntry& map,
	std::string& error)
{
	error.clear();
	if (!NeoAdmin_IsZombieSurvivalMap(map))
		return true;
	if (!kZombieSurvivalImplemented)
	{
		error = "Zombie Survival is not implemented yet.";
		return false;
	}

	if (!ZR_EnsureConfiguration(error) ||
		!ZR_SaveEnabledPreference(true, error))
	{
		return false;
	}

	return true;
}

// This mess is needed to get the pointer to sm_pFirst so we can insert game systems
bool InitGameSystems()
{
	// This signature directly points to the instruction referencing sm_pFirst
	uintptr_t pAddr = (uintptr_t)g_GameConfig->ResolveSignature("IGameSystem_InitAllSystems_pFirst");

	if (!pAddr)
	{
		Panic("Failed to InitGameSystems, see warnings above.\n");
		return false;
	}

	// the opcode is 3 bytes so we skip those
	pAddr += 3;

	// Grab the offset as 4 bytes
	uint32 offset = *(uint32*)pAddr;

	// Go to the next instruction, which is the starting point of the relative jump
	pAddr += 4;

	// Now grab our pointer
	CBaseGameSystemFactory::sm_pFirst = (CBaseGameSystemFactory**)(pAddr + offset);

	// And insert the game system(s)
	CGameSystem::sm_Factory = new CGameSystemStaticCustomFactory<CGameSystem>("CS2Fixes_GameSystem", &g_GameSystem);

	return true;
}

bool UnregisterGameSystem()
{
	// This signature directly points to the instruction referencing sm_pEventDispatcher
	uintptr_t pAddr = (uintptr_t)g_GameConfig->ResolveSignature("IGameSystem_LoopPostInitAllSystems_pEventDispatcher");

	if (!pAddr)
	{
		Panic("Failed to UnregisterGameSystem, see warnings above.\n");
		return false;
	}

	// the opcode is 3 bytes so we skip those
	pAddr += 3;

	uint32 offset = *(uint32*)pAddr;

	// Go to the next instruction, which is the starting point of the relative jump
	pAddr += 4;

	CGameSystemEventDispatcher** ppDispatchers = (CGameSystemEventDispatcher**)(pAddr + offset);

	pAddr = (uintptr_t)g_GameConfig->ResolveSignature("IGameSystem_LoopDestroyAllSystems_s_GameSystems");

	if (!pAddr)
	{
		Panic("Failed to UnregisterGameSystem, see warnings above.\n");
		return false;
	}

	// Here the opcode is 2 bytes as it's moving a dword, not a qword, but that's the start of a vector object
	pAddr += 2;

	offset = *(uint32*)pAddr;

	pAddr += 4;

	CUtlVector<AddedGameSystem_t>* pGameSystems = (CUtlVector<AddedGameSystem_t>*)(pAddr + offset);

	auto* pDispatcher = *ppDispatchers;

	if (!pDispatcher || !pGameSystems)
	{
		Panic("Gamesystems and/or dispatchers is null, server is probably shutting down\n");
		return false;
	}

	auto& funcListeners = *pDispatcher->m_funcListeners;
	auto& gameSystems = *pGameSystems;

	FOR_EACH_VEC_BACK(gameSystems, i)
	{
		if (&g_GameSystem == gameSystems[i].m_pGameSystem)
		{
			gameSystems.FastRemove(i);
			break;
		}
	}

	FOR_EACH_VEC_BACK(funcListeners, i)
	{
		auto& vecListeners = funcListeners[i];

		FOR_EACH_VEC_BACK(vecListeners, j)
		{
			if (&g_GameSystem == vecListeners[j])
			{
				vecListeners.FastRemove(j);

				break;
			}
		}

		if (!vecListeners.Count())
			funcListeners.FastRemove(i);
	}

	CGameSystem::sm_Factory->DestroyGameSystem(&g_GameSystem);
	CGameSystem::sm_Factory->Destroy();

	return true;
}

GS_EVENT_MEMBER(CGameSystem, BuildGameSessionManifest)
{
	Message("CGameSystem::BuildGameSessionManifest\n");

	IEntityResourceManifest* pResourceManifest = msg->m_pResourceManifest;

	// This takes any resource type, model or not
	// Any resource adding MUST be done here, the resource manifest is not long-lived
	// pResourceManifest->AddResource("characters/models/my_character_model.vmdl");

	if (kZombieSurvivalImplemented)
		ZR_Precache(pResourceManifest);
	PrecacheBeaconParticle(pResourceManifest);
	Leader_Precache(pResourceManifest);

	pResourceManifest->AddResource(g_cvarBurnParticle.Get().String());
}

// Called every frame before entities think


// NEO ADMIN safe-ground drag-teleport support.
class NeoNavPhysicsInterface
{
private:
    virtual ~NeoNavPhysicsInterface() = 0;

    virtual void Nav_TraceLine(
        const Vector& start,
        const Vector& end,
        CBaseEntity* ignored,
        uint64 interacts_with,
        uint8 collision_group,
        uint8 object_set_mask,
        CGameTrace* trace) = 0;

    virtual void Nav_TraceLine(
        const Vector& start,
        const Vector& end,
        CTraceFilter* filter,
        CGameTrace* trace) = 0;

    static inline void** vtable_ = nullptr;

public:
    static bool TraceLine(
        const Vector& start,
        const Vector& end,
        CTraceFilter* filter,
        CGameTrace* trace)
    {
        if (!vtable_)
        {
            if (!modules::server)
                return false;

            vtable_ = static_cast<void**>(
                modules::server->FindVirtualTable(
                    "CNavPhysicsInterface"));
        }

        if (!vtable_)
            return false;

        auto* interface_pointer =
            reinterpret_cast<NeoNavPhysicsInterface*>(&vtable_);

        interface_pointer->Nav_TraceLine(
            start,
            end,
            filter,
            trace);

        return true;
    }
};

class NeoSafeTeleportTraceFilter final : public CTraceFilter
{
public:
    explicit NeoSafeTeleportTraceFilter(
        CEntityInstance* ignored_entity)
        : CTraceFilter(
              static_cast<uint64>(MASK_PLAYERSOLID),
              COLLISION_GROUP_PLAYER,
              true),
          ignored_entity_(ignored_entity)
    {
    }

    bool ShouldHitEntity(CEntityInstance* entity) override
    {
        return entity != ignored_entity_;
    }

private:
    CEntityInstance* ignored_entity_;
};

static bool NeoFindSafeTeleportDestination(
    CCSPlayerPawn* pawn,
    float x,
    float y,
    float preferred_z,
    Vector& destination)
{
    if (!pawn ||
        !std::isfinite(x) ||
        !std::isfinite(y) ||
        !std::isfinite(preferred_z))
    {
        return false;
    }

    NeoSafeTeleportTraceFilter filter(
        static_cast<CEntityInstance*>(pawn));

    // Try several heights so floors above and below the player's
    // previous elevation can be discovered. The closest valid floor
    // to preferred_z wins.
    static constexpr float search_start_offsets[] = {
        64.0f,
        256.0f,
        768.0f,
        1536.0f,
        3072.0f
    };

    bool found_floor = false;
    float best_floor_z = 0.0f;
    float best_height_difference = 1.0e30f;

    for (const float start_offset : search_start_offsets)
    {
        Vector trace_start;
        trace_start.x = x;
        trace_start.y = y;
        trace_start.z = preferred_z + start_offset;

        Vector trace_end;
        trace_end.x = x;
        trace_end.y = y;
        trace_end.z = preferred_z - 4096.0f;

        CGameTrace trace;

        if (!NeoNavPhysicsInterface::TraceLine(
                trace_start,
                trace_end,
                &filter,
                &trace))
        {
            return false;
        }

        if (trace.m_bStartInSolid ||
            trace.m_flFraction < 0.0f ||
            trace.m_flFraction >= 1.0f)
        {
            continue;
        }

        const float floor_z =
            trace_start.z +
            ((trace_end.z - trace_start.z) *
             trace.m_flFraction);

        if (!std::isfinite(floor_z))
            continue;

        const float height_difference =
            std::fabs(floor_z - preferred_z);

        if (!found_floor ||
            height_difference < best_height_difference)
        {
            found_floor = true;
            best_floor_z = floor_z;
            best_height_difference = height_difference;
        }
    }

    if (!found_floor)
        return false;

    // Approximate the standing player hull using the center,
    // sides, and corners of a 28-unit-wide footprint.
    static constexpr float clearance_points[][2] = {
        {  0.0f,   0.0f },
        { 14.0f,   0.0f },
        {-14.0f,   0.0f },
        {  0.0f,  14.0f },
        {  0.0f, -14.0f },
        { 14.0f,  14.0f },
        { 14.0f, -14.0f },
        {-14.0f,  14.0f },
        {-14.0f, -14.0f }
    };

    const float feet_z = best_floor_z + 2.0f;
    const float head_z = feet_z + 72.0f;

    for (const auto& point : clearance_points)
    {
        Vector clearance_start;
        clearance_start.x = x + point[0];
        clearance_start.y = y + point[1];
        clearance_start.z = feet_z + 0.5f;

        Vector clearance_end;
        clearance_end.x = clearance_start.x;
        clearance_end.y = clearance_start.y;
        clearance_end.z = head_z;

        CGameTrace clearance_trace;

        if (!NeoNavPhysicsInterface::TraceLine(
                clearance_start,
                clearance_end,
                &filter,
                &clearance_trace))
        {
            return false;
        }

        if (clearance_trace.m_bStartInSolid ||
            clearance_trace.m_flFraction < 0.999f)
        {
            return false;
        }
    }

    destination.x = x;
    destination.y = y;
    destination.z = feet_z;
    return true;
}

int NeoAdmin_RemoveGameplayBots()
{
	if (!GetGlobals())
		return 0;
	int removed = 0;
	for (int slot = 0; slot < GetGlobals()->maxClients; ++slot)
	{
		CCSPlayerController* controller =
			CCSPlayerController::FromSlot(slot);
		if (!controller || !controller->IsConnected() ||
			!controller->IsBot() || controller->m_bIsHLTV())
		{
			continue;
		}

		g_pEngineServer2->DisconnectClient(
			controller->GetPlayerSlot(),
			NETWORK_DISCONNECT_KICKED,
			"Removed by an administrator");
		++removed;
	}
	return removed;
}

static void VoiceBridge_OnServerFrame()
{
	NeoAdmin_ProtectNativeSourceTv();

    // NEO PTT STAGE 3O HLTV DELAYED READ-ONLY PROBE BEGIN
    //
    // Waits for CS2's native SourceTV/HLTV controller to finish connecting
    // instead of scanning only once during early server startup.
    //
    // NO bot creation.
    // NO gameplay-bot fallback.
    // NO team changes.
    // NO network sending.
    // NO CServerSideClient.
    // NO GetClientBySlot().
    //

    static std::uint32_t
        neo_ptt_stage3o_frame_counter = 0;

    static std::uint32_t
        neo_ptt_stage3o_scan_counter = 0;

    static bool
        neo_ptt_stage3o_finished = false;

    // Stage 3R stores ONLY numeric sender metadata.
    //
    // No controller pointer is retained because the temporary
    // SourceTV player controller can disappear after startup.
    static int neo_ptt_stage3r_cached_slot = -1;
    static int neo_ptt_stage3r_cached_entity = -1;
    static bool neo_ptt_stage3r_identity_ready = false;

    if (!neo_ptt_stage3o_finished &&
        GetGlobals())
    {
        ++neo_ptt_stage3o_frame_counter;

        // Scan once every 32 VoiceBridge frames.
        if ((neo_ptt_stage3o_frame_counter % 32) == 0)
        {
            ++neo_ptt_stage3o_scan_counter;

            CCSPlayerController* relay = nullptr;
            int relay_slot = -1;

            if (neo_ptt_stage3o_scan_counter == 1 ||
                (neo_ptt_stage3o_scan_counter % 10) == 0)
            {
                Message(
                    "[NEO PTT] Stage 3O "
                    "HLTV scan attempt=%u\n",
                    neo_ptt_stage3o_scan_counter);
            }

            for (int slot = 0;
                 slot < GetGlobals()->maxClients;
                 ++slot)
            {
                CCSPlayerController* candidate =
                    CCSPlayerController::FromSlot(slot);

                if (!candidate)
                    continue;

                if (!candidate->IsConnected())
                    continue;

                const NeoAdminVoiceRelayKind candidate_kind =
                    NeoAdmin_GetVoiceRelayKind(candidate);
                if (candidate_kind != NeoAdminVoiceRelayKind::SourceTv)
                    continue;

                relay = candidate;
                relay_slot = slot;
                break;
            }

            if (relay)
            {
                const std::string relay_name = relay->GetPlayerName();
                neo_ptt_stage3r_cached_slot = relay_slot;
                neo_ptt_stage3r_cached_entity = relay_slot + 1;
                neo_ptt_stage3r_identity_ready = true;
                neo_ptt_stage3o_finished = true;

                Message(
                    "[NEO PTT] Stage 3O MATCH "
                    "slot=%d entity=%d kind=%s team=%d name=\"%s\"\n",
                    neo_ptt_stage3r_cached_slot,
                    neo_ptt_stage3r_cached_entity,
                    "SourceTV",
                    relay->m_iTeamNum(),
                    relay_name.c_str());

                Message(
                    "[NEO PTT] Stage 3R voice relay ready\n");
            }
            else if (neo_ptt_stage3o_scan_counter == 40)
            {
                Warning(
                    "[NEO PTT] Stage 3O "
                    "voice relay probe TIMEOUT - "
                    "no native SourceTV/HLTV controller found; continuing to retry\n");
            }
        }
    }

    // NEO PTT STAGE 3O HLTV DELAYED READ-ONLY PROBE END

    if (!GetGlobals())
        return;

    static bool logged = false;
    if (!logged)
    {
        Message("[VoiceBridge] ServerPreEntityThink map feed active\n");
        logged = true;
    }

    g_VoiceBridge.TickPresence();

    // NEO PTT STAGE 2 POLL BEGIN
    //
    // Receive/authenticate/count only.
    // NO CS2 voice injection.
    const std::uint64_t neo_ptt_accepted_now =
        NeoPtt_Poll();

    static bool neo_admin_had_peer = false;
    const bool neo_admin_has_peer = NeoPtt_HasPeer();
    if (neo_admin_has_peer && !neo_admin_had_peer)
    {
        g_VoiceBridge.SetCurrentMap(GetGlobals()->mapname.ToCStr());
    }
    neo_admin_had_peer = neo_admin_has_peer;

    if (neo_ptt_accepted_now > 0)
    {
        static std::uint64_t
            neo_ptt_last_report = 0;

        const NeoPttStats stats =
            NeoPtt_GetStats();

        if (stats.authenticated == 1 ||
            stats.authenticated -
                neo_ptt_last_report >= 50)
        {
            Message(
                "[NEO PTT] Stage 2 "
                "authenticated=%llu "
                "rejected=%llu "
                "bytes=%llu "
                "last_seq=%u\n",
                static_cast<unsigned long long>(
                    stats.authenticated),
                static_cast<unsigned long long>(
                    stats.rejected),
                static_cast<unsigned long long>(
                    stats.payload_bytes),
                stats.last_sequence);

            neo_ptt_last_report =
                stats.authenticated;
        }
    }

    // NEO PTT STAGE 2 POLL END


    // NEO PTT STAGE 3R CACHED HLTV IDENTITY BEGIN
    //
    // Uses only CS2's native SourceTV/HLTV controller. A gameplay bot is
    // never borrowed, renamed, or moved between teams for voice relay duty.
    //
    // NO CServerSideClient.
    // NO GetClientBySlot().
    // NO INetChannel.
    // BroadcastMessage only.

    static std::uint32_t neo_ptt_stage3f_frames = 0;
    static bool neo_ptt_stage3f_failed = false;
    static int neo_ptt_stage3f_sender_slot = -1;

    if (neo_ptt_stage3r_identity_ready &&
        NeoAdmin_GetVoiceRelayKind(
            CCSPlayerController::FromSlot(neo_ptt_stage3r_cached_slot)) ==
            NeoAdminVoiceRelayKind::None)
    {
        Warning(
            "[NEO PTT] Cached voice relay slot changed; searching again.\n");
        neo_ptt_stage3r_cached_slot = -1;
        neo_ptt_stage3r_cached_entity = -1;
        neo_ptt_stage3r_identity_ready = false;
        neo_ptt_stage3f_sender_slot = -1;
        neo_ptt_stage3o_frame_counter = 0;
        neo_ptt_stage3o_scan_counter = 0;
        neo_ptt_stage3o_finished = false;
    }

    if (!neo_ptt_stage3f_failed)
    {
        NeoPttFrame frame{};

        if (NeoPtt_TryPop(frame))
        {
            // Resolve the SourceTV/HLTV identity ONCE.
            if (neo_ptt_stage3f_sender_slot < 0)
            {
                if (neo_ptt_stage3r_identity_ready &&
                    neo_ptt_stage3r_cached_slot >= 0 &&
                    neo_ptt_stage3r_cached_entity > 0)
                {
                    // IMPORTANT:
                    //
                    // This is numeric metadata only.
                    // No stale SourceTV controller pointer is
                    // dereferenced here.
                    neo_ptt_stage3f_sender_slot =
                        neo_ptt_stage3r_cached_slot;

                    Message(
                        "[NEO PTT] Stage 3R "
                        "using cached voice relay identity "
                        "slot=%d entity=%d\n",
                        neo_ptt_stage3r_cached_slot,
                        neo_ptt_stage3r_cached_entity);
                }
                else
                {
                    static std::uint32_t
                        neo_ptt_stage3r_cache_misses = 0;

                    ++neo_ptt_stage3r_cache_misses;

                    if (neo_ptt_stage3r_cache_misses <= 5 ||
                        (neo_ptt_stage3r_cache_misses % 50) == 0)
                    {
                        Warning(
                            "[NEO PTT] Stage 3R WAIT: "
                            "voice relay identity "
                            "not cached yet; "
                            "will retry\n");
                    }
                }
            }

            if (!neo_ptt_stage3f_failed &&
                neo_ptt_stage3f_sender_slot >= 0)
            {
                INetworkGameServer* server =
                    GetNetworkGameServer();

                if (!server)
                {
                    Warning(
                        "[NEO PTT] Stage 3R FAIL: "
                        "network game server null\n");

                    neo_ptt_stage3f_failed = true;
                }
                else if (!g_pNetworkMessages)
                {
                    Warning(
                        "[NEO PTT] Stage 3R FAIL: "
                        "network messages null\n");

                    neo_ptt_stage3f_failed = true;
                }
                else
                {
                    INetworkMessageInternal* voice_event =
                        g_pNetworkMessages
                            ->FindNetworkMessagePartial(
                                "CSVCMsg_VoiceData");

                    if (!voice_event)
                    {
                        Warning(
                            "[NEO PTT] Stage 3R FAIL: "
                            "VoiceData binding missing\n");

                        neo_ptt_stage3f_failed = true;
                    }
                    else
                    {
                        auto* data =
                            voice_event->AllocateMessage();

                        if (!data)
                        {
                            Warning(
                                "[NEO PTT] Stage 3R FAIL: "
                                "AllocateMessage null\n");

                            neo_ptt_stage3f_failed = true;
                        }
                        else
                        {
                            auto* message =
                                data->ToPB<
                                    CSVCMsg_VoiceData>();

                            if (!message)
                            {
                                Warning(
                                    "[NEO PTT] Stage 3R FAIL: "
                                    "ToPB null\n");

                                g_pNetworkMessages
                                    ->DeallocateNetMessageAbstract(
                                        voice_event,
                                        data);

                                neo_ptt_stage3f_failed =
                                    true;
                            }
                            else
                            {
                                auto* audio =
                                    message->mutable_audio();

                                if (!audio)
                                {
                                    Warning(
                                        "[NEO PTT] Stage 3R FAIL: "
                                        "mutable_audio null\n");

                                    g_pNetworkMessages
                                        ->DeallocateNetMessageAbstract(
                                            voice_event,
                                            data);

                                    neo_ptt_stage3f_failed =
                                        true;
                                }
                                else
                                {
                                    audio->set_format(
                                        static_cast<
                                            decltype(
                                                audio->format())
                                        >(2));

                                    audio->set_voice_data(
                                        std::string(
                                            reinterpret_cast<
                                                const char*
                                            >(
                                                frame.payload.data()),
                                            frame.payload.size()));

                                    audio->set_sequence_bytes(
                                        frame.sequence_bytes);

                                    audio->set_section_number(
                                        frame.section_number);

                                    audio->set_sample_rate(
                                        frame.sample_rate);

                                    audio->set_uncompressed_sample_offset(
                                        frame.uncompressed_sample_offset);

                                    audio->set_num_packets(1);

                                    audio->clear_packet_offsets();
                                    audio->add_packet_offsets(
                                        static_cast<std::uint32_t>(
                                            frame.payload.size()));
                                    audio->add_packet_offsets(0);
                                    audio->add_packet_offsets(0);
                                    audio->add_packet_offsets(0);

                                    float neo_ptt_voice_db = -96.0f;

                                    if (frame.voice_level > 0.000001f)
                                        neo_ptt_voice_db =
                                            20.0f * std::log10(
                                                frame.voice_level);

                                    if (neo_ptt_voice_db > 0.0f)
                                        neo_ptt_voice_db = 0.0f;

                                    if (neo_ptt_voice_db < -96.0f)
                                        neo_ptt_voice_db = -96.0f;

                                    audio->set_voice_level(
                                        neo_ptt_voice_db);

                                    // REAL CS2 speaker identity.
                                    message
                                        ->set_client_deprecated(
                                            neo_ptt_stage3f_sender_slot);

                                    message->set_entity(
                                        neo_ptt_stage3f_sender_slot
                                        + 1);

                                    // Bots normally do not have a
                                    // Steam XUID, so leave this zero.
                                    message->set_xuid(0);

                                    message->set_proximity(false);
                                    message->set_audible_mask(-1);

                                    if (GetGlobals())
                                    {
                                        message->set_tick(
                                            static_cast<
                                                std::uint32_t
                                            >(
                                                GetGlobals()
                                                    ->tickcount));
                                    }

                                    message->set_passthrough(1);

                                    CRecipientFilter filter(
                                        BUF_VOICE);

                                    filter.AddAllPlayers();

                                    const std::uint32_t
                                        next_frame =
                                            neo_ptt_stage3f_frames
                                            + 1;

                                    if (next_frame == 1 ||
                                        next_frame == 5 ||
                                        next_frame == 10 ||
                                        next_frame == 15 ||
                                        next_frame == 20 ||
                                        next_frame == 25)
                                    {
                                        Message(
                                            "[NEO PTT] Stage 3R "
                                            "BEFORE frame=%u/1500 "
                                            "speaker=%d "
                                            "opus=%llu "
                                            "protobuf=%llu\n",
                                            next_frame,
                                            neo_ptt_stage3f_sender_slot,
                                            static_cast<
                                                unsigned long long
                                            >(
                                                frame.payload.size()),
                                            static_cast<
                                                unsigned long long
                                            >(
                                                message
                                                    ->ByteSizeLong()));
                                    }

                                    server->BroadcastMessage(
                                        voice_event,
                                        data,
                                        &filter);

                                    ++neo_ptt_stage3f_frames;

                                    if (neo_ptt_stage3f_frames == 1 ||
                                        neo_ptt_stage3f_frames == 5 ||
                                        neo_ptt_stage3f_frames == 10 ||
                                        neo_ptt_stage3f_frames == 15 ||
                                        neo_ptt_stage3f_frames == 20 ||
                                        neo_ptt_stage3f_frames == 1500)
                                    {
                                        Message(
                                            "[NEO PTT] Stage 3R "
                                            "AFTER frame=%u/1500\n",
                                            neo_ptt_stage3f_frames);
                                    }

                                    g_pNetworkMessages
                                        ->DeallocateNetMessageAbstract(
                                            voice_event,
                                            data);

                                    if (neo_ptt_stage3f_frames == 1500)
                                    {
                                        Message(
                                            "[NEO PTT] Stage 3R "
                                            "COMPLETE - "
                                            "REAL CS2 FRAMING\n");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // NEO PTT STAGE 3R CACHED HLTV IDENTITY END












    // NEO CHAT STAGE 3S GAME-FRAME DELIVERY BEGIN
    //
    // UDP authentication/parsing happens in NeoPtt_Poll().
    // Actual CS2 chat delivery remains on the game thread.
    for (int chat_index = 0;
         chat_index < 8;
         ++chat_index)
    {
        NeoPttAdminChatCommand chat{};

        if (!NeoPtt_TakeAdminChat(
                chat))
        {
            break;
        }

        if (chat.message.empty())
            continue;

        if (!chat.authorized)
        {
            g_VoiceBridge.SendAdminActionResult(
                chat.sequence,
                90,
                -1,
                false,
                chat.denial_message.c_str(),
                chat.session_id);
            continue;
        }

        NeoAdmin_BroadcastChat(
            chat.message.c_str(),
            chat.operator_name.c_str());
    }
    // NEO CHAT STAGE 3S GAME-FRAME DELIVERY END
    // NEO ADMIN CONTROL STAGE 3V FILESYSTEM MAP CONTROL BEGIN
    //
    // UDP/HMAC authentication already occurred in NeoPtt_Poll().
    // All CS2 entity/server operations execute here on the
    // game thread.
    //
    // Supported authenticated action codes:
    //
    //   1  Kick
    //   2  Slay
    //   3  Respawn
    //   4  Move to Terrorists
    //   5  Move to Counter-Terrorists
    //   6  Move to Spectator
    //  23  Give an allowlisted weapon or item
    //
    //  40  Change map
    //  41  Restart round
    //  42  Restart match
    //  43  End warmup
    //  44  Pause match
    //  45  Unpause match
    //  46  Swap teams
    //  47  Add bot
    //  48  Remove bots
    //  49  Request filesystem map catalog
    //  50  Request server health
    //  51  Request a server-hosted map overview chunk
    // 141  Request Zombie Survival status
    // 142  Enable or disable Zombie Survival
    //
    // IMPORTANT:
    // Windows-supplied text is NEVER passed to ServerCommand().
    //
    // For ChangeMap, command.text is only passed to the
    // CS2Fixes server-owned map lookup.
    for (int admin_action_index = 0;
         admin_action_index < 8;
         ++admin_action_index)
    {
        NeoPttAdminActionCommand command{};

        if (!NeoPtt_TakeAdminAction(
                command))
        {
            break;
        }

        auto AuditActionName =
            [](std::uint32_t action) -> const char*
            {
                switch (action)
                {
                    case 1: return "Kick player";
                    case 2: return "Slay player";
                    case 3: return "Respawn player";
                    case 4: return "Move player to Terrorists";
                    case 5: return "Move player to Counter-Terrorists";
                    case 6: return "Move player to Spectator";
                    case 23: return "Give weapon or item";
                    case 40: return "Change map";
                    case 41: return "Restart round";
                    case 42: return "Restart match";
                    case 43: return "End warmup";
                    case 44: return "Pause match";
                    case 45: return "Unpause match";
                    case 46: return "Swap teams";
                    case 47: return "Add bot";
                    case 48: return "Remove bots";
                    case 101: return "Save administrator account";
                    case 102: return "Delete administrator account";
                    case 105: return "Save in-game administrator";
                    case 106: return "Delete in-game administrator";
                    case 111: return "Ban player";
                    case 112: return "Unban player";
                    case 114: return "Apply player restriction";
                    case 115: return "Remove player restriction";
                    case 121: return "Save map rotation";
                    case 122: return "Run next rotation map";
                    case 123: return "Schedule map change";
                    case 124: return "Remove scheduled map change";
                    case 131: return "Send announcement";
                    case 132: return "Schedule announcement";
                    case 133: return "Remove scheduled announcement";
                    case 140: return "Run server console command";
                    case 142: return "Toggle Zombie Survival";
                    case 143: return "Install or switch Workshop map";
                    default: return "";
                }
            };

        std::string audit_target = "server";
        if (command.player_slot >= 0)
            audit_target = "slot " + std::to_string(command.player_slot);
        if (command.action == 40)
            audit_target = command.text;
        else if (command.action == 101)
            audit_target = "administrator account";
        else if (command.action == 102)
            audit_target = command.text;
        else if (command.action == 105)
            audit_target = "in-game administrator";
        else if (command.action == 106)
            audit_target = command.text;
        else if (command.action == 140)
        {
            const std::size_t separator =
                command.text.find_first_of(" \t;");
            audit_target = command.text.substr(0, separator);
            if (audit_target.empty())
                audit_target = "server console";
        }
        else if (command.action == 142)
            audit_target = command.value == 1 ? "enabled" : "disabled";
        else if (command.action == 143)
            audit_target = command.text;

        auto SendResult =
            [&](bool success,
                const char* message)
            {
                g_VoiceBridge.SendAdminActionResult(
                    command.sequence,
                    command.action,
                    command.player_slot,
                    success,
                    message,
                    command.session_id);

                const char* audit_action =
                    AuditActionName(command.action);
                if (*audit_action)
                {
                    const char* result_details =
                        command.action == 140
                            ? (success
                                ? "Console command executed."
                                : "Console command rejected.")
                            : (message ? message : "");
                    std::string audit_details;
                    if (!command.operator_name.empty())
                    {
                        audit_details = "Operator " +
                            command.operator_name + ". ";
                    }
                    audit_details += result_details;
                    NeoPtt_RecordAudit(
                        command.account_id,
                        audit_action,
                        audit_target,
                        success,
                        audit_details);
                }
            };

        if (!command.authorized)
        {
            SendResult(
                false,
                command.denial_message.empty()
                    ? "Permission denied."
                    : command.denial_message.c_str());
            continue;
        }


        // =====================================================
        // PLAYER ADMINISTRATION
        // Stage 3T actions 1..6 and allowlisted item action 23
        // =====================================================

        if ((command.action >= 1 &&
             command.action <= 6) ||
            command.action == 23)
        {
            if (command.player_slot < 0 ||
                command.player_slot >= MAXPLAYERS)
            {
                SendResult(
                    false,
                    "Invalid player slot.");

                continue;
            }

            CCSPlayerController* controller =
                CCSPlayerController::FromSlot(
                    command.player_slot);

            if (!controller ||
                !controller->IsConnected())
            {
                SendResult(
                    false,
                    "Player is not connected.");

                continue;
            }

            if (controller->m_bIsHLTV())
            {
                SendResult(
                    false,
                    "SourceTV cannot be targeted.");

                continue;
            }

            const std::string player_name =
                controller->GetPlayerName();

            audit_target = player_name + " (slot " +
                std::to_string(command.player_slot) + ")";

            switch (command.action)
            {
                // ---------------------------------------------
                // Kick
                // ---------------------------------------------
                case 1:
                {
                    ZEPlayer* managed_player =
                        controller->GetZEPlayer();

                    if (!managed_player)
                    {
                        SendResult(
                            false,
                            "Player manager entry is unavailable.");

                        break;
                    }

                    SendResult(
                        true,
                        "Player kicked.");

                    const std::uint64_t steam_id = controller->m_steamID();
                    if (steam_id >= 76561197960265728ULL)
                    {
                        (void)NeoPtt_RecordDiscipline(
                            steam_id,
                            player_name,
                            "Kick",
                            command.text.empty() ? "Kicked by administrator" : command.text,
                            command.account_id);
                    }

                    g_pEngineServer2->DisconnectClient(
                        managed_player->GetPlayerSlot(),
                        NETWORK_DISCONNECT_KICKED,
                        "Kicked by NEO ADMIN");

                    Message(
                        "[NEO ADMIN] kicked slot=%d name=\"%s\"\n",
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                // ---------------------------------------------
                // Slay
                // ---------------------------------------------
                case 2:
                {
                    CCSPlayerPawn* pawn =
                        controller->GetPlayerPawn();

                    if (!pawn ||
                        !pawn->IsAlive())
                    {
                        SendResult(
                            false,
                            "Player is not alive.");

                        break;
                    }

                    pawn->CommitSuicide(
                        false,
                        true);

                    SendResult(
                        true,
                        "Player slayed.");

                    Message(
                        "[NEO ADMIN] slayed slot=%d name=\"%s\"\n",
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                // ---------------------------------------------
                // Respawn
                // ---------------------------------------------
                case 3:
                {
                    CCSPlayerPawn* pawn =
                        controller->GetPlayerPawn();

                    if (!pawn)
                    {
                        SendResult(
                            false,
                            "Player pawn is unavailable.");

                        break;
                    }

                    if (pawn->IsAlive())
                    {
                        SendResult(
                            false,
                            "Player is already alive.");

                        break;
                    }

                    controller->Respawn();

                    SendResult(
                        true,
                        "Player respawned.");

                    Message(
                        "[NEO ADMIN] respawned slot=%d name=\"%s\"\n",
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                // ---------------------------------------------
                // Move to Terrorists
                // ---------------------------------------------
                case 4:
                {
                    controller->SwitchTeam(
                        CS_TEAM_T);

                    SendResult(
                        true,
                        "Player moved to Terrorists.");

                    Message(
                        "[NEO ADMIN] moved slot=%d name=\"%s\" to T\n",
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                // ---------------------------------------------
                // Move to Counter-Terrorists
                // ---------------------------------------------
                case 5:
                {
                    controller->SwitchTeam(
                        CS_TEAM_CT);

                    SendResult(
                        true,
                        "Player moved to Counter-Terrorists.");

                    Message(
                        "[NEO ADMIN] moved slot=%d name=\"%s\" to CT\n",
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                // ---------------------------------------------
                // Move to Spectator
                // ---------------------------------------------
                case 6:
                {
                    controller->SwitchTeam(
                        CS_TEAM_SPECTATOR);

                    SendResult(
                        true,
                        "Player moved to Spectator.");

                    Message(
                        "[NEO ADMIN] moved slot=%d name=\"%s\" to SPEC\n",
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                // ---------------------------------------------
                // Give an allowlisted weapon or item
                // ---------------------------------------------
                case 23:
                {
                    const neo_admin::GiveItemDefinition* item =
                        neo_admin::FindGiveItem(command.text);

                    if (!item)
                    {
                        SendResult(
                            false,
                            "Weapon or item is not allowed.");

                        break;
                    }

                    CCSPlayerPawn* pawn =
                        controller->GetPlayerPawn();

                    if (!pawn || !pawn->IsAlive())
                    {
                        SendResult(
                            false,
                            "Player must be alive to receive an item.");

                        break;
                    }

                    CCSPlayer_ItemServices* item_services =
                        pawn->m_pItemServices();

                    if (!item_services)
                    {
                        SendResult(
                            false,
                            "Player item services are unavailable.");

                        break;
                    }

                    CBasePlayerWeapon* given_weapon =
                        item_services->GiveNamedItemAws(
                            item->entity_class.data());

                    if (item->entity_class.starts_with("weapon_") &&
                        !given_weapon)
                    {
                        SendResult(
                            false,
                            "CS2 could not give that weapon to the player.");

                        break;
                    }

                    audit_target = player_name + " (slot " +
                        std::to_string(command.player_slot) + ") <- " +
                        std::string(item->display_name);

                    const std::string result =
                        std::string(item->display_name) +
                        " given to " + player_name + ".";

                    SendResult(
                        true,
                        result.c_str());

                    Message(
                        "[NEO ADMIN] gave item=\"%s\" to slot=%d name=\"%s\"\n",
                        item->entity_class.data(),
                        command.player_slot,
                        player_name.c_str());

                    break;
                }


                default:
                {
                    SendResult(
                        false,
                        "Unsupported player action.");

                    break;
                }
            }

            continue;
        }


        // =====================================================
        // SERVER CONTROL
        // Stage 3U actions 40..48
        // =====================================================

        switch (command.action)
        {
            // ---------------------------------------------
            // 40 - CHANGE MAP
            //
            // Stage 3V no longer depends on the CS2Fixes
            // maplist.jsonc file.
            //
            // The requested value must resolve to a map in the
            // server-owned catalog. This includes trusted curated
            // Workshop profiles and maps beneath game/csgo/maps.
            // ---------------------------------------------
            case 40:
            {
                if (command.text.empty())
                {
                    SendResult(
                        false,
                        "Map name is empty.");

                    break;
                }

                const auto maps =
                    NeoAdmin_ScanFilesystemMaps();

                if (maps.empty())
                {
                    SendResult(
                        false,
                        "No maps were discovered in the server maps folder.");

                    break;
                }

                const NeoFilesystemMapEntry*
                    selected =
                        NeoAdmin_FindFilesystemMap(
                            maps,
                            command.text);

                if (!selected)
                {
                    SendResult(
                        false,
                        "Map was not found in the server-owned map catalog.");

                    break;
                }

				if (NeoAdmin_IsZombieSurvivalMap(*selected) &&
					!neo_admin::HasPermission(
						command.permissions,
						neo_admin::Permission::ManageZombieMode))
				{
					SendResult(
						false,
						"The Manage Zombie Mode permission is required for this map profile.");
					break;
				}

				std::string profile_error;
				if (!NeoAdmin_PrepareMapProfile(*selected, profile_error))
				{
					SendResult(false, profile_error.c_str());
					break;
				}

                const std::string selected_name =
                    selected->token;
				const bool zombie_survival =
					NeoAdmin_IsZombieSurvivalMap(*selected);

                audit_target = selected_name;

                SendResult(
                    true,
                    zombie_survival
						? "Zombie Survival enabled; changing to Lila Panic."
						: "Changing map.");

                Message(
                    "[NEO ADMIN] filesystem map change to \"%s\"\n",
                    selected_name.c_str());

				// The actual server-owned token is used here, never the
				// Windows-provided command text.
				NeoAdmin_ChangeStoredMap(selected_name);

                break;
            }


            // ---------------------------------------------
            // 41 - RESTART ROUND
            // ---------------------------------------------
            case 41:
            {
                SendResult(
                    true,
                    "Restarting round.");

                g_pEngineServer2->ServerCommand(
                    "mp_restartround 1");

                Message(
                    "[NEO ADMIN] restart round requested\n");

                break;
            }


            // ---------------------------------------------
            // 42 - RESTART MATCH
            // ---------------------------------------------
            case 42:
            {
                SendResult(
                    true,
                    "Restarting match.");

                g_pEngineServer2->ServerCommand(
                    "mp_restartgame 1");

                Message(
                    "[NEO ADMIN] restart match requested\n");

                break;
            }


            // ---------------------------------------------
            // 43 - END WARMUP
            // ---------------------------------------------
            case 43:
            {
                SendResult(
                    true,
                    "Ending warmup.");

                g_pEngineServer2->ServerCommand(
                    "mp_warmup_end");

                Message(
                    "[NEO ADMIN] end warmup requested\n");

                break;
            }


            // ---------------------------------------------
            // 44 - PAUSE
            // ---------------------------------------------
            case 44:
            {
                SendResult(
                    true,
                    "Pausing match.");

                g_pEngineServer2->ServerCommand(
                    "mp_pause_match");

                Message(
                    "[NEO ADMIN] pause match requested\n");

                break;
            }


            // ---------------------------------------------
            // 45 - UNPAUSE
            // ---------------------------------------------
            case 45:
            {
                SendResult(
                    true,
                    "Unpausing match.");

                g_pEngineServer2->ServerCommand(
                    "mp_unpause_match");

                Message(
                    "[NEO ADMIN] unpause match requested\n");

                break;
            }


            // ---------------------------------------------
            // 46 - SWAP TEAMS
            // ---------------------------------------------
            case 46:
            {
                SendResult(
                    true,
                    "Swapping teams.");

                g_pEngineServer2->ServerCommand(
                    "mp_swapteams");

                Message(
                    "[NEO ADMIN] swap teams requested\n");

                break;
            }


            // ---------------------------------------------
            // 47 - ADD BOT
            //
            // command.value:
            //   2 = Terrorist
            //   3 = Counter-Terrorist
            // ---------------------------------------------
            case 47:
            {
                if (command.value == CS_TEAM_T)
                {
                    SendResult(
                        true,
                        "Adding Terrorist bot.");

                    g_pEngineServer2->ServerCommand(
                        "bot_add_t");

                    Message(
                        "[NEO ADMIN] add T bot requested\n");

                    break;
                }

                if (command.value == CS_TEAM_CT)
                {
                    SendResult(
                        true,
                        "Adding Counter-Terrorist bot.");

                    g_pEngineServer2->ServerCommand(
                        "bot_add_ct");

                    Message(
                        "[NEO ADMIN] add CT bot requested\n");

                    break;
                }

                SendResult(
                    false,
                    "AddBot requires team value 2 or 3.");

                break;
            }


            // ---------------------------------------------
            // 48 - REMOVE BOTS
            // ---------------------------------------------
            case 48:
            {
                const int removed = NeoAdmin_RemoveGameplayBots();
                SendResult(
                    true,
                    removed == 1
                        ? "Removed 1 gameplay bot; SourceTV was retained."
                        : "Removed gameplay bots; SourceTV was retained.");
                Message(
                    "[NEO ADMIN] removed %d gameplay bots; native SourceTV retained\n",
                    removed);

                break;
            }


            // ---------------------------------------------
            // 49 - REQUEST FILESYSTEM MAP CATALOG
            // ---------------------------------------------
            case 49:
            {
                constexpr std::size_t
                    kMaxCatalogPayloadBytes = 60000;

                const auto maps =
                    NeoAdmin_ScanFilesystemMaps();

                if (maps.empty())
                {
                    SendResult(
                        false,
                        "No maps were discovered in the server maps folder.");

                    break;
                }

                std::string catalog;
                catalog.reserve(
                    std::min<std::size_t>(
                        8192,
                        maps.size() * 32));

                std::uint32_t included = 0;

                for (const auto& map : maps)
                {
                    const std::size_t required =
                        map.token.size() + 1;

                    if (catalog.size() +
                            required
                        >
                        kMaxCatalogPayloadBytes)
                    {
                        break;
                    }

                    catalog.append(
                        map.token);

                    catalog.push_back('\n');

                    ++included;
                }

                if (included == 0 ||
                    catalog.empty())
                {
                    SendResult(
                        false,
                        "Filesystem map catalog is empty.");

                    break;
                }

                const bool sent =
                    g_VoiceBridge.SendMapCatalog(
                        catalog,
                        included,
                        command.session_id);

                if (sent)
                {
                    SendResult(
                        true,
                        "Filesystem map list refreshed.");

                    Message(
                        "[NEO ADMIN] sent filesystem map catalog count=%u\n",
                        included);
                }
                else
                {
                    SendResult(
                        false,
                        "Filesystem map list reply failed.");
                }

                break;
            }


            // ---------------------------------------------
            // 50 - REQUEST SERVER HEALTH
            // ---------------------------------------------
            case 50:
            {
                int connected_players = 0;

                for (int slot = 0;
                     slot < GetGlobals()->maxClients;
                     ++slot)
                {
                    CCSPlayerController* controller =
                        CCSPlayerController::FromSlot(slot);

                    if (controller && controller->IsConnected())
                        ++connected_players;
                }

                g_VoiceBridge.SendServerHealth(
                    command.sequence,
                    static_cast<std::uint32_t>(
                        GetGlobals()->tickcount),
                    connected_players,
                    static_cast<std::uint32_t>(
                        GetGlobals()->maxClients),
                    PLUGIN_FULL_VERSION,
                    command.session_id);

                break;
            }


            // ---------------------------------------------
            // 51 - REQUEST MAP OVERVIEW CHUNK
            // ---------------------------------------------
            case 51:
            {
                constexpr std::size_t kChunkBytes = 1100;
                std::string overview_error;
                const NeoAdminMapOverviewPackage* package =
                    NeoAdmin_GetMapOverviewPackage(
                        command.text,
                        overview_error);
                if (!package)
                {
                    SendResult(false, overview_error.c_str());
                    break;
                }

                const std::size_t chunk_count =
                    (package->bytes.size() + kChunkBytes - 1U) /
                    kChunkBytes;
                if (command.value < 0 ||
                    static_cast<std::size_t>(command.value) >= chunk_count)
                {
                    SendResult(false, "Invalid map overview chunk index.");
                    break;
                }

                const std::size_t chunk_index =
                    static_cast<std::size_t>(command.value);
                const std::size_t offset = chunk_index * kChunkBytes;
                const std::size_t length = std::min(
                    kChunkBytes,
                    package->bytes.size() - offset);
                const bool sent = g_VoiceBridge.SendMapOverviewChunk(
                    command.sequence,
                    package->map_name,
                    static_cast<std::uint32_t>(chunk_index),
                    static_cast<std::uint32_t>(chunk_count),
                    package->bytes.size(),
                    package->hash,
                    package->definition_length,
                    std::span<const std::uint8_t>(
                        package->bytes.data() + offset,
                        length),
                    command.session_id);
                if (!sent)
                    SendResult(false, "Map overview chunk reply failed.");
                break;
            }


            // ---------------------------------------------
            // 100 - REQUEST ADMINISTRATOR ACCOUNTS
            // ---------------------------------------------
            case 100:
            {
                const bool sent = NeoPtt_SendAccountCatalog(command.session_id);
                SendResult(
                    sent,
                    sent
                        ? "Administrator accounts refreshed."
                        : "Administrator account list reply failed.");
                break;
            }


            // ---------------------------------------------
            // 101 - CREATE OR UPDATE ADMINISTRATOR ACCOUNT
            // ---------------------------------------------
            case 101:
            {
                std::string message;
                const bool saved = NeoPtt_SaveAdminAccount(
                    command.text,
                    command.account_id,
                    message);
                SendResult(saved, message.c_str());
                if (saved)
                    (void)NeoPtt_SendAccountCatalog(command.session_id);
                break;
            }


            // ---------------------------------------------
            // 102 - DELETE ADMINISTRATOR ACCOUNT
            // ---------------------------------------------
            case 102:
            {
                std::string message;
                const bool removed = NeoPtt_DeleteAdminAccount(
                    command.text,
                    command.account_id,
                    message);
                SendResult(removed, message.c_str());
                if (removed)
                    (void)NeoPtt_SendAccountCatalog(command.session_id);
                break;
            }


            // ---------------------------------------------
            // 103 - REQUEST ADMINISTRATOR AUDIT LOG
            // ---------------------------------------------
            case 103:
            {
                const bool sent = NeoPtt_SendAuditCatalog(command.session_id);
                SendResult(
                    sent,
                    sent ? "Audit log refreshed." : "Audit log reply failed.");
                break;
            }

            // ---------------------------------------------
            // 104-106 - IN-GAME ADMINISTRATORS
            // ---------------------------------------------
            case 104:
            {
                const bool sent = NeoPtt_SendGameAdminCatalog(command.session_id);
                SendResult(
                    sent,
                    sent
                        ? "In-game administrators refreshed."
                        : "In-game administrator list reply failed.");
                break;
            }

            case 105:
            {
                std::string message;
                const bool saved = NeoPtt_SaveGameAdmin(command.text, message);
                SendResult(saved, message.c_str());
                if (saved)
                    (void)NeoPtt_SendGameAdminCatalog(command.session_id);
                break;
            }

            case 106:
            {
                std::string message;
                const bool removed = NeoPtt_DeleteGameAdmin(command.text, message);
                SendResult(removed, message.c_str());
                if (removed)
                    (void)NeoPtt_SendGameAdminCatalog(command.session_id);
                break;
            }


            // ---------------------------------------------
            // 110 - REQUEST ACTIVE BANS
            // ---------------------------------------------
            case 110:
            {
                const bool sent = NeoPtt_SendBanCatalog(command.session_id);
                SendResult(
                    sent,
                    sent ? "Ban list refreshed." : "Ban list reply failed.");
                break;
            }


            // ---------------------------------------------
            // 111 - CREATE OR UPDATE BAN
            // ---------------------------------------------
            case 111:
            {
                std::uint64_t banned_steam_id = 0;
                std::string message;
                const bool saved = NeoPtt_SaveBan(
                    command.text,
                    command.account_id,
                    banned_steam_id,
                    audit_target,
                    message);
                SendResult(saved, message.c_str());
                if (!saved)
                    break;

                (void)NeoPtt_SendBanCatalog(command.session_id);
                if (command.player_slot < 0 ||
                    command.player_slot >= MAXPLAYERS)
                {
                    break;
                }

                CCSPlayerController* controller =
                    CCSPlayerController::FromSlot(command.player_slot);
                if (!controller || !controller->IsConnected() ||
                    controller->m_bIsHLTV() ||
                    controller->m_steamID() != banned_steam_id)
                {
                    break;
                }

                ZEPlayer* managed_player = controller->GetZEPlayer();
                if (managed_player)
                {
                    g_pEngineServer2->DisconnectClient(
                        managed_player->GetPlayerSlot(),
                        NETWORK_DISCONNECT_KICKED,
                        "Banned by NEO ADMIN");
                }
                break;
            }


            // ---------------------------------------------
            // 112 - REMOVE BAN
            // ---------------------------------------------
            case 112:
            {
                std::string message;
                const bool removed = NeoPtt_DeleteBan(
                    command.text,
                    command.account_id,
                    audit_target,
                    message);
                SendResult(removed, message.c_str());
                if (removed)
                    (void)NeoPtt_SendBanCatalog(command.session_id);
                break;
            }


            // ---------------------------------------------
            // 113 - REQUEST ACTIVE MUTES AND GAGS
            // ---------------------------------------------
            case 113:
            {
                const bool sent = NeoPtt_SendDisciplineCatalog(command.session_id);
                SendResult(sent, sent ? "Mute and gag list refreshed."
                    : "Mute and gag list reply failed.");
                break;
            }


            // ---------------------------------------------
            // 114 - CREATE OR UPDATE MUTE/GAG
            // ---------------------------------------------
            case 114:
            {
                neo_admin::RestrictionRecord restriction{};
                std::string message;
                const bool saved = NeoPtt_SaveRestriction(
                    command.text, command.account_id, restriction, message);
                if (!saved)
                {
                    SendResult(false, message.c_str());
                    break;
                }

                const CInfractionBase::EInfractionType infraction_type =
                    restriction.type == "Mute" ? CInfractionBase::Mute : CInfractionBase::Gag;
                (void)g_pAdminSystem->FindAndRemoveInfractionSteamId64(
                    restriction.steam_id, infraction_type);
                std::shared_ptr<CInfractionBase> infraction = restriction.type == "Mute"
                    ? std::static_pointer_cast<CInfractionBase>(std::make_shared<CMuteInfraction>(
                        static_cast<time_t>(restriction.duration_minutes), restriction.steam_id))
                    : std::static_pointer_cast<CInfractionBase>(std::make_shared<CGagInfraction>(
                        static_cast<time_t>(restriction.duration_minutes), restriction.steam_id));
                g_pAdminSystem->AddInfraction(infraction);
                g_pAdminSystem->SaveInfractions();

                if (command.player_slot >= 0 && command.player_slot < MAXPLAYERS)
                {
                    CCSPlayerController* controller =
                        CCSPlayerController::FromSlot(command.player_slot);
                    if (controller && controller->IsConnected() && !controller->m_bIsHLTV() &&
                        controller->m_steamID() == restriction.steam_id)
                    {
                        if (ZEPlayer* player = controller->GetZEPlayer())
                            infraction->ApplyInfraction(player);
                    }
                }
                audit_target = restriction.player_name + " (" +
                    std::to_string(restriction.steam_id) + ")";
                SendResult(true, message.c_str());
                (void)NeoPtt_SendDisciplineCatalog(command.session_id);
                break;
            }


            // ---------------------------------------------
            // 115 - REMOVE MUTE/GAG
            // ---------------------------------------------
            case 115:
            {
                neo_admin::RestrictionRecord restriction{};
                std::string message;
                const bool removed = NeoPtt_DeleteRestriction(
                    command.text, command.account_id, restriction, message);
                if (!removed)
                {
                    SendResult(false, message.c_str());
                    break;
                }
                const CInfractionBase::EInfractionType infraction_type =
                    restriction.type == "Mute" ? CInfractionBase::Mute : CInfractionBase::Gag;
                bool live_removed = false;
                if (command.player_slot >= 0 && command.player_slot < MAXPLAYERS)
                {
                    CCSPlayerController* controller =
                        CCSPlayerController::FromSlot(command.player_slot);
                    if (controller && controller->IsConnected() &&
                        controller->m_steamID() == restriction.steam_id)
                    {
                        if (ZEPlayer* player = controller->GetZEPlayer())
                            live_removed = g_pAdminSystem->FindAndRemoveInfraction(player, infraction_type);
                    }
                }
                if (!live_removed)
                {
                    (void)g_pAdminSystem->FindAndRemoveInfractionSteamId64(
                        restriction.steam_id, infraction_type);
                }
                g_pAdminSystem->SaveInfractions();
                audit_target = restriction.player_name + " (" +
                    std::to_string(restriction.steam_id) + ")";
                SendResult(true, message.c_str());
                (void)NeoPtt_SendDisciplineCatalog(command.session_id);
                break;
            }


            // ---------------------------------------------
            // 116 - REQUEST DISCIPLINE HISTORY BY STEAMID64
            // ---------------------------------------------
            case 116:
            {
                const bool sent = NeoPtt_SendDisciplineHistory(
                    command.session_id,
                    command.text);
                SendResult(sent, sent ? "Player discipline history refreshed."
                    : "Player discipline history reply failed.");
                break;
            }


            case 120:
            {
                const bool sent = NeoPtt_SendMapRotationCatalog(command.session_id);
                SendResult(sent, sent ? "Map rotation refreshed." : "Map rotation reply failed.");
                break;
            }

            case 121:
            {
                const auto maps = NeoAdmin_ScanFilesystemMaps();
                std::vector<std::string> allowed;
                allowed.reserve(maps.size());
                for (const NeoFilesystemMapEntry& map : maps)
				{
					if (NeoAdmin_IsZombieSurvivalMap(map) &&
						!neo_admin::HasPermission(command.permissions,
							neo_admin::Permission::ManageZombieMode))
					{
						continue;
					}
                    allowed.push_back(map.token);
				}
                std::string message;
                const bool saved = NeoPtt_SaveMapRotation(
                    command.text, allowed, command.account_id, message);
                SendResult(saved, message.c_str());
                if (saved)
                    (void)NeoPtt_SendMapRotationCatalog(command.session_id);
                break;
            }

            case 122:
            {
                neo_admin::DueMapChange due{};
                std::string message;
                const bool ready = NeoPtt_RunNextMap(due, message);
                audit_target = due.map.empty() ? "rotation" : due.map;
                if (ready)
                {
					const auto maps = NeoAdmin_ScanFilesystemMaps();
					const NeoFilesystemMapEntry* selected =
						NeoAdmin_FindFilesystemMap(maps, due.map);
					if (!selected)
					{
						SendResult(false, "The next rotation map is no longer available.");
						break;
					}
					if (NeoAdmin_IsZombieSurvivalMap(*selected) &&
						!neo_admin::HasPermission(command.permissions,
							neo_admin::Permission::ManageZombieMode))
					{
						SendResult(false,
							"The Manage Zombie Mode permission is required for this map profile.");
						break;
					}
					std::string profile_error;
					if (!NeoAdmin_PrepareMapProfile(*selected, profile_error))
					{
						SendResult(false, profile_error.c_str());
						break;
					}
					SendResult(true, message.c_str());
                    (void)NeoPtt_SendMapRotationCatalog(command.session_id);
                    NeoAdmin_ChangeStoredMap(due.map);
                }
				else
				{
					SendResult(false, message.c_str());
				}
                break;
            }

            case 123:
            {
                const auto maps = NeoAdmin_ScanFilesystemMaps();
                std::vector<std::string> allowed;
                allowed.reserve(maps.size());
                for (const NeoFilesystemMapEntry& map : maps)
				{
					if (NeoAdmin_IsZombieSurvivalMap(map) &&
						!neo_admin::HasPermission(command.permissions,
							neo_admin::Permission::ManageZombieMode))
					{
						continue;
					}
                    allowed.push_back(map.token);
				}
                std::string message;
                const bool saved = NeoPtt_SaveScheduledMap(
                    command.text, allowed, command.account_id, message);
                SendResult(saved, message.c_str());
                if (saved)
                    (void)NeoPtt_SendMapRotationCatalog(command.session_id);
                break;
            }

            case 124:
            {
                std::string message;
                const bool removed = NeoPtt_DeleteScheduledMap(command.text, message);
                audit_target = "schedule " + command.text;
                SendResult(removed, message.c_str());
                if (removed)
                    (void)NeoPtt_SendMapRotationCatalog(command.session_id);
                break;
            }

            case 130:
            {
                const bool sent = NeoPtt_SendAnnouncementCatalog(command.session_id);
                SendResult(sent, sent ? "Announcements refreshed." : "Announcement reply failed.");
                break;
            }

            case 131:
            {
                const bool valid = !command.text.empty() && command.text.size() <= 220 &&
                    std::none_of(command.text.begin(), command.text.end(), [](unsigned char ch)
                        { return ch == 0 || ch == '\r' || ch == '\n' || ch < 0x20U; });
                if (!valid)
                {
                    SendResult(false, "Announcement must be 1-220 characters on one line.");
                    break;
                }
                audit_target = "all players";
                NeoAdmin_BroadcastChat(command.text.c_str());
                SendResult(true, "Announcement sent.");
                break;
            }

            case 132:
            {
                std::string message;
                const bool saved = NeoPtt_SaveAnnouncement(
                    command.text, command.account_id, message);
                SendResult(saved, message.c_str());
                if (saved)
                    (void)NeoPtt_SendAnnouncementCatalog(command.session_id);
                break;
            }

            case 133:
            {
                std::string message;
                const bool removed = NeoPtt_DeleteAnnouncement(command.text, message);
                audit_target = "announcement " + command.text;
                SendResult(removed, message.c_str());
                if (removed)
                    (void)NeoPtt_SendAnnouncementCatalog(command.session_id);
                break;
            }

            case 140:
            {
                const bool valid =
                    !command.text.empty() &&
                    command.text.size() <= 2048 &&
                    std::none_of(
                        command.text.begin(),
                        command.text.end(),
                        [](unsigned char ch)
                        {
                            return ch == 0 || ch == '\r' || ch == '\n' ||
                                ch < 0x20U;
                        });

                if (!valid)
                {
                    SendResult(
                        false,
                        "Console commands must be 1-2048 characters on one line.");
                    break;
                }

                Message(
                    "[NEO ADMIN] %s (%s) ran server console command: %s\n",
                    command.operator_name.empty()
                        ? command.account_id.c_str()
                        : command.operator_name.c_str(),
                    command.account_id.c_str(),
                    audit_target.c_str());

                std::string output;
                const bool executed =
                    ExecuteServerConsoleCommand(
                        command.text.c_str(),
                        output);

                if (output.empty())
                    output = executed
                        ? "Command executed; no console output was returned."
                        : "The command could not be executed.";

                SendResult(executed, output.c_str());
                break;
            }

            case 141:
            {
				if (!kZombieSurvivalImplemented)
				{
					SendResult(false, "Zombie Survival is not implemented yet.");
					break;
				}
                SendResult(
                    true,
                    g_cvarEnableZR.Get()
                        ? "Zombie Survival is enabled."
                        : "Zombie Survival is disabled.");
                break;
            }

            case 142:
            {
				if (!kZombieSurvivalImplemented)
				{
					g_cvarEnableZR.Set(false);
					SendResult(false, "Zombie Survival is not implemented yet.");
					break;
				}
                if (command.value != 0 && command.value != 1)
                {
                    SendResult(false, "Zombie Survival requires an enabled or disabled value.");
                    break;
                }

                const bool enabled = command.value == 1;
                std::string message;
                if (enabled && !ZR_EnsureConfiguration(message))
                {
                    SendResult(false, message.c_str());
                    break;
                }
                if (!ZR_SaveEnabledPreference(enabled, message))
                {
                    SendResult(false, message.c_str());
                    break;
                }

				// Activation is deferred until the reloaded map has precached and
				// initialized every ZR resource. Deactivation is safe immediately.
				if (!enabled)
					g_cvarEnableZR.Set(false);
                const char* currentMap = GetGlobals()
                    ? GetGlobals()->mapname.ToCStr()
                    : nullptr;
                message = enabled
                    ? "Zombie Survival enabled; reloading the current map."
                    : "Zombie Survival disabled; reloading the current map.";
                SendResult(true, message.c_str());

                if (currentMap && *currentMap)
                    NeoAdmin_ChangeStoredMap(currentMap);
                break;
            }

            case 143:
            {
                std::uint64_t workshop_id = 0;
                const auto parsed = std::from_chars(
                    command.text.data(),
                    command.text.data() + command.text.size(),
                    workshop_id);
                if (command.text.empty() || workshop_id == 0 ||
                    parsed.ec != std::errc{} ||
                    parsed.ptr != command.text.data() + command.text.size())
                {
                    SendResult(false, "A valid numeric Workshop map ID is required.");
                    break;
                }

                const std::string workshop_command =
                    "host_workshop_map " + std::to_string(workshop_id);
                audit_target = std::to_string(workshop_id);
                SendResult(
                    true,
                    "CS2 is downloading the latest Workshop map and will switch when ready.");
                g_pEngineServer2->ServerCommand(workshop_command.c_str());
                break;
            }


            default:
            {
                SendResult(
                    false,
                    "Action is reserved for a later control stage.");

                break;
            }
        }
    }
    // NEO ADMIN CONTROL STAGE 3V FILESYSTEM MAP CONTROL END

    for (int due_index = 0; due_index < 4; ++due_index)
    {
        neo_admin::DueAnnouncement due{};
        if (!NeoPtt_TakeDueAnnouncement(due))
            break;
        NeoAdmin_BroadcastChat(due.message.c_str());
        NeoPtt_RecordAudit(due.created_by, "Send scheduled announcement",
            "all players", true, due.message);
    }

    neo_admin::DueMapChange due_map{};
    if (NeoPtt_TakeDueMap(due_map))
    {
		const auto maps = NeoAdmin_ScanFilesystemMaps();
		const NeoFilesystemMapEntry* selected =
			NeoAdmin_FindFilesystemMap(maps, due_map.map);
		std::string profile_error;
		const bool ready = selected &&
			NeoAdmin_PrepareMapProfile(*selected, profile_error);
		NeoPtt_RecordAudit(due_map.created_by, "Run scheduled map change",
			due_map.map, ready, ready ? due_map.source :
				(selected ? profile_error : "Scheduled map is no longer available."));
		if (ready)
			NeoAdmin_ChangeStoredMap(due_map.map);
    }



    // NEO ADMIN authenticated drag-teleport commands.
    for (int command_index = 0; command_index < 8; ++command_index)
    {
        VoiceBridge::TeleportCommand command{};
        if (!g_VoiceBridge.ReceiveTeleportCommand(command))
            break;

        if (command.player_slot < 0 ||
            command.player_slot >= GetGlobals()->maxClients)
        {
            continue;
        }

        CCSPlayerController* controller =
            CCSPlayerController::FromSlot(command.player_slot);
        CServerSideClient* server_client =
            GetClientBySlot(CPlayerSlot(command.player_slot));

        if (!controller ||
            !controller->IsConnected() ||
            !server_client)
        {
            continue;
        }

        const std::uint64_t actual_steam_id =
            server_client->GetClientSteamID().ConvertToUint64();

        if (command.steam_id != 0 &&
            actual_steam_id != command.steam_id)
        {
            Warning(
                "[NEO ADMIN] Rejected teleport: "
                "slot/SteamID mismatch for slot %d\n",
                command.player_slot);
            continue;
        }

        CCSPlayerPawn* pawn = controller->GetPlayerPawn();
        if (!pawn || !pawn->IsAlive())
            continue;

        // Reject NaN and coordinates far outside practical Source 2 maps.
        if (command.x != command.x ||
            command.y != command.y ||
            command.z != command.z ||
            command.x < -100000.0f ||
            command.x > 100000.0f ||
            command.y < -100000.0f ||
            command.y > 100000.0f ||
            command.z < -100000.0f ||
            command.z > 100000.0f)
        {
            continue;
        }

        Vector destination;

        if (!NeoFindSafeTeleportDestination(
                pawn,
                command.x,
                command.y,
                command.z,
                destination))
        {
            continue;
        }

        pawn->Teleport(
            &destination,
            nullptr,
            &vec3_origin);

        Message(
            "[NEO ADMIN] Safe-ground drag-teleported %s "
            "(slot %d) to %.1f %.1f %.1f\n",
            server_client->GetClientName(),
            command.player_slot,
            destination.x,
            destination.y,
            destination.z);
    }

    if (!GetGlobals()->m_bInSimulation ||
        !g_VoiceBridge.ShouldSendPositionFrame())
        return;

    for (int i = 0; i < GetGlobals()->maxClients; ++i)
    {
        CCSPlayerController* controller = CCSPlayerController::FromSlot(i);
        if (!controller || !controller->IsConnected())
            continue;

        CCSPlayerPawn* pawn = controller->GetPlayerPawn();
        CServerSideClient* serverClient = GetClientBySlot(CPlayerSlot(i));
        if (!pawn || !serverClient)
            continue;

        const Vector origin = pawn->GetAbsOrigin();
        const QAngle angles = pawn->GetAbsRotation();

        g_VoiceBridge.SendPlayerPosition(
            static_cast<std::uint32_t>(GetGlobals()->tickcount),
            serverClient->GetClientSteamID().ConvertToUint64(),
            i,
            serverClient->GetClientName(),
            origin.x,
            origin.y,
            origin.z,
            angles[YAW],
            controller->m_iTeamNum(),
            pawn->m_iHealth(),
            pawn->IsAlive(),
            controller->IsBot());
    }
}

GS_EVENT_MEMBER(CGameSystem, ServerPreEntityThink)
{
    VoiceBridge_OnServerFrame();
	VPROF_BUDGET("CGameSystem::ServerPreEntityThink", "CS2FixesPerFrame")
	g_playerManager->FlashLightThink();
	g_pIdleSystem->UpdateIdleTimes();

	if (GetGlobals())
		EntityHandler_OnGameFramePre(GetGlobals()->m_bInSimulation, GetGlobals()->tickcount);
}

// Called every frame after entities think
GS_EVENT_MEMBER(CGameSystem, ServerPostEntityThink)
{
	VPROF_BUDGET("CGameSystem::ServerPostEntityThink", "CS2FixesPerFrame")
	g_playerManager->UpdatePlayerStates();
}

GS_EVENT_MEMBER(CGameSystem, GameShutdown)
{
	g_pGameRules = nullptr;
}

GS_EVENT_MEMBER(CGameSystem, PostSpawnGroupLoad)
{
	if (!g_pSpawnGroupMgr)
		return;

	CUtlVector<SpawnGroupHandle_t> vecActualSpawnGroups;
	addresses::GetSpawnGroups(g_pSpawnGroupMgr, &vecActualSpawnGroups);

	auto pClients = GetClientList();

	// Ensure clients have no leaked spawngroups every time a new one loads
	// Due to a timing problem for leaked spawngroups with this callback, clients may have one lingering, this is fine since it'll still be taken care of next time a spawngroup loads
	FOR_EACH_VEC(*pClients, i)
	{
		auto pClient = (*pClients)[i];

		if (!pClient || pClient->m_vecLoadedSpawnGroups.Count() == vecActualSpawnGroups.Count())
			continue;

		pClient->m_vecLoadedSpawnGroups = vecActualSpawnGroups;
	}
}

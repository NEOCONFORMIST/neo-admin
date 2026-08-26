#include "zr_prop_push.h"

#include <iostream>
#include <string_view>
#include <utility>

int main()
{
	constexpr std::pair<std::string_view, bool> propCases[] = {
		{"prop_physics", true},
		{"prop_physics_multiplayer", true},
		{"prop_physics_override", true},
		{"prop_dynamic", false},
		{"prop_ragdoll", false},
		{"", false},
	};

	for (const auto& [className, expected] : propCases)
	{
		if (zr::prop_push::IsMovablePhysicsPropClass(className) != expected)
		{
			std::cerr << "unexpected prop classification for " << className << '\n';
			return 1;
		}
	}

	constexpr std::pair<std::string_view, bool> weaponCases[] = {
		{"weapon_knife", true},
		{"weapon_knife_t", true},
		{"weapon_knife_karambit", true},
		{"weapon_knifegg", true},
		{"weapon_bayonet", true},
		{"weapon_awp", false},
		{"knife", false},
		{"", false},
	};

	for (const auto& [className, expected] : weaponCases)
	{
		if (zr::prop_push::IsKnifeWeaponClass(className) != expected)
		{
			std::cerr << "unexpected knife classification for " << className << '\n';
			return 1;
		}
	}

	struct ForceCase
	{
		float nativeForce;
		float scale;
		float minimumForce;
		float maximumForce;
		float expected;
	};

	constexpr ForceCase forceCases[] = {
		{12000.0f, 3.0f, 18000.0f, 45000.0f, 36000.0f},
		{1000.0f, 3.0f, 18000.0f, 45000.0f, 18000.0f},
		{30000.0f, 3.0f, 18000.0f, 45000.0f, 45000.0f},
		{0.0f, 3.0f, 18000.0f, 45000.0f, 18000.0f},
		{12000.0f, -1.0f, 18000.0f, 45000.0f, 18000.0f},
		{12000.0f, 3.0f, 45000.0f, 18000.0f, 45000.0f},
	};

	for (const ForceCase& forceCase : forceCases)
	{
		const float actual = zr::prop_push::ComputeForceMagnitude(
			forceCase.nativeForce,
			forceCase.scale,
			forceCase.minimumForce,
			forceCase.maximumForce);
		if (actual != forceCase.expected)
		{
			std::cerr << "unexpected force magnitude: expected " << forceCase.expected << ", got " << actual << '\n';
			return 1;
		}
	}

	std::cout << "Zombie Survival prop-push self-test passed.\n";
	return 0;
}

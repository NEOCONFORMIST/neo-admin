#pragma once

#include <string_view>

namespace zr::prop_push
{
inline constexpr bool IsMovablePhysicsPropClass(std::string_view className)
{
	return className.starts_with("prop_physics");
}

inline constexpr bool IsKnifeWeaponClass(std::string_view className)
{
	return className.starts_with("weapon_knife") || className == "weapon_bayonet";
}

inline constexpr float ComputeForceMagnitude(float nativeForce, float scale, float minimumForce, float maximumForce)
{
	const float safeNativeForce = nativeForce > 0.0f ? nativeForce : 0.0f;
	const float safeScale = scale > 0.0f ? scale : 0.0f;
	const float safeMinimum = minimumForce > 0.0f ? minimumForce : 0.0f;
	const float safeMaximum = maximumForce > safeMinimum ? maximumForce : safeMinimum;
	const float scaledForce = safeNativeForce * safeScale;

	if (scaledForce < safeMinimum)
		return safeMinimum;
	if (scaledForce > safeMaximum)
		return safeMaximum;
	return scaledForce;
}
}

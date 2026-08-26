#pragma once

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace zr::runtime
{
enum class ModeCvarTransition
{
	None,
	CaptureAndApply,
	Apply,
	Restore,
};

inline constexpr ModeCvarTransition DetermineModeCvarTransition(
	bool snapshotCaptured,
	bool modeEnabled) noexcept
{
	if (modeEnabled)
	{
		return snapshotCaptured
			? ModeCvarTransition::Apply
			: ModeCvarTransition::CaptureAndApply;
	}
	return snapshotCaptured
		? ModeCvarTransition::Restore
		: ModeCvarTransition::None;
}

inline constexpr bool TryRandomIndex(
	std::size_t size,
	std::uint32_t randomValue,
	std::size_t& index) noexcept
{
	if (size == 0)
		return false;
	index = static_cast<std::size_t>(randomValue) % size;
	return true;
}

inline bool TryInitialMoanDelay(
	float interval,
	std::uint32_t randomValue,
	float& delay) noexcept
{
	if (!(interval > 0.0f) || !std::isfinite(interval))
		return false;
	delay = static_cast<float>(std::fmod(
		static_cast<double>(randomValue),
		static_cast<double>(interval)));
	return true;
}

inline constexpr bool ShouldPlayOneInN(
	std::uint32_t randomValue,
	int chance) noexcept
{
	return chance > 0 && randomValue % static_cast<std::uint32_t>(chance) == 0;
}

inline constexpr int MotherZombieCount(
	std::size_t candidateCount,
	int ratio,
	int minimumCount,
	int minimumPlayersRequired) noexcept
{
	if (candidateCount == 0 ||
		candidateCount < static_cast<std::size_t>(
			minimumPlayersRequired > 0 ? minimumPlayersRequired : 0))
	{
		return 0;
	}

	const std::size_t safeRatio = static_cast<std::size_t>(ratio > 0 ? ratio : 1);
	std::size_t count = candidateCount / safeRatio;
	const std::size_t safeMinimum = static_cast<std::size_t>(
		minimumCount > 0 ? minimumCount : 0);
	if (count < safeMinimum)
		count = safeMinimum;
	if (count > candidateCount)
		count = candidateCount;
	if (count > static_cast<std::size_t>(std::numeric_limits<int>::max()))
		count = static_cast<std::size_t>(std::numeric_limits<int>::max());
	return static_cast<int>(count);
}

inline constexpr int ReduceImmunity(int immunity, int reduction) noexcept
{
	const int safeReduction = reduction > 0 ? reduction : 0;
	return immunity > safeReduction ? immunity - safeReduction : 0;
}
} // namespace zr::runtime

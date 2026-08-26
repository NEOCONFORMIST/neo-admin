#include "zr_runtime_safety.h"

#include <cmath>
#include <cstddef>
#include <iostream>
#include <limits>

int main()
{
	using zr::runtime::ModeCvarTransition;
	if (zr::runtime::DetermineModeCvarTransition(false, false) !=
			ModeCvarTransition::None ||
		zr::runtime::DetermineModeCvarTransition(false, true) !=
			ModeCvarTransition::CaptureAndApply ||
		zr::runtime::DetermineModeCvarTransition(true, true) !=
			ModeCvarTransition::Apply ||
		zr::runtime::DetermineModeCvarTransition(true, false) !=
			ModeCvarTransition::Restore)
	{
		std::cerr << "Zombie Survival CVar transition decision failed\n";
		return 1;
	}

	std::size_t index = 99;
	if (zr::runtime::TryRandomIndex(0, 7, index))
	{
		std::cerr << "empty random selection was accepted\n";
		return 1;
	}
	if (!zr::runtime::TryRandomIndex(4, 7, index) || index != 3)
	{
		std::cerr << "bounded random selection failed\n";
		return 1;
	}

	float delay = -1.0f;
	if (zr::runtime::TryInitialMoanDelay(0.0f, 7, delay) ||
		zr::runtime::TryInitialMoanDelay(
			std::numeric_limits<float>::infinity(), 7, delay))
	{
		std::cerr << "invalid moan interval was accepted\n";
		return 1;
	}
	if (!zr::runtime::TryInitialMoanDelay(2.5f, 7, delay) ||
		std::fabs(delay - 2.0f) > 0.001f)
	{
		std::cerr << "moan delay was not bounded by its interval\n";
		return 1;
	}

	if (!zr::runtime::ShouldPlayOneInN(0, 1) ||
		!zr::runtime::ShouldPlayOneInN(10, 5) ||
		zr::runtime::ShouldPlayOneInN(11, 5) ||
		zr::runtime::ShouldPlayOneInN(0, 0))
	{
		std::cerr << "one-in-N probability check failed\n";
		return 1;
	}

	if (zr::runtime::MotherZombieCount(0, 7, 1, 0) != 0 ||
		zr::runtime::MotherZombieCount(1, 7, 1, 2) != 0 ||
		zr::runtime::MotherZombieCount(2, 7, 1, 2) != 1 ||
		zr::runtime::MotherZombieCount(3, 7, 8, 0) != 3 ||
		zr::runtime::MotherZombieCount(14, 7, 1, 2) != 2)
	{
		std::cerr << "mother-zombie count was not safely clamped\n";
		return 1;
	}

	if (zr::runtime::ReduceImmunity(100, 20) != 80 ||
		zr::runtime::ReduceImmunity(10, 20) != 0 ||
		zr::runtime::ReduceImmunity(-5, 20) != 0)
	{
		std::cerr << "immunity reduction escaped the valid range\n";
		return 1;
	}

	std::cout << "Zombie Survival runtime-safety self-test passed.\n";
	return 0;
}

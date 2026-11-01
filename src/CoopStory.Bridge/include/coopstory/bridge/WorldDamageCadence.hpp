#pragma once
#include <algorithm>
#include <cstdint>

namespace coopstory::bridge {
// Preserve the 90 ms source cadence, while accepting at most two delayed
// requests together. Receiver credit bounds sustained damage even if source
// timestamps jump ahead. Commit a candidate only after damage is applied.
class WorldDamageCadence final {
public:
    bool TryAccept(std::uint64_t receivedAtMs, std::uint64_t senderTickMs) noexcept {
        auto available = creditMs_;
        if (initialized_) {
            if (receivedAtMs < lastReceivedMs_) return false;
            const auto elapsed = receivedAtMs - lastReceivedMs_;
            if (senderTickMs != 0U && lastSenderMs_ != 0U) {
                if (senderTickMs < lastSenderMs_ || senderTickMs - lastSenderMs_ < kIntervalMs)
                    return false;
            } else if (elapsed < kIntervalMs) {
                // Legacy/no-clock messages cannot claim a source-time burst.
                return false;
            }
            available = std::min(kCapacityMs, available + std::min(elapsed, kCapacityMs));
        }
        if (available < kIntervalMs) return false;
        creditMs_ = available - kIntervalMs;
        lastReceivedMs_ = receivedAtMs;
        lastSenderMs_ = senderTickMs;
        initialized_ = true;
        return true;
    }
    void Reset() noexcept { *this = WorldDamageCadence{}; }
private:
    static constexpr std::uint64_t kIntervalMs = 90U;
    static constexpr std::uint64_t kCapacityMs = kIntervalMs * 2U;
    std::uint64_t creditMs_{kCapacityMs};
    std::uint64_t lastReceivedMs_{};
    std::uint64_t lastSenderMs_{};
    bool initialized_{};
};
} // namespace coopstory::bridge

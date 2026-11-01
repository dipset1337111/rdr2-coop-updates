#pragma once
#include "coopstory/bridge/Domain.hpp"
#include <array>
#include <span>
#include <vector>

namespace coopstory::bridge {
// Read-only diagnostics. An observed fully-looted bit is NOT an inventory
// receipt, a skinning receipt, or a stable network identity.
struct CorpseLootObservation final {
    LocalEntityHandle body{};
    std::uint32_t modelHash{};
    LocalEntityHandle looter{};
    bool human{}, fullyLooted{};
    bool operator==(const CorpseLootObservation&) const noexcept = default;
};
enum class CorpseLootProbeEventKind { FirstSeen, Changed, Lost };
struct CorpseLootProbeEvent final {
    CorpseLootProbeEventKind kind{};
    CorpseLootObservation previous{}, current{};
};
class CorpseLootProbe final {
public:
    static constexpr std::size_t Capacity = 16;
    bool Due(std::uint64_t now, bool enabled) noexcept {
        if (!enabled) { count_ = 0; hasTick_ = false; return false; }
        if (hasTick_ && now >= tick_ && now - tick_ < 250) return false;
        if (hasTick_ && now < tick_) count_ = 0;
        tick_ = now; hasTick_ = true; return true;
    }
    std::vector<CorpseLootProbeEvent> Observe(std::span<const CorpseLootObservation> samples) {
        std::array<CorpseLootObservation, Capacity> next{};
        std::size_t size{};
        for (const auto& sample : samples) {
            if (!sample.body || !sample.modelHash) continue;
            bool duplicate{};
            for (std::size_t i = 0; i < size; ++i) duplicate |= next[i].body == sample.body;
            if (!duplicate && size < Capacity) next[size++] = sample;
        }
        std::vector<CorpseLootProbeEvent> events;
        for (std::size_t i = 0; i < count_; ++i) {
            bool found{};
            for (std::size_t j = 0; j < size; ++j)
                found |= states_[i].body == next[j].body && states_[i].modelHash == next[j].modelHash;
            if (!found) events.push_back({CorpseLootProbeEventKind::Lost, states_[i], {}});
        }
        for (std::size_t i = 0; i < size; ++i) {
            const CorpseLootObservation* previous{};
            for (std::size_t j = 0; j < count_; ++j)
                if (states_[j].body == next[i].body && states_[j].modelHash == next[i].modelHash) previous = &states_[j];
            if (!previous) events.push_back({CorpseLootProbeEventKind::FirstSeen, {}, next[i]});
            else if (*previous != next[i]) events.push_back({CorpseLootProbeEventKind::Changed, *previous, next[i]});
        }
        states_ = next; count_ = size;
        return events;
    }
private:
    std::array<CorpseLootObservation, Capacity> states_{};
    std::size_t count_{};
    std::uint64_t tick_{};
    bool hasTick_{};
};
}

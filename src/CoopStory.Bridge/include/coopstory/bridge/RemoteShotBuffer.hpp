#pragma once

#include "coopstory/bridge/FrameCodec.hpp"
#include <array>
#include <cmath>
#include <optional>

namespace coopstory::bridge {

// Visual-only shots. Authoritative hit/damage processing is independent.
// Keep the captured direction while equipment streams, never retarget a shot
// from a later aim sample and never replay an old baseline on reconnect.
class RemoteShotBuffer final {
public:
    struct Shot final {
        PlayerStatePayload state{};
        std::uint32_t weaponHash{};
        std::uint64_t observedAtMs{};
    };
    bool Observe(const PlayerStatePayload& state, std::uint32_t weaponHash,
                 std::uint64_t nowMs) noexcept {
        if (state.fireSequence == 0U) return false;
        const auto disposition = sequences_.Observe(state.fireSequence);
        const bool newShot = disposition == SequenceDisposition::Newer ||
            (disposition == SequenceDisposition::First &&
             (state.flags & static_cast<std::uint32_t>(PlayerStateFlag::Firing)) != 0U);
        if (!newShot ||
            (state.flags & static_cast<std::uint32_t>(PlayerStateFlag::AimTargetValid)) == 0U ||
            !Finite(state.position) || !Finite(state.aimTarget)) return false;
        if (size_ == shots_.size()) { Pop(); ++dropped_; }
        shots_[size_++] = {state, weaponHash, nowMs};
        return true;
    }
    std::optional<Shot> TakeReady(std::uint64_t nowMs, std::uint32_t weaponHash,
                                  bool ready) noexcept {
        while (size_ != 0U) {
            const auto shot = shots_[0];
            if (nowMs < shot.observedAtMs || nowMs - shot.observedAtMs > 350U ||
                (shot.weaponHash != 0U && weaponHash != 0U && shot.weaponHash != weaponHash)) {
                Pop(); ++dropped_;
                continue;
            }
            if (!ready || weaponHash == 0U) return std::nullopt;
            Pop();
            return shot;
        }
        return std::nullopt;
    }
    std::size_t Size() const noexcept { return size_; }
    std::uint64_t Dropped() const noexcept { return dropped_; }
    void Reset() noexcept { sequences_.Reset(); size_ = 0U; dropped_ = 0U; }
private:
    static bool Finite(const Vec3& v) noexcept {
        return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
    }
    void Pop() noexcept {
        for (std::size_t i = 1U; i < size_; ++i) shots_[i - 1U] = shots_[i];
        --size_;
    }
    SequenceWindow sequences_{};
    std::array<Shot, 8U> shots_{};
    std::size_t size_{};
    std::uint64_t dropped_{};
};
} // namespace coopstory::bridge

#pragma once
#include "coopstory/bridge/FrameCodec.hpp"
#include <optional>
namespace coopstory::bridge {
// Independent from either motion motor. Never feeds back into movement.
class RemoteDiagnosticTarget final {
public:
    void Reset() noexcept { state_.reset(); }
    void Observe(const PlayerStatePayload& state, std::uint64_t now) noexcept {
        if (!state.entityId.IsValid() || !std::isfinite(state.position.x) ||
            !std::isfinite(state.position.y) || !std::isfinite(state.position.z) ||
            !std::isfinite(state.heading) || !std::isfinite(state.desiredMoveBlend)) { Reset(); return; }
        state_ = state; tick_ = now;
    }
    std::optional<PlayerStatePayload> Read(NetEntityId id, std::uint64_t now) const noexcept {
        if (!state_ || state_->entityId != id || now < tick_ || now - tick_ > 1500) return std::nullopt;
        return state_;
    }
private:
    std::optional<PlayerStatePayload> state_{};
    std::uint64_t tick_{};
};
}

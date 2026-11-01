#pragma once
#include "coopstory/bridge/CorpseControl.hpp"
#include <limits>

namespace coopstory::bridge {
// Staged client component: NOT connected to vanilla loot input or native tasks.
// Keep one instance per authenticated session/actor, including reconnects.
// All methods run on the game thread. Only authenticated replies may enter.
struct CorpseLocalBinding final {
    NetEntityId target{};
    LocalEntityHandle handle{};
    std::uint32_t modelHash{};
    std::uint64_t incarnation{}; // Mapping generation, not just a reusable handle.
    bool operator==(const CorpseLocalBinding&) const noexcept = default;
};
struct CorpseExecutionObservation final {
    CorpseLocalBinding binding{};
    bool actorAlive{}, targetDead{}, actionAvailable{};
    float distance{};
    std::uint64_t observedAtMs{};
};
enum class CorpseClientPhase { Idle, Reserving, Starting, Ready, Executing, Cancelling, Reporting, Reconciling, Blocked };

class CorpseActionGate final {
public:
    CorpseActionGate(std::array<std::uint8_t, 16> session, NetEntityId actor)
        : session_(session), actor_(actor) {}
    CorpseActionGate(const CorpseActionGate&) = delete;
    CorpseActionGate& operator=(const CorpseActionGate&) = delete;
    CorpseClientPhase Phase() const noexcept { return phase_; }
    // Caller may resend this exact command. Reading it never authorizes a task.
    std::optional<CorpseCommand> Pending() const noexcept { return pending_; }

    bool Begin(CorpseLocalBinding binding, CorpseAction action, std::uint64_t nowMs) {
        if (!Advance(nowMs) || phase_ != CorpseClientPhase::Idle || !connected_ ||
            binding.handle == 0 || binding.modelHash == 0 || binding.incarnation == 0 ||
            request_ == std::numeric_limits<std::uint32_t>::max()) return false;
        CorpseCommand command{session_, actor_, binding.target, request_ + 1, action, CorpseCommandKind::Reserve, 0};
        if (!corpse_codec::Valid(command)) return false;
        ++request_; binding_ = binding; command_ = command;
        Queue(CorpseCommandKind::Reserve, CorpseClientPhase::Reserving, nowMs);
        return true;
    }
    bool Receive(const CorpseReply& reply, std::uint64_t nowMs) {
        if (!Advance(nowMs) || !connected_ || !pending_ || !corpse_codec::Valid(reply) ||
            reply.command != *pending_) return false;
        const auto previous = phase_;
        pending_.reset();
        const bool granted = reply.decision == CorpseDecision::Allowed || reply.decision == CorpseDecision::Replayed;
        if (granted && reply.hasState && reply.phase == CorpseUsePhase::Consumed) {
            phase_ = CorpseClientPhase::Idle;
        } else if (previous == CorpseClientPhase::Reserving && granted && reply.phase == CorpseUsePhase::Reserved) {
            command_.token = reply.stateToken;
            Queue(CorpseCommandKind::Start, CorpseClientPhase::Starting, nowMs);
        } else if (previous == CorpseClientPhase::Starting && granted && reply.phase == CorpseUsePhase::Executing) {
            phase_ = CorpseClientPhase::Ready;
            // Retain the START deadline. Network delay cannot renew permission.
        } else if ((previous == CorpseClientPhase::Cancelling || previous == CorpseClientPhase::Reconciling) &&
                   granted && reply.phase == CorpseUsePhase::Available) {
            phase_ = CorpseClientPhase::Idle;
        } else if (previous == CorpseClientPhase::Reserving && !granted && !reply.hasState) {
            phase_ = CorpseClientPhase::Idle;
        } else {
            // Never infer item delivery or a free body from an ambiguous reply.
            phase_ = CorpseClientPhase::Blocked;
        }
        return true;
    }
    // The sole edge that permits a native task. Revalidate the mapping and local
    // context immediately before invoking it; duplicate replies cannot re-arm it.
    bool TakeExecutionPermit(const CorpseExecutionObservation& observation, std::uint64_t nowMs) {
        if (!Advance(nowMs) || phase_ != CorpseClientPhase::Ready || !connected_) return false;
        if (observation.binding != binding_ || !observation.actorAlive || !observation.targetDead ||
            !observation.actionAvailable || !std::isfinite(observation.distance) ||
            observation.distance < 0 || observation.distance > 3.0F ||
            observation.observedAtMs > nowMs || nowMs - observation.observedAtMs > 100) {
            Queue(CorpseCommandKind::Cancel, CorpseClientPhase::Cancelling, nowMs);
            return false;
        }
        phase_ = CorpseClientPhase::Executing;
        return true;
    }
    void Cancel(std::uint64_t nowMs) {
        if (!Advance(nowMs) || phase_ == CorpseClientPhase::Idle || phase_ == CorpseClientPhase::Blocked ||
            phase_ == CorpseClientPhase::Cancelling || phase_ == CorpseClientPhase::Reporting ||
            phase_ == CorpseClientPhase::Reconciling) return;
        if (command_.token == 0) { Block(); return; }
        Queue(CorpseCommandKind::Cancel, CorpseClientPhase::Cancelling, nowMs);
    }
    // This is a report, NOT evidence of inventory delivery. The host quarantines
    // it until a separate verifier confirms the engine receipt.
    bool ReportCompletion(std::uint64_t nowMs) {
        if (!Advance(nowMs) || phase_ != CorpseClientPhase::Executing) return false;
        Queue(CorpseCommandKind::ReportComplete, CorpseClientPhase::Reporting, nowMs);
        return true;
    }
    // Caller must first authenticate the current transport to this SAME session
    // and actor and retire old callbacks. No execution is resumed by this query.
    bool ReconcileAfterAuthentication(std::uint64_t nowMs) {
        if (!Advance(nowMs) || phase_ != CorpseClientPhase::Blocked || !corpse_codec::Valid(command_)) return false;
        connected_ = true;
        Queue(CorpseCommandKind::Reconcile, CorpseClientPhase::Reconciling, nowMs);
        return true;
    }
    void Poll(std::uint64_t nowMs) { Advance(nowMs); }
    void Disconnect() noexcept { connected_ = false; Block(); }

private:
    bool Advance(std::uint64_t nowMs) {
        if (hasClock_ && nowMs < clock_) { Block(); return false; }
        hasClock_ = true; clock_ = nowMs;
        if ((pending_ || phase_ == CorpseClientPhase::Ready) && nowMs - issuedAt_ >= 1500) {
            // Unknown reservation/start outcome: no timeout retry with a new ID.
            Block(); return false;
        }
        return true;
    }
    void Block() noexcept { pending_.reset(); phase_ = CorpseClientPhase::Blocked; }
    void Queue(CorpseCommandKind kind, CorpseClientPhase phase, std::uint64_t nowMs) {
        command_.kind = kind; pending_ = command_; phase_ = phase; issuedAt_ = nowMs;
    }
    std::array<std::uint8_t, 16> session_{};
    NetEntityId actor_{};
    CorpseLocalBinding binding_{};
    CorpseCommand command_{};
    std::optional<CorpseCommand> pending_{};
    CorpseClientPhase phase_{CorpseClientPhase::Idle};
    std::uint32_t request_{};
    std::uint64_t clock_{}, issuedAt_{};
    bool hasClock_{}, connected_{true};
};
}

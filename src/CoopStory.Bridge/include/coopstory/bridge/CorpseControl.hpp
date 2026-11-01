#pragma once
#include "coopstory/bridge/Domain.hpp"
#include <array>
#include <optional>
#include <span>
#include <stdexcept>
#include <vector>

namespace coopstory::bridge {
// Protocol-38 payloads; live native loot routing remains disabled.
enum class CorpseAction : std::uint8_t { LootBody = 1, SkinAnimal = 2 };
enum class CorpseUsePhase : std::uint8_t { Available, Reserved, Executing, Uncertain, Consumed };
enum class CorpseDecision : std::uint8_t { Allowed, Replayed, Invalid, Stale, Unavailable, Busy, TooFar, Capacity, WrongOwner };
enum class CorpseCommandKind : std::uint8_t { Reserve = 1, Start = 2, Cancel = 3, ReportComplete = 4, Reconcile = 5 };
struct CorpseCommand final {
    std::array<std::uint8_t, 16> sessionId{}; // .NET Guid.TryWriteBytes byte order.
    NetEntityId actor{}, target{};
    std::uint32_t requestId{};
    CorpseAction action{CorpseAction::LootBody};
    CorpseCommandKind kind{CorpseCommandKind::Reserve};
    std::uint64_t token{};
    bool operator==(const CorpseCommand&) const noexcept = default;
};
struct CorpseReply final {
    CorpseCommand command{};
    CorpseDecision decision{CorpseDecision::Invalid};
    bool hasState{};
    CorpseUsePhase phase{CorpseUsePhase::Available};
    std::uint64_t stateToken{};
    bool operator==(const CorpseReply&) const noexcept = default;
};
namespace corpse_codec {
inline bool Valid(const CorpseCommand& c) noexcept {
    bool session{}; for (auto byte : c.sessionId) session |= byte != 0;
    return session && c.actor.IsValid() && c.target.IsValid() && c.actor != c.target && c.requestId != 0 &&
        (c.action == CorpseAction::LootBody || c.action == CorpseAction::SkinAnimal) &&
        static_cast<unsigned>(c.kind) >= 1 && static_cast<unsigned>(c.kind) <= 5 &&
        (c.kind == CorpseCommandKind::Reserve ? c.token == 0 : c.kind == CorpseCommandKind::Reconcile || c.token != 0);
}
inline bool Valid(const CorpseReply& r) noexcept {
    return Valid(r.command) && static_cast<unsigned>(r.decision) <= 8 && static_cast<unsigned>(r.phase) <= 4 &&
        (r.hasState ? r.stateToken != 0 : r.stateToken == 0 && r.phase == CorpseUsePhase::Available) &&
        ((r.decision != CorpseDecision::Allowed && r.decision != CorpseDecision::Replayed) || r.hasState) &&
        (!r.hasState || r.command.kind == CorpseCommandKind::Reserve ||
         (r.command.kind == CorpseCommandKind::Reconcile && r.command.token == 0) || r.stateToken == r.command.token);
}
inline void Put(std::vector<std::uint8_t>& b, std::size_t offset, std::uint64_t value, std::size_t size) {
    for (std::size_t i = 0; i < size; ++i) b[offset + i] = static_cast<std::uint8_t>(value >> (i * 8));
}
inline std::uint64_t Get(std::span<const std::uint8_t> b, std::size_t offset, std::size_t size) {
    std::uint64_t value{};
    for (std::size_t i = 0; i < size; ++i) value |= static_cast<std::uint64_t>(b[offset + i]) << (i * 8);
    return value;
}
}
inline std::vector<std::uint8_t> EncodeCorpseCommand(const CorpseCommand& c) {
    if (!corpse_codec::Valid(c)) throw std::invalid_argument("Invalid corpse command");
    std::vector<std::uint8_t> b(48);
    b[0] = 1; b[1] = static_cast<std::uint8_t>(c.kind); b[2] = static_cast<std::uint8_t>(c.action);
    corpse_codec::Put(b, 4, c.requestId, 4); corpse_codec::Put(b, 8, c.actor.Value(), 8);
    corpse_codec::Put(b, 16, c.target.Value(), 8); corpse_codec::Put(b, 24, c.token, 8);
    for (std::size_t i = 0; i < 16; ++i) b[32 + i] = c.sessionId[i];
    return b;
}
inline std::optional<CorpseCommand> DecodeCorpseCommand(std::span<const std::uint8_t> b) {
    if (b.size() != 48 || b[0] != 1 || b[3] != 0) return std::nullopt;
    CorpseCommand c;
    c.kind = static_cast<CorpseCommandKind>(b[1]); c.action = static_cast<CorpseAction>(b[2]);
    c.requestId = static_cast<std::uint32_t>(corpse_codec::Get(b, 4, 4));
    c.actor = NetEntityId(corpse_codec::Get(b, 8, 8)); c.target = NetEntityId(corpse_codec::Get(b, 16, 8));
    c.token = corpse_codec::Get(b, 24, 8);
    for (std::size_t i = 0; i < 16; ++i) c.sessionId[i] = b[32 + i];
    return corpse_codec::Valid(c) ? std::optional(c) : std::nullopt;
}
inline std::vector<std::uint8_t> EncodeCorpseReply(const CorpseReply& r) {
    if (!corpse_codec::Valid(r)) throw std::invalid_argument("Invalid corpse reply");
    auto b = EncodeCorpseCommand(r.command); b.resize(64);
    b[48] = static_cast<std::uint8_t>(r.decision); b[49] = r.hasState ? 1 : 0;
    b[50] = static_cast<std::uint8_t>(r.phase); corpse_codec::Put(b, 56, r.stateToken, 8);
    return b;
}
inline std::optional<CorpseReply> DecodeCorpseReply(std::span<const std::uint8_t> b) {
    if (b.size() != 64 || b[49] > 1) return std::nullopt;
    for (std::size_t i = 51; i < 56; ++i) if (b[i] != 0) return std::nullopt;
    const auto c = DecodeCorpseCommand(b.first(48)); if (!c) return std::nullopt;
    CorpseReply r{*c, static_cast<CorpseDecision>(b[48]), b[49] != 0,
        static_cast<CorpseUsePhase>(b[50]), corpse_codec::Get(b, 56, 8)};
    return corpse_codec::Valid(r) ? std::optional(r) : std::nullopt;
}
}

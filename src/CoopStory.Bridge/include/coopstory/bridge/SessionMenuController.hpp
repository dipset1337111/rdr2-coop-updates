#pragma once

#include "coopstory/bridge/MenuController.hpp"

#include <cstddef>
#include <optional>
#include <span>
#include <string>
#include <string_view>

namespace coopstory::bridge {

// State for the in-game host/join overlay.
// It collects a player's choice, then BridgeRuntime sends that request to Sidecar; this UI does not open LAN sockets.
enum class SessionOverlayAction {
    Host,
    JoinFromClipboard,
    StopSession,
    ToggleHud,
    Close,
};

enum class SessionOverlayPhase {
    WaitingForSidecar,
    ChooseMode,
    StartingHost,
    StartingGuest,
    ReadyHost,
    ReadyGuest,
    Error,
};

struct SessionMenuUpdate final {
    bool visibilityChanged{};
    bool hudVisibilityChanged{};
    std::optional<SessionOverlayAction> action{};
};

struct SessionOverlayView final {
    bool open{};
    bool hudVisible{};
    bool sessionReady{};
    SessionOverlayPhase phase{SessionOverlayPhase::WaitingForSidecar};
    std::size_t selection{};
    std::span<const SessionOverlayAction> actions{};
    std::string_view status{};
};

class SessionMenuController final {
public:
    [[nodiscard]] SessionMenuUpdate Update(const MenuInputState& input);
    void OpenRealtimeMenu() noexcept { open_ = true; selection_ = 4U; }
    void SetSidecarConnected(bool connected);
    void SetStatus(SessionOverlayPhase phase, std::string message);
    void MarkSessionReady(bool host, std::string message);
    void MarkSessionStopped(std::string message);

    [[nodiscard]] SessionOverlayView View() const noexcept;
    [[nodiscard]] bool IsOpen() const noexcept { return open_; }
    [[nodiscard]] bool IsHudVisible() const noexcept { return hudVisible_; }
    [[nodiscard]] bool IsSessionReady() const noexcept {
        return sessionReady_;
    }
    [[nodiscard]] static std::string_view Label(
        SessionOverlayAction action) noexcept;

private:
    [[nodiscard]] static bool Rising(bool current, bool previous) noexcept {
        return current && !previous;
    }

    bool open_{};
    bool hudVisible_{};
    bool sessionReady_{};
    bool sidecarConnected_{};
    std::size_t selection_{};
    SessionOverlayPhase phase_{SessionOverlayPhase::WaitingForSidecar};
    std::string status_{
        "Press F8 for the co-op menu."};
    MenuInputState previous_{};
};

}  // namespace coopstory::bridge

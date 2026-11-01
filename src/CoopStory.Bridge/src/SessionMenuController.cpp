#include "coopstory/bridge/SessionMenuController.hpp"

#include <array>
#include <utility>

namespace coopstory::bridge {
namespace {

// Game-thread menu requests session changes through the authenticated local sidecar.
constexpr std::array kActions{
    SessionOverlayAction::Host,
    SessionOverlayAction::JoinFromClipboard,
    SessionOverlayAction::StopSession,
    SessionOverlayAction::ToggleHud,
    SessionOverlayAction::Close,
};

}  // namespace

SessionMenuUpdate SessionMenuController::Update(
    const MenuInputState& input) {
    SessionMenuUpdate result;

    // Keep key actions edge-triggered so one F8 press changes visibility once.
    if (Rising(input.f8, previous_.f8)) {
        open_ = !open_;
        result.visibilityChanged = true;
    }
    if (Rising(input.f10, previous_.f10)) {
        hudVisible_ = !hudVisible_;
        result.hudVisibilityChanged = true;
    }

    if (open_) {
        if (Rising(input.cancel, previous_.cancel)) {
            open_ = false;
            result.visibilityChanged = true;
        } else {
            if (Rising(input.up, previous_.up)) {
                selection_ =
                    selection_ == 0U
                        ? kActions.size() - 1U
                        : selection_ - 1U;
            }
            if (Rising(input.down, previous_.down)) {
                selection_ = (selection_ + 1U) % kActions.size();
            }
            if (Rising(input.confirm, previous_.confirm)) {
                const auto action = kActions[selection_];
                if (action == SessionOverlayAction::ToggleHud) {
                    hudVisible_ = !hudVisible_;
                    result.hudVisibilityChanged = true;
                } else if (action == SessionOverlayAction::Close) {
                    open_ = false;
                    result.visibilityChanged = true;
                } else {
                    result.action = action;
                }
            }
        }
    }

    previous_ = input;
    return result;
}

void SessionMenuController::SetSidecarConnected(const bool connected) {
    // Connection state drives the helpful overlay phase without directly starting/stopping a session—that remains a sidecar authority decision.
    if (connected == sidecarConnected_) {
        return;
    }
    sidecarConnected_ = connected;
    if (!connected && !sessionReady_) {
        phase_ = SessionOverlayPhase::WaitingForSidecar;
        status_ =
            "Start the game with the co-op launcher, then press F8.";
    } else if (
        connected &&
        phase_ == SessionOverlayPhase::WaitingForSidecar) {
        phase_ = SessionOverlayPhase::ChooseMode;
        status_ =
            "Create a session, or copy your friend's invitation and select Join.";
    }
}

void SessionMenuController::SetStatus(
    const SessionOverlayPhase phase,
    std::string message) {
    phase_ = phase;
    status_ = std::move(message);
    if (phase == SessionOverlayPhase::Error) {
        open_ = true;
    }
}

void SessionMenuController::MarkSessionReady(
    const bool host,
    std::string message) {
    // A matching role acknowledgement unlocks the normal in-game presentation.
    sessionReady_ = true;
    phase_ = host
                 ? SessionOverlayPhase::ReadyHost
                 : SessionOverlayPhase::ReadyGuest;
    status_ = std::move(message);
    open_ = false;
}

void SessionMenuController::MarkSessionStopped(
    std::string message) {
    sessionReady_ = false;
    phase_ = SessionOverlayPhase::ChooseMode;
    status_ = std::move(message);
    selection_ = 0U;
    open_ = false;
}

SessionOverlayView SessionMenuController::View() const noexcept {
    return {
        open_,
        hudVisible_,
        sessionReady_,
        phase_,
        selection_,
        kActions,
        status_};
}

std::string_view SessionMenuController::Label(
    const SessionOverlayAction action) noexcept {
    switch (action) {
        case SessionOverlayAction::Host:
            return "Create session / copy invitation";
        case SessionOverlayAction::JoinFromClipboard:
            return "Join with copied invitation";
        case SessionOverlayAction::StopSession:
            return "STOP THE CURRENT CO-OP SESSION";
        case SessionOverlayAction::ToggleHud:
            return "Show/hide diagnostics (F10)";
        case SessionOverlayAction::Close:
            return "Close menu (F8 reopens it)";
    }
    return "Unknown action";
}

}  // namespace coopstory::bridge

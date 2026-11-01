# Recovered co-op source backup

Complete source snapshot in CoopStory-source-0fd887e.zip, corresponding to local commit 0fd887ebcc5662db398864e10576c6ba795257df. Extract to build; upstream license and attribution are included. No game saves, runtime settings, credentials, SDK or diagnostic logs are included.

Includes Sync24 diagnostics, opt-in F8 connection menu, hidden-by-default diagnostic HUD (F10), and clearer Live Clone guidance. Live Clone requires SOLO TEST, then F9 > Live mirror; multiplayer HOST/JOIN does not create a synthetic peer.

Tests: 105 native, 33 launcher, 72 sidecar passed. Full campaign synchronization and the join fade/placement flow are not implemented. The host owns campaign progress; guest single-player saves remain separate.

This is a source backup, not a new launcher release. Published Sync24 is unchanged.

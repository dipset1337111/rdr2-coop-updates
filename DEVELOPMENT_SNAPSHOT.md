# Development snapshot

This branch preserves the recovered source and current local work. It does not replace the published Sync24 release.

Included: local per-session diagnostic capture; AnimGraph diagnostic target fix; opt-in F8 host/join menu; diagnostic HUD hidden by default (F10); clearer Live Clone errors when the local connection is absent or the launcher is in multiplayer mode.

Live Clone: close RDR2, stop the previous session, select SOLO TEST in the launcher, load Story Mode, then select F9 > Live mirror. HOST/JOIN runs multiplayer and does not create a synthetic peer.

Campaign decision: the host owns campaign progression; guest single-player save files must remain separate. The new join fade/placement flow and complete mission synchronization are not implemented. No personal saves, runtime credentials or diagnostic archives are included.

Validation: native self-tests 105/105; launcher tests 33/33 on the in-game menu change. In-game two-PC validation remains pending. Upstream attribution and license files are retained.

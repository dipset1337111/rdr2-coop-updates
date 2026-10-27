# RDR2 Coop Story — experimental updates

Test builds of a modified version of [JuliPodie/rdr2-coop-story](https://github.com/JuliPodie/rdr2-coop-story), distributed under its MIT license. Unofficial, non-commercial project, not affiliated with Rockstar Games or Take-Two.

Full campaign synchronization is **not implemented or verified**. Player movement, combat, NPCs and mission synchronization are under development. These builds are for testing only.

Release ZIPs contain the launcher, sidecar and project bridge. Script Hook RDR2, its loader, SDK, game files, saves and personal settings are not included. Obtain Script Hook separately from its author.

The updater reads `update.json` from the latest release, checks the ZIP length and SHA-256, and stages a new version without overwriting the running launcher. The game and coop session must be stopped before switching versions. Local settings remain in LocalAppData. Keep both players on the same release.

No release is available until a tested ZIP and its matching `update.json` have been uploaded.

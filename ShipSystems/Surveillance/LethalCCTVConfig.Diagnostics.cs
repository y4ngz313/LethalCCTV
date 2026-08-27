using System;
using BepInEx.Configuration;
using CSync.Extensions;
using CSync.Lib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    public partial class LethalCCTVConfig
    {
        private void BindDiagnostics(ConfigFile cfg)
        {
            // === Phase 0 Diagnostics ===
            ReconLoggingEnabled = cfg.Bind(
                DiagnosticsSection,
                "Recon Logging Enabled",
                false,
                "Enables the once-per-session BepInEx log dump of MonitorWall materials, mapScreen prefab values, ship camera prefabs, diageticMixer groups, and OpenBodyCams routing state. Also gates per-camera, per-tile, and per-monitor debug logs added by later phases. Local entry; not synced.");

            PlacementDebugLoggingEnabled = cfg.Bind(
                DiagnosticsSection,
                "Placement Debug Logging Enabled",
                false,
                "Enables verbose CCTV placement/classification reports during dungeon generation. Dev-only; leave false for normal play. Purely a logging switch — it never changes what gets spawned.");
        }

        private void BindLegibilityProbe(ConfigFile cfg)
        {
            // === Phase 2.0 — Legibility Probe ===
            // Default FALSE so a normal player session is unaffected — the
            // probe writes 8N CAND lines per tile and runs a Physics
            // linecast per candidate, both of which are dev-only costs.
            // Toggle to true in BepInEx config to capture probe data for
            // Gate 1 review. It fires post-selection (after
            // SortPicksForInstantiation) and is independent of
            // PlacementDebugLoggingEnabled. The Gate A / PlacementDiagnosticOnly
            // placement STOP flags it used to be deliberately independent of
            // were deleted for 1.0 (#575), so the probe now always runs on a
            // roll that also spawns cameras.
            P20ProbeEnabled = cfg.Bind(
                DiagnosticsSection,
                "Enable Placement Probe",
                false,
                "Diagnostic. When true, after the cluster→select→cap pipeline picks tiles, a per-tile sightline probe runs: for each picked tile it emits 4 corners × 2 height tiers × N doorways = 8N CAND lines per tile, plus a fail-loud Room-layer assertion at the start and a unified-rule entrance-pick preview at the end. Linecasts run on the 'Room' layer. Pure read — does not alter placement. Per-client, not synced. Default false (dev-only).");
        }
    }
}

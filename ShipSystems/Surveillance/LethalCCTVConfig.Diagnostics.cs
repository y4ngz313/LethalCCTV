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
                "When on, writes a one-time report to the log at the start of a session about what the mod found (the ship monitor, the map screen, the ship cameras, the sound setup and whether OpenBodyCams is sending a feed) and adds extra logging for cameras, rooms and monitors. Off by default; leave it off for normal play. Your own setting.");

            PlacementDebugLoggingEnabled = cfg.Bind(
                DiagnosticsSection,
                "Placement Debug Logging Enabled",
                false,
                "When on, writes detailed reports to the log about how rooms were sorted and where cameras went whenever a building is generated. Off by default; it only adds logging and never changes where cameras are placed. Your own setting.");

            PerformanceTimingLogging = cfg.Bind(
                DiagnosticsSection,
                "Performance Timing Logging",
                false,
                "When on, the mod reports even small delays in its own per-frame work as log warnings, which helps when looking into stutter. Off by default: then only work that takes longer than a whole frame is noted quietly, though extremely slow work is always reported. Your own setting.");
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
                "Developer tool: when on, once cameras have picked their rooms, every chosen room is tested for lines of sight and each possible mounting spot is written to the log, which makes the log long. Off by default; it never changes where cameras are placed. Your own setting.");
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using DunGen;
using DunGen.Graph;
using Y4NGZCompany.Facility.Interior.Placement;
using Y4NGZCompany.Facility.Interior.Placement.Authored;
using Unity.Netcode;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Mainframe;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Facility.Security;
using Y4NGZCompany.Facility.Stash;
namespace Y4NGZCompany.Facility.Interior
{
    internal static class InteriorSupportSpawner
    {
        private static readonly int VaultsPerRoom = 0;
        private const float MountSearchDistance = 18f;
        private const float WallInset = 0.12f;
        private const float FixtureBodyEmission = 0f;
        private const float FixtureAccentEmission = 1.15f;
        private const float FixtureOutlineEmission = 1.65f;
        private const string MainframeAssetName = "CCTVMainframeSupport";
        private const string MainframeScreenControllerTypeName = "Y4NGZCompany.ShipSystems.Surveillance.MainframeScreenController";
        private const string MainframeInteractionControllerTypeName = "Y4NGZCompany.ShipSystems.Surveillance.MainframeInteractionController";
        private const string CompanyStashAssetName = "CompanyStash";
        private const double SpawnFrameBudgetMilliseconds = 3.0;

        private static bool _spawnAttemptedThisRound;
        private static bool _loggedAutoSpawnDisabledThisRound;
        private static bool _reportedDisabledAuthoredSupportThisRound;
        private static bool _reportedBudgetedStartupThisRound;
        private static readonly List<GameObject> RuntimeInstances = new List<GameObject>();
        private static GameObject _mainframePrefab;
        private static GameObject _vaultPrefab;
        private static Coroutine _spawnCoroutine;
        private static InteriorSupportSpawnRunner _spawnRunner;

        internal static void ResetRunState()
        {
            StopSpawnCoroutine("run reset");
            _spawnAttemptedThisRound = false;
            _loggedAutoSpawnDisabledThisRound = false;
            _reportedDisabledAuthoredSupportThisRound = false;
            _reportedBudgetedStartupThisRound = false;
            MainframeSpawnDiagnostics.ResetRunState();
            CleanupRuntimeInstances("run reset");
            InteriorPlacementService.ResetRunState();
        }

        internal static void WarmUpRuntimePrefabs()
        {
            try
            {
                EnsureRuntimePrefabsRegistered();
                EnsureVaultPrefabRegistered();
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogDebug($"[MoonContracts] Interior support prefab warm-up skipped: {ex.Message}");
            }
        }

        internal static void UpdateRuntime()
        {
            MainframeSpawnDiagnostics.UpdateRuntime();
            if (_spawnAttemptedThisRound) return;
            if (_spawnCoroutine != null) return;
            if (!CctvModuleConfig.InteriorSupportAutoSpawnEnabled.Value)
            {
                if (!_loggedAutoSpawnDisabledThisRound)
                {
                    _loggedAutoSpawnDisabledThisRound = true;
                    CctvModuleConfig.Log?.LogWarning("[MoonContracts] Interior support auto-spawn skipped because CCTV Support/AutoSpawnFixtures is false; Mainframe and Company Stash codes may be generated, but the fixture GameObjects will not spawn.");
                }
                RoundManager disabledRound = RoundManager.Instance;
                if (!_reportedDisabledAuthoredSupportThisRound &&
                    disabledRound != null &&
                    disabledRound.IsServer &&
                    IsCurrentDungeonReady())
                {
                    _reportedDisabledAuthoredSupportThisRound = true;
                    ReportDisabledAuthoredSupportRecords();
                    AuthoredPlacementRoundReport.NotifyHostFixturePassComplete();
                }
                return;
            }
            var start = StartOfRound.Instance;
            if (start == null) return;
            RoundManager round = RoundManager.Instance;
            if (!IsSupportSpawnReady(start, round)) return;

            if (StartSpawnForRoundCoroutine())
                return;

            if (!TrySpawnForRoundFallback(out int roomsUsed, out int fixturesSpawned))
            {
                AuthoredPlacementRoundReport.NotifyHostFixturePassComplete();
                return;
            }
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Interior support auto-spawn placed fixtures in {roomsUsed} room(s); {fixturesSpawned} fixture(s) total.");
            AuthoredPlacementRoundReport.NotifyHostFixturePassComplete();
        }

        private static bool IsSupportSpawnReady(StartOfRound start, RoundManager round)
        {
            if (start == null || round == null) return false;
            if (start.inShipPhase || start.shipIsLeaving) return false;
            if (!round.IsServer) return false;
            if (!round.bakedNavMesh) return false;
            if (!round.dungeonFinishedGeneratingForAllPlayers) return false;
            return IsCurrentDungeonReady();
        }

        private static bool IsCurrentDungeonReady()
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            return dungeon?.AllTiles != null && dungeon.AllTiles.Count > 0;
        }

        private static void ReportDisabledAuthoredSupportRecords()
        {
            MarkResolvedKindUnused(
                AuthoredInteriorPlacementKinds.Mainframe,
                "consumer=mainframe reason=fixture-auto-spawn-disabled");
            MarkResolvedKindUnused(
                AuthoredInteriorPlacementKinds.Vault,
                "consumer=company-stash reason=fixture-auto-spawn-disabled");
        }

        private static void MarkResolvedKindUnused(string objectKind, string reason)
        {
            if (!AuthoredInteriorPlacementStore.TryResolvePoses(objectKind, out List<AuthoredInteriorPlacementPose> poses) ||
                poses == null)
            {
                return;
            }

            for (int i = 0; i < poses.Count; i++)
                AuthoredPlacementRoundReport.RecordResolvedButUnused(poses[i].Record, reason);
        }

        private static bool StartSpawnForRoundCoroutine()
        {
            InteriorSupportSpawnRunner runner = EnsureSpawnRunner();
            if (runner == null)
                return false;

            _spawnAttemptedThisRound = true;
            if (!_reportedBudgetedStartupThisRound)
            {
                _reportedBudgetedStartupThisRound = true;
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.Perf] Interior support spawn is deferring its synchronous coroutine startup " +
                    $"out of UpdateRuntime and enforcing a {SpawnFrameBudgetMilliseconds:0.00}ms frame budget between startup steps.");
            }
            _spawnCoroutine = runner.StartCoroutine(SpawnForRoundCoroutine());
            return true;
        }

        private static bool TrySpawnForRoundFallback(out int roomsUsed, out int fixturesSpawned)
        {
            var perf = new SpawnBudgetContext();
            _spawnAttemptedThisRound = true;
            if (!TrySpawnForRound(out roomsUsed, out fixturesSpawned))
            {
                perf.Total.Stop();
                LogSupportSpawnPerf(perf, new SpawnRoundResult
                {
                    RoomsUsed = roomsUsed,
                    FixturesSpawned = fixturesSpawned,
                    Succeeded = false
                });
                return false;
            }

            perf.Total.Stop();
            LogSupportSpawnPerf(perf, new SpawnRoundResult
            {
                RoomsUsed = roomsUsed,
                FixturesSpawned = fixturesSpawned,
                Succeeded = true
            });
            return true;
        }

        private static IEnumerator SpawnForRoundCoroutine()
        {
            var result = new SpawnRoundResult();
            var perf = new SpawnBudgetContext();
            IEnumerator routine = SpawnForRoundRoutine(result, perf);
            while (true)
            {
                bool moved;
                try
                {
                    moved = routine.MoveNext();
                }
                catch (Exception ex)
                {
                    result.Succeeded = false;
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Interior support auto-spawn coroutine failed: {ex.Message}");
                    break;
                }

                if (!moved)
                    break;

                yield return routine.Current;
            }

            perf.Total.Stop();
            _spawnCoroutine = null;
            LogSupportSpawnPerf(perf, result);
            if (result.Succeeded)
                CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Interior support auto-spawn placed fixtures in {result.RoomsUsed} room(s); {result.FixturesSpawned} fixture(s) total.");
            AuthoredPlacementRoundReport.NotifyHostFixturePassComplete();
        }

        private static bool ShouldYieldFrame(SpawnBudgetContext perf)
        {
            if (perf == null)
                return false;

            if (perf.Frame.Elapsed.TotalMilliseconds < SpawnFrameBudgetMilliseconds)
                return false;

            perf.Frames++;
            perf.Frame.Restart();
            return true;
        }

        private static object YieldNextFrame(SpawnBudgetContext perf)
        {
            if (perf != null)
            {
                perf.Frames++;
                perf.Frame.Restart();
            }

            return null;
        }

        private static void LogSupportSpawnPerf(SpawnBudgetContext perf, SpawnRoundResult result)
        {
            if (perf == null || result == null)
                return;

            int navmeshPaths = Math.Max(0, InteriorAnchorService.NavMeshPathCalculationCount - perf.NavMeshPathStart);
            int physicsCasts = Math.Max(0, InteriorAnchorService.PhysicsQueryCount - perf.PhysicsQueryStart);
            CctvModuleConfig.Log?.LogWarning(
                $"[MoonContracts.Perf] support-spawn total={Math.Max(0, perf.Total.ElapsedMilliseconds):0.00}ms " +
                $"frames={Math.Max(1, perf.Frames)} startupCpu={Math.Max(0, perf.StartupCpuMilliseconds):0.00}ms " +
                $"slowestStartup={perf.SlowestStartupStep}:{Math.Max(0, perf.SlowestStartupStepMilliseconds):0.00}ms " +
                $"navmeshPaths={navmeshPaths} physicsCasts~={physicsCasts} fixtures={result.FixturesSpawned} cameras={result.CamerasTouched}");
        }

        private static void RecordStartupStep(SpawnBudgetContext perf, string stepName, long startTicks)
        {
            if (perf == null)
                return;

            double elapsed = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
            perf.StartupCpuMilliseconds += elapsed;
            if (elapsed <= perf.SlowestStartupStepMilliseconds)
                return;

            perf.SlowestStartupStepMilliseconds = elapsed;
            perf.SlowestStartupStep = stepName;
        }

        private static InteriorSupportSpawnRunner EnsureSpawnRunner()
        {
            if (_spawnRunner != null)
                return _spawnRunner;

            GameObject host = RoundManager.Instance != null ? RoundManager.Instance.gameObject : null;
            if (host == null)
                return null;

            _spawnRunner = host.GetComponent<InteriorSupportSpawnRunner>();
            if (_spawnRunner == null)
                _spawnRunner = host.AddComponent<InteriorSupportSpawnRunner>();
            return _spawnRunner;
        }

        private static void StopSpawnCoroutine(string reason)
        {
            if (_spawnCoroutine == null)
                return;

            try
            {
                if (_spawnRunner != null)
                    _spawnRunner.StopCoroutine(_spawnCoroutine);
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogDebug($"[MoonContracts] Failed stopping interior support spawn coroutine during {reason}: {ex.Message}");
            }

            _spawnCoroutine = null;
        }

        private sealed class SpawnBudgetContext
        {
            internal readonly Stopwatch Total = Stopwatch.StartNew();
            internal readonly Stopwatch Frame = Stopwatch.StartNew();
            internal readonly int NavMeshPathStart = InteriorAnchorService.NavMeshPathCalculationCount;
            internal readonly int PhysicsQueryStart = InteriorAnchorService.PhysicsQueryCount;
            internal int Frames = 1;
            internal double StartupCpuMilliseconds;
            internal double SlowestStartupStepMilliseconds;
            internal string SlowestStartupStep = "none";
        }

        private sealed class SpawnRoundResult
        {
            internal int RoomsUsed;
            internal int FixturesSpawned;
            internal int CamerasTouched;
            internal bool Succeeded;
        }

        internal static bool TrySpawnForRound(out int roomsUsed, out int fixturesSpawned)
        {
            roomsUsed = 0;
            fixturesSpawned = 0;
            CleanupRuntimeInstances("pre-spawn");

            CctvSupportState.EnsureInitialized();
            EnsureRuntimePrefabsRegistered();

            var supportPoses = new List<PlacementPose>(4);
            var usedTiles = new HashSet<Tile>();
            var occupiedStashTiles = new HashSet<Tile>();
            Tile mansionMainframeTile = FindCurrentDungeonTileByName("garagetileclone");
            if (mansionMainframeTile != null)
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts] Mansion authored support tile: mainframe='{FormatTileName(mansionMainframeTile)}'.");
            }

            Tile spawnedMainframeTile = null;
            bool hasExistingMainframe = TryFindExistingMainframe(out MainframeSupport existingMainframe);
            if (hasExistingMainframe)
            {
                RemoveExtraMainframes(existingMainframe);
                spawnedMainframeTile = FindCurrentDungeonTileContainingPosition(existingMainframe.transform.position);
                TryRegisterExistingMainframeSupportPose(existingMainframe, spawnedMainframeTile, supportPoses, usedTiles);
                CctvModuleConfig.Log?.LogInfo("[MoonContracts.Mainframe] Mainframe spawn skipped because one already exists.");
                MainframeSpawnDiagnostics.ScheduleLiveInstanceDump(existingMainframe.gameObject, "mainframe-already-exists");
            }

            bool configuredStashCodes = false;
            int desiredStashCount = 0;
            int spawnedAuthoredVaults = 0;
            bool hadAuthoredVaultRecords = false;
            if (AuthoredInteriorPlacementStore.TryResolvePoses(AuthoredInteriorPlacementKinds.Vault, out List<AuthoredInteriorPlacementPose> authoredVaults)
                && TrySelectCompanyStashes(authoredVaults, out List<AuthoredInteriorPlacementPose> selectedVaults, out int targetVaultCount, out string riskLabel))
            {
                hadAuthoredVaultRecords = true;
                desiredStashCount = targetVaultCount;
                EnsureVaultPrefabRegistered();
                CctvSupportState.ConfigureStashCodesForRound(desiredStashCount);
                configuredStashCodes = true;
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts] Company Stash selection risk='{riskLabel}' target={targetVaultCount} compatible={authoredVaults.Count} selected={selectedVaults.Count} desired={desiredStashCount}.");

                var authoredVaultRequest = new InteriorPlacementRequest
                {
                    Role = InteriorPlacementRole.VaultWallOrFloorSafe,
                    Footprint = FixtureFootprint.Vault(GetPrefabBackOffset(_vaultPrefab, 0.36f)),
                    MinEntranceDistance = 28f,
                    AllowEntranceRoom = false,
                    PreferImportantRoom = true,
                    RequireImportantRoomWhenReviewed = false,
                    DisallowHallway = true,
                    RequireWallContact = true,
                    RequireFloorContact = true,
                    RequireBodyClearance = true,
                    RequireReachableInteractionPoint = true,
                    DebugLabel = "AuthoredVault"
                };

                int authoredIndex = 0;
                for (; authoredIndex < selectedVaults.Count && spawnedAuthoredVaults < desiredStashCount; authoredIndex++)
                {
                    AuthoredInteriorPlacementPose vaultPose = selectedVaults[authoredIndex];
                    if (vaultPose.Tile == null)
                    {
                        AuthoredPlacementRoundReport.RecordResolvedButUnused(vaultPose.Record, "consumer=company-stash reason=resolved-tile-null");
                        continue;
                    }

                    if (occupiedStashTiles.Contains(vaultPose.Tile))
                    {
                        AuthoredPlacementRoundReport.RecordResolvedButUnused(vaultPose.Record, "consumer=company-stash reason=duplicate-tile");
                        CctvModuleConfig.Log?.LogWarning(
                            $"[MoonContracts.CompanyStash] Skipped authored Company Stash id='{vaultPose.Record?.objectId ?? "<unknown>"}' tile='{vaultPose.Tile.name}' because another stash already uses that tile.");
                        continue;
                    }

                    string source = string.IsNullOrWhiteSpace(vaultPose.Source) ? "authored-company-stash" : vaultPose.Source;
                    if (!ValidateAuthoredPlacementPose(
                            authoredVaultRequest,
                            vaultPose.Tile,
                            vaultPose.WorldPosition,
                            vaultPose.WorldRotation,
                            "Company Stash",
                            source,
                            20000f,
                            out PlacementPose supportPose,
                            vaultPose.Record))
                    {
                        continue;
                    }

                    if (SpawnVault(supportPose.Position, supportPose.Rotation, spawnedAuthoredVaults, vaultPose.Tile.name))
                    {
                        AuthoredPlacementRoundReport.RecordSpawned(vaultPose.Record);
                        fixturesSpawned++;
                        spawnedAuthoredVaults++;
                        supportPoses.Add(supportPose);
                        usedTiles.Add(vaultPose.Tile);
                        occupiedStashTiles.Add(vaultPose.Tile);
                    }
                    else
                    {
                        AuthoredPlacementRoundReport.RecordResolvedButUnused(vaultPose.Record, "consumer=company-stash reason=spawn-failed");
                    }
                }

                for (; authoredIndex < selectedVaults.Count; authoredIndex++)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        selectedVaults[authoredIndex].Record,
                        "consumer=company-stash reason=round-cap");
                }
            }

            // Mainframe authored placement takes precedence only when its tile naturally
            // exists in this generated interior. No authored mainframe tile is forced.
            bool spawnedAuthoredMainframe = false;
            if (!hasExistingMainframe)
            {
                spawnedAuthoredMainframe = TrySpawnAuthoredMainframe(supportPoses, usedTiles, ref fixturesSpawned, out spawnedMainframeTile);
            }

            if (!hasExistingMainframe && !spawnedAuthoredMainframe && HasAuthoredMainframeRecordsForCurrentFlow())
            {
                CctvModuleConfig.Log?.LogWarning("[MoonContracts.Mainframe] Authored mainframe records exist for this flow, but none resolved on generated tiles; continuing to reviewed/automatic fallback.");
            }

            InteriorPlacementPlan mainframePlan = null;
            float mainframeBackOffset = GetPrefabBackOffset(_mainframePrefab, 0.34f);
            var mainframeRequest = new InteriorPlacementRequest
            {
                Role = InteriorPlacementRole.MainframeStandingBackToWall,
                Footprint = FixtureFootprint.Mainframe(mainframeBackOffset),
                PreferredTile = mansionMainframeTile,
                ForcePreferredTile = mansionMainframeTile != null,
                MinEntranceDistance = 36f,
                AllowEntranceRoom = false,
                PreferImportantRoom = true,
                RequireImportantRoomWhenReviewed = true,
                DisallowHallway = true,
                RequireWallContact = true,
                RequireFloorContact = true,
                RequireBodyClearance = true,
                RequireReachableInteractionPoint = true,
                DebugLabel = "Mainframe"
            };

            if (!hasExistingMainframe && !spawnedAuthoredMainframe && InteriorPlacementService.TryBuildPlan(mainframeRequest, out mainframePlan))
            {
                PlacementPose mainframePose = mainframePlan.Pose;
                mainframePose = TagMainframeSupportPose(mainframePose, "automatic-mainframe-");

                if (TryRegisterMainframePlacement(mainframePose, true, supportPoses, usedTiles))
                {
                    fixturesSpawned++;
                    spawnedMainframeTile = mainframePose.Tile;
                }
            }

            if (!configuredStashCodes)
            {
                desiredStashCount = ResolveAutomaticCompanyStashTargetCount();
                EnsureVaultPrefabRegistered();
                CctvSupportState.ConfigureStashCodesForRound(desiredStashCount);
                configuredStashCodes = true;
            }

            var automaticStashExcludedTiles = new HashSet<Tile>();
            if (spawnedMainframeTile != null)
                automaticStashExcludedTiles.Add(spawnedMainframeTile);
            foreach (Tile tile in occupiedStashTiles)
            {
                if (tile != null)
                    automaticStashExcludedTiles.Add(tile);
            }

            int automaticStashTargetCount = Mathf.Max(0, desiredStashCount - spawnedAuthoredVaults);
            if (hadAuthoredVaultRecords && spawnedAuthoredVaults < desiredStashCount)
            {
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CompanyStash] Authored stashes spawned {spawnedAuthoredVaults}/{desiredStashCount}; filling {automaticStashTargetCount} missing stash(es) with automatic fallback.");
            }

            int spawnedTotalVaults = spawnedAuthoredVaults;
            for (int automaticIndex = 0; automaticIndex < automaticStashTargetCount; automaticIndex++)
            {
                int vaultIndex = spawnedTotalVaults;
                var vaultRequest = new InteriorPlacementRequest
                {
                    Role = InteriorPlacementRole.VaultWallOrFloorSafe,
                    Footprint = FixtureFootprint.Vault(GetPrefabBackOffset(_vaultPrefab, 0.36f)),
                    PreferredTile = null,
                    MustDifferFromTile = spawnedMainframeTile,
                    ExcludedTiles = automaticStashExcludedTiles,
                    MinEntranceDistance = 28f,
                    AllowEntranceRoom = false,
                    PreferImportantRoom = true,
                    RequireImportantRoomWhenReviewed = true,
                    DisallowHallway = true,
                    RequireWallContact = true,
                    RequireFloorContact = true,
                    RequireBodyClearance = true,
                    RequireReachableInteractionPoint = true,
                    VariantIndex = vaultIndex + 1,
                    DebugLabel = $"Vault{vaultIndex + 1}"
                };

                if (InteriorPlacementService.TryBuildPlan(vaultRequest, out InteriorPlacementPlan vaultPlan))
                {
                    if (SpawnVault(vaultPlan.Pose.Position, vaultPlan.Pose.Rotation, vaultIndex, vaultPlan.Pose.TileName))
                    {
                        fixturesSpawned++;
                        spawnedTotalVaults++;
                        supportPoses.Add(vaultPlan.Pose);
                        if (vaultPlan.Pose.Tile != null)
                        {
                            usedTiles.Add(vaultPlan.Pose.Tile);
                            automaticStashExcludedTiles.Add(vaultPlan.Pose.Tile);
                        }
                    }
                    else
                    {
                        CctvModuleConfig.Log?.LogWarning(
                            $"[MoonContracts.CompanyStash] Automatic fallback plan produced tile='{vaultPlan.Pose.TileName}' but spawn failed for stashIndex={vaultIndex}.");
                    }
                }
                else
                {
                    CctvModuleConfig.Log?.LogWarning(
                        $"[MoonContracts.CompanyStash] Automatic fallback failed to build plan for stashIndex={vaultIndex}.");
                }
            }

            // AlarmBoxSupport is no longer spawned as a separate fixture â€” alarm state and
            // behavior are consolidated into MainframeSupport (see CctvAlarmSystem).

            if (configuredStashCodes && spawnedTotalVaults != desiredStashCount)
            {
                CctvSupportState.FinalizeStashCodesForSpawnedCount(spawnedTotalVaults);
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CompanyStash] Adjusted configured stash codes to spawned count {spawnedTotalVaults}/{desiredStashCount}.");
            }

            roomsUsed = usedTiles.Count;
            bool automaticMainframeCovered = EnsureCamerasCoverSupportPoses(supportPoses);
            if (!automaticMainframeCovered)
                CctvModuleConfig.Log?.LogWarning("[MoonContracts.Mainframe] Mainframe spawned but no CCTV support camera could confirm coverage.");
            return roomsUsed > 0;
        }

        private static IEnumerator SpawnForRoundRoutine(SpawnRoundResult result, SpawnBudgetContext perf)
        {
            int roomsUsed = 0;
            int fixturesSpawned = 0;

            // Unity advances a coroutine synchronously through its first yield. The old first yield
            // sat after all startup discovery, so UpdateRuntime inherited the entire one-shot burst.
            yield return YieldNextFrame(perf);

            long startupStep = Stopwatch.GetTimestamp();
            CleanupRuntimeInstances("pre-spawn");
            RecordStartupStep(perf, "CleanupRuntimeInstances", startupStep);
            if (ShouldYieldFrame(perf))
                yield return null;

            startupStep = Stopwatch.GetTimestamp();
            CctvSupportState.EnsureInitialized();
            EnsureRuntimePrefabsRegistered();
            RecordStartupStep(perf, "EnsureRuntimeStateAndPrefabs", startupStep);
            if (ShouldYieldFrame(perf))
                yield return null;

            var supportPoses = new List<PlacementPose>(4);
            var usedTiles = new HashSet<Tile>();
            var occupiedStashTiles = new HashSet<Tile>();
            startupStep = Stopwatch.GetTimestamp();
            Tile mansionMainframeTile = FindCurrentDungeonTileByName("garagetileclone");
            RecordStartupStep(perf, "FindMansionMainframeTile", startupStep);
            if (mansionMainframeTile != null)
            {
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts] Mansion authored support tile: mainframe='{FormatTileName(mansionMainframeTile)}'.");
            }
            if (ShouldYieldFrame(perf))
                yield return null;

            Tile spawnedMainframeTile = null;
            startupStep = Stopwatch.GetTimestamp();
            bool hasExistingMainframe = TryFindExistingMainframe(out MainframeSupport existingMainframe);
            if (hasExistingMainframe)
            {
                RemoveExtraMainframes(existingMainframe);
                spawnedMainframeTile = FindCurrentDungeonTileContainingPosition(existingMainframe.transform.position);
                TryRegisterExistingMainframeSupportPose(existingMainframe, spawnedMainframeTile, supportPoses, usedTiles);
                CctvModuleConfig.Log?.LogInfo("[MoonContracts.Mainframe] Mainframe spawn skipped because one already exists.");
                MainframeSpawnDiagnostics.ScheduleLiveInstanceDump(existingMainframe.gameObject, "mainframe-already-exists");
            }
            RecordStartupStep(perf, "DiscoverExistingMainframe", startupStep);

            if (ShouldYieldFrame(perf))
                yield return null;

            bool configuredStashCodes = false;
            int desiredStashCount = 0;
            int spawnedAuthoredVaults = 0;
            bool hadAuthoredVaultRecords = false;
            if (AuthoredInteriorPlacementStore.TryResolvePoses(AuthoredInteriorPlacementKinds.Vault, out List<AuthoredInteriorPlacementPose> authoredVaults)
                && TrySelectCompanyStashes(authoredVaults, out List<AuthoredInteriorPlacementPose> selectedVaults, out int targetVaultCount, out string riskLabel))
            {
                hadAuthoredVaultRecords = true;
                desiredStashCount = targetVaultCount;
                EnsureVaultPrefabRegistered();
                CctvSupportState.ConfigureStashCodesForRound(desiredStashCount);
                configuredStashCodes = true;
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts] Company Stash selection risk='{riskLabel}' target={targetVaultCount} compatible={authoredVaults.Count} selected={selectedVaults.Count} desired={desiredStashCount}.");

                var authoredVaultRequest = new InteriorPlacementRequest
                {
                    Role = InteriorPlacementRole.VaultWallOrFloorSafe,
                    Footprint = FixtureFootprint.Vault(GetPrefabBackOffset(_vaultPrefab, 0.36f)),
                    MinEntranceDistance = 28f,
                    AllowEntranceRoom = false,
                    PreferImportantRoom = true,
                    RequireImportantRoomWhenReviewed = false,
                    DisallowHallway = true,
                    RequireWallContact = true,
                    RequireFloorContact = true,
                    RequireBodyClearance = true,
                    RequireReachableInteractionPoint = true,
                    DebugLabel = "AuthoredVault"
                };

                int authoredIndex = 0;
                for (; authoredIndex < selectedVaults.Count && spawnedAuthoredVaults < desiredStashCount; authoredIndex++)
                {
                    AuthoredInteriorPlacementPose vaultPose = selectedVaults[authoredIndex];
                    if (vaultPose.Tile == null)
                    {
                        AuthoredPlacementRoundReport.RecordResolvedButUnused(vaultPose.Record, "consumer=company-stash reason=resolved-tile-null");
                        continue;
                    }

                    if (occupiedStashTiles.Contains(vaultPose.Tile))
                    {
                        AuthoredPlacementRoundReport.RecordResolvedButUnused(vaultPose.Record, "consumer=company-stash reason=duplicate-tile");
                        CctvModuleConfig.Log?.LogWarning(
                            $"[MoonContracts.CompanyStash] Skipped authored Company Stash id='{vaultPose.Record?.objectId ?? "<unknown>"}' tile='{vaultPose.Tile.name}' because another stash already uses that tile.");
                        continue;
                    }

                    string source = string.IsNullOrWhiteSpace(vaultPose.Source) ? "authored-company-stash" : vaultPose.Source;
                    if (!ValidateAuthoredPlacementPose(
                            authoredVaultRequest,
                            vaultPose.Tile,
                            vaultPose.WorldPosition,
                            vaultPose.WorldRotation,
                            "Company Stash",
                            source,
                            20000f,
                            out PlacementPose supportPose,
                            vaultPose.Record))
                    {
                        if (ShouldYieldFrame(perf))
                            yield return null;
                        continue;
                    }

                    if (SpawnVault(supportPose.Position, supportPose.Rotation, spawnedAuthoredVaults, vaultPose.Tile.name))
                    {
                        AuthoredPlacementRoundReport.RecordSpawned(vaultPose.Record);
                        fixturesSpawned++;
                        spawnedAuthoredVaults++;
                        supportPoses.Add(supportPose);
                        usedTiles.Add(vaultPose.Tile);
                        occupiedStashTiles.Add(vaultPose.Tile);
                        yield return YieldNextFrame(perf);
                    }
                    else
                    {
                        AuthoredPlacementRoundReport.RecordResolvedButUnused(vaultPose.Record, "consumer=company-stash reason=spawn-failed");
                        if (ShouldYieldFrame(perf))
                            yield return null;
                    }
                }

                for (; authoredIndex < selectedVaults.Count; authoredIndex++)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        selectedVaults[authoredIndex].Record,
                        "consumer=company-stash reason=round-cap");
                }
            }

            bool spawnedAuthoredMainframe = false;
            if (!hasExistingMainframe)
            {
                spawnedAuthoredMainframe = TrySpawnAuthoredMainframe(supportPoses, usedTiles, ref fixturesSpawned, out spawnedMainframeTile);
                if (spawnedAuthoredMainframe)
                    yield return YieldNextFrame(perf);
            }

            if (!hasExistingMainframe && !spawnedAuthoredMainframe && HasAuthoredMainframeRecordsForCurrentFlow())
            {
                CctvModuleConfig.Log?.LogWarning("[MoonContracts.Mainframe] Authored mainframe records exist for this flow, but none resolved on generated tiles; continuing to reviewed/automatic fallback.");
            }

            float mainframeBackOffset = GetPrefabBackOffset(_mainframePrefab, 0.34f);
            var mainframeRequest = new InteriorPlacementRequest
            {
                Role = InteriorPlacementRole.MainframeStandingBackToWall,
                Footprint = FixtureFootprint.Mainframe(mainframeBackOffset),
                PreferredTile = mansionMainframeTile,
                ForcePreferredTile = mansionMainframeTile != null,
                MinEntranceDistance = 36f,
                AllowEntranceRoom = false,
                PreferImportantRoom = true,
                RequireImportantRoomWhenReviewed = true,
                DisallowHallway = true,
                RequireWallContact = true,
                RequireFloorContact = true,
                RequireBodyClearance = true,
                RequireReachableInteractionPoint = true,
                DebugLabel = "Mainframe"
            };

            if (!hasExistingMainframe && !spawnedAuthoredMainframe)
            {
                InteriorPlacementPlan mainframePlan = null;
                bool mainframePlanBuilt = false;
                IEnumerator buildMainframe = InteriorPlacementService.TryBuildPlanBudgeted(mainframeRequest, (ok, plan) =>
                {
                    mainframePlanBuilt = ok;
                    mainframePlan = plan;
                }, () => ShouldYieldFrame(perf));
                while (buildMainframe.MoveNext())
                    yield return buildMainframe.Current;

                if (mainframePlanBuilt && mainframePlan != null)
                {
                    PlacementPose mainframePose = mainframePlan.Pose;
                    mainframePose = TagMainframeSupportPose(mainframePose, "automatic-mainframe-");

                    if (TryRegisterMainframePlacement(mainframePose, true, supportPoses, usedTiles))
                    {
                        fixturesSpawned++;
                        spawnedMainframeTile = mainframePose.Tile;
                        yield return YieldNextFrame(perf);
                    }
                }
            }

            if (!configuredStashCodes)
            {
                desiredStashCount = ResolveAutomaticCompanyStashTargetCount();
                EnsureVaultPrefabRegistered();
                CctvSupportState.ConfigureStashCodesForRound(desiredStashCount);
                configuredStashCodes = true;
            }

            var automaticStashExcludedTiles = new HashSet<Tile>();
            if (spawnedMainframeTile != null)
                automaticStashExcludedTiles.Add(spawnedMainframeTile);
            foreach (Tile tile in occupiedStashTiles)
            {
                if (tile != null)
                    automaticStashExcludedTiles.Add(tile);
            }

            int automaticStashTargetCount = Mathf.Max(0, desiredStashCount - spawnedAuthoredVaults);
            if (hadAuthoredVaultRecords && spawnedAuthoredVaults < desiredStashCount)
            {
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CompanyStash] Authored stashes spawned {spawnedAuthoredVaults}/{desiredStashCount}; filling {automaticStashTargetCount} missing stash(es) with automatic fallback.");
            }

            int spawnedTotalVaults = spawnedAuthoredVaults;
            for (int automaticIndex = 0; automaticIndex < automaticStashTargetCount; automaticIndex++)
            {
                int vaultIndex = spawnedTotalVaults;
                var vaultRequest = new InteriorPlacementRequest
                {
                    Role = InteriorPlacementRole.VaultWallOrFloorSafe,
                    Footprint = FixtureFootprint.Vault(GetPrefabBackOffset(_vaultPrefab, 0.36f)),
                    PreferredTile = null,
                    MustDifferFromTile = spawnedMainframeTile,
                    ExcludedTiles = automaticStashExcludedTiles,
                    MinEntranceDistance = 28f,
                    AllowEntranceRoom = false,
                    PreferImportantRoom = true,
                    RequireImportantRoomWhenReviewed = true,
                    DisallowHallway = true,
                    RequireWallContact = true,
                    RequireFloorContact = true,
                    RequireBodyClearance = true,
                    RequireReachableInteractionPoint = true,
                    VariantIndex = vaultIndex + 1,
                    DebugLabel = $"Vault{vaultIndex + 1}"
                };

                InteriorPlacementPlan vaultPlan = null;
                bool vaultPlanBuilt = false;
                IEnumerator buildVault = InteriorPlacementService.TryBuildPlanBudgeted(vaultRequest, (ok, plan) =>
                {
                    vaultPlanBuilt = ok;
                    vaultPlan = plan;
                }, () => ShouldYieldFrame(perf));
                while (buildVault.MoveNext())
                    yield return buildVault.Current;

                if (vaultPlanBuilt && vaultPlan != null)
                {
                    if (SpawnVault(vaultPlan.Pose.Position, vaultPlan.Pose.Rotation, vaultIndex, vaultPlan.Pose.TileName))
                    {
                        fixturesSpawned++;
                        spawnedTotalVaults++;
                        supportPoses.Add(vaultPlan.Pose);
                        if (vaultPlan.Pose.Tile != null)
                        {
                            usedTiles.Add(vaultPlan.Pose.Tile);
                            automaticStashExcludedTiles.Add(vaultPlan.Pose.Tile);
                        }
                        yield return YieldNextFrame(perf);
                    }
                    else
                    {
                        CctvModuleConfig.Log?.LogWarning(
                            $"[MoonContracts.CompanyStash] Automatic fallback plan produced tile='{vaultPlan.Pose.TileName}' but spawn failed for stashIndex={vaultIndex}.");
                    }
                }
                else
                {
                    CctvModuleConfig.Log?.LogWarning(
                        $"[MoonContracts.CompanyStash] Automatic fallback failed to build plan for stashIndex={vaultIndex}.");
                }

                if (ShouldYieldFrame(perf))
                    yield return null;
            }

            if (configuredStashCodes && spawnedTotalVaults != desiredStashCount)
            {
                CctvSupportState.FinalizeStashCodesForSpawnedCount(spawnedTotalVaults);
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CompanyStash] Adjusted configured stash codes to spawned count {spawnedTotalVaults}/{desiredStashCount}.");
            }

            roomsUsed = usedTiles.Count;
            bool automaticMainframeCovered = true;
            IEnumerator cameraRoutine = EnsureCamerasCoverSupportPosesBudgeted(supportPoses, perf, result, covered => automaticMainframeCovered = covered);
            while (cameraRoutine.MoveNext())
                yield return cameraRoutine.Current;
            if (!automaticMainframeCovered)
                CctvModuleConfig.Log?.LogWarning("[MoonContracts.Mainframe] Mainframe spawned but no CCTV support camera could confirm coverage.");

            result.RoomsUsed = roomsUsed;
            result.FixturesSpawned = fixturesSpawned;
            result.Succeeded = roomsUsed > 0;
        }

        private static void CleanupRuntimeInstances(string reason)
        {
            if (RuntimeInstances.Count == 0)
                return;

            int cleaned = 0;
            for (int i = RuntimeInstances.Count - 1; i >= 0; i--)
            {
                GameObject go = RuntimeInstances[i];
                if (go == null)
                    continue;

                bool destroyed = false;
                try
                {
                    NetworkObject netObject = go.GetComponent<NetworkObject>();
                    if (netObject != null && netObject.IsSpawned && RoundManager.Instance != null && RoundManager.Instance.IsServer)
                    {
                        netObject.Despawn(true);
                        destroyed = true;
                    }
                }
                catch (Exception ex)
                {
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed despawning runtime support object during {reason}: {ex.Message}");
                }

                if (!destroyed)
                {
                    try
                    {
                        UnityEngine.Object.Destroy(go);
                    }
                    catch (Exception ex)
                    {
                        CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed destroying runtime support object during {reason}: {ex.Message}");
                    }
                }

                cleaned++;
            }

            RuntimeInstances.Clear();
            if (cleaned > 0)
                CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Cleaned {cleaned} runtime support object(s) during {reason}.");
        }

        private static bool TryFindExistingMainframe(out MainframeSupport existingMainframe)
        {
            existingMainframe = null;
            MainframeSupport active = MainframeSupport.Active;
            if (active != null && active.gameObject != null && active.gameObject.activeInHierarchy)
            {
                existingMainframe = active;
                return true;
            }

            MainframeSupport candidate = UnityEngine.Object.FindAnyObjectByType<MainframeSupport>(
                FindObjectsInactive.Exclude);
            if (candidate != null &&
                candidate.gameObject != null &&
                candidate.gameObject.activeInHierarchy)
            {
                existingMainframe = candidate;
                return true;
            }

            return false;
        }

        internal static void RegisterExistingMainframeBeforePlacementRequests()
        {
            RoundManager round = RoundManager.Instance;
            if (round == null || !round.IsServer)
                return;
            if (!TryFindExistingMainframe(out MainframeSupport existingMainframe))
                return;

            Tile tile = FindCurrentDungeonTileContainingPosition(existingMainframe.transform.position);
            PlacementPose pose = BuildExistingMainframeSupportPose(existingMainframe, tile);
            TryRegisterMainframePlacement(pose, false, null, null);
        }

        private static void RemoveExtraMainframes(MainframeSupport keep)
        {
            if (keep == null)
                return;

            MainframeSupport[] mainframes = UnityEngine.Object.FindObjectsByType<MainframeSupport>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            int removed = 0;
            for (int i = 0; i < mainframes.Length; i++)
            {
                MainframeSupport candidate = mainframes[i];
                if (candidate == null || candidate == keep || candidate.gameObject == null)
                    continue;

                GameObject go = candidate.gameObject;
                RuntimeInstances.Remove(go);
                try
                {
                    NetworkObject netObject = go.GetComponent<NetworkObject>();
                    if (netObject != null && netObject.IsSpawned)
                        netObject.Despawn(true);
                    else
                        UnityEngine.Object.Destroy(go);
                    removed++;
                }
                catch (Exception ex)
                {
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts.Mainframe] Failed removing duplicate MainframeSupport: {ex.Message}");
                }
            }

            if (removed > 0)
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts.Mainframe] Removed {removed} duplicate MainframeSupport instance(s); keeping '{keep.name}'.");
        }

        private static bool TryRegisterExistingMainframeSupportPose(
            MainframeSupport existingMainframe,
            Tile tile,
            List<PlacementPose> supportPoses,
            HashSet<Tile> usedTiles)
        {
            if (existingMainframe == null || supportPoses == null)
                return false;

            PlacementPose pose = BuildExistingMainframeSupportPose(existingMainframe, tile);
            return TryRegisterMainframePlacement(pose, false, supportPoses, usedTiles);
        }

        private static PlacementPose BuildExistingMainframeSupportPose(MainframeSupport existingMainframe, Tile tile)
        {
            FixtureFootprint footprint = FixtureFootprint.Mainframe(GetPrefabBackOffset(_mainframePrefab, 0.34f));
            Vector3 position = existingMainframe.transform.position;
            Quaternion rotation = existingMainframe.transform.rotation;
            Vector3 forward = ResolveLogicalForward(rotation, footprint);
            Vector3 interactionPoint = position
                + forward * Mathf.Max(0.65f, footprint.FrontClearance * 0.55f)
                + Vector3.up * footprint.InteractionHeight;
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            float path = InteriorAnchorService.EstimateNearestPathDistance(entrances, position);

            return new PlacementPose(
                position,
                rotation,
                forward,
                interactionPoint,
                tile,
                "existing-mainframe",
                30000f,
                path,
                forward);
        }

        private static bool TryRegisterMainframePlacement(
            PlacementPose placementPose,
            bool spawnFixture,
            List<PlacementPose> supportPoses,
            HashSet<Tile> usedTiles)
        {
            FixtureFootprint footprint = FixtureFootprint.Mainframe(GetPrefabBackOffset(_mainframePrefab, 0.34f));
            const string label = "Company mainframe";
            string source = string.IsNullOrWhiteSpace(placementPose.Source)
                ? "unknown-mainframe-source"
                : placementPose.Source;

            if (!FixtureReservationRegistry.TryReserve(
                    label,
                    source,
                    footprint,
                    placementPose.Position,
                    placementPose.Rotation,
                    out FixtureReservationHandle reservation,
                    out FixtureReservationConflict conflict))
            {
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.Mainframe] Reservation rejected source='{source}' {conflict.ToDiagnosticString()}.");
                return false;
            }

            bool spawnSucceeded = !spawnFixture;
            if (spawnFixture)
            {
                try
                {
                    spawnSucceeded = SpawnFixture(
                        _mainframePrefab,
                        placementPose.Position,
                        placementPose.Rotation,
                        "Mainframe",
                        placementPose.TileName);
                }
                catch (Exception ex)
                {
                    CctvModuleConfig.Log?.LogWarning(
                        $"[MoonContracts.Mainframe] Spawn operation aborted source='{source}' tile='{placementPose.TileName}': {ex.Message}");
                }
            }

            if (!spawnSucceeded)
            {
                FixtureReservationRegistry.Release(reservation);
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.Mainframe] Released reservation after spawn failure source='{source}' tile='{placementPose.TileName}'.");
                return false;
            }

            supportPoses?.Add(placementPose);
            if (placementPose.Tile != null)
                usedTiles?.Add(placementPose.Tile);
            return true;
        }

        private static bool TrySpawnAuthoredMainframe(List<PlacementPose> supportPoses, HashSet<Tile> usedTiles, ref int fixturesSpawned, out Tile spawnedTile)
        {
            spawnedTile = null;
            Tile tile;
            Vector3 worldPosition;
            Quaternion worldRotation;
            Vector3 local;
            string source;

            var request = new InteriorPlacementRequest
            {
                Role = InteriorPlacementRole.MainframeStandingBackToWall,
                Footprint = FixtureFootprint.Mainframe(GetPrefabBackOffset(_mainframePrefab, 0.34f)),
                MinEntranceDistance = 36f,
                AllowEntranceRoom = false,
                PreferImportantRoom = true,
                RequireImportantRoomWhenReviewed = false,
                DisallowHallway = true,
                RequireWallContact = true,
                RequireFloorContact = true,
                RequireBodyClearance = true,
                RequireReachableInteractionPoint = true,
                DebugLabel = "AuthoredMainframe"
            };

            PlacementPose placementPose;
            bool selectedCanonical = TrySelectViableAuthoredMainframe(request, out AuthoredInteriorPlacementPose canonicalPose, out placementPose);
            if (selectedCanonical)
            {
                tile = canonicalPose.Tile;
                worldPosition = canonicalPose.WorldPosition;
                worldRotation = canonicalPose.WorldRotation;
                local = canonicalPose.Record != null ? canonicalPose.Record.tileLocalPosition : Vector3.zero;
                source = string.IsNullOrWhiteSpace(canonicalPose.Source) ? "canonical-authored" : canonicalPose.Source;
            }
            else
            {
                return false;
            }

            if (tile == null)
                return false;

            placementPose = TagMainframeSupportPose(placementPose, "authored-mainframe-");
            if (!TryRegisterMainframePlacement(placementPose, true, supportPoses, usedTiles))
            {
                if (selectedCanonical)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        canonicalPose.Record,
                        "consumer=mainframe reason=reservation-or-spawn-failed");
                }
                return false;
            }

            fixturesSpawned++;
            if (selectedCanonical)
                AuthoredPlacementRoundReport.RecordSpawned(canonicalPose.Record);
            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts.Mainframe] AUTHORED_SPAWN tile='{tile.name}' world=({worldPosition.x:F2},{worldPosition.y:F2},{worldPosition.z:F2}) local=({local.x:F2},{local.y:F2},{local.z:F2}) yaw={worldRotation.eulerAngles.y:F1} source={source}.");

            spawnedTile = tile;
            return true;
        }

        private static bool TrySelectCompanyStashes(
            IReadOnlyList<AuthoredInteriorPlacementPose> candidates,
            out List<AuthoredInteriorPlacementPose> selected,
            out int targetCount,
            out string riskLabel)
        {
            selected = new List<AuthoredInteriorPlacementPose>();
            riskLabel = ResolveCurrentRiskLabel();
            targetCount = 0;

            if (candidates == null || candidates.Count == 0)
                return false;

            var compatible = new List<AuthoredInteriorPlacementPose>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                AuthoredInteriorPlacementPose candidate = candidates[i];
                if (candidate.Tile != null)
                    compatible.Add(candidate);
            }

            if (compatible.Count == 0)
                return false;

            compatible.Sort(CompareAuthoredPoseForDisplay);
            targetCount = RollCompanyStashTargetCount(riskLabel);
            Shuffle(compatible, new System.Random(BuildSelectionSeed("company-stash-authored")));
            int selectedCount = Mathf.Min(targetCount, compatible.Count);
            for (int i = 0; i < selectedCount; i++)
                selected.Add(compatible[i]);
            for (int i = selectedCount; i < compatible.Count; i++)
            {
                AuthoredPlacementRoundReport.RecordResolvedButUnused(
                    compatible[i].Record,
                    "consumer=company-stash reason=risk-count-selection");
            }

            return true;
        }

        private static PlacementPose WithSupportPoseSource(PlacementPose pose, string source)
        {
            return new PlacementPose(
                pose.Position,
                pose.Rotation,
                pose.WallNormal,
                pose.InteractionPoint,
                pose.Tile,
                source,
                pose.Score,
                pose.EntrancePathDistance,
                pose.Forward);
        }

        private static PlacementPose TagMainframeSupportPose(PlacementPose pose, string prefix)
        {
            string source = string.IsNullOrWhiteSpace(pose.Source) ? "unknown" : pose.Source.Trim();
            if (source.IndexOf("mainframe", StringComparison.OrdinalIgnoreCase) < 0)
                source = prefix + source;
            return WithSupportPoseSource(pose, source);
        }

        private static bool TrySelectViableAuthoredMainframe(
            InteriorPlacementRequest request,
            out AuthoredInteriorPlacementPose selected,
            out PlacementPose placementPose)
        {
            selected = default;
            placementPose = default;
            if (!AuthoredInteriorPlacementStore.TryResolvePoses(AuthoredInteriorPlacementKinds.Mainframe, out List<AuthoredInteriorPlacementPose> candidates)
                || candidates == null
                || candidates.Count == 0)
            {
                return false;
            }

            var compatible = new List<AuthoredInteriorPlacementPose>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                AuthoredInteriorPlacementPose candidate = candidates[i];
                if (candidate.Tile != null)
                    compatible.Add(candidate);
            }

            if (compatible.Count == 0)
                return false;

            Shuffle(compatible, new System.Random(BuildSelectionSeed("mainframe")));
            for (int i = 0; i < compatible.Count; i++)
            {
                AuthoredInteriorPlacementPose candidate = compatible[i];
                string source = string.IsNullOrWhiteSpace(candidate.Source) ? "canonical-authored" : candidate.Source;
                if (!ValidateAuthoredPlacementPose(
                        request,
                        candidate.Tile,
                        candidate.WorldPosition,
                        candidate.WorldRotation,
                        "Mainframe",
                        source,
                        25000f,
                        out PlacementPose candidatePose,
                        candidate.Record))
                {
                    continue;
                }

                for (int unusedIndex = i + 1; unusedIndex < compatible.Count; unusedIndex++)
                {
                    AuthoredPlacementRoundReport.RecordResolvedButUnused(
                        compatible[unusedIndex].Record,
                        "consumer=mainframe reason=seed-selection");
                }
                selected = candidate;
                placementPose = candidatePose;
                CctvModuleConfig.Log?.LogInfo(
                    $"[MoonContracts.Mainframe] Selected authored mainframe id='{selected.Record?.objectId ?? "<unknown>"}' from compatible={compatible.Count}.");
                return true;
            }

            return false;
        }

        private static bool HasAuthoredMainframeRecordsForCurrentFlow()
        {
            try
            {
                IReadOnlyList<AuthoredInteriorPlacementRecord> records =
                    AuthoredInteriorPlacementStore.GetRecordsForFlow(AuthoredInteriorPlacementKinds.Mainframe, ResolveCurrentDungeonFlowName());
                return records != null && records.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static int RollCompanyStashTargetCount(string riskLabel)
        {
            double oneVaultChance;
            switch (ResolveRiskTier(riskLabel))
            {
                case 'D':
                    oneVaultChance = 0.60;
                    break;
                case 'C':
                    oneVaultChance = 0.50;
                    break;
                case 'B':
                    oneVaultChance = 0.40;
                    break;
                case 'A':
                    oneVaultChance = 0.25;
                    break;
                case 'S':
                    oneVaultChance = 0.10;
                    break;
                default:
                    oneVaultChance = 0.40;
                    break;
            }

            var rng = new System.Random(BuildSelectionSeed("company-stash-count"));
            int rolled = rng.NextDouble() < oneVaultChance ? 1 : 2 + rng.Next(0, 3);
            ResolveCompanyStashCountBounds(riskLabel, out int minimum, out int maximum);
            return Mathf.Clamp(rolled, minimum, maximum);
        }

        private static int ResolveAutomaticCompanyStashTargetCount()
        {
            string riskLabel = ResolveCurrentRiskLabel();
            return RollCompanyStashTargetCount(riskLabel);
        }

        private static void ResolveCompanyStashCountBounds(string riskLabel, out int minimum, out int maximum)
        {
            ConfigEntry<int> minimumEntry;
            ConfigEntry<int> maximumEntry;
            switch (ResolveRiskTier(riskLabel))
            {
                case 'D':
                    minimumEntry = CctvModuleConfig.StashMinimumRiskD;
                    maximumEntry = CctvModuleConfig.StashMaximumRiskD;
                    break;
                case 'C':
                    minimumEntry = CctvModuleConfig.StashMinimumRiskC;
                    maximumEntry = CctvModuleConfig.StashMaximumRiskC;
                    break;
                case 'B':
                    minimumEntry = CctvModuleConfig.StashMinimumRiskB;
                    maximumEntry = CctvModuleConfig.StashMaximumRiskB;
                    break;
                case 'A':
                    minimumEntry = CctvModuleConfig.StashMinimumRiskA;
                    maximumEntry = CctvModuleConfig.StashMaximumRiskA;
                    break;
                case 'S':
                    minimumEntry = CctvModuleConfig.StashMinimumRiskS;
                    maximumEntry = CctvModuleConfig.StashMaximumRiskS;
                    break;
                default:
                    minimumEntry = CctvModuleConfig.StashMinimumUnknownRisk;
                    maximumEntry = CctvModuleConfig.StashMaximumUnknownRisk;
                    break;
            }

            minimum = Mathf.Clamp(minimumEntry?.Value ?? 1, 0, 12);
            maximum = Mathf.Clamp(maximumEntry?.Value ?? 4, 0, 12);
            if (maximum < minimum)
                maximum = minimum;
        }

        private static char ResolveRiskTier(string risk)
        {
            if (string.IsNullOrWhiteSpace(risk))
                return '\0';

            string normalized = risk.Trim().ToUpperInvariant()
                .Replace("RISK", string.Empty)
                .Replace("LEVEL", string.Empty)
                .Replace(":", string.Empty)
                .Trim();
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c == 'S' || c == 'A' || c == 'B' || c == 'C' || c == 'D')
                    return c;
            }

            return '\0';
        }

        private static string ResolveCurrentRiskLabel()
        {
            string risk = StartOfRound.Instance?.currentLevel?.riskLevel;
            return string.IsNullOrWhiteSpace(risk) ? "UNKNOWN" : risk.Trim();
        }

        private static int BuildSelectionSeed(string salt)
        {
            unchecked
            {
                int seed = StartOfRound.Instance != null ? StartOfRound.Instance.randomMapSeed : Environment.TickCount;
                SelectableLevel level = StartOfRound.Instance?.currentLevel ?? RoundManager.Instance?.currentLevel;
                seed = (seed * 397) ^ StableHash(salt);
                seed = (seed * 397) ^ (level != null ? level.levelID : 0);
                seed = (seed * 397) ^ StableHash(level != null ? level.PlanetName : string.Empty);
                seed = (seed * 397) ^ StableHash(ResolveCurrentDungeonFlowName());
                return seed;
            }
        }

        private static string ResolveCurrentDungeonFlowName()
        {
            try
            {
                DungeonFlow flow = RoundManager.Instance?.dungeonGenerator?.Generator?.DungeonFlow;
                return flow != null && !string.IsNullOrWhiteSpace(flow.name) ? flow.name : "unknown-flow";
            }
            catch
            {
                return "unknown-flow";
            }
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = (int)2166136261;
                if (!string.IsNullOrEmpty(value))
                {
                    for (int i = 0; i < value.Length; i++)
                        hash = (hash ^ value[i]) * 16777619;
                }
                return hash;
            }
        }

        private static void Shuffle<T>(IList<T> values, System.Random rng)
        {
            if (values == null || rng == null)
                return;

            for (int i = values.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T tmp = values[i];
                values[i] = values[j];
                values[j] = tmp;
            }
        }

        private static int CompareAuthoredPoseForDisplay(AuthoredInteriorPlacementPose a, AuthoredInteriorPlacementPose b)
        {
            int id = string.Compare(a.Record?.objectId ?? string.Empty, b.Record?.objectId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (id != 0)
                return id;

            int tile = string.Compare(a.Tile?.name ?? string.Empty, b.Tile?.name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            if (tile != 0)
                return tile;

            return a.WorldPosition.sqrMagnitude.CompareTo(b.WorldPosition.sqrMagnitude);
        }

        private static Tile FindCurrentDungeonTileByName(string expectedName)
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0) return null;
            string expected = NormalizeTileName(expectedName);
            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                Tile tile = dungeon.AllTiles[i];
                if (tile == null) continue;
                if (NormalizeTileName(tile.name) == expected)
                    return tile;
            }
            return null;
        }

        private static Tile FindCurrentDungeonTileContainingPosition(Vector3 position)
        {
            Dungeon dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0) return null;

            for (int i = 0; i < dungeon.AllTiles.Count; i++)
            {
                Tile tile = dungeon.AllTiles[i];
                if (tile == null) continue;

                Bounds bounds = tile.Bounds;
                bounds.Expand(new Vector3(0.5f, 8f, 0.5f));
                if (bounds.Contains(position))
                    return tile;
            }

            return null;
        }

        private static string NormalizeTileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static string FormatTileName(Tile tile)
        {
            return tile != null && !string.IsNullOrEmpty(tile.name) ? tile.name : "missing";
        }

        private static bool ValidateAuthoredPlacementPose(
            InteriorPlacementRequest request,
            Tile tile,
            Vector3 position,
            Quaternion rotation,
            string label,
            string source,
            float score,
            out PlacementPose pose,
            AuthoredInteriorPlacementRecord reportRecord = null)
        {
            pose = default;
            if (!PlacementValidator.ValidateAuthoredRelaxed(
                    request,
                    tile,
                    position,
                    rotation,
                    out Vector3 snappedPosition,
                    out PlacementValidationResult validation))
            {
                IReadOnlyList<string> reasons = validation.Reasons;
                AuthoredPlacementRoundReport.RecordValidationFailed(reportRecord, reasons);
                string kind = reportRecord?.objectKind ?? request?.Role.ToString() ?? "<unknown>";
                string id = reportRecord?.objectId ?? "<legacy-or-unreported>";
                string tileName = tile != null ? tile.name : "<null>";
                CctvModuleConfig.Log?.LogWarning(
                    $"[Y4NGZ.AuthoredPlacement] AUTHORED_RELAXED_REJECT profile=relaxed kind='{kind}' id='{id}' label='{label}' source='{source ?? "<unknown>"}' tile='{tileName}' " +
                    $"Skipped authored placement pos=({position.x:F2},{position.y:F2},{position.z:F2}) yaw={rotation.eulerAngles.y:F1} reasons={InteriorProbeExporter.FormatReasons(reasons)}");
                return false;
            }

            Vector3 forward = ResolveLogicalForward(rotation, request.Footprint);
            Vector3 interactionPoint = snappedPosition
                + forward * Mathf.Max(0.65f, request.Footprint.FrontClearance * 0.55f)
                + Vector3.up * request.Footprint.InteractionHeight;
            Vector3 wallNormal = forward;
            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            float path = InteriorAnchorService.EstimateNearestPathDistance(entrances, snappedPosition);
            pose = new PlacementPose(
                snappedPosition,
                rotation,
                wallNormal,
                interactionPoint,
                tile,
                string.IsNullOrWhiteSpace(source) ? "authored" : source,
                score,
                path,
                forward);
            return true;
        }

        private static Vector3 ResolveLogicalForward(Quaternion rotation, FixtureFootprint footprint)
        {
            Vector3 forward = rotation * footprint.LocalFront;
            forward.y = 0f;
            if (forward.sqrMagnitude >= 1e-6f)
                return forward.normalized;

            Vector3 fallback = rotation * Vector3.forward;
            fallback.y = 0f;
            if (fallback.sqrMagnitude >= 1e-6f)
                return fallback.normalized;

            return Vector3.forward;
        }

        private static bool EnsureCamerasCoverSupportPoses(List<PlacementPose> poses)
        {
            return EnsureCamerasCoverSupportPoses(poses, out _);
        }

        private static bool EnsureCamerasCoverSupportPoses(List<PlacementPose> poses, out int camerasTouched)
        {
            camerasTouched = 0;
            if (poses == null || poses.Count == 0) return true;
            var poseObjects = new List<object>(poses.Count);
            var tileObjects = new List<object>(poses.Count);
            var mainframeTiles = new HashSet<Tile>();
            bool mainframeCoverageRequested = false;
            for (int i = 0; i < poses.Count; i++)
            {
                poseObjects.Add(poses[i]);
                if (poses[i].Tile != null && !tileObjects.Contains(poses[i].Tile))
                    tileObjects.Add(poses[i].Tile);
                if (poses[i].Tile != null
                    && !string.IsNullOrEmpty(poses[i].Source)
                    && poses[i].Source.IndexOf("mainframe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mainframeCoverageRequested = true;
                    mainframeTiles.Add(poses[i].Tile);
                }
            }
            if (poseObjects.Count == 0 && tileObjects.Count == 0) return !mainframeCoverageRequested;

            try
            {
                Type injectorType = Type.GetType("Y4NGZCompany.Facility.Cameras.InteriorSupportCameraInjector, LethalCCTV", throwOnError: false);
                if (injectorType == null) return !mainframeCoverageRequested;
                MethodInfo method = injectorType.GetMethod("EnsureCamerasForSupportPoses", BindingFlags.Public | BindingFlags.Static)
                                    ?? injectorType.GetMethod("EnsureCamerasInTiles", BindingFlags.Public | BindingFlags.Static);
                if (method == null) return !mainframeCoverageRequested;
                object arg = method.Name == "EnsureCamerasForSupportPoses" ? (object)poseObjects : tileObjects;
                object result = method.Invoke(null, new object[] { arg });
                bool mainframeCovered = false;
                if (result is System.Collections.IList list)
                {
                    int added = 0;
                    int failed = 0;
                    foreach (object entry in list)
                    {
                        if (entry == null) continue;
                        PropertyInfo succeeded = entry.GetType().GetProperty("Succeeded");
                        PropertyInfo tileProp = entry.GetType().GetProperty("Tile");
                        PropertyInfo reasonProp = entry.GetType().GetProperty("FailureReason");
                        PropertyInfo poseSourceProp = entry.GetType().GetProperty("PoseSource");
                        bool ok = succeeded != null && succeeded.GetValue(entry) is bool succeededValue && succeededValue;
                        object tile = tileProp != null ? tileProp.GetValue(entry) : null;
                        string poseSource = poseSourceProp != null ? poseSourceProp.GetValue(entry) as string : null;
                        if (ok)
                        {
                            added++;
                            bool coversMainframePose = !string.IsNullOrEmpty(poseSource)
                                && poseSource.IndexOf("mainframe", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (coversMainframePose && tile is Tile resultTile && mainframeTiles.Contains(resultTile))
                                mainframeCovered = true;
                        }
                        else
                        {
                            failed++;
                            string tileName = tile is Tile t ? t.name : "<unknown>";
                            string reason = reasonProp != null ? reasonProp.GetValue(entry) as string : null;
                            CctvModuleConfig.Log?.LogInfo(
                                $"[MoonContracts] Interior support camera injector did not add camera for tile='{tileName}' reason={reason ?? "unknown"}.");
                        }
                    }
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts] Interior support camera injector result: added={added}, failed={failed}, requested={list.Count}.");
                    camerasTouched = added;
                }
                return !mainframeCoverageRequested || mainframeCovered;
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Interior support camera injector failed: {ex.Message}");
            }

            return !mainframeCoverageRequested;
        }

        private static IEnumerator EnsureCamerasCoverSupportPosesBudgeted(
            List<PlacementPose> poses,
            SpawnBudgetContext perf,
            SpawnRoundResult result,
            Action<bool> onComplete)
        {
            result.CamerasTouched = 0;
            if (poses == null || poses.Count == 0)
            {
                onComplete?.Invoke(true);
                yield break;
            }

            var poseObjects = new List<object>(poses.Count);
            var tileObjects = new List<object>(poses.Count);
            var mainframeTiles = new HashSet<Tile>();
            bool mainframeCoverageRequested = false;
            for (int i = 0; i < poses.Count; i++)
            {
                poseObjects.Add(poses[i]);
                if (poses[i].Tile != null && !tileObjects.Contains(poses[i].Tile))
                    tileObjects.Add(poses[i].Tile);
                if (poses[i].Tile != null
                    && !string.IsNullOrEmpty(poses[i].Source)
                    && poses[i].Source.IndexOf("mainframe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    mainframeCoverageRequested = true;
                    mainframeTiles.Add(poses[i].Tile);
                }
            }

            if (poseObjects.Count == 0 && tileObjects.Count == 0)
            {
                onComplete?.Invoke(!mainframeCoverageRequested);
                yield break;
            }

            var entries = new List<object>();
            IEnumerator reflectedRoutine = null;
            bool completeWithoutInjector = false;
            try
            {
                Type injectorType = Type.GetType("Y4NGZCompany.Facility.Cameras.InteriorSupportCameraInjector, LethalCCTV", throwOnError: false);
                if (injectorType == null)
                {
                    completeWithoutInjector = true;
                }
                else
                {
                    MethodInfo budgeted = injectorType.GetMethod("EnsureCamerasForSupportPosesBudgeted", BindingFlags.Public | BindingFlags.Static);
                    if (budgeted != null)
                    {
                        var collect = new Action<object>(entry =>
                        {
                            if (entry != null)
                                entries.Add(entry);
                        });
                        var shouldYield = new Func<bool>(() => ShouldYieldFrame(perf));
                        object routineObject = budgeted.Invoke(null, new object[] { poseObjects, collect, shouldYield });
                        reflectedRoutine = routineObject as IEnumerator;
                    }
                    else
                    {
                        MethodInfo method = injectorType.GetMethod("EnsureCamerasForSupportPoses", BindingFlags.Public | BindingFlags.Static)
                                            ?? injectorType.GetMethod("EnsureCamerasInTiles", BindingFlags.Public | BindingFlags.Static);
                        if (method == null)
                        {
                            completeWithoutInjector = true;
                        }
                        else
                        {
                            object arg = method.Name == "EnsureCamerasForSupportPoses" ? (object)poseObjects : tileObjects;
                            object syncResult = method.Invoke(null, new object[] { arg });
                            if (syncResult is System.Collections.IEnumerable enumerable)
                            {
                                foreach (object entry in enumerable)
                                {
                                    if (entry != null)
                                        entries.Add(entry);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Interior support camera injector failed: {ex.Message}");
                onComplete?.Invoke(!mainframeCoverageRequested);
                yield break;
            }

            if (completeWithoutInjector)
            {
                onComplete?.Invoke(!mainframeCoverageRequested);
                yield break;
            }

            if (reflectedRoutine != null)
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = reflectedRoutine.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Interior support camera injector failed: {ex.Message}");
                        onComplete?.Invoke(!mainframeCoverageRequested);
                        yield break;
                    }

                    if (!moved)
                        break;

                    yield return reflectedRoutine.Current;
                }
            }

            bool mainframeCovered = false;
            int added = 0;
            int failed = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                object entry = entries[i];
                if (entry == null) continue;
                PropertyInfo succeeded = entry.GetType().GetProperty("Succeeded");
                PropertyInfo tileProp = entry.GetType().GetProperty("Tile");
                PropertyInfo reasonProp = entry.GetType().GetProperty("FailureReason");
                PropertyInfo poseSourceProp = entry.GetType().GetProperty("PoseSource");
                bool ok = succeeded != null && succeeded.GetValue(entry) is bool succeededValue && succeededValue;
                object tile = tileProp != null ? tileProp.GetValue(entry) : null;
                string poseSource = poseSourceProp != null ? poseSourceProp.GetValue(entry) as string : null;
                if (ok)
                {
                    added++;
                    bool coversMainframePose = !string.IsNullOrEmpty(poseSource)
                        && poseSource.IndexOf("mainframe", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (coversMainframePose && tile is Tile resultTile && mainframeTiles.Contains(resultTile))
                        mainframeCovered = true;
                }
                else
                {
                    failed++;
                    string tileName = tile is Tile t ? t.name : "<unknown>";
                    string reason = reasonProp != null ? reasonProp.GetValue(entry) as string : null;
                    CctvModuleConfig.Log?.LogInfo(
                        $"[MoonContracts] Interior support camera injector did not add camera for tile='{tileName}' reason={reason ?? "unknown"}.");
                }

                if (ShouldYieldFrame(perf))
                    yield return null;
            }

            result.CamerasTouched = added;
            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts] Interior support camera injector result: added={added}, failed={failed}, requested={entries.Count}.");
            onComplete?.Invoke(!mainframeCoverageRequested || mainframeCovered);
        }

        private static List<InteriorTileSelection> SelectSupportRooms(int count)
        {
            var picks = new List<InteriorTileSelection>(count);
            var dungeon = RoundManager.Instance?.dungeonGenerator?.Generator?.CurrentDungeon;
            if (dungeon?.AllTiles == null || dungeon.AllTiles.Count == 0) return picks;

            List<Vector3> entrances = InteriorAnchorService.GetInteriorEntrancePositions();
            var sorted = new List<Tile>(dungeon.AllTiles);
            sorted.Sort((a, b) => CompareTileScore(b, a, entrances));
            int picked = 0;
            for (int i = 0; i < sorted.Count && picked < count; i++)
            {
                Tile tile = sorted[i];
                if (tile == null) continue;
                // #550: the score above rewards entrance path distance and depth, which a sealed
                // tile fakes perfectly - it carries its own NavMesh island, so its estimate falls
                // back to euclidean distance and it sorts straight to the top. A support camera
                // the crew can never walk to is dead coverage.
                if (!InteriorAnchorService.IsTileReachableFromEntrance(tile, entrances)) continue;
                InteriorTileSelection sel = BuildSelectionForTile(tile, entrances);
                if (sel.Tile == null) continue;
                picks.Add(sel);
                picked++;
            }
            return picks;
        }

        private static int CompareTileScore(Tile a, Tile b, List<Vector3> entrances)
        {
            float depthA = InteriorAnchorService.GetTileDepth(a);
            float depthB = InteriorAnchorService.GetTileDepth(b);
            float areaA = InteriorAnchorService.GetTileArea(a);
            float areaB = InteriorAnchorService.GetTileArea(b);
            float pathA = InteriorAnchorService.EstimateTileEntrancePathDistance(a, entrances);
            float pathB = InteriorAnchorService.EstimateTileEntrancePathDistance(b, entrances);
            float scoreA = pathA + depthA * 6f + ModerateRoomBonus(areaA);
            float scoreB = pathB + depthB * 6f + ModerateRoomBonus(areaB);
            return scoreB.CompareTo(scoreA);
        }

        private static float ModerateRoomBonus(float area)
        {
            float ideal = 120f;
            return 10f - Mathf.Abs(area - ideal) * 0.035f;
        }

        private static InteriorTileSelection BuildSelectionForTile(Tile tile, List<Vector3> entrances)
        {
            Bounds bounds = tile.Bounds;
            float area = InteriorAnchorService.GetTileArea(tile);
            if (area < 55f) return default;

            float minSpan = Mathf.Min(bounds.size.x, bounds.size.z);
            float maxSpan = Mathf.Max(bounds.size.x, bounds.size.z);
            bool hallwayLike = minSpan < 4.4f || maxSpan > minSpan * 3.2f;
            if (hallwayLike)
                return default;

            Vector3 floor = InteriorAnchorService.ResolveFloorPosition(bounds.center);
            Vector3 nearestEntrance = InteriorAnchorService.GetNearestEntrancePoint(entrances, floor);
            Vector3 away = floor - nearestEntrance;
            away.y = 0f;
            Quaternion rot = away.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(away.normalized, Vector3.up)
                : Quaternion.identity;
            return new InteriorTileSelection(
                tile, floor, rot,
                0f, 0f, 0f,
                InteriorAnchorService.GetTileDepth(tile),
                0f,
                area, tile.UsedDoorways?.Count ?? 0,
                false);
        }

        private static bool TrySpawnMainframe(InteriorTileSelection room, Vector3 floor, Quaternion rot)
        {
            const float HEIGHT = 1.2f;
            float wallOffset = GetPrefabBackOffset(_mainframePrefab, 0.34f);
            if (!TryFindWallMount(room, floor, HEIGHT, wallOffset, new Vector3(-1f, 0f, 0.2f), out Vector3 pos, out Quaternion mountedRot))
                ResolveWallMountPose(room, new Vector3(-1f, 0f, +0.2f), HEIGHT, out pos, out mountedRot);
            pos.y = floor.y;
            return SpawnFixture(_mainframePrefab, pos, mountedRot, "Mainframe", room.TileName);
        }

        private static bool TryFindWallMount(
            InteriorTileSelection room, Vector3 floor, float heightM, float wallOffsetM, Vector3 preferredLocalDirection,
            out Vector3 position, out Quaternion rotation)
        {
            position = floor;
            rotation = Quaternion.identity;

            StartOfRound sor = StartOfRound.Instance;
            if (sor == null) return false;
            int mask = sor.collidersAndRoomMaskAndDefault;

            Vector3 origin = new Vector3(floor.x, floor.y + heightM, floor.z);
            Vector3[] dirs = GetWallSearchDirections(room, preferredLocalDirection);

            var hits = new List<(Vector3 point, Vector3 normal, float dist)>(12);
            foreach (Vector3 dir in dirs)
            {
                Vector3 flattenedDir = dir;
                flattenedDir.y = 0f;
                if (flattenedDir.sqrMagnitude < 0.001f) continue;
                flattenedDir.Normalize();
                Vector3 lateral = Vector3.Cross(Vector3.up, flattenedDir).normalized;

                for (int offsetIndex = -1; offsetIndex <= 1; offsetIndex++)
                {
                    Vector3 castOrigin = origin + lateral * (offsetIndex * 0.65f);
                    if (!Physics.Raycast(castOrigin, flattenedDir, out RaycastHit hit, MountSearchDistance, mask, QueryTriggerInteraction.Ignore))
                        continue;
                    if (hit.distance < 0.35f) continue;
                    float dotUp = Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up));
                    if (dotUp > 0.55f) continue;
                    if (Vector3.Dot(hit.normal, flattenedDir) > -0.2f) continue;
                    if (!TryResolveMountCandidate(room, floor.y + heightM, hit.point, hit.normal, wallOffsetM, out Vector3 candidatePosition, out Quaternion candidateRotation))
                        continue;
                    hits.Add((candidatePosition, hit.normal, hit.distance));
                }
            }

            if (hits.Count == 0) return false;

            hits.Sort((a, b) => b.dist.CompareTo(a.dist));
            var chosen = hits[0];

            Vector3 normal = chosen.normal;
            normal.y = 0f;
            if (normal.sqrMagnitude < 1e-6f) return false;
            normal.Normalize();

            position = chosen.point;
            rotation = Quaternion.LookRotation(normal, Vector3.up);
            return true;
        }

        private static bool TrySpawnVault(InteriorTileSelection room, Vector3 floor, Quaternion rot, int index)
        {
            float xSide = index % 2 == 0 ? -1f : +1f;
            float zSide = index / 2 == 0 ? -1f : +1f;
            Vector3 preferredLocalDirection = new Vector3(xSide, 0f, zSide);
            float wallOffset = GetPrefabBackOffset(_vaultPrefab, 0.36f);
            if (!TryFindWallMount(room, floor, 0.82f, wallOffset, preferredLocalDirection, out Vector3 pos, out Quaternion rotation))
                ResolveWallMountPose(room, preferredLocalDirection, 0.82f, out pos, out rotation);
            pos.y = floor.y;
            return SpawnVault(pos, rotation, index, room.TileName);
        }

        private static bool SpawnVault(Vector3 position, Quaternion rotation, int index, string roomLabel)
        {
            if (_vaultPrefab == null)
                return false;

            if (!CctvSupportState.TryGetAssignedStashCode(index, out int assignedCode))
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Cannot spawn Company Stash {index + 1}: no assigned code generated.");
                return false;
            }

            string reservationLabel = $"Company stash {index + 1}";
            if (!FixtureReservationRegistry.TryReserve(
                    reservationLabel,
                    "company-stash-spawn",
                    FixtureFootprint.Vault(GetPrefabBackOffset(_vaultPrefab, 0.36f)),
                    position,
                    rotation,
                    out FixtureReservationHandle reservation,
                    out FixtureReservationConflict conflict))
            {
                CctvModuleConfig.Log?.LogWarning(
                    $"[MoonContracts.CompanyStash] Reservation rejected index={index} room='{roomLabel}' " +
                    conflict.ToDiagnosticString() + ".");
                return false;
            }

            var go = UnityEngine.Object.Instantiate(_vaultPrefab, position, rotation);
            go.transform.SetPositionAndRotation(position, rotation);
            // Do not parent runtime NetworkObjects before spawning. NGO throws
            // SpawnStateException and the support spawn path can abort the round.
            // Keep the object active while spawning so child InteractTrigger NetworkBehaviours
            // are not skipped by NGO as disabled components.
            go.transform.SetPositionAndRotation(position, rotation);

            var stash = go.GetComponent<CompanyStashController>();
            var keypad = go.GetComponentInChildren<CompanyStashKeypadInteractor>(includeInactive: true) ?? go.GetComponent<CompanyStashKeypadInteractor>();
            if (stash == null)
            {
                CctvModuleConfig.Log?.LogWarning("[MoonContracts] Company Stash prefab missing required controller.");
                FixtureReservationRegistry.Release(reservation);
                UnityEngine.Object.Destroy(go);
                return false;
            }

            if (keypad == null)
                keypad = go.AddComponent<CompanyStashKeypadInteractor>();

            string displayName = $"Company Stash {index + 1}";
            stash.InitializeAssignedCode(assignedCode, displayName);
            // The keypad resolves its owner from the parent chain since #393; nothing to wire.
            ActivateInteriorSupportInstanceForNetworkSpawn(go, "Company Stash");
            var netObject = go.GetComponent<NetworkObject>();
            if (netObject == null) netObject = go.AddComponent<NetworkObject>();
            netObject.SynchronizeTransform = true;
            if (!netObject.IsSpawned)
            {
                try
                {
                    netObject.Spawn();
                }
                catch (System.Exception ex)
                {
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed to spawn mini vault: {ex.Message}");
                }
            }
            RuntimeInstances.Add(go);
            LogSpawn("Company Stash", roomLabel, position, rotation);
            return true;
        }

        private static void ResolveWallMountPose(InteriorTileSelection room, Vector3 localDirection, float heightM, out Vector3 position, out Quaternion rotation)
        {
            Bounds bounds = room.Bounds;
            Vector3 local = new Vector3(
                Mathf.Clamp(localDirection.x, -1f, 1f),
                0f,
                Mathf.Clamp(localDirection.z, -1f, 1f));
            if (local.sqrMagnitude < 1e-6f)
                local = Vector3.forward;
            local.Normalize();

            Vector3 worldDir = room.Rotation * local;
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f)
                worldDir = room.Rotation * Vector3.forward;
            worldDir.Normalize();

            Vector3 center = bounds.center;
            float inset = 0.65f;
            float xExtent = Mathf.Max(0.25f, bounds.extents.x - inset);
            float zExtent = Mathf.Max(0.25f, bounds.extents.z - inset);
            float y = room.FloorPosition.y;

            if (Mathf.Abs(worldDir.x) >= Mathf.Abs(worldDir.z))
            {
                float side = Mathf.Sign(worldDir.x);
                float zOffset = Mathf.Clamp(worldDir.z * bounds.extents.z * 0.35f, -zExtent, zExtent);
                position = new Vector3(center.x + side * xExtent, y, center.z + zOffset);
            }
            else
            {
                float side = Mathf.Sign(worldDir.z);
                float xOffset = Mathf.Clamp(worldDir.x * bounds.extents.x * 0.35f, -xExtent, xExtent);
                position = new Vector3(center.x + xOffset, y, center.z + side * zExtent);
            }

            Vector3 faceIntoRoom = room.Bounds.center - position;
            faceIntoRoom.y = 0f;
            rotation = faceIntoRoom.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(faceIntoRoom.normalized, Vector3.up)
                : room.Rotation;
            position = ClampToRoomBounds(room, position, WallInset);
        }

        private static bool SpawnFixture(GameObject prefab, Vector3 position, Quaternion rotation, string label, string roomLabel)
        {
            if (prefab == null)
                return false;

            GameObject go;
            try
            {
                go = UnityEngine.Object.Instantiate(prefab, position, rotation);
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed to instantiate interior support '{label}': {ex.Message}");
                return false;
            }

            if (go == null)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed to instantiate interior support '{label}': clone was null.");
                return false;
            }

            if (go.GetComponent<PreserveInteriorSupportVisuals>() == null)
                NormalizeFixtureVisuals(go, label);
            go.transform.SetPositionAndRotation(position, rotation);
            // Do not parent runtime NetworkObjects before spawning. NGO throws
            // SpawnStateException and the support spawn path can abort the round.
            // Activate cloned prefabs before spawning so Netcode sees child behaviours
            // and inactive bundled/fallback prefabs become visible at runtime.
            go.transform.SetPositionAndRotation(position, rotation);
            ActivateInteriorSupportInstanceForNetworkSpawn(go, label);

            var netObject = go.GetComponent<NetworkObject>();
            if (netObject == null) netObject = go.AddComponent<NetworkObject>();
            netObject.SynchronizeTransform = true;
            if (netObject != null && !netObject.IsSpawned)
            {
                try
                {
                    netObject.Spawn();
                }
                catch (System.Exception ex)
                {
                    CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed to spawn interior support '{label}': {ex.Message}");
                    UnityEngine.Object.Destroy(go);
                    return false;
                }
            }

            if (!netObject.IsSpawned)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] Failed to spawn interior support '{label}': NetworkObject did not enter the spawned state.");
                UnityEngine.Object.Destroy(go);
                return false;
            }

            RuntimeInstances.Add(go);
            LogSpawn(label, roomLabel, position, rotation);
            if (string.Equals(label, "Mainframe", StringComparison.OrdinalIgnoreCase))
            {
                LogPrefabDiagnostics("Spawned Mainframe", go, "runtime instance");
                MainframeSpawnDiagnostics.ScheduleLiveInstanceDump(go, $"spawned-mainframe room='{roomLabel}'");
            }
            return true;
        }

        private static void ActivateInteriorSupportInstanceForNetworkSpawn(GameObject go, string label)
        {
            if (go == null || go.activeSelf)
                return;

            go.SetActive(true);
            CctvModuleConfig.Log?.LogDebug($"[MoonContracts] Activated {label ?? "interior support"} runtime clone before network spawn.");
        }

        private static void EnsureRuntimePrefabsRegistered()
        {
            if (_mainframePrefab == null)
            {
                GameObject bundledMainframe = LoadBundledFixturePrefab<MainframeSupport>(MainframeAssetName);
                if (bundledMainframe != null)
                {
                    _mainframePrefab = bundledMainframe;
                    LogPrefabDiagnostics("Mainframe", _mainframePrefab, "bundled CCTVMainframeSupport");
                }
                else
                {
                    _mainframePrefab = CreateRegisteredFixturePrefab<MainframeSupport>("MoonContracts_MainframeSupportPrefab", BuildMainframeVisual);
                    CctvModuleConfig.Log?.LogWarning("[MoonContracts] CCTVMainframeSupport was not found in lgucontracts.bundle; using generated fallback mainframe visual.");
                    LogPrefabDiagnostics("Mainframe", _mainframePrefab, "generated fallback");
                    MainframeSpawnDiagnostics.LogPreparedPrefab(MainframeAssetName, _mainframePrefab, "generated-fallback-prefab");
                }
            }
            // AlarmBoxSupport prefab intentionally not loaded/registered â€” alarm is part of
            // the mainframe now (Part A consolidation).
            if (VaultsPerRoom > 0)
                EnsureVaultPrefabRegistered();
        }

        public static GameObject CreatePlacementPreview(string objectKind, string metadataJson)
        {
            string kind = AuthoredInteriorPlacementKinds.Normalize(objectKind);
            GameObject prefab = null;
            string previewName = null;

            switch (kind)
            {
                case AuthoredInteriorPlacementKinds.Mainframe:
                    EnsureRuntimePrefabsRegistered();
                    prefab = _mainframePrefab;
                    previewName = "Y4NGZDebug_MainframePreview";
                    break;
                case AuthoredInteriorPlacementKinds.Vault:
                    EnsureVaultPrefabRegistered();
                    prefab = _vaultPrefab;
                    previewName = "Y4NGZDebug_CompanyStashPreview";
                    break;
                case AuthoredInteriorPlacementKinds.CctvNote:
                    return CreateCctvNotePlacementPreview();
                default:
                    return null;
            }

            if (prefab == null)
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (instance == null)
                return null;

            instance.name = previewName;
            StripPlacementPreview(instance);
            instance.SetActive(true);
            return instance;
        }

        /// <summary>
        /// #582 — ghost of the CCTV controls sticky note for the F9 authoring flow.
        /// Clones the real registered prefab so the ghost has the note's true mesh
        /// and texture, then strips it like every other preview. The registered
        /// template is inactive and HideAndDontSave; the clone must shed both or
        /// the editor cannot show it. Scaled to the note anchor's current world
        /// scale when the station exists, so the ghost previews the size the note
        /// will actually spawn at.
        /// </summary>
        private static GameObject CreateCctvNotePlacementPreview()
        {
            GameObject prefab = ShipSystems.Surveillance.CCTVStickyNoteItem.Prefab;
            if (prefab == null)
                return null;

            // The registered template ships with an EMPTY MeshFilter/MeshRenderer —
            // the live prop borrows the vanilla note's visual in Start, and the
            // strip below removes that prop before it could ever run. A ghost with
            // no mesh starts a "successful" edit session the user authors blind,
            // so resolve the visual here and decline (aborting the session with
            // the standard "preview failed" message) when it is not available yet.
            if (!ShipSystems.Surveillance.CCTVStickyNoteItem.TryResolveVisual(out Mesh noteMesh, out Material noteMaterial))
                return null;

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            if (instance == null)
                return null;

            MeshFilter ghostFilter = instance.GetComponent<MeshFilter>();
            MeshRenderer ghostRenderer = instance.GetComponent<MeshRenderer>();
            if (ghostFilter == null || ghostRenderer == null)
            {
                DestroyUnityObject(instance);
                return null;
            }

            ghostFilter.sharedMesh = noteMesh;
            ghostRenderer.sharedMaterial = noteMaterial;

            instance.name = "Y4NGZDebug_CctvNotePreview";
            instance.hideFlags = HideFlags.None;
            Transform anchor = ShipSystems.Surveillance.CCTVOperatorStation.NoteAnchor;
            if (anchor != null)
                instance.transform.localScale = anchor.lossyScale;
            StripPlacementPreview(instance);
            instance.SetActive(true);
            return instance;
        }

        private static void EnsureVaultPrefabRegistered()
        {
            if (_vaultPrefab != null)
                return;

            GameObject bundledStash = LoadBundledFixturePrefab<CompanyStashController>(CompanyStashAssetName);
            if (bundledStash != null)
            {
                if (bundledStash.GetComponentInChildren<CompanyStashKeypadInteractor>(true) == null)
                    bundledStash.AddComponent<CompanyStashKeypadInteractor>();
                _vaultPrefab = bundledStash;
                LogPrefabDiagnostics("Company Stash", _vaultPrefab, "bundled CompanyStash");
                return;
            }

            _vaultPrefab = CreateRegisteredVaultPrefab();
            CctvModuleConfig.Log?.LogWarning("[MoonContracts] CompanyStash was not found in lgucontracts.bundle; using generated fallback stash visual.");
        }

        private static GameObject LoadBundledFixturePrefab<TComponent>(string assetName)
            where TComponent : Component
        {
            GameObject prefab = CctvFixtureAssets.GetPreparedInteriorSupportPrefab(assetName);
            if (prefab == null)
                return null;

            if (prefab.GetComponent<TComponent>() == null)
                prefab.AddComponent<TComponent>();
            if (prefab.GetComponent<PreserveInteriorSupportVisuals>() == null)
                prefab.AddComponent<PreserveInteriorSupportVisuals>();
            if (typeof(TComponent) == typeof(MainframeSupport))
            {
                if (prefab.GetComponent<MainframeLightFlicker>() == null)
                    prefab.AddComponent<MainframeLightFlicker>();
                TryAddOptionalMainframeScreenController(prefab);
                TryAddOptionalMainframeInteractionController(prefab);
            }
            AddRootRaycastCollider(prefab);
            CctvFixtureAssets.PrepareRuntimeInteriorSupportPrefab(prefab, assetName);
            if (typeof(TComponent) == typeof(MainframeSupport))
                MainframeSpawnDiagnostics.LogPreparedPrefab(assetName, prefab, "after-PrepareRuntimeInteriorSupportPrefab");
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Using bundled interior support prefab '{assetName}' with authored materials preserved.");
            return prefab;
        }
        private static void TryAddOptionalMainframeScreenController(GameObject prefab)
        {
            if (prefab == null)
                return;

            Type controllerType = FindLoadedType(MainframeScreenControllerTypeName);
            if (controllerType == null)
            {
                CctvModuleConfig.Log?.LogDebug("[MoonContracts] MainframeScreenController type not loaded; physical mainframe screen host skipped for this build.");
                return;
            }

            if (!typeof(Component).IsAssignableFrom(controllerType))
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] MainframeScreenController type '{controllerType.FullName}' is not a Unity Component.");
                return;
            }

            if (prefab.GetComponent(controllerType) == null)
                prefab.AddComponent(controllerType);
        }

        private static void TryAddOptionalMainframeInteractionController(GameObject prefab)
        {
            if (prefab == null)
                return;

            Type controllerType = FindLoadedType(MainframeInteractionControllerTypeName);
            if (controllerType == null)
            {
                CctvModuleConfig.Log?.LogDebug("[MoonContracts] MainframeInteractionController type not loaded; physical mainframe interaction prompt skipped for this build.");
                return;
            }

            if (!typeof(Component).IsAssignableFrom(controllerType))
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] MainframeInteractionController type '{controllerType.FullName}' is not a Unity Component.");
                return;
            }

            if (prefab.GetComponent(controllerType) == null)
                prefab.AddComponent(controllerType);
        }

        private static Type FindLoadedType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            Type direct = Type.GetType(fullName, throwOnError: false);
            if (direct != null)
                return direct;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type match = assemblies[i].GetType(fullName, throwOnError: false);
                if (match != null)
                    return match;
            }

            return null;
        }
        private static GameObject CreateRegisteredFixturePrefab<TComponent>(string prefabName, Action<GameObject> buildVisual)
            where TComponent : Component
        {
            GameObject prefab = new GameObject(prefabName);
            prefab.hideFlags = HideFlags.HideAndDontSave;
            buildVisual?.Invoke(prefab);
            NormalizeFixtureVisuals(prefab, prefabName);
            AddRootRaycastCollider(prefab);
            prefab.AddComponent<TComponent>();
            NetworkObject netObject = prefab.AddComponent<NetworkObject>();
            netObject.SynchronizeTransform = true;
            // A NetworkObject added at runtime keeps GlobalObjectIdHash 0, which NGO reports as
            // "duplicate GlobalObjectIdHash source entry value of: 0" the moment a second such
            // prefab is registered, and leaves clients unable to resolve the prefab on spawn.
            CctvFixtureAssets.AssignStableNetworkHash(netObject, "InteriorSupport", prefabName);
            RegisterRuntimePrefab(prefab);
            prefab.SetActive(false);
            return prefab;
        }

        private static GameObject CreateRegisteredVaultPrefab()
        {
            GameObject prefab = new GameObject("MoonContracts_CompanyStashPrefab");
            prefab.hideFlags = HideFlags.HideAndDontSave;
            BuildCompanyStashVisual(prefab);
            NormalizeFixtureVisuals(prefab, prefab.name);
            AddRootRaycastCollider(prefab);
            prefab.AddComponent<CompanyStashController>();
            prefab.AddComponent<CompanyStashKeypadInteractor>();
            NetworkObject netObject = prefab.AddComponent<NetworkObject>();
            netObject.SynchronizeTransform = true;
            CctvFixtureAssets.AssignStableNetworkHash(netObject, "InteriorSupport", prefab.name);
            RegisterRuntimePrefab(prefab);
            prefab.SetActive(false);
            return prefab;
        }

        private static void RegisterRuntimePrefab(GameObject prefab)
        {
            if (prefab == null) return;
            try
            {
                LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(prefab);
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogDebug($"[MoonContracts] LethalLib network prefab registration skipped for '{prefab.name}': {ex.Message}");
            }

            try
            {
                DawnLibCompat.RegisterNetworkPrefab(prefab);
            }
            catch (Exception ex)
            {
                CctvModuleConfig.Log?.LogDebug($"[MoonContracts] Dawn network prefab registration skipped for '{prefab.name}': {ex.Message}");
            }
        }

        private static Vector3[] GetWallSearchDirections(InteriorTileSelection room, Vector3 preferredLocalDirection)
        {
            Transform tileTransform = room.Tile != null ? room.Tile.transform : null;
            Vector3[] basis = tileTransform != null
                ? new[] { tileTransform.forward, tileTransform.right, -tileTransform.forward, -tileTransform.right }
                : new[] { room.Rotation * Vector3.forward, room.Rotation * Vector3.right, -(room.Rotation * Vector3.forward), -(room.Rotation * Vector3.right) };

            Vector3 preferredWorld = room.Rotation * preferredLocalDirection;
            preferredWorld.y = 0f;
            if (preferredWorld.sqrMagnitude < 0.001f)
                preferredWorld = room.Rotation * Vector3.forward;
            preferredWorld.Normalize();

            return basis
                .Select(direction =>
                {
                    Vector3 flattened = direction;
                    flattened.y = 0f;
                    return flattened.sqrMagnitude > 0.001f ? flattened.normalized : Vector3.forward;
                })
                .OrderByDescending(direction => Vector3.Dot(direction, preferredWorld))
                .ToArray();
        }

        private static bool TryResolveMountCandidate(InteriorTileSelection room, float y, Vector3 hitPoint, Vector3 hitNormal, float wallOffsetM,
            out Vector3 position, out Quaternion rotation)
        {
            Vector3 normal = hitNormal;
            normal.y = 0f;
            position = hitPoint;
            rotation = Quaternion.identity;
            if (normal.sqrMagnitude < 0.001f)
                return false;

            normal.Normalize();
            position = hitPoint + normal * Mathf.Max(WallInset, wallOffsetM);
            position.y = room.FloorPosition.y;
            position = ClampToRoomBounds(room, position, WallInset);

            Vector3 floorAtPosition = InteriorAnchorService.ResolveFloorPosition(position + Vector3.up * 1.2f);
            if (Mathf.Abs(floorAtPosition.y - room.FloorPosition.y) > 1.35f)
                return false;

            rotation = Quaternion.LookRotation(normal, Vector3.up);
            if (!HasClearApproach(room, position, rotation))
                return false;
            return true;
        }

        private static bool HasClearApproach(InteriorTileSelection room, Vector3 position, Quaternion rotation)
        {
            if (room.Tile?.UsedDoorways != null)
            {
                for (int i = 0; i < room.Tile.UsedDoorways.Count; i++)
                {
                    Doorway doorway = room.Tile.UsedDoorways[i];
                    if (doorway == null) continue;
                    Vector3 doorwayPos = doorway.transform.position;
                    doorwayPos.y = position.y;
                    if (Vector3.Distance(doorwayPos, position) < 1.75f)
                        return false;
                }
            }

            StartOfRound sor = StartOfRound.Instance;
            int mask = sor != null ? sor.collidersAndRoomMaskAndDefault : ~0;
            Vector3 forward = rotation * Vector3.forward;
            Vector3 checkCenter = position + forward * 0.85f + Vector3.up * 0.8f;
            Vector3 halfExtents = new Vector3(0.45f, 0.7f, 0.45f);
            return !Physics.CheckBox(checkCenter, halfExtents, rotation, mask, QueryTriggerInteraction.Ignore);
        }

        private static Vector3 ClampToRoomBounds(InteriorTileSelection room, Vector3 position, float inset)
        {
            Bounds bounds = room.Bounds;
            Vector3 min = bounds.min + new Vector3(inset, 0f, inset);
            Vector3 max = bounds.max - new Vector3(inset, 0f, inset);
            position.x = Mathf.Clamp(position.x, min.x, max.x);
            position.z = Mathf.Clamp(position.z, min.z, max.z);
            return position;
        }

        private static void AddRootRaycastCollider(GameObject root)
        {
            if (root == null) return;
            Bounds? combinedBounds = null;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                Bounds rendererBounds = ConvertWorldBoundsToLocal(root.transform, renderer.bounds);
                combinedBounds = combinedBounds.HasValue ? Encapsulate(combinedBounds.Value, rendererBounds) : rendererBounds;
            }

            if (!combinedBounds.HasValue)
                return;

            BoxCollider collider = root.GetComponent<BoxCollider>();
            if (collider == null)
                collider = root.AddComponent<BoxCollider>();

            Bounds bounds = combinedBounds.Value;
            bounds.Expand(new Vector3(0.08f, 0.08f, 0.16f));
            collider.center = bounds.center;
            collider.size = new Vector3(
                Mathf.Max(0.4f, bounds.size.x),
                Mathf.Max(0.8f, bounds.size.y),
                Mathf.Max(0.24f, bounds.size.z));
        }

        private static Bounds ConvertWorldBoundsToLocal(Transform root, Bounds worldBounds)
        {
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            Vector3[] corners = new Vector3[8]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z),
            };

            Vector3 localMin = root.InverseTransformPoint(corners[0]);
            Vector3 localMax = localMin;
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 local = root.InverseTransformPoint(corners[i]);
                localMin = Vector3.Min(localMin, local);
                localMax = Vector3.Max(localMax, local);
            }

            return new Bounds((localMin + localMax) * 0.5f, localMax - localMin);
        }

        private static Bounds Encapsulate(Bounds a, Bounds b)
        {
            a.Encapsulate(b.min);
            a.Encapsulate(b.max);
            return a;
        }

        private static float GetPrefabBackOffset(GameObject prefab, float fallback)
        {
            if (prefab == null) return fallback;
            Bounds? bounds = null;
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                Bounds local = ConvertWorldBoundsToLocal(prefab.transform, renderer.bounds);
                bounds = bounds.HasValue ? Encapsulate(bounds.Value, local) : local;
            }

            if (!bounds.HasValue) return fallback;

            // The mainframe's declared front is local -Z (FixtureFootprint.Mainframe), so its
            // wall-facing back extent is max.z; other fixtures keep the min.z convention.
            bool positiveZBack = prefab.GetComponent<MainframeSupport>() != null;
            float zBack = positiveZBack ? bounds.Value.max.z : bounds.Value.min.z;
            return Mathf.Clamp(Mathf.Abs(zBack) + 0.035f, 0.12f, 1.25f);
        }

        private static void LogSpawn(string label, string roomLabel, Vector3 position, Quaternion rotation)
        {
            CctvModuleConfig.Log?.LogInfo($"[MoonContracts] Spawned {label} in '{roomLabel}' at {position.x:0.00},{position.y:0.00},{position.z:0.00} facing {rotation.eulerAngles.y:0.0}deg.");
        }

        private static void LogPrefabDiagnostics(string label, GameObject root, string source)
        {
            if (root == null)
            {
                CctvModuleConfig.Log?.LogWarning($"[MoonContracts] {label} prefab diagnostics unavailable: root is null source={source}.");
                return;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            Collider[] colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            Bounds? bounds = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                Bounds local = ConvertWorldBoundsToLocal(root.transform, renderer.bounds);
                bounds = bounds.HasValue ? Encapsulate(bounds.Value, local) : local;
            }

            string boundsText = bounds.HasValue
                ? $"bounds=({bounds.Value.size.x:0.00},{bounds.Value.size.y:0.00},{bounds.Value.size.z:0.00})"
                : "bounds=none";
            string rendererNames = renderers.Length > 0
                ? string.Join(",", renderers.Take(6).Select(r => r != null ? r.gameObject.name : "null"))
                : "none";

            CctvModuleConfig.Log?.LogInfo(
                $"[MoonContracts] {label} prefab source={source} root='{root.name}' renderers={renderers.Length} " +
                $"colliders={colliders.Length} {boundsText} rendererNames={rendererNames}.");
        }

        private sealed class PreserveInteriorSupportVisuals : MonoBehaviour
        {
        }

        private sealed class InteriorSupportSpawnRunner : MonoBehaviour
        {
        }

        private static void BuildMainframeVisual(GameObject root)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "MainframeBody";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            body.transform.localScale = new Vector3(1.4f, 2.0f, 0.6f);
            TintRenderer(body, new Color(0.15f, 0.2f, 0.18f), FixtureBodyEmission);
            DestroyCollider(body);

            var screen = GameObject.CreatePrimitive(PrimitiveType.Cube);
            screen.name = "MainframeScreen";
            screen.transform.SetParent(root.transform, false);
            screen.transform.localPosition = new Vector3(0f, 1.2f, 0.31f);
            screen.transform.localScale = new Vector3(1.0f, 0.7f, 0.05f);
            TintRenderer(screen, new Color(0.18f, 0.85f, 0.5f), FixtureAccentEmission);
            DestroyCollider(screen);

            var antenna = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            antenna.name = "MainframeAntenna";
            antenna.transform.SetParent(root.transform, false);
            antenna.transform.localPosition = new Vector3(0.5f, 2.2f, 0f);
            antenna.transform.localScale = new Vector3(0.06f, 0.4f, 0.06f);
            TintRenderer(antenna, new Color(0.6f, 0.6f, 0.6f), FixtureBodyEmission);
            DestroyCollider(antenna);

            AddFixtureOutlineFrame(root, new Vector3(1.6f, 2.4f, 0.8f));
        }

        private static void BuildCompanyStashVisual(GameObject root)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "CompanyStashBody";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.95f, -0.08f);
            body.transform.localScale = new Vector3(1.05f, 1.9f, 0.42f);
            TintRenderer(body, new Color(0.24f, 0.26f, 0.28f), FixtureBodyEmission);
            DestroyCollider(body);

            var leftDoor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftDoor.name = "door-l";
            leftDoor.transform.SetParent(root.transform, false);
            leftDoor.transform.localPosition = new Vector3(-0.265f, 0.95f, 0.145f);
            leftDoor.transform.localScale = new Vector3(0.5f, 1.78f, 0.055f);
            TintRenderer(leftDoor, new Color(0.18f, 0.2f, 0.22f), FixtureBodyEmission);
            DestroyCollider(leftDoor);

            var rightDoor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightDoor.name = "door-r";
            rightDoor.transform.SetParent(root.transform, false);
            rightDoor.transform.localPosition = new Vector3(0.265f, 0.95f, 0.145f);
            rightDoor.transform.localScale = new Vector3(0.5f, 1.78f, 0.055f);
            TintRenderer(rightDoor, new Color(0.19f, 0.21f, 0.23f), FixtureBodyEmission);
            DestroyCollider(rightDoor);

            var keypad = new GameObject("Keypad");
            keypad.transform.SetParent(rightDoor.transform, false);
            keypad.transform.localPosition = new Vector3(-0.14f, 0.2f, 0.58f);
            keypad.transform.localRotation = Quaternion.identity;
            keypad.transform.localScale = Vector3.one;

            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "KeypadPanel";
            panel.transform.SetParent(keypad.transform, false);
            panel.transform.localPosition = new Vector3(0f, 0f, 0f);
            panel.transform.localScale = new Vector3(0.28f, 0.46f, 0.045f);
            TintRenderer(panel, new Color(0.025f, 0.028f, 0.032f), FixtureAccentEmission * 0.25f);
            DestroyCollider(panel);

            var display = GameObject.CreatePrimitive(PrimitiveType.Cube);
            display.name = "Display";
            display.transform.SetParent(keypad.transform, false);
            display.transform.localPosition = new Vector3(0f, 0.16f, 0.035f);
            display.transform.localScale = new Vector3(0.2f, 0.07f, 0.012f);
            TintRenderer(display, new Color(0.98f, 0.5f, 0.032f), FixtureAccentEmission);
            DestroyCollider(display);

            int[] buttons =
            {
                1, 2, 3,
                4, 5, 6,
                7, 8, 9,
                0
            };

            for (int i = 0; i < buttons.Length; i++)
            {
                int digit = buttons[i];
                int row = digit == 0 ? 3 : (digit - 1) / 3;
                int col = digit == 0 ? 1 : (digit - 1) % 3;
                CreateCompanyStashKey(keypad.transform, $"bttn{digit}", new Vector3((col - 1) * 0.068f, 0.075f - row * 0.065f, 0.04f), new Vector3(0.046f, 0.044f, 0.018f));
            }

            CreateCompanyStashKey(keypad.transform, "bttnEnter", new Vector3(0.068f, -0.12f, 0.04f), new Vector3(0.046f, 0.044f, 0.018f));

            var lootAnchor = new GameObject("GoldBarSpawnAnchor");
            lootAnchor.transform.SetParent(root.transform, false);
            lootAnchor.transform.localPosition = new Vector3(0f, 0.72f, 0.04f);
            lootAnchor.transform.localRotation = Quaternion.identity;

            AddFixtureOutlineFrame(root, new Vector3(1.18f, 2.0f, 0.62f));
        }

        private static void CreateCompanyStashKey(Transform parent, string name, Vector3 localPosition, Vector3 localScale)
        {
            var key = GameObject.CreatePrimitive(PrimitiveType.Cube);
            key.name = name;
            key.transform.SetParent(parent, false);
            key.transform.localPosition = localPosition;
            key.transform.localScale = localScale;
            TintRenderer(key, new Color(0.11f, 0.12f, 0.13f), FixtureAccentEmission * 0.2f);
            DestroyCollider(key);
        }

        private static void BuildVaultVisual(GameObject root)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "VaultBody";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.65f, 0f);
            body.transform.localScale = new Vector3(0.9f, 1.3f, 0.7f);
            TintRenderer(body, new Color(0.2f, 0.22f, 0.27f), FixtureBodyEmission);
            DestroyCollider(body);

            var keypad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            keypad.name = "VaultKeypad";
            keypad.transform.SetParent(root.transform, false);
            keypad.transform.localPosition = new Vector3(0f, 0.95f, 0.36f);
            keypad.transform.localScale = new Vector3(0.34f, 0.42f, 0.04f);
            TintRenderer(keypad, new Color(0.05f, 0.05f, 0.05f), FixtureAccentEmission * 0.35f);
            DestroyCollider(keypad);

            AddFixtureOutlineFrame(root, new Vector3(1.0f, 1.45f, 0.85f));
        }

        private static void AddFixtureOutlineFrame(GameObject root, Vector3 overallSize)
        {
            const float thickness = 0.08f;
            Vector3 half = overallSize * 0.5f;

            CreateFrameBar(root, new Vector3(0f, +half.y, 0f), new Vector3(overallSize.x, thickness, thickness));
            CreateFrameBar(root, new Vector3(0f, -half.y, 0f), new Vector3(overallSize.x, thickness, thickness));
            CreateFrameBar(root, new Vector3(+half.x, 0f, 0f), new Vector3(thickness, overallSize.y, thickness));
            CreateFrameBar(root, new Vector3(-half.x, 0f, 0f), new Vector3(thickness, overallSize.y, thickness));

            CreateFrameBar(root, new Vector3(0f, +half.y, 0f), new Vector3(thickness, thickness, overallSize.z));
            CreateFrameBar(root, new Vector3(0f, -half.y, 0f), new Vector3(thickness, thickness, overallSize.z));
            CreateFrameBar(root, new Vector3(0f, 0f, +half.z), new Vector3(overallSize.x, thickness, thickness));
            CreateFrameBar(root, new Vector3(0f, 0f, -half.z), new Vector3(overallSize.x, thickness, thickness));
        }

        private static void CreateFrameBar(GameObject root, Vector3 localPos, Vector3 localScale)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "FixtureOutlineBar";
            bar.transform.SetParent(root.transform, false);
            bar.transform.localPosition = localPos;
            bar.transform.localScale = localScale;
            TintRenderer(bar, new Color(0.1f, 1f, 0.32f), FixtureOutlineEmission);
            DestroyCollider(bar);
        }

        private static void NormalizeFixtureVisuals(GameObject root, string label)
        {
            if (root == null) return;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                if (TryResolveFixturePartVisual(label, renderer.gameObject.name, out Color color, out float emission))
                    TintRenderer(renderer, color, emission);
            }
        }

        private static bool TryResolveFixturePartVisual(string label, string partName, out Color color, out float emission)
        {
            string labelText = label ?? string.Empty;
            string part = partName ?? string.Empty;
            bool mainframe = labelText.IndexOf("Mainframe", StringComparison.OrdinalIgnoreCase) >= 0;
            bool alarm = labelText.IndexOf("Alarm", StringComparison.OrdinalIgnoreCase) >= 0;
            bool vault = labelText.IndexOf("Vault", StringComparison.OrdinalIgnoreCase) >= 0;

            if (Contains(part, "outline") || Contains(part, "frame") || Contains(part, "bar"))
            {
                color = new Color(0.1f, 1f, 0.32f, 1f);
                emission = FixtureOutlineEmission;
                return true;
            }

            if (mainframe)
            {
                if (Contains(part, "screen") || Contains(part, "display") || Contains(part, "monitor"))
                {
                    color = new Color(0.18f, 0.85f, 0.5f, 1f);
                    emission = FixtureAccentEmission;
                    return true;
                }
                if (Contains(part, "antenna") || Contains(part, "metal"))
                {
                    color = new Color(0.55f, 0.57f, 0.56f, 1f);
                    emission = FixtureBodyEmission;
                    return true;
                }
                color = new Color(0.15f, 0.2f, 0.18f, 1f);
                emission = FixtureBodyEmission;
                return true;
            }

            if (alarm)
            {
                if (Contains(part, "light") || Contains(part, "lamp") || Contains(part, "beacon"))
                {
                    color = new Color(1.0f, 0.3f, 0.25f, 1f);
                    emission = FixtureAccentEmission;
                    return true;
                }
                color = new Color(0.55f, 0.13f, 0.10f, 1f);
                emission = FixtureBodyEmission;
                return true;
            }

            if (vault)
            {
                color = Contains(part, "keypad")
                    ? new Color(0.05f, 0.05f, 0.05f, 1f)
                    : new Color(0.2f, 0.22f, 0.27f, 1f);
                emission = Contains(part, "keypad") ? FixtureAccentEmission * 0.35f : FixtureBodyEmission;
                return true;
            }

            color = Color.white;
            emission = 0f;
            return false;
        }

        private static bool Contains(string value, string token)
        {
            return value != null && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TintRenderer(GameObject go, Color color, float emissionIntensity)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return;
            TintRenderer(renderer, color, emissionIntensity);
        }

        private static void TintRenderer(Renderer renderer, Color color, float emissionIntensity)
        {
            if (renderer == null) return;
            Shader shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? renderer.sharedMaterial?.shader;
            Material mat = shader != null ? new Material(shader) : new Material(renderer.sharedMaterial);
            mat.color = color;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            Color emission = emissionIntensity > 0f ? color * emissionIntensity : Color.black;
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.SetColor("_EmissionColor", emission);
            }
            if (mat.HasProperty("_EmissiveColor")) mat.SetColor("_EmissiveColor", emission);
            if (mat.HasProperty("_EmissiveColorLDR")) mat.SetColor("_EmissiveColorLDR", emission);
            if (mat.HasProperty("_EmissiveIntensity")) mat.SetFloat("_EmissiveIntensity", Mathf.Max(0f, emissionIntensity));
            if (mat.HasProperty("_UseEmissiveIntensity")) mat.SetFloat("_UseEmissiveIntensity", emissionIntensity > 0f ? 1f : 0f);
            if (emissionIntensity > 0f) mat.EnableKeyword("_EMISSION");
            else mat.DisableKeyword("_EMISSION");
            renderer.sharedMaterial = mat;
        }

        private static void DestroyCollider(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
        }

        private static void StripPlacementPreview(GameObject root)
        {
            if (root == null)
                return;

            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform)
                    continue;

                if (component is Collider collider)
                {
                    collider.enabled = false;
                    DestroyUnityObject(collider);
                    continue;
                }

                if (component is NetworkObject ||
                    component is NetworkBehaviour ||
                    component is Rigidbody ||
                    component is VaultInteractor ||
                    component is CompanyStashKeypadInteractor ||
                    component is CompanyStashKeypadButton ||
                    IsNetworkTransformComponent(component))
                {
                    if (component is Behaviour behaviour)
                        behaviour.enabled = false;

                    DestroyUnityObject(component);
                }
            }
        }

        private static bool IsNetworkTransformComponent(Component component)
        {
            Type type = component?.GetType();
            while (type != null)
            {
                if (type.Name.IndexOf("NetworkTransform", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                type = type.BaseType;
            }

            return false;
        }

        private static void DestroyUnityObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}

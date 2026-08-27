using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.ShipSystems.Surveillance
{
    internal static partial class CCTVOperatorStation
    {
        private static CCTVStationPlacementEditor GetPlacementEditor()
        {
            if (_placementEditor == null)
                _placementEditor = new CCTVStationPlacementEditor();
            return _placementEditor;
        }

        internal static bool TryGetDebugPlacementTarget(string targetName, out Transform target, out string normalizedTarget)
        {
            normalizedTarget = NormalizePlacementTarget(targetName);
            target = null;

            switch (normalizedTarget)
            {
                case "throttle":
                    target = ResolveThrottleTransform();
                    return target != null;
                case "focus":
                    target = _focusViewAnchor;
                    return target != null;
                case "radar":
                    target = _radarViewAnchor;
                    return target != null;
                case "player":
                    target = PlayerRootAnchor;
                    return target != null;
                case "pose":
                    target = _operatorPoseAnchor;
                    return target != null;
                case "button":
                    if (_root != null)
                        CCTVAccessButton.Ensure(_root.transform);
                    target = CCTVAccessButton.Root;
                    return target != null;
                default:
                    return false;
            }
        }

        internal static void SaveDebugPlacement(string targetName, Transform target)
        {
            if (target == null)
                return;

            string normalized = NormalizePlacementTarget(targetName);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            StationPlacementRecord record = new StationPlacementRecord
            {
                target = normalized,
                localPosition = target.localPosition,
                localEuler = target.localEulerAngles,
                localScale = NormalizePlacementScale(target.localScale)
            };

            UpsertPlacement(record);
            // #582: the old code logged "Saved ..." whether or not anything
            // reached the disk. SavePlacementProfile now reads the file back and
            // only reports success when the record it was asked to persist is
            // actually in it.
            if (!SavePlacementProfile(normalized))
            {
                SurveillanceBootstrap.Log?.LogError(
                    $"[LethalCCTV] FAILED to save CCTV station placement target={normalized}; " +
                    $"the on-disk profile does not contain it. Path: {PlacementProfilePath}");
                return;
            }

            // Message level, not Info: the standard test profile runs
            // LogLevels = Fatal, Error, Warning, Message, so an Info line here
            // is invisible in exactly the runs that need to prove a save landed.
            SurveillanceBootstrap.Log?.LogMessage(
                $"[LethalCCTV] Saved CCTV station placement target={normalized} " +
                $"pos={FormatVector(record.localPosition)} euler={FormatVector(record.localEuler)} scale={FormatVector(record.localScale)}.");
        }

        internal static StationPlacementRecord GetDefaultPlacementRecord(string targetName, Transform target)
        {
            string normalized = NormalizePlacementTarget(targetName);
            switch (normalized)
            {
                case "throttle":
                    return CaptureDefaultThrottlePlacement(target);
                case "focus":
                    return new StationPlacementRecord
                    {
                        target = "focus",
                        localPosition = FocusLocalPosition,
                        localEuler = FocusLocalEuler,
                        localScale = Vector3.one
                    };
                case "radar":
                    return new StationPlacementRecord
                    {
                        target = "radar",
                        localPosition = RadarFocusLocalPosition,
                        localEuler = RadarFocusLocalEuler,
                        localScale = Vector3.one
                    };
                case "player":
                    return new StationPlacementRecord
                    {
                        target = "player",
                        localPosition = PlayerRootLocalPosition,
                        localEuler = PlayerRootLocalEuler,
                        localScale = Vector3.one
                    };
                case "pose":
                    return new StationPlacementRecord
                    {
                        target = "pose",
                        localPosition = OperatorPoseLocalPosition,
                        localEuler = OperatorPoseLocalEuler,
                        localScale = Vector3.one
                    };
                case "button":
                    return new StationPlacementRecord
                    {
                        target = "button",
                        localPosition = AccessButtonLocalPosition,
                        localEuler = AccessButtonLocalEuler,
                        localScale = AccessButtonLocalScale
                    };
                case "note":
                    return new StationPlacementRecord
                    {
                        target = "note",
                        localPosition = NoteLocalPosition,
                        localEuler = NoteLocalEuler,
                        localScale = NoteLocalScale
                    };
                default:
                    return null;
            }
        }

        /// <summary>
        /// Saved-or-default placement for the access button, applied by
        /// CCTVAccessButton.Ensure at spawn time (the button can spawn after
        /// ApplySavedStationPlacements ran, e.g. on a mid-day purchase).
        /// </summary>
        internal static void ApplyAccessButtonPlacement(Transform buttonRoot)
        {
            if (buttonRoot == null)
                return;

            if (!TryGetSavedPlacement("button", out StationPlacementRecord record) || record == null)
                record = GetDefaultPlacementRecord("button", buttonRoot);
            if (record != null)
                ApplyPlacementRecord(buttonRoot, record);
        }

        /// <summary>
        /// #582 — saved-or-default placement for the sticky note anchor, applied
        /// when the anchor is created. CCTVStickyNoteItem reads the anchor's world
        /// pose when it spawns the note, so this is what makes an authored pose the
        /// spawn default.
        /// </summary>
        internal static void ApplyNotePlacement(Transform noteAnchor)
        {
            if (noteAnchor == null)
                return;

            if (!TryGetSavedPlacement("note", out StationPlacementRecord record) || record == null)
                record = GetDefaultPlacementRecord("note", noteAnchor);
            if (record != null)
                ApplyPlacementRecord(noteAnchor, record);
        }

        /// <summary>
        /// #582 — ShipScopedPlacementHooks save handler, reached from the F9
        /// Y4NGZDebugTools ghost editor through Y4NGZCompany's authored-placement
        /// debug API. Adopts the ghost's world pose onto the note anchor (which
        /// is a child of the station root, so the stored record is ship-local
        /// and rides ship movement), persists it, and republishes the live note.
        /// The anchor's scale is deliberately left alone: the F9 flow has no
        /// scale input, so whatever was saved or defaulted stays authoritative.
        /// </summary>
        internal static string SaveShipScopedPlacement(string objectKind, string objectId, Transform source, string metadataJson)
        {
            if (!IsAuthoredNoteKind(objectKind))
                return null;
            if (source == null)
                return "save failed: no source transform";

            Transform anchor = EnsureNoteAnchor();
            if (anchor == null)
                return "save failed: CCTV station root not spawned yet (load into the ship first)";

            anchor.SetPositionAndRotation(source.position, source.rotation);
            SaveDebugPlacement("note", anchor);
            string respawn = CCTVStickyNoteItem.RespawnForAuthoring();
            return $"saved cctv note anchor local pos={FormatVector(anchor.localPosition)} euler={FormatVector(anchor.localEulerAngles)}; {respawn}";
        }

        /// <summary>#582 — ShipScopedPlacementHooks pose resolver: the anchor's
        /// current world pose (saved-or-default is applied when it is created,
        /// and edits keep it current), so the F9 ghost starts on the note.</summary>
        internal static bool TryGetShipScopedSavedPose(string objectKind, string objectId, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            if (!IsAuthoredNoteKind(objectKind))
                return false;

            Transform anchor = EnsureNoteAnchor();
            if (anchor == null)
                return false;

            worldPosition = anchor.position;
            worldRotation = anchor.rotation;
            return true;
        }

        /// <summary>#582 — ShipScopedPlacementHooks delete handler: removes the
        /// authored record, restores the code-default pose onto the anchor, and
        /// republishes the live note at that default.</summary>
        internal static string DeleteShipScopedPlacement(string objectKind, string objectId)
        {
            if (!IsAuthoredNoteKind(objectKind))
                return null;

            bool removed = RemovePlacement("note");
            Transform anchor = EnsureNoteAnchor();
            if (anchor != null)
                ApplyNotePlacement(anchor);

            if (!removed)
                return "no saved cctv note placement; anchor holds the code default";

            string respawn = CCTVStickyNoteItem.RespawnForAuthoring();
            return $"deleted the saved cctv note placement; anchor reset to the code default; {respawn}";
        }

        private static bool IsAuthoredNoteKind(string objectKind)
        {
            return string.Equals(
                Y4NGZCompany.Facility.Interior.Placement.Authored.AuthoredInteriorPlacementKinds.Normalize(objectKind),
                Y4NGZCompany.Facility.Interior.Placement.Authored.AuthoredInteriorPlacementKinds.CctvNote,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool RemovePlacement(string targetName)
        {
            EnsurePlacementProfileLoaded();
            string normalized = NormalizePlacementTarget(targetName);
            if (string.IsNullOrWhiteSpace(normalized) || _placementProfile?.placements == null)
                return false;

            bool removed = false;
            for (int i = _placementProfile.placements.Count - 1; i >= 0; i--)
            {
                StationPlacementRecord candidate = _placementProfile.placements[i];
                if (candidate != null &&
                    string.Equals(NormalizePlacementTarget(candidate.target), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _placementProfile.placements.RemoveAt(i);
                    removed = true;
                }
            }

            if (!removed)
                return false;

            // #582: a removal that never reached the disk is not a removal. The
            // caller reports "deleted ..." to the player from this return value,
            // so it must reflect the file, not just the in-memory list.
            if (!SavePlacementProfile())
            {
                SurveillanceBootstrap.Log?.LogError(
                    $"[LethalCCTV] FAILED to remove CCTV station placement target={normalized}; " +
                    $"the on-disk profile still disagrees with memory. Path: {PlacementProfilePath}");
                return false;
            }

            return true;
        }

        private static void ApplySavedStationPlacements()
        {
            // "note" is deliberately absent: the note anchor can be created later
            // than this pass (a mid-day purchase, or a debug station spawn), so
            // EnsureNoteAnchor applies ApplyNotePlacement at creation time instead.
            // Listing it here as well would just apply the same record twice.
            //
            // The station viewport is authored through the local placement editor.
            // Once saved, focus/radar placements must remain authoritative until
            // the player intentionally edits them again.
            ApplySavedPlacement("focus", _focusViewAnchor);
            if (HasSavedPlacement("radar"))
                ApplySavedPlacement("radar", _radarViewAnchor);
            else
                AimRadarAnchorFromFocusDefault();
            ApplySavedPlacement("player", _playerRootAnchor);
            ApplySavedPlacement("pose", _operatorPoseAnchor);
        }

        private static void ApplySavedPlacement(string targetName, Transform target)
        {
            if (target == null)
                return;

            string normalized = NormalizePlacementTarget(targetName);
            StationPlacementRecord record;
            if (!TryGetSavedPlacement(normalized, out record))
                record = GetDefaultPlacementRecord(normalized, target);

            ApplyPlacementRecord(target, record);
        }

        private static void ApplyPlacementRecord(Transform target, StationPlacementRecord record)
        {
            if (target == null || record == null)
                return;

            target.localPosition = record.localPosition;
            target.localRotation = Quaternion.Euler(record.localEuler);
            target.localScale = NormalizePlacementScale(record.localScale);
        }

        private static bool HasSavedPlacement(string targetName)
        {
            return TryGetSavedPlacement(targetName, out _);
        }

        internal static void SyncThrottleSecondaryTargetsToPrimary(Transform primary)
        {
            if (primary == null || !_defaultThrottlePlacementCaptured || _throttleSecondaryTargets.Count == 0)
                return;

            Quaternion rotationDelta = primary.rotation * Quaternion.Inverse(_defaultThrottleWorldRotation);
            Vector3 worldScaleRatio = DivideScale(primary.lossyScale, _defaultThrottleWorldScale);
            Vector3 localScaleRatio = DivideScale(primary.localScale, _defaultThrottleLocalScale);

            int count = Mathf.Min(
                _throttleSecondaryTargets.Count,
                Mathf.Min(
                    _throttleSecondaryDefaultWorld.Count,
                    Mathf.Min(_throttleSecondaryDefaultWorldRotations.Count, _throttleSecondaryDefaultLocalScales.Count)));

            for (int i = 0; i < count; i++)
            {
                Transform secondary = _throttleSecondaryTargets[i];
                if (secondary == null)
                    continue;

                Vector3 defaultRelative = Quaternion.Inverse(_defaultThrottleWorldRotation) *
                                          (_throttleSecondaryDefaultWorld[i] - _defaultThrottleWorldPosition);
                Vector3 targetPosition = primary.position + primary.rotation * Vector3.Scale(defaultRelative, worldScaleRatio);
                Quaternion targetRotation = rotationDelta * _throttleSecondaryDefaultWorldRotations[i];
                Vector3 targetLocalScale = NormalizePlacementScale(Vector3.Scale(_throttleSecondaryDefaultLocalScales[i], localScaleRatio));

                secondary.SetPositionAndRotation(targetPosition, targetRotation);
                secondary.localScale = targetLocalScale;
            }
        }

        private static Vector3 DivideScale(Vector3 numerator, Vector3 denominator)
        {
            return new Vector3(
                DivideScaleComponent(numerator.x, denominator.x),
                DivideScaleComponent(numerator.y, denominator.y),
                DivideScaleComponent(numerator.z, denominator.z));
        }

        private static float DivideScaleComponent(float numerator, float denominator)
        {
            if (Mathf.Abs(denominator) < 0.0001f || float.IsNaN(denominator) || float.IsInfinity(denominator))
                return 1f;
            if (float.IsNaN(numerator) || float.IsInfinity(numerator))
                return 1f;
            return Mathf.Clamp(numerator / denominator, 0.05f, 20f);
        }

        private static StationPlacementRecord CaptureDefaultThrottlePlacement(Transform throttle)
        {
            if (throttle == null)
                return null;

            if (!_defaultThrottlePlacementCaptured)
            {
                _defaultThrottleLocalPosition = throttle.localPosition;
                _defaultThrottleLocalEuler = throttle.localEulerAngles;
                _defaultThrottleLocalScale = NormalizePlacementScale(throttle.localScale);
                _defaultThrottleWorldPosition = throttle.position;
                _defaultThrottleWorldRotation = throttle.rotation;
                _defaultThrottleWorldScale = throttle.lossyScale;
                _defaultThrottlePlacementCaptured = true;
            }

            return new StationPlacementRecord
            {
                target = "throttle",
                localPosition = ComputeDefaultThrottleRelocatedLocalPosition(throttle),
                localEuler = _defaultThrottleLocalEuler,
                localScale = _defaultThrottleLocalScale
            };
        }

        private static Vector3 ComputeDefaultThrottleRelocatedLocalPosition(Transform throttle)
        {
            if (throttle == null)
                return _defaultThrottleLocalPosition;

            Vector3 worldOffset = _root != null
                ? -_root.transform.right * DefaultThrottleLeftOffsetMeters
                : Vector3.left * DefaultThrottleLeftOffsetMeters;

            Transform parent = throttle.parent;
            Vector3 desiredWorld = _defaultThrottleWorldPosition + worldOffset;
            return parent != null
                ? parent.InverseTransformPoint(desiredWorld)
                : desiredWorld;
        }

        private static string NormalizePlacementTarget(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return "focus";

            string lower = targetName.Trim().ToLowerInvariant();
            if (lower == "seat" || lower == "operator" || lower == "operatorchair")
                return "chair";
            if (lower == "stick" || lower == "joy" || lower == "joystick" || lower == "control")
                return "throttle";
            if (lower == "lever" || lower == "startlever" || lower == "shiplever" || lower == "startmatchlever" ||
                lower == "brake" || lower == "brakelever" || lower == "throttlelever" || lower == "gearstick" ||
                lower == "gear" || lower == "sticklever")
                return "throttle";
            if (lower == "view" || lower == "camera" || lower == "focusview" || lower == "focusanchor")
                return "focus";
            if (lower == "radar" || lower == "radarview" || lower == "radaranchor" ||
                lower == "radarfocus" || lower == "spaceview" || lower == "spacelook")
                return "radar";
            if (lower == "player" || lower == "playerroot" || lower == "playerrootanchor" ||
                lower == "thirdperson" || lower == "thirdpersonplayer" || lower == "thirdpersonmodel" ||
                lower == "playermodel" || lower == "model" || lower == "root")
                return "player";
            if (lower == "operatorpose" || lower == "poseanchor" || lower == "bodypose")
                return "pose";
            if (lower == "button" || lower == "accessbutton" || lower == "access" ||
                lower == "entrybutton" || lower == "cctvbutton" || lower == "redbutton" ||
                lower == "bigredbutton" || lower == "teleporterbutton")
                return "button";
            // Deliberately no "controls" alias: "control" already means the throttle
            // a few lines up, and a one-character gap between two different targets
            // is a trap, not a convenience.
            if (lower == "note" || lower == "stickynote" || lower == "sticky" ||
                lower == "noteanchor" || lower == "cctvnote")
                return "note";
            if (lower == "chair" || lower == "throttle" || lower == "focus" || lower == "radar" ||
                lower == "player" || lower == "pose" || lower == "button" || lower == "note")
                return lower;

            // #582: an unrecognized name used to be returned verbatim, so a typo
            // at the console silently minted a placement record for a target that
            // nothing ever applies — junk in the profile that looks authored.
            // Reject instead; every caller already treats null/blank as "no target".
            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV] Unknown CCTV station placement target '{targetName}'. " +
                "Known targets: chair, throttle, focus, radar, player, pose, button, note.");
            return null;
        }

        private static Vector3 NormalizePlacementScale(Vector3 scale)
        {
            float x = IsValidScaleComponent(scale.x) ? scale.x : 1f;
            float y = IsValidScaleComponent(scale.y) ? scale.y : x;
            float z = IsValidScaleComponent(scale.z) ? scale.z : x;
            return new Vector3(
                Mathf.Clamp(x, 0.05f, 5f),
                Mathf.Clamp(y, 0.05f, 5f),
                Mathf.Clamp(z, 0.05f, 5f));
        }

        private static bool IsValidScaleComponent(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static bool TryGetSavedPlacement(string targetName, out StationPlacementRecord record)
        {
            EnsurePlacementProfileLoaded();
            string normalized = NormalizePlacementTarget(targetName);
            record = null;

            // #582: a rejected target must never match a legacy junk record whose
            // own name also normalizes to null.
            if (string.IsNullOrWhiteSpace(normalized) || _placementProfile?.placements == null)
                return false;

            for (int i = 0; i < _placementProfile.placements.Count; i++)
            {
                StationPlacementRecord candidate = _placementProfile.placements[i];
                if (candidate == null)
                    continue;

                if (string.Equals(NormalizePlacementTarget(candidate.target), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    record = candidate;
                    record.target = normalized;
                    record.localScale = NormalizePlacementScale(record.localScale);
                    return true;
                }
            }

            return false;
        }

        private static void UpsertPlacement(StationPlacementRecord record)
        {
            EnsurePlacementProfileLoaded();
            if (_placementProfile.placements == null)
                _placementProfile.placements = new List<StationPlacementRecord>();

            record.target = NormalizePlacementTarget(record.target);
            if (string.IsNullOrWhiteSpace(record.target))
                return;
            record.localScale = NormalizePlacementScale(record.localScale);

            for (int i = 0; i < _placementProfile.placements.Count; i++)
            {
                StationPlacementRecord existing = _placementProfile.placements[i];
                if (existing != null &&
                    string.Equals(NormalizePlacementTarget(existing.target), record.target, StringComparison.OrdinalIgnoreCase))
                {
                    _placementProfile.placements[i] = record;
                    return;
                }
            }

            _placementProfile.placements.Add(record);
        }

        private static void EnsurePlacementProfileLoaded()
        {
            if (_placementProfileLoaded)
                return;

            _placementProfileLoaded = true;
            _placementProfile = new StationPlacementProfile();
            string path = PlacementProfilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            try
            {
                StationPlacementProfile loaded = ReadPlacementProfile(File.ReadAllText(path));
                if (loaded != null && loaded.placements != null)
                    _placementProfile = loaded;
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed reading CCTV station placement profile: {ex.Message}");
                _placementProfile = new StationPlacementProfile();
            }
        }

        /// <summary>
        /// #591 — explicit mapping in place of JsonUtility. Field names and order
        /// match what JsonUtility used to emit, so existing profiles keep loading.
        /// </summary>
        private static StationPlacementProfile ReadPlacementProfile(string json)
        {
            Core.Y4NGZJsonObject root = Core.Y4NGZJson.Parse(json);
            if (root == null)
                return null;

            StationPlacementProfile profile = new StationPlacementProfile();
            Core.Y4NGZJsonArray placements = root.GetArray("placements");
            if (placements == null)
                return profile;

            for (int i = 0; i < placements.Count; i++)
            {
                Core.Y4NGZJsonObject entry = placements.GetObject(i);
                if (entry == null)
                    continue;

                profile.placements.Add(new StationPlacementRecord
                {
                    target = entry.GetString("target"),
                    localPosition = entry.GetVector3("localPosition"),
                    localEuler = entry.GetVector3("localEuler"),
                    localScale = entry.GetVector3("localScale", Vector3.one)
                });
            }

            return profile;
        }

        private static string WritePlacementProfile(StationPlacementProfile profile)
        {
            Core.Y4NGZJsonObject root = new Core.Y4NGZJsonObject();
            Core.Y4NGZJsonArray placements = root.SetArray("placements");
            if (profile?.placements != null)
            {
                for (int i = 0; i < profile.placements.Count; i++)
                {
                    StationPlacementRecord record = profile.placements[i];
                    if (record == null)
                        continue;

                    Core.Y4NGZJsonObject entry = placements.AddObject();
                    entry.SetString("target", record.target);
                    entry.SetVector3("localPosition", record.localPosition);
                    entry.SetVector3("localEuler", record.localEuler);
                    entry.SetVector3("localScale", record.localScale);
                }
            }

            return Core.Y4NGZJson.Write(root);
        }

        /// <summary>
        /// #582 — writes the profile and then reads the file back, so callers can
        /// tell the player the truth. Returns true only when the parsed on-disk
        /// targets match the in-memory ones (and, when
        /// <paramref name="requiredTarget"/> is given, when that target is among
        /// them).
        /// </summary>
        private static bool SavePlacementProfile(string requiredTarget = null)
        {
            EnsurePlacementProfileLoaded();
            string path = PlacementProfilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(path, WritePlacementProfile(_placementProfile));
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning($"[LethalCCTV] Failed writing CCTV station placement profile: {ex.Message}");
                return false;
            }

            return VerifySavedPlacementProfile(path, requiredTarget);
        }

        private static bool VerifySavedPlacementProfile(string path, string requiredTarget)
        {
            StationPlacementProfile onDisk;
            try
            {
                onDisk = ReadPlacementProfile(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                SurveillanceBootstrap.Log?.LogWarning(
                    $"[LethalCCTV] Wrote the CCTV station placement profile but could not read it back: {ex.Message}");
                return false;
            }

            if (onDisk?.placements == null)
                return false;

            int expected = _placementProfile?.placements?.Count ?? 0;
            if (onDisk.placements.Count != expected)
                return false;

            if (string.IsNullOrWhiteSpace(requiredTarget))
                return true;

            for (int i = 0; i < onDisk.placements.Count; i++)
            {
                StationPlacementRecord candidate = onDisk.placements[i];
                if (candidate != null &&
                    string.Equals(NormalizePlacementTarget(candidate.target), requiredTarget, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string PlacementProfilePath =>
            Core.Y4NGZCompanyPaths.LocalDataFile("cctv-station-placements.local.json");

        private static StationPlacementProfile _placementProfile;
        private static bool _placementProfileLoaded;

        [Serializable]
        internal sealed class StationPlacementProfile
        {
            public List<StationPlacementRecord> placements = new List<StationPlacementRecord>();
        }

        [Serializable]
        internal sealed class StationPlacementRecord
        {
            public string target;
            public Vector3 localPosition;
            public Vector3 localEuler;
            public Vector3 localScale;
        }
    }
}

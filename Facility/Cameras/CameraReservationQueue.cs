using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Interior.Placement;

namespace Y4NGZCompany.Facility.Cameras
{
    /// <summary>
    /// #1271. Host-only queue for the CCTV cameras' fixture reservations.
    ///
    /// The camera pass runs synchronously inside OnFinishedGeneratingDungeon (#1283), so it
    /// finishes ahead of the host support pass. Claims made at that point would sit inside the
    /// strict tier's 25 m separation around every Mainframe and Company Stash candidate and push
    /// both into other rooms. Each claim is queued instead. <see cref="Commit"/> runs once, when
    /// <c>InteriorSupportSpawner</c> reports that its pass has settled, or at the landing if
    /// it has not. That keeps the order the sliced pass produced before #1271: supports first,
    /// then every camera, and all of them before the first contract fixture plans. A claim
    /// made after the commit goes straight to the registry. Clients never claim, and the
    /// round reset drops the queue.
    /// </summary>
    internal static class CameraReservationQueue
    {
        private const string Source = "cctv-camera-pipeline";

        private static readonly FixtureFootprint CameraFootprint = new FixtureFootprint(
            Vector3.forward,
            Vector3.back,
            Vector3.up,
            new Vector3(0.70f, 0.55f, 0.75f),
            0f,
            -0.275f,
            0.35f,
            0.20f);

        private static readonly List<Claim> Pending = new List<Claim>();
        private static bool _committed;

        /// <summary>One camera's claim. It is queued until the commit, then holds the
        /// registry handle when the reservation succeeded.</summary>
        internal sealed class Claim
        {
            internal readonly int CameraIndex;
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal FixtureReservationHandle Handle;

            internal Claim(int cameraIndex, Vector3 position, Quaternion rotation)
            {
                CameraIndex = cameraIndex;
                Position = position;
                Rotation = rotation;
            }
        }

        /// <summary>Host only. Queues the camera's claim, or reserves it at once when this
        /// round's claims are already committed.</summary>
        internal static Claim Reserve(int cameraIndex, Vector3 position, Quaternion rotation)
        {
            var claim = new Claim(cameraIndex, position, rotation);
            if (_committed)
                TryReserve(claim);
            else
                Pending.Add(claim);
            return claim;
        }

        /// <summary>Withdraws a claim, whether it is still queued or already reserved.</summary>
        internal static void Release(Claim claim)
        {
            if (claim == null)
                return;
            if (Pending.Remove(claim))
                return;
            if (claim.Handle.IsValid)
                FixtureReservationRegistry.Release(claim.Handle);
        }

        /// <summary>Reserves every queued claim in the order the cameras made them. Runs once
        /// per round; later calls return at once.</summary>
        internal static void Commit(string trigger)
        {
            if (_committed)
                return;

            _committed = true;
            int claims = Pending.Count;
            int conflicts = 0;
            for (int i = 0; i < claims; i++)
            {
                if (!TryReserve(Pending[i]))
                    conflicts++;
            }
            Pending.Clear();

            if (claims > 0)
            {
                SurveillanceBootstrap.Log?.LogInfo(
                    $"[LethalCCTV] Committed {claims} queued camera reservation(s): trigger={trigger} conflicts={conflicts}.");
            }
        }

        internal static void ResetRound()
        {
            Pending.Clear();
            _committed = false;
        }

        // A conflict keeps the camera: the peer-local camera list is deterministic and never
        // depends on host-only reservations.
        private static bool TryReserve(Claim claim)
        {
            if (FixtureReservationRegistry.TryReserve(
                    $"CCTV camera {claim.CameraIndex}",
                    Source,
                    CameraFootprint,
                    claim.Position,
                    claim.Rotation,
                    out claim.Handle,
                    out FixtureReservationConflict conflict))
            {
                return true;
            }

            SurveillanceBootstrap.Log?.LogWarning(
                $"[LethalCCTV] Camera reservation overlaps " +
                $"an existing fixture; retaining the deterministic camera list. cam={claim.CameraIndex} {conflict.ToDiagnosticString()}.");
            return false;
        }
    }
}

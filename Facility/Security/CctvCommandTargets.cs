using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Facility.Security
{
    public sealed class CctvCommandTargetDescriptor
    {
        public Component Target { get; set; }
        public string DisplayName { get; set; }
        public string CommandHint { get; set; }
        public Vector3 Position { get; set; }
        public float Radius { get; set; }
    }

    public sealed class CctvCommandExecutionResult
    {
        public bool Success { get; set; }
        public string StatusText { get; set; }

        public static CctvCommandExecutionResult Fail(string statusText)
        {
            return new CctvCommandExecutionResult
            {
                Success = false,
                StatusText = string.IsNullOrWhiteSpace(statusText) ? "INVALID COMMAND" : statusText,
            };
        }

        public static CctvCommandExecutionResult Succeed(string statusText)
        {
            return new CctvCommandExecutionResult
            {
                Success = true,
                StatusText = string.IsNullOrWhiteSpace(statusText) ? "COMMAND ACCEPTED" : statusText,
            };
        }
    }

    public abstract class CctvCommandableFixture : NetworkBehaviour
    {
        private static readonly HashSet<CctvCommandableFixture> ActiveFixtures = new HashSet<CctvCommandableFixture>();

        public virtual bool IsCctvTargetAvailable => isActiveAndEnabled && gameObject.activeInHierarchy;
        public virtual string CctvDisplayName => "SUPPORT NODE";
        public virtual string CctvCommandHint => "activate";
        public virtual Vector3 CctvFocusPosition => transform.position;
        public virtual float CctvFocusRadius => 0.9f;

        public abstract CctvCommandExecutionResult ExecuteCctvCommand(string command);

        protected virtual void OnEnable()
        {
            ActiveFixtures.Add(this);
        }

        protected virtual void OnDisable()
        {
            ActiveFixtures.Remove(this);
        }

        public override void OnDestroy()
        {
            ActiveFixtures.Remove(this);
            base.OnDestroy();
        }

        internal static IEnumerable<CctvCommandableFixture> EnumerateActiveFixtures()
        {
            foreach (CctvCommandableFixture fixture in ActiveFixtures)
            {
                if (fixture != null && fixture.IsCctvTargetAvailable)
                    yield return fixture;
            }
        }
    }

    public static class CctvCommandTargetApi
    {
        public static CctvCommandTargetDescriptor DescribeTarget(Component component)
        {
            CctvCommandableFixture fixture = ResolveFixture(component);
            return fixture != null ? CreateDescriptor(fixture) : null;
        }

        public static List<CctvCommandTargetDescriptor> GetActiveTargets()
        {
            var targets = new List<CctvCommandTargetDescriptor>();
            foreach (CctvCommandableFixture fixture in CctvCommandableFixture.EnumerateActiveFixtures())
                targets.Add(CreateDescriptor(fixture));
            return targets;
        }

        public static CctvCommandExecutionResult ExecuteCommand(Component component, string command)
        {
            if (string.Equals(command?.Trim(), "hack", System.StringComparison.OrdinalIgnoreCase) &&
                SurveillanceBootstrap.Config != null &&
                !SurveillanceBootstrap.Config.AllowRemoteHacking.Value)
            {
                return CctvCommandExecutionResult.Fail("REMOTE HACKING DISABLED BY HOST");
            }

            CctvCommandableFixture fixture = ResolveFixture(component);
            if (fixture == null)
                return CctvCommandExecutionResult.Fail("TARGET LOST");

            return fixture.ExecuteCctvCommand(command ?? string.Empty) ?? CctvCommandExecutionResult.Fail("NO RESPONSE");
        }

        private static CctvCommandableFixture ResolveFixture(Component component)
        {
            if (component == null)
                return null;

            return component as CctvCommandableFixture
                   ?? component.GetComponent<CctvCommandableFixture>()
                   ?? component.GetComponentInParent<CctvCommandableFixture>();
        }

        private static CctvCommandTargetDescriptor CreateDescriptor(CctvCommandableFixture fixture)
        {
            return new CctvCommandTargetDescriptor
            {
                Target = fixture,
                DisplayName = string.IsNullOrWhiteSpace(fixture.CctvDisplayName) ? "SUPPORT NODE" : fixture.CctvDisplayName,
                CommandHint = fixture.CctvCommandHint ?? string.Empty,
                Position = fixture.CctvFocusPosition,
                Radius = Mathf.Clamp(fixture.CctvFocusRadius, 0.35f, 4f),
            };
        }
    }
}

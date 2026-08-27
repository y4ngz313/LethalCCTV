using System;
using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Facility.Security
{
    public sealed class CctvHackingGame
    {
        private const int OriginNodeId = 0;

        private readonly System.Random _random;
        private readonly List<SignalNode> _nodes = new List<SignalNode>(12);
        private readonly List<SignalEdge> _edges = new List<SignalEdge>(16);
        private readonly HashSet<int> _requiredTargets = new HashSet<int>();
        private readonly HashSet<int> _completedTargets = new HashSet<int>();

        private float _pulseUnitsPerSecond;
        private float _traceSeconds;
        private int _selectedNodeId;
        private int _fromNodeId;
        private int _toNodeId;
        private float _pulseT;
        private float _trace;
        private bool _solved;
        private bool _locked;

        public IReadOnlyList<SignalNode> Nodes => _nodes;
        public IReadOnlyList<SignalEdge> Edges => _edges;
        public SignalSpliceDifficulty Difficulty { get; }
        public string DifficultyLabel => FormatDifficulty(Difficulty);
        public string MoonRiskLabel { get; }
        public int OriginId => OriginNodeId;
        public int SelectedNodeId => _selectedNodeId;
        public int FromNodeId => _fromNodeId;
        public int ToNodeId => _toNodeId;
        public float PulseT => _pulseT;
        public float TraceNormalized => _trace;
        public bool Solved => _solved;
        public bool Locked => _locked;
        public int RequiredTargetCount => _requiredTargets.Count;
        public int CompletedTargetCount => _completedTargets.Count;
        public bool MustReturnToOrigin => CompletedTargetCount >= RequiredTargetCount;

        public CctvHackingGame(int seed)
        {
            _random = new System.Random(seed);
            Difficulty = ResolveCurrentMoonDifficulty(out string riskLabel);
            MoonRiskLabel = riskLabel;
            ApplyDifficultySettings(Difficulty);
            GenerateGraph(Difficulty);
            RandomizeRelayStates();
            ResetPulse();
        }

        public SignalSpliceUpdateResult Update(float deltaTime)
        {
            if (_solved) return SignalSpliceUpdateResult.Solved;
            if (_locked) return SignalSpliceUpdateResult.TraceLocked;

            float dt = Mathf.Max(0f, deltaTime);
            _trace = Mathf.Clamp01(_trace + dt / Mathf.Max(1f, _traceSeconds));
            if (_trace >= 1f)
            {
                _locked = true;
                return SignalSpliceUpdateResult.TraceLocked;
            }

            SignalSpliceUpdateResult result = SignalSpliceUpdateResult.None;
            int safety = 0;
            _pulseT += (dt * _pulseUnitsPerSecond) / Mathf.Max(0.06f, DistanceBetween(_fromNodeId, _toNodeId));
            while (_pulseT >= 1f && safety++ < 6 && !_solved && !_locked)
            {
                _pulseT -= 1f;
                SignalSpliceUpdateResult stepResult = ArriveAtNode();
                if (stepResult != SignalSpliceUpdateResult.None)
                    result = stepResult;
            }

            return result;
        }

        public bool MoveSelection(Vector2 direction)
        {
            if (_solved || _locked || direction.sqrMagnitude < 0.001f)
                return false;

            SignalNode current = GetNode(_selectedNodeId);
            if (current == null)
                return false;

            direction.Normalize();
            int bestId = -1;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < _nodes.Count; i++)
            {
                SignalNode candidate = _nodes[i];
                if (candidate == null || candidate.Id == current.Id || !CanRotateNode(candidate.Id))
                    continue;

                Vector2 offset = candidate.Position - current.Position;
                float distance = offset.magnitude;
                if (distance <= 0.001f)
                    continue;

                float dot = Vector2.Dot(offset / distance, direction);
                if (dot < 0.24f)
                    continue;

                float score = dot * 2.25f - distance;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = candidate.Id;
                }
            }

            if (bestId < 0)
                return false;

            _selectedNodeId = bestId;
            return true;
        }

        public bool RotateSelectedRelay()
        {
            if (_solved || _locked)
                return false;

            SignalNode node = GetNode(_selectedNodeId);
            if (node == null || node.NeighborCount <= 1 || node.IsTarget)
                return false;

            node.ActiveNeighborIndex = PositiveModulo(node.ActiveNeighborIndex + 1, node.NeighborCount);
            return true;
        }

        public bool CanRotateNode(int nodeId)
        {
            SignalNode node = GetNode(nodeId);
            return node != null && !node.IsTarget && node.NeighborCount > 1;
        }

        public bool IsRequiredTarget(int nodeId)
        {
            return _requiredTargets.Contains(nodeId);
        }

        public bool IsTargetCompleted(int nodeId)
        {
            return _completedTargets.Contains(nodeId);
        }

        public bool IsCurrentSegment(int first, int second)
        {
            return (_fromNodeId == first && _toNodeId == second) ||
                   (_fromNodeId == second && _toNodeId == first);
        }

        public Vector2 GetPulsePosition()
        {
            SignalNode from = GetNode(_fromNodeId);
            SignalNode to = GetNode(_toNodeId);
            if (from == null) return Vector2.zero;
            if (to == null) return from.Position;
            return Vector2.Lerp(from.Position, to.Position, Mathf.Clamp01(_pulseT));
        }

        public SignalNode GetNode(int nodeId)
        {
            if (nodeId >= 0 && nodeId < _nodes.Count && _nodes[nodeId].Id == nodeId)
                return _nodes[nodeId];

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i] != null && _nodes[i].Id == nodeId)
                    return _nodes[i];
            }
            return null;
        }

        private SignalSpliceUpdateResult ArriveAtNode()
        {
            int arrivedNodeId = _toNodeId;
            int cameFromNodeId = _fromNodeId;
            SignalNode arrived = GetNode(arrivedNodeId);
            if (arrived == null)
            {
                ResetPulse();
                return SignalSpliceUpdateResult.None;
            }

            if (arrivedNodeId == OriginNodeId)
            {
                if (MustReturnToOrigin)
                {
                    _pulseT = 1f;
                    _solved = true;
                    return SignalSpliceUpdateResult.Solved;
                }

                _fromNodeId = arrivedNodeId;
                _toNodeId = ResolveOutgoingNode(arrived, cameFromNodeId);
                return SignalSpliceUpdateResult.ReturnedToOrigin;
            }

            if (_requiredTargets.Contains(arrivedNodeId) && !_completedTargets.Contains(arrivedNodeId))
            {
                _completedTargets.Add(arrivedNodeId);
                _fromNodeId = arrivedNodeId;
                _toNodeId = cameFromNodeId;
                return SignalSpliceUpdateResult.TargetHit;
            }

            _fromNodeId = arrivedNodeId;
            _toNodeId = ResolveOutgoingNode(arrived, cameFromNodeId);
            return SignalSpliceUpdateResult.None;
        }

        private int ResolveOutgoingNode(SignalNode node, int cameFromNodeId)
        {
            if (node == null || node.NeighborCount <= 0)
                return Mathf.Max(OriginNodeId, cameFromNodeId);

            int active = node.GetActiveNeighbor();
            if (active >= 0 && active != node.Id)
                return active;

            if (cameFromNodeId >= 0)
                return cameFromNodeId;

            return node.Neighbors[0];
        }

        private void ResetPulse()
        {
            _selectedNodeId = FindFirstRotatableNode();
            _fromNodeId = OriginNodeId;
            SignalNode origin = GetNode(OriginNodeId);
            _toNodeId = origin != null && origin.NeighborCount > 0 ? origin.Neighbors[0] : OriginNodeId;
            _pulseT = 0f;
            _trace = 0f;
            _solved = false;
            _locked = false;
        }

        private int FindFirstRotatableNode()
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (CanRotateNode(_nodes[i].Id))
                    return _nodes[i].Id;
            }
            return OriginNodeId;
        }

        private void ApplyDifficultySettings(SignalSpliceDifficulty difficulty)
        {
            switch (difficulty)
            {
                case SignalSpliceDifficulty.Easy:
                    _pulseUnitsPerSecond = 0.18f;
                    _traceSeconds = 62f;
                    break;
                case SignalSpliceDifficulty.Normal:
                    _pulseUnitsPerSecond = 0.20f;
                    _traceSeconds = 54f;
                    break;
                case SignalSpliceDifficulty.Hard:
                    _pulseUnitsPerSecond = 0.23f;
                    _traceSeconds = 46f;
                    break;
                case SignalSpliceDifficulty.Brutal:
                    _pulseUnitsPerSecond = 0.26f;
                    _traceSeconds = 38f;
                    break;
                default:
                    _pulseUnitsPerSecond = 0.20f;
                    _traceSeconds = 54f;
                    break;
            }
        }

        private void GenerateGraph(SignalSpliceDifficulty difficulty)
        {
            _nodes.Clear();
            _edges.Clear();
            _requiredTargets.Clear();
            _completedTargets.Clear();

            switch (difficulty)
            {
                case SignalSpliceDifficulty.Easy:
                    BuildEasyGraph();
                    break;
                case SignalSpliceDifficulty.Hard:
                    BuildHardGraph();
                    break;
                case SignalSpliceDifficulty.Brutal:
                    BuildBrutalGraph();
                    break;
                default:
                    BuildNormalGraph();
                    break;
            }
        }

        private void BuildEasyGraph()
        {
            AddNode(0, 0.12f, 0.50f, origin: true);
            AddNode(1, 0.32f, 0.50f);
            AddNode(2, 0.55f, 0.50f);
            AddNode(3, 0.82f, 0.50f, targetSlot: 1);
            AddNode(4, 0.55f, 0.72f);

            AddEdge(0, 1);
            AddEdge(1, 2);
            AddEdge(2, 3);
            AddEdge(1, 4);
            AddEdge(4, 2);
        }

        private void BuildNormalGraph()
        {
            AddNode(0, 0.10f, 0.50f, origin: true);
            AddNode(1, 0.26f, 0.50f);
            AddNode(2, 0.44f, 0.68f);
            AddNode(3, 0.44f, 0.32f);
            AddNode(4, 0.62f, 0.50f);
            AddNode(5, 0.84f, 0.72f, targetSlot: 1);
            AddNode(6, 0.84f, 0.28f, targetSlot: 2);

            AddEdge(0, 1);
            AddEdge(1, 2);
            AddEdge(1, 3);
            AddEdge(2, 4);
            AddEdge(3, 4);
            AddEdge(2, 5);
            AddEdge(3, 6);
            AddEdge(4, 5);
            AddEdge(4, 6);
        }

        private void BuildHardGraph()
        {
            AddNode(0, 0.08f, 0.50f, origin: true);
            AddNode(1, 0.23f, 0.50f);
            AddNode(2, 0.38f, 0.70f);
            AddNode(3, 0.38f, 0.30f);
            AddNode(4, 0.56f, 0.62f);
            AddNode(5, 0.56f, 0.38f);
            AddNode(6, 0.82f, 0.78f, targetSlot: 1);
            AddNode(7, 0.88f, 0.50f, targetSlot: 2);
            AddNode(8, 0.82f, 0.22f, targetSlot: 3);

            AddEdge(0, 1);
            AddEdge(1, 2);
            AddEdge(1, 3);
            AddEdge(2, 4);
            AddEdge(3, 5);
            AddEdge(4, 5);
            AddEdge(4, 6);
            AddEdge(4, 7);
            AddEdge(5, 7);
            AddEdge(5, 8);
            AddEdge(2, 5);
            AddEdge(3, 4);
        }

        private void BuildBrutalGraph()
        {
            AddNode(0, 0.07f, 0.50f, origin: true);
            AddNode(1, 0.20f, 0.50f);
            AddNode(2, 0.34f, 0.72f);
            AddNode(3, 0.34f, 0.28f);
            AddNode(4, 0.50f, 0.64f);
            AddNode(5, 0.50f, 0.36f);
            AddNode(6, 0.66f, 0.50f);
            AddNode(7, 0.84f, 0.82f, targetSlot: 1);
            AddNode(8, 0.91f, 0.58f, targetSlot: 2);
            AddNode(9, 0.91f, 0.42f, targetSlot: 3);
            AddNode(10, 0.84f, 0.18f, targetSlot: 4);

            AddEdge(0, 1);
            AddEdge(1, 2);
            AddEdge(1, 3);
            AddEdge(2, 4);
            AddEdge(3, 5);
            AddEdge(4, 6);
            AddEdge(5, 6);
            AddEdge(4, 7);
            AddEdge(6, 8);
            AddEdge(6, 9);
            AddEdge(5, 10);
            AddEdge(2, 5);
            AddEdge(3, 4);
        }

        private void AddNode(int id, float x, float y, bool origin = false, int targetSlot = 0)
        {
            while (_nodes.Count <= id)
                _nodes.Add(null);

            var node = new SignalNode(id, new Vector2(x, y), origin, targetSlot);
            _nodes[id] = node;
            if (targetSlot > 0)
                _requiredTargets.Add(id);
        }

        private void AddEdge(int first, int second)
        {
            SignalNode a = GetNode(first);
            SignalNode b = GetNode(second);
            if (a == null || b == null)
                return;

            a.AddNeighbor(second);
            b.AddNeighbor(first);
            _edges.Add(new SignalEdge(first, second));
        }

        private void RandomizeRelayStates()
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                SignalNode node = _nodes[i];
                if (node == null || node.NeighborCount <= 0)
                    continue;

                if (node.IsOrigin)
                {
                    node.ActiveNeighborIndex = 0;
                    continue;
                }

                node.ActiveNeighborIndex = node.NeighborCount > 1 ? _random.Next(0, node.NeighborCount) : 0;
            }
        }

        private float DistanceBetween(int first, int second)
        {
            SignalNode a = GetNode(first);
            SignalNode b = GetNode(second);
            if (a == null || b == null)
                return 0.12f;
            return Vector2.Distance(a.Position, b.Position);
        }

        public static SignalSpliceDifficulty ResolveCurrentMoonDifficulty(out string riskLabel)
        {
            string risk = StartOfRound.Instance?.currentLevel?.riskLevel ?? string.Empty;
            riskLabel = string.IsNullOrWhiteSpace(risk) ? "UNKNOWN" : risk.Trim();

            char tier = ResolveRiskTier(risk);
            switch (tier)
            {
                case 'S':
                    return SignalSpliceDifficulty.Brutal;
                case 'A':
                    return SignalSpliceDifficulty.Hard;
                case 'B':
                    return SignalSpliceDifficulty.Normal;
                case 'C':
                case 'D':
                    return SignalSpliceDifficulty.Easy;
                default:
                    return SignalSpliceDifficulty.Normal;
            }
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

        private static string FormatDifficulty(SignalSpliceDifficulty difficulty)
        {
            switch (difficulty)
            {
                case SignalSpliceDifficulty.Easy:
                    return "EASY";
                case SignalSpliceDifficulty.Hard:
                    return "HARD";
                case SignalSpliceDifficulty.Brutal:
                    return "BRUTAL";
                default:
                    return "NORMAL";
            }
        }

        private static int PositiveModulo(int value, int modulus)
        {
            if (modulus <= 0) return 0;
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        public sealed class SignalNode
        {
            private readonly List<int> _neighbors = new List<int>(4);

            public int Id { get; }
            public Vector2 Position { get; }
            public bool IsOrigin { get; }
            public bool IsTarget => TargetSlot > 0;
            public int TargetSlot { get; }
            public int ActiveNeighborIndex { get; internal set; }
            public IReadOnlyList<int> Neighbors => _neighbors;
            public int NeighborCount => _neighbors.Count;

            internal SignalNode(int id, Vector2 position, bool isOrigin, int targetSlot)
            {
                Id = id;
                Position = position;
                IsOrigin = isOrigin;
                TargetSlot = Mathf.Max(0, targetSlot);
            }

            internal void AddNeighbor(int nodeId)
            {
                if (!_neighbors.Contains(nodeId))
                    _neighbors.Add(nodeId);
            }

            public int GetActiveNeighbor()
            {
                if (_neighbors.Count <= 0)
                    return -1;

                int index = PositiveModulo(ActiveNeighborIndex, _neighbors.Count);
                return _neighbors[index];
            }
        }
    }

    public enum SignalSpliceDifficulty
    {
        Easy,
        Normal,
        Hard,
        Brutal
    }

    public enum SignalSpliceUpdateResult
    {
        None,
        TargetHit,
        ReturnedToOrigin,
        Solved,
        TraceLocked
    }

    public readonly struct SignalEdge
    {
        public readonly int From;
        public readonly int To;

        public SignalEdge(int from, int to)
        {
            From = from;
            To = to;
        }
    }
}

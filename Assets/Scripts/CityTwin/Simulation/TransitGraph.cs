using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityTwin.Simulation
{
    /// <summary>Transit graph for shortest-path (Dijkstra). Nodes = stops/hubs, edges = segments with lengths.</summary>
    public class TransitGraph
    {
        private readonly List<TransitNode> _nodes = new List<TransitNode>();
        private readonly List<TransitEdge> _edges = new List<TransitEdge>();
        private readonly Dictionary<int, List<TransitEdge>> _outgoing = new Dictionary<int, List<TransitEdge>>();
        private readonly List<TransitStop> _stops = new List<TransitStop>();

        public IReadOnlyList<TransitNode> Nodes => _nodes;
        public IReadOnlyList<TransitEdge> Edges => _edges;
        public IReadOnlyList<TransitStop> Stops => _stops;

        public struct TransitNode
        {
            public int Id;
            public Vector2 Position;
            public float Population;
        }

        public struct TransitEdge
        {
            public int FromId;
            public int ToId;
            public float Length;
        }

        public struct TransitStop
        {
            public Vector2 Position;
            public int EdgeFromId;
            public int EdgeToId;
        }

        public void Clear()
        {
            _nodes.Clear();
            _edges.Clear();
            _outgoing.Clear();
            _stops.Clear();
        }

        public int AddNode(Vector2 position, float population = 0f)
        {
            int id = _nodes.Count;
            _nodes.Add(new TransitNode { Id = id, Position = position, Population = population });
            _outgoing[id] = new List<TransitEdge>();
            return id;
        }

        public void AddEdge(int fromId, int toId, float length)
        {
            if (fromId < 0 || fromId >= _nodes.Count || toId < 0 || toId >= _nodes.Count)
                return;
            var edge = new TransitEdge { FromId = fromId, ToId = toId, Length = length };
            _edges.Add(edge);
            _outgoing[fromId].Add(edge);
        }

        /// <summary>
        /// Generate transit stops along edges at regular intervals.
        /// Matches HTML logic: evenly spaced along each edge, rejected if too close to a node or another stop.
        /// Optional spacingJitter (0..1) randomizes per-stop offset along segment; removalRate (0..1) drops
        /// random stops to mimic real-world sparsity. Pass a non-negative seed for reproducible layouts.
        /// </summary>
        public void GenerateStops(float spacing = 60f, float minDistFromNode = 30f, float minDistBetweenStops = 30f,
                                  float spacingJitter = 0f, float removalRate = 0f, int seed = -1)
        {
            _stops.Clear();
            if (_edges.Count == 0 || _nodes.Count < 2) return;

            spacingJitter = Mathf.Clamp01(spacingJitter);
            removalRate = Mathf.Clamp01(removalRate);
            var rng = seed >= 0 ? new System.Random(seed) : new System.Random();

            // Deduplicate edges (A->B and B->A are the same road segment)
            var processed = new HashSet<long>();

            foreach (var edge in _edges)
            {
                int lo = Mathf.Min(edge.FromId, edge.ToId);
                int hi = Mathf.Max(edge.FromId, edge.ToId);
                long key = ((long)lo << 32) | (uint)hi;
                if (!processed.Add(key)) continue;

                Vector2 a = GetNode(edge.FromId).Position;
                Vector2 b = GetNode(edge.ToId).Position;
                float segLen = Vector2.Distance(a, b);

                int count = Mathf.FloorToInt(segLen / spacing);
                for (int j = 1; j <= count; j++)
                {
                    float baseT = (float)j / (count + 1);
                    // Jitter the slot's t-parameter inside its own half-slot so stops never swap order.
                    float slotHalf = 0.5f / (count + 1);
                    float jitterT = spacingJitter > 0f
                        ? ((float)rng.NextDouble() * 2f - 1f) * slotHalf * spacingJitter
                        : 0f;
                    float t = Mathf.Clamp01(baseT + jitterT);
                    Vector2 pos = Vector2.Lerp(a, b, t);

                    // Random removal — mimics real-world stop sparsity, seeded for reproducibility.
                    if (removalRate > 0f && rng.NextDouble() < removalRate) continue;

                    // Reject if too close to any node
                    bool tooCloseToNode = false;
                    foreach (var node in _nodes)
                    {
                        if (Vector2.Distance(pos, node.Position) < minDistFromNode)
                        {
                            tooCloseToNode = true;
                            break;
                        }
                    }
                    if (tooCloseToNode) continue;

                    // Reject if too close to existing stop
                    bool tooCloseToStop = false;
                    foreach (var stop in _stops)
                    {
                        if (Vector2.Distance(pos, stop.Position) < minDistBetweenStops)
                        {
                            tooCloseToStop = true;
                            break;
                        }
                    }
                    if (tooCloseToStop) continue;

                    _stops.Add(new TransitStop
                    {
                        Position = pos,
                        EdgeFromId = edge.FromId,
                        EdgeToId = edge.ToId
                    });
                }
            }
        }

        /// <summary>
        /// Sprinkle stops along an arbitrary polyline (e.g. a road extension drawn beyond a hub)
        /// using the same spacing/jitter/rejection rules as GenerateStops. Stops enter the graph
        /// at entryNodeId — the hub the extension hangs off — so pathfinding rides the extension
        /// into the network. Appends to the stop list; call after GenerateStops.
        /// </summary>
        public void GenerateStopsAlongPath(IReadOnlyList<Vector2> path, int entryNodeId,
                                           float spacing = 60f, float minDistFromNode = 30f, float minDistBetweenStops = 30f,
                                           float spacingJitter = 0f, float removalRate = 0f, int seed = -1)
        {
            if (path == null || path.Count < 2) return;
            if (entryNodeId < 0 || entryNodeId >= _nodes.Count) return;

            spacingJitter = Mathf.Clamp01(spacingJitter);
            removalRate = Mathf.Clamp01(removalRate);
            var rng = seed >= 0 ? new System.Random(seed) : new System.Random();

            float totalLen = 0f;
            for (int i = 1; i < path.Count; i++)
                totalLen += Vector2.Distance(path[i - 1], path[i]);
            if (totalLen < spacing) return;

            int count = Mathf.FloorToInt(totalLen / spacing);
            for (int j = 1; j <= count; j++)
            {
                float baseT = (float)j / (count + 1);
                float slotHalf = 0.5f / (count + 1);
                float jitterT = spacingJitter > 0f
                    ? ((float)rng.NextDouble() * 2f - 1f) * slotHalf * spacingJitter
                    : 0f;
                float t = Mathf.Clamp01(baseT + jitterT);
                Vector2 pos = PointAlongPath(path, totalLen * t);

                if (removalRate > 0f && rng.NextDouble() < removalRate) continue;
                if (IsTooCloseToNodeOrStop(pos, minDistFromNode, minDistBetweenStops)) continue;

                _stops.Add(new TransitStop
                {
                    Position = pos,
                    EdgeFromId = entryNodeId,
                    EdgeToId = entryNodeId
                });
            }
        }

        private bool IsTooCloseToNodeOrStop(Vector2 pos, float minDistFromNode, float minDistBetweenStops)
        {
            foreach (var node in _nodes)
                if (Vector2.Distance(pos, node.Position) < minDistFromNode) return true;
            foreach (var stop in _stops)
                if (Vector2.Distance(pos, stop.Position) < minDistBetweenStops) return true;
            return false;
        }

        private static Vector2 PointAlongPath(IReadOnlyList<Vector2> path, float dist)
        {
            for (int i = 1; i < path.Count; i++)
            {
                float seg = Vector2.Distance(path[i - 1], path[i]);
                if (dist <= seg || i == path.Count - 1)
                    return Vector2.Lerp(path[i - 1], path[i], seg > 0.0001f ? Mathf.Clamp01(dist / seg) : 0f);
                dist -= seg;
            }
            return path[path.Count - 1];
        }

        /// <summary>Shortest path from startId to all nodes. Returns distances (key = node id, value = distance). Use float.MaxValue for unreachable.</summary>
        public Dictionary<int, float> Dijkstra(int startId)
        {
            var dist = new Dictionary<int, float>();
            var pq = new SortedSet<(float d, int id)>(Comparer<(float d, int id)>.Create((a, b) =>
            {
                int c = a.d.CompareTo(b.d);
                return c != 0 ? c : a.id.CompareTo(b.id);
            }));

            foreach (var n in _nodes)
                dist[n.Id] = float.MaxValue;
            dist[startId] = 0;
            pq.Add((0, startId));

            while (pq.Count > 0)
            {
                var (d, u) = pq.Min;
                pq.Remove(pq.Min);
                if (d > dist[u]) continue;
                if (!_outgoing.TryGetValue(u, out var edges)) continue;
                foreach (var e in edges)
                {
                    float alt = dist[u] + e.Length;
                    if (alt < dist[e.ToId])
                    {
                        dist[e.ToId] = alt;
                        pq.Add((alt, e.ToId));
                    }
                }
            }
            return dist;
        }

        /// <summary>Shortest path distance from fromId to toId. Returns float.MaxValue if unreachable.</summary>
        public float ShortestPathDistance(int fromId, int toId)
        {
            var dist = Dijkstra(fromId);
            return dist.TryGetValue(toId, out float d) ? d : float.MaxValue;
        }

        public TransitNode GetNode(int id)
        {
            return id >= 0 && id < _nodes.Count ? _nodes[id] : default;
        }

        /// <summary>Returns the Id of the transit node nearest to the given world point, or -1 if the graph has no nodes.</summary>
        public int NearestNodeId(Vector2 point)
        {
            int bestId = -1;
            float bestDist = float.MaxValue;
            foreach (var n in _nodes)
            {
                float d = Vector2.Distance(point, n.Position);
                if (d < bestDist) { bestDist = d; bestId = n.Id; }
            }
            return bestId;
        }

        /// <summary>Distance from point to nearest road segment (edge). Used for HTML-style "connected to road" check.</summary>
        public float DistanceToNearestSegment(Vector2 point)
        {
            float best = float.MaxValue;
            foreach (var e in _edges)
            {
                var a = GetNode(e.FromId).Position;
                var b = GetNode(e.ToId).Position;
                float d = DistancePointToSegment(point, a, b);
                if (d < best) best = d;
            }
            return _edges.Count == 0 ? float.MaxValue : best;
        }

        public struct ConnectionPoint
        {
            public Vector2 Position;
            public float Distance;
            public int EdgeIndex;
        }

        /// <summary>Find all road segment snap-points within range of buildingPos, deduplicated by 20-unit threshold.</summary>
        public List<ConnectionPoint> GetRoadConnections(Vector2 buildingPos, float range)
        {
            var results = new List<ConnectionPoint>();
            const float deduplicationThreshold = 20f;

            for (int i = 0; i < _edges.Count; i++)
            {
                var e = _edges[i];
                var a = GetNode(e.FromId).Position;
                var b = GetNode(e.ToId).Position;
                Vector2 closest = ClosestPointOnSegment(buildingPos, a, b);
                float dist = Vector2.Distance(buildingPos, closest);

                if (dist >= range) continue;

                bool isDuplicate = false;
                for (int j = 0; j < results.Count; j++)
                {
                    if (Vector2.Distance(results[j].Position, closest) < deduplicationThreshold)
                    {
                        isDuplicate = true;
                        break;
                    }
                }
                if (isDuplicate) continue;

                results.Add(new ConnectionPoint
                {
                    Position = closest,
                    Distance = dist,
                    EdgeIndex = i
                });
            }

            results.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            return results;
        }

        public struct StopConnectionPoint
        {
            public Vector2 Position;
            public float Distance;
            public int StopIndex;
        }

        /// <summary>Find all stops within range of buildingPos, sorted by distance.</summary>
        public List<StopConnectionPoint> GetStopConnections(Vector2 buildingPos, float range)
        {
            var results = new List<StopConnectionPoint>();
            for (int i = 0; i < _stops.Count; i++)
            {
                float dist = Vector2.Distance(buildingPos, _stops[i].Position);
                if (dist >= range) continue;
                results.Add(new StopConnectionPoint
                {
                    Position = _stops[i].Position,
                    Distance = dist,
                    StopIndex = i
                });
            }
            results.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            return results;
        }

        public static Vector2 ClosestPointOnSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 v = b - a;
            float c2 = Vector2.Dot(v, v);
            if (c2 <= 0.0001f) return a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, v) / c2);
            return a + t * v;
        }

        private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 closest = ClosestPointOnSegment(p, a, b);
            return Vector2.Distance(p, closest);
        }
    }
}

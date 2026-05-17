using System;
using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.Config
{
    /// <summary>Loaded game config (instance-held, no statics).</summary>
    [Serializable]
    public class GameConfig
    {
        public MetaData Meta;
        public SessionData Session;
        public BudgetData Budget;
        public ScoringData Scoring;
        public AccessibilityData Accessibility;
        public OscData Osc;
        public BuildingDefinition[] Buildings;
        public MapData Map;
        public TooltipsData Tooltips;
        public StopsData Stops;
        public TutorialData Tutorial;
        public InactivityData Inactivity;
        public EndMessageData[] EndMessages;
        public Dictionary<string, Dictionary<string, string>> Localization;

        /// <summary>Map layout: hubs (nodes) and roads (edges). Like HTML generateMap. Optional obstacles mark placement inactive.</summary>
        [Serializable]
        public class MapData
        {
            public MapNodeData[] nodes;
            public MapEdgeData[] edges;
            public MapObstacleData[] obstacles;
        }

        [Serializable]
        public class MapNodeData
        {
            public float x;
            public float y;
            public float population = 50000f;
        }

        [Serializable]
        public class MapEdgeData
        {
            public int from;
            public int to;
            public float length;
        }

        [Serializable]
        public class MapObstacleData
        {
            public float x;
            public float y;
            public float radius;
            public string type;
        }

        [Serializable]
        public class MetaData
        {
            public string version = "1.0.0";
            public string defaultLanguage = "EN";
        }

        [Serializable]
        public class SessionData
        {
            public int gameplaySeconds = 270;
            public int maxPlayers = 4;
        }

        [Serializable]
        public class BudgetData
        {
            public string mode = "PerQuadrant";
            public int startingBudget = 1000;
        }

        [Serializable]
        public class ScoringData
        {
            public float epsilonDistance = 1f;
            /// <summary>Normalization constant. One full contribution ≈ 1×NORM raw → 100%. HTML default 150.</summary>
            public float norm = 150f;
            /// <summary>Equal weight for all districts. HTML default 100000.</summary>
            public float equalDistrictWeight = 100000f;
            /// <summary>Divides building baseValue to normalize raw influence. Higher = weaker per-building impact.
            /// HTML reference default = 10. (Was 20 in Unity = half-impact bug.)</summary>
            public float influenceRefBase = 10f;
            /// <summary>Reference distance (metres) for decay curve. At path = ref, curve = 0.5^exp. HTML default 15.</summary>
            public float influenceReferenceMeters = 15f;
            /// <summary>Exponent for distance decay curve. Higher = steeper falloff. HTML default 1.</summary>
            public float distanceExponent = 1f;
            /// <summary>Minimum effective distance in game units (prevents near-zero path from inflating score). HTML default 50.</summary>
            public float distanceFloor = 50f;
            /// <summary>Converts game units to metres (gameUnits / distanceScale = metres). HTML: 100px = 1m.</summary>
            public float distanceScale = 100f;
            /// <summary>Base max road-network distance in game units. Multiplied by building connectionDistanceMult. HTML: 1.5m × 100 = 150.</summary>
            public float maxRoadDistance = 150f;
            /// <summary>Balance penalty: city QOL = mean(hubQol) − penalty × (maxHubQol − minHubQol). HTML default 0.5.</summary>
            public float qolBalancePenalty = 0.5f;
            /// <summary>Hard cap on final QOL score.</summary>
            public float qolCap = 80f;
            public float sizeBoostSmall = 1.2f;
            public float sizeBoostMedium = 1.1f;
            public float sizeBoostLarge = 1f;
            /// <summary>nearCap for direct-range (0 transport hops) small buildings. HTML default 0.5.</summary>
            public float nearCapSmall = 0.5f;
            public float nearCapMedium = 0.75f;
            public float nearCapLarge = 1f;
            /// <summary>Impact radius (seed stop search) per building size. Buildings find stops within this radius for scoring.</summary>
            public float impactRadiusSmall = 75f;
            public float impactRadiusMedium = 112f;
            public float impactRadiusLarge = 150f;
            /// <summary>Pillar weight for per-hub QOL weighted average.</summary>
            public float qolWeightEnv = 1f;
            public float qolWeightEco = 1f;
            public float qolWeightSaf = 1f;
            public float qolWeightCul = 1f;
        }

        [Serializable]
        public class AccessibilityData
        {
            public float walkingDistance = 0.5f;
            public float snapToTransitMaxDistance = 0.5f;
            /// <summary>Max distance from building to road segment to count as "connected" (HTML: 200).</summary>
            public float roadConnectRange = 200f;
            /// <summary>Radius around hub to count buildings for proximity score (HTML: 200).</summary>
            public float zoneRadius = 200f;
            /// <summary>Default connection radius for buildings that don't specify one (HTML: 500).</summary>
            public float defaultConnectionRadius = 500f;
        }

        [Serializable]
        public class OscData
        {
            public OscSourceData[] sources;
        }

        [Serializable]
        public class OscSourceData
        {
            public string id;
            public int listenPort;
            public string expectedSenderIp;
        }

        [Serializable]
        public class TooltipsData
        {
            public string[] introKeys;
        }

        [Serializable]
        public class EndMessageData
        {
            public int min;
            public int max;
            public string titleKey;
            public string bodyKey;
        }

        [Serializable]
        public class StopsData
        {
            public float spacing = 60f;
            public float minDistanceFromNode = 30f;
            public float minDistanceBetweenStops = 30f;
            /// <summary>0..1. Randomises each stop's offset within its own slot. HTML default 0.25.</summary>
            public float spacingJitter = 0.25f;
            /// <summary>0..1. Probability that a candidate stop is dropped. HTML default 0.30.</summary>
            public float removalRate = 0.30f;
            /// <summary>Seed for reproducible stop layout. -1 = non-deterministic.</summary>
            public int seed = 1234;
        }

        [Serializable]
        public class TutorialData
        {
            public TutorialStepData[] steps;
        }

        [Serializable]
        public class TutorialStepData
        {
            public string textKey;
            public float durationSeconds = 5f;
        }

        [Serializable]
        public class InactivityData
        {
            public float timeoutSeconds = 30f;
            public string textKey = "ui.inactivity";
        }
    }
}

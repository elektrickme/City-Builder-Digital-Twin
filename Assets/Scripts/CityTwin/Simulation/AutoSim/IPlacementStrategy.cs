using System.Collections.Generic;
using UnityEngine;
using CityTwin.Core;

namespace CityTwin.Simulation.AutoSim
{
    /// <summary>
    /// Decides where + which building a virtual player drops next. Strategies are stateless w.r.t. the
    /// simulation — the harness feeds them the current snapshot each tick.
    /// </summary>
    public interface IPlacementStrategy
    {
        string Name { get; }

        /// <summary>Returns false to skip this tick (e.g. budget too low). True with populated outputs to place.</summary>
        bool ChooseNextPlacement(SimulationSnapshot snapshot, System.Random rng, out string buildingId, out Vector2 position);
    }

    /// <summary>Read-only view the strategy uses to pick its next move.</summary>
    public struct SimulationSnapshot
    {
        public int RemainingBudget;
        public float ElapsedSeconds;
        public float Qol;
        public float Environment;
        public float Economy;
        public float HealthSafety;
        public float CultureEdu;
        public IReadOnlyList<BuildingDefinition> Catalog;
        public IReadOnlyList<Vector2> HubPositions;
        public IReadOnlyList<Vector2> PlacedPositions;
        public Vector2 FieldHalfExtent;          // play area half-extent in content-local units
        public IReadOnlyList<(Vector2 a, Vector2 b)> Roads;
        public System.Func<string, float> ImpactRadius;
    }
}

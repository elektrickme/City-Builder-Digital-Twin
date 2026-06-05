using System;
using CityTwin.Core;

namespace CityTwin.Input
{
    /// <summary>
    /// Ingestion contract for tile placements, independent of transport.
    /// TUIO/OSC is one implementation (<see cref="TileTrackingManager"/>); replay,
    /// mock, or live-data sources can implement this without touching consumers.
    /// </summary>
    public interface ITileSource
    {
        /// <summary>Raised when a tile is placed or its pose changes.</summary>
        event Action<TilePose> OnTileUpdated;

        /// <summary>Raised when a previously seen tile is removed (by stable tile id).</summary>
        event Action<string> OnTileRemoved;

        /// <summary>Forget all tracked sessions so still-present tiles re-enter as new placements (used by restart flows).</summary>
        void ClearSessions();
    }
}

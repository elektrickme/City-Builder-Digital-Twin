using UnityEngine;

namespace CityTwin.Core
{
    /// <summary>
    /// A self-contained hub layout preset. Add ResidentialHubMono instances as children.
    /// Hub-to-hub connections are built automatically via k-nearest neighbors at runtime.
    /// HubLayoutManager activates one preset at random on startup and deactivates the rest.
    /// </summary>
    public class HubLayoutPreset : MonoBehaviour
    {
    }
}

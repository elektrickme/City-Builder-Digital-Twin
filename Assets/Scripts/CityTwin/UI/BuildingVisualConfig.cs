using System;
using UnityEngine;

namespace CityTwin.UI
{
    /// <summary>
    /// ScriptableObject that defines per-building visual settings for markers,
    /// such as halo color and halo scale multiplier.
    /// Create via Assets → Create → CityTwin → Building Visual Config.
    /// </summary>
    [CreateAssetMenu(menuName = "CityTwin/Building Visual Config", fileName = "BuildingVisualConfig")]
    public class BuildingVisualConfig : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string buildingId;
            [Tooltip("Sprite shown on the building marker icon. If null, keeps the prefab default.")]
            public Sprite sprite;
            public Color haloColor = Color.white;
            [Tooltip("Multiplier applied on top of the base halo scale.")]
            public float haloScaleMultiplier = 1f;
        }

        [SerializeField] private Entry[] entries;

        public Entry GetEntry(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId) || entries == null) return null;
            for (int i = 0; i < entries.Length; i++)
            {
                var e = entries[i];
                if (e != null && !string.IsNullOrEmpty(e.buildingId) &&
                    string.Equals(e.buildingId, buildingId, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }
    }
}


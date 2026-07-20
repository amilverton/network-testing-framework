using PurrNet;
using UnityEngine;

namespace Caffeinated.NetworkTesting
{
    /// <summary>
    /// Adds NetworkTransform before PurrNet inspects a freshly instantiated generated fixture.
    /// </summary>
    public sealed class NetworkTransformFixtureInstaller : MonoBehaviour
    {
        private void Awake()
        {
            if (GetComponent<NetworkTransform>() == null)
                gameObject.AddComponent<NetworkTransform>();
        }
    }
}

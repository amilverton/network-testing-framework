using ConsumerProject.Core;
using PurrNet;

namespace ConsumerProject.Networking
{
    /// <summary>
    /// Project-owned non-scenario network prefab retained by the authored provider.
    /// </summary>
    public sealed class ConsumerProjectNetworkEntity : NetworkIdentity
    {
        public int InitialCounter => ConsumerCounterRules.InitialValue;
    }
}

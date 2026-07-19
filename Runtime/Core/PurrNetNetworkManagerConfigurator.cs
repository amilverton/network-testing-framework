using System;
using System.Reflection;
using PurrNet;
using UnityEngine;

namespace Amilverton.PurrNetTesting
{
    /// <summary>
    /// Isolates the one version-sensitive PurrNet configuration seam used by generated Players.
    /// </summary>
    internal static class PurrNetNetworkManagerConfigurator
    {
        private const string NetworkRulesFieldName = "_networkRules";

        private static readonly FieldInfo NetworkRulesField = typeof(NetworkManager).GetField(
            NetworkRulesFieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool TryApplyDefaultRules(NetworkManager networkManager, out string failure)
        {
            if (networkManager == null)
            {
                failure = "Cannot configure PurrNet rules on a null NetworkManager.";
                return false;
            }

            if (NetworkRulesField == null || NetworkRulesField.FieldType != typeof(NetworkRules))
            {
                failure =
                    $"PurrNet compatibility failure under Unity {Application.unityVersion}: NetworkManager field " +
                    $"'{NetworkRulesFieldName}' was not found with type NetworkRules.";
                return false;
            }

            NetworkRules rules = ScriptableObject.CreateInstance<NetworkRules>();
            rules.name = "PurrNet Network Test Rules";
            rules.hideFlags = HideFlags.HideAndDontSave;
            NetworkRulesField.SetValue(networkManager, rules);

            if (networkManager.networkRules != rules)
            {
                failure = "PurrNet did not retain the runtime NetworkRules instance.";
                UnityEngine.Object.Destroy(rules);
                return false;
            }

            failure = null;
            return true;
        }
    }
}

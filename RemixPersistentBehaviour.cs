using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Persistent behaviour that survives scene changes and handles LateUpdate for skinned mesh capture
    /// </summary>
    public class RemixPersistentBehaviour : MonoBehaviour
    {
        private UnityRemixPlugin plugin;
        private static ManualLogSource LogSource => UnityRemixPlugin.LogSource;
        
        public void Initialize(UnityRemixPlugin sourcePlugin)
        {
            plugin = sourcePlugin;
            LogSource.LogInfo("RemixPersistentBehaviour initialized");
        }
        
        void LateUpdate()
        {
            // Cast to object to avoid Unity's lifetime check (overloaded == operator)
            // We know the C# object exists and we want to keep using it
            if ((object)plugin != null)
            {
                plugin.UpdateFromPersistent();
            }
            else
            {
                if (Time.frameCount % 300 == 0)
                    LogSource.LogWarning("RemixPersistentBehaviour: Plugin reference is null!");
            }
        }
        
        void OnApplicationQuit()
        {
            UnityRemixPlugin.SetQuitting();
        }
    }
}

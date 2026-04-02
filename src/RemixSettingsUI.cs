using System;
using System.Diagnostics.CodeAnalysis;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Renders plugin settings inside the Remix developer menu "Plugin" tab.
    /// Registered as a draw callback via <see cref="RemixImGui"/>.
    /// </summary>
    public class RemixSettingsUI
    {
        private readonly ManualLogSource _log;
        private readonly UnityRemixPlugin _plugin;
        private bool _initialized;

        // Cached state for ImGui controls (avoids per-frame alloc)
        private float _maxRenderDistance;
        private float _lightIntensityMultiplier;
        private int _targetFPS;
        private bool _enableGameGeometry;
        private bool _enableDistanceCulling;
        private bool _enableVisibilityCulling;
        private bool _enableLights;
        private bool _captureStaticMeshes;
        private bool _captureSkinnedMeshes;
        private bool _hardwareSkinning;
        private bool _captureTextures;
        private bool _captureMaterials;
        private bool _enableSceneScan;
        private string _selectedCameraName;

        public RemixSettingsUI(ManualLogSource log, UnityRemixPlugin plugin)
        {
            _log = log;
            _plugin = plugin;
        }

        /// <summary>
        /// Called from the Remix render thread when the "Plugin" tab is active.
        /// Draws directly into the tab content area — no Begin/End window needed.
        /// </summary>
        public void Draw(IntPtr userData)
        {
            if (!_initialized)
            {
                SyncFromConfig();
                _initialized = true;
            }

            DrawRenderingSection();
            DrawCameraSection();
            DrawRendererSection();
            DrawLightingSection();
            DrawPerformanceSection();
            DrawDebugSection();

            RemixImGui.Separator();
            RemixImGui.Spacing();

            if (RemixImGui.Button("Save Settings", 130, 0))
                _plugin.SaveConfig();

            RemixImGui.SameLine();
            if (RemixImGui.Button("Refresh", 80, 0))
                SyncFromConfig();
        }

        private void DrawRenderingSection()
        {
            if (!RemixImGui.CollapsingHeader("Rendering", RemixImGui.TreeNodeFlags_DefaultOpen))
                return;

            if (RemixImGui.Checkbox("Game Geometry", ref _enableGameGeometry))
                _plugin.SetConfig("EnableGameGeometry", _enableGameGeometry);

            if (RemixImGui.Checkbox("Distance Culling", ref _enableDistanceCulling))
                _plugin.SetConfig("EnableDistanceCulling", _enableDistanceCulling);

            if (_enableDistanceCulling)
            {
                RemixImGui.Indent();
                if (RemixImGui.SliderFloat("Max Distance", ref _maxRenderDistance, 10f, 10000f))
                    _plugin.SetConfig("MaxRenderDistance", _maxRenderDistance);
                RemixImGui.Unindent();
            }

            if (RemixImGui.Checkbox("Visibility Culling", ref _enableVisibilityCulling))
                _plugin.SetConfig("UseVisibilityCulling", _enableVisibilityCulling);

            if (RemixImGui.Checkbox("Scene Scan", ref _enableSceneScan))
                _plugin.SetConfig("EnableSceneScan", _enableSceneScan);
        }

        private void DrawCameraSection()
        {
            if (!RemixImGui.CollapsingHeader("Camera", RemixImGui.TreeNodeFlags_DefaultOpen))
                return;

            var camHandler = _plugin.CameraHandler;
            if (camHandler == null)
            {
                RemixImGui.Text("Camera handler not initialized.");
                return;
            }

            var snapshots = camHandler.CameraSnapshots;
            bool isAuto = string.IsNullOrEmpty(_selectedCameraName);

            if (RemixImGui.RadioButton("Auto-detect", isAuto))
            {
                _selectedCameraName = "";
                _plugin.SetConfig("CameraName", "");
            }

            if (snapshots.Length == 0)
            {
                RemixImGui.Text("No cameras found.");
                return;
            }

            int tableFlags = RemixImGui.TableFlags_Borders | RemixImGui.TableFlags_RowBg | RemixImGui.TableFlags_SizingStretchProp;
            if (RemixImGui.BeginTable("##cameras", 3, tableFlags))
            {
                RemixImGui.TableSetupColumn("", 0, 0.05f);         // radio button
                RemixImGui.TableSetupColumn("Name", 0, 0.75f);
                RemixImGui.TableSetupColumn("Depth", 0, 0.2f);
                RemixImGui.TableHeadersRow();

                for (int i = 0; i < snapshots.Length; i++)
                {
                    var snap = snapshots[i];
                    bool selected = !isAuto && _selectedCameraName == snap.Name;

                    RemixImGui.TableNextRow();

                    // Radio button column
                    RemixImGui.TableSetColumnIndex(0);
                    if (RemixImGui.RadioButton($"##cam_{i}", selected))
                    {
                        _selectedCameraName = snap.Name;
                        _plugin.SetConfig("CameraName", snap.Name);
                    }

                    // Name column
                    RemixImGui.TableSetColumnIndex(1);
                    RemixImGui.Text(snap.Name);

                    // Depth column
                    RemixImGui.TableSetColumnIndex(2);
                    RemixImGui.Text(snap.Depth.ToString("F0"));
                }

                RemixImGui.EndTable();
            }
        }

        private void DrawRendererSection()
        {
            if (!RemixImGui.CollapsingHeader("Renderers"))
                return;

            var fc = _plugin.FrameCapture;
            if (fc == null)
            {
                RemixImGui.Text("Frame capture not initialized.");
                return;
            }

            var layers = fc.LayerSnapshots;
            var renderers = fc.RendererSnapshots;

            // Merge scanned instance counts from SceneMeshScanner
            var scanner = _plugin.SceneMeshScanner;
            var scannedCounts = scanner?.GetLayerCounts();

            RemixImGui.Text($"{layers.Length} layers, {renderers.Length} active renderers");

            if (layers.Length == 0)
                return;

            int tableFlags = RemixImGui.TableFlags_Borders | RemixImGui.TableFlags_RowBg | RemixImGui.TableFlags_SizingStretchProp;
            if (RemixImGui.BeginTable("##layers", 4, tableFlags, 0, 300))
            {
                RemixImGui.TableSetupColumn("On", 0, 0.06f);
                RemixImGui.TableSetupColumn("Layer", 0, 0.50f);
                RemixImGui.TableSetupColumn("Active", 0, 0.22f);
                RemixImGui.TableSetupColumn("Scanned", 0, 0.22f);
                RemixImGui.TableHeadersRow();

                for (int i = 0; i < layers.Length; i++)
                {
                    var layer = layers[i];
                    bool enabled = !layer.UserDisabled;
                    int scannedCount = 0;
                    scannedCounts?.TryGetValue(layer.LayerIndex, out scannedCount);
                    int activeCount = layer.StaticCount + layer.SkinnedCount;

                    RemixImGui.TableNextRow();

                    // Checkbox column
                    RemixImGui.TableSetColumnIndex(0);
                    if (RemixImGui.Checkbox($"##ly_{layer.LayerIndex}", ref enabled))
                    {
                        fc.SetLayerDisabled(layer.LayerIndex, !enabled);
                        _plugin.SetConfig("DisabledLayers", fc.GetDisabledLayersString());
                    }

                    // Layer name — expandable tree node for drill-down
                    RemixImGui.TableSetColumnIndex(1);
                    string label = string.IsNullOrEmpty(layer.LayerName)
                        ? $"Layer {layer.LayerIndex}"
                        : $"{layer.LayerName} ({layer.LayerIndex})";
                    bool expanded = RemixImGui.TreeNode($"{label}##ly_{layer.LayerIndex}");

                    // Active count column
                    RemixImGui.TableSetColumnIndex(2);
                    RemixImGui.Text($"{activeCount}");

                    // Scanned count column
                    RemixImGui.TableSetColumnIndex(3);
                    RemixImGui.Text(scannedCount > 0 ? $"{scannedCount}" : "-");

                    // Drill-down: show individual renderers in this layer
                    if (expanded)
                    {
                        for (int j = 0; j < renderers.Length; j++)
                        {
                            if (renderers[j].Layer != layer.LayerIndex)
                                continue;

                            bool rEnabled = !fc.IsRendererDisabled(renderers[j].InstanceId);

                            RemixImGui.TableNextRow();

                            RemixImGui.TableSetColumnIndex(0);
                            if (RemixImGui.Checkbox($"##r_{renderers[j].InstanceId}", ref rEnabled))
                                fc.SetRendererDisabled(renderers[j].InstanceId, !rEnabled);

                            RemixImGui.TableSetColumnIndex(1);
                            RemixImGui.Text($"  {renderers[j].Name}");
                            RemixImGui.TableSetColumnIndex(2);
                            RemixImGui.Text(renderers[j].Type);
                            RemixImGui.TableSetColumnIndex(3); // empty
                        }
                        RemixImGui.TreePop();
                    }
                }

                RemixImGui.EndTable();
            }
        }

        private void DrawLightingSection()
        {
            if (!RemixImGui.CollapsingHeader("Lighting", RemixImGui.TreeNodeFlags_DefaultOpen))
                return;

            if (RemixImGui.Checkbox("Enable Lights", ref _enableLights))
                _plugin.SetConfig("EnableLights", _enableLights);

            if (RemixImGui.SliderFloat("Intensity Multiplier", ref _lightIntensityMultiplier, 0.01f, 100f))
                _plugin.SetConfig("IntensityMultiplier", _lightIntensityMultiplier);
        }

        private void DrawPerformanceSection()
        {
            if (!RemixImGui.CollapsingHeader("Performance"))
                return;

            RemixImGui.Text("Used to lock the framerate of the wrapper itself, not the game.");

            if (RemixImGui.DragInt("Target FPS", ref _targetFPS, 1, 0, 500))
                _plugin.SetConfig("TargetFPS", _targetFPS);

            RemixImGui.Text(_targetFPS == 0 ? "(Uncapped)" : "");

            if (RemixImGui.Checkbox("Hardware Skinning", ref _hardwareSkinning))
                _plugin.SetConfig("HardwareSkinning", _hardwareSkinning);

            RemixImGui.Text("Requires scene reload to apply");
        }

        private void DrawDebugSection()
        {
            if (!RemixImGui.CollapsingHeader("Debug Toggles"))
                return;

            if (RemixImGui.Checkbox("Static Meshes", ref _captureStaticMeshes))
                _plugin.SetConfig("CaptureStaticMeshes", _captureStaticMeshes);
            if (RemixImGui.Checkbox("Skinned Meshes", ref _captureSkinnedMeshes))
                _plugin.SetConfig("CaptureSkinnedMeshes", _captureSkinnedMeshes);
            if (RemixImGui.Checkbox("Textures", ref _captureTextures))
                _plugin.SetConfig("CaptureTextures", _captureTextures);
            if (RemixImGui.Checkbox("Materials", ref _captureMaterials))
                _plugin.SetConfig("CaptureMaterials", _captureMaterials);
        }

        private void SyncFromConfig()
        {
            _enableGameGeometry = _plugin.GetConfigBool("EnableGameGeometry");
            _enableDistanceCulling = _plugin.GetConfigBool("EnableDistanceCulling");
            _maxRenderDistance = _plugin.GetConfigFloat("MaxRenderDistance");
            _enableVisibilityCulling = _plugin.GetConfigBool("UseVisibilityCulling");
            _enableSceneScan = _plugin.GetConfigBool("EnableSceneScan");
            _enableLights = _plugin.GetConfigBool("EnableLights");
            _lightIntensityMultiplier = _plugin.GetConfigFloat("IntensityMultiplier");
            _targetFPS = _plugin.GetConfigInt("TargetFPS");
            _captureStaticMeshes = _plugin.GetConfigBool("CaptureStaticMeshes");
            _captureSkinnedMeshes = _plugin.GetConfigBool("CaptureSkinnedMeshes");
            _hardwareSkinning = _plugin.GetConfigBool("HardwareSkinning");
            _captureTextures = _plugin.GetConfigBool("CaptureTextures");
            _captureMaterials = _plugin.GetConfigBool("CaptureMaterials");
            _selectedCameraName = _plugin.GetConfigString("CameraName");
        }

        public void ResetState() { _initialized = false; }
    }
}

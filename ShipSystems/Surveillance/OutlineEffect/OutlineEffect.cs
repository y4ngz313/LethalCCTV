using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.ShipSystems.Rendering;

namespace Y4NGZCompany.ShipSystems.Surveillance.OutlineEffect
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class OutlineEffect : MonoBehaviour
    {
        public static OutlineEffect Instance { get; private set; }
        private static readonly System.Collections.Generic.HashSet<OutlineEffect> Instances =
            new System.Collections.Generic.HashSet<OutlineEffect>();
        internal bool SquadOnly;

        internal static void RegisterOutline(Outline outline)
        {
            foreach (OutlineEffect effect in Instances) if (effect != null) effect.AddOutline(outline);
        }

        internal static void UnregisterOutline(Outline outline)
        {
            foreach (OutlineEffect effect in Instances) if (effect != null) effect.RemoveOutline(outline);
        }

        private readonly LinkedSet<Outline> _outlines = new LinkedSet<Outline>();
        private readonly List<Material> _materialBuffer = new List<Material>();

        [Range(1f, 6f)] public float lineThickness = 1.25f;
        [Range(0f, 10f)] public float lineIntensity = 0.5f;
        [Range(0f, 1f)] public float fillAmount = 0.2f;

        public Color lineColor0 = Color.red;
        public Color lineColor1 = Color.green;
        public Color lineColor2 = Color.blue;
        public bool additiveRendering;
        public bool backfaceCulling = true;
        public Color fillColor = Color.blue;
        public bool useFillColor;
        public bool cornerOutlines;
        public bool addLinesBetweenColors;
        public bool scaleWithScreenSize = true;
        [Range(0f, 1f)] public float alphaCutoff = 0.5f;
        public bool flipY;
        public Camera sourceCamera;
        public bool autoEnableOutlines;

        public Camera outlineCamera;
        public Material outlineShaderMaterial;
        private Material outlineOverlayMaterial;
        public RenderTexture renderTexture;
        public RenderTexture extraRenderTexture;

        private Material _outline1Material;
        private Material _outline2Material;
        private Material _outline3Material;
        private Material _outlineEraseMaterial;
        private Shader _outlineShader;
        private Shader _outlineBufferShader;
        private Shader _outlineOverlayShader;
        private Shader _maskShader;
        private CommandBuffer _immediateCommandBuffer;
        private CommandBuffer _feedCommandBuffer;
        private GameObject _customPassVolumeObject;
        private CustomPassVolume _customPassVolume;
        private Y4NGZHDRPOutlinePass _customPass;
        private CameraRenderLease _renderLease;
        private bool _feedMode;
        private bool _renderNextFrame;
        private bool _pipelineCallbacksRegistered;
        private bool _outlineTextureValid;
        private float _nextHdrpTraceAt;
        private float _nextHdrpWarningAt;

        /// <summary>The name this effect's leases carry; FeedGuard logs it.</summary>
        internal const string EffectName = "cctv-outline";

        private static readonly int CompositeTempId = Shader.PropertyToID("_Y4NGZOutlineCompositeTemp");
        private static readonly int OutlineSourceId = Shader.PropertyToID("_OutlineSource");

        private void Awake()
        {
            Instances.Add(this);
            Instance = this;
        }

        private void Start()
        {
            CreateMaterialsIfNeeded();
            UpdateMaterialsPublicProperties();
            if (sourceCamera == null)
                sourceCamera = GetComponent<Camera>() ?? Camera.main;

            RecreateRenderTexturesIfNeeded();
            EnsureHdrpCustomPass();
        }

        private void OnEnable()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                AcquireRenderOwnership();
                EnsureHdrpCustomPass();
            }
            else
            {
                RegisterPipelineCallbacks();
            }

            Outline[] outlines = FindObjectsOfType<Outline>();
            for (int i = 0; i < outlines.Length; i++)
            {
                if (outlines[i] != null && !_outlines.Contains(outlines[i]))
                    _outlines.Add(outlines[i]);
            }
        }

        private void OnDisable()
        {
            UnregisterPipelineCallbacks();
            ReleaseRenderOwnership();
            if (_customPassVolume != null)
                _customPassVolume.enabled = false;
        }

        private void OnPreRender()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
                return;

            if (_immediateCommandBuffer == null)
                _immediateCommandBuffer = new CommandBuffer { name = "Y4NGZ Outline Buffer" };

            _immediateCommandBuffer.Clear();
            if (!RenderOutlineBuffer(_immediateCommandBuffer))
                return;

            Graphics.ExecuteCommandBuffer(_immediateCommandBuffer);
        }

        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (outlineShaderMaterial == null || renderTexture == null || !_outlineTextureValid)
            {
                Graphics.Blit(source, destination);
                return;
            }

            outlineShaderMaterial.SetTexture("_OutlineSource", renderTexture);
            if (addLinesBetweenColors && extraRenderTexture != null)
            {
                Graphics.Blit(source, extraRenderTexture, outlineShaderMaterial, 0);
                outlineShaderMaterial.SetTexture("_OutlineSource", extraRenderTexture);
            }

            Graphics.Blit(source, destination, outlineShaderMaterial, 1);
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
            if (Instance == this)
                Instance = null;

            UnregisterPipelineCallbacks();
            ReleaseRenderOwnership();
            if (_customPassVolumeObject != null)
            {
                Destroy(_customPassVolumeObject);
                _customPassVolumeObject = null;
                _customPassVolume = null;
                _customPass = null;
            }

            if (_immediateCommandBuffer != null)
            {
                _immediateCommandBuffer.Release();
                _immediateCommandBuffer = null;
            }

            if (_feedCommandBuffer != null)
            {
                _feedCommandBuffer.Release();
                _feedCommandBuffer = null;
            }

            if (renderTexture != null)
                renderTexture.Release();
            if (extraRenderTexture != null)
                extraRenderTexture.Release();

            DestroyMaterials();
        }

        public void AddOutline(Outline outline)
        {
            if (outline == null)
                return;

            _outlines.Add(outline);
            EnsureHdrpCustomPass();
        }

        public void RemoveOutline(Outline outline)
        {
            if (outline != null)
                _outlines.Remove(outline);
        }

        private bool HasActiveOutlines()
        {
            if (_outlines.Count == 0)
                return false;

            foreach (Outline outline in _outlines)
            {
                if (outline != null && outline.enabled && (!SquadOnly || outline.SquadHighlighted) &&
                    outline.Renderer != null && (outline.Renderer.enabled || outline.SquadHighlighted))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Takes the effect's hold on its camera through Core's <see cref="CameraRenderProfile"/>. A
        /// CCTV feed camera refuses settings leases, so there the effect is an overlay: an overlay
        /// lease the feed guard can see and revoke, no custom pass, and the outline composited by
        /// <see cref="NightVisionBaker"/> after the bake (<see cref="CompositeFeedOverlay"/>). Any other
        /// camera (the gameplay camera) holds its CustomPass frame setting on through a ref-counted
        /// lease shared with Contracted's objective hint, and draws through a custom pass volume that
        /// targets that camera only.
        /// </summary>
        private void AcquireRenderOwnership()
        {
            Camera camera = ResolveSourceCamera();
            if (camera == null)
                return;

            _feedMode = CameraRenderProfile.IsFeed(camera);
            if (_feedMode)
            {
                if (_customPassVolume != null)
                    _customPassVolume.enabled = false;
                CameraRenderProfile.TryBeginOverlay(camera, EffectName, out _renderLease);
                return;
            }

            // Refused only on a camera without HDRP camera data (its defaults already run custom
            // passes) or under an opposite-value lease; the pass still registers either way.
            CameraRenderProfile.TryAcquireFrameSetting(camera, FrameSettingsField.CustomPass, true, EffectName,
                out _renderLease);
        }

        private void ReleaseRenderOwnership()
        {
            if (_renderLease != null)
                _renderLease.Dispose();
            _renderLease = null;
        }

        /// <summary>
        /// Re-takes the hold when the camera's feed classification changed (a slot rebind) or the
        /// lease ended underneath the effect. Allocation-free while the hold is intact. A lease the feed
        /// guard revoked stays refused until the feed's profile is released.
        /// </summary>
        internal void RefreshRenderOwnership()
        {
            if (!isActiveAndEnabled || GraphicsSettings.currentRenderPipeline == null)
                return;

            Camera camera = ResolveSourceCamera();
            if (camera == null)
                return;

            bool feed = CameraRenderProfile.IsFeed(camera);
            if (feed == _feedMode && _renderLease != null && _renderLease.IsActive)
                return;

            ReleaseRenderOwnership();
            AcquireRenderOwnership();
            EnsureHdrpCustomPass();
        }

        /// <summary>
        /// Called by <see cref="NightVisionBaker"/> right after it bakes <paramref name="camera"/>'s raw
        /// frame into <paramref name="display"/>. Draws the outline mask with an owned CommandBuffer
        /// and the feed camera's own matrices, then composites it over the baked image with the
        /// outline shader's additive pass, which leaves every non-outline pixel as baked. False when
        /// the camera has no active feed overlay.
        /// </summary>
        internal static bool CompositeFeedOverlay(Camera camera, RenderTexture display)
        {
            if (camera == null || display == null)
                return false;

            foreach (OutlineEffect effect in Instances)
            {
                if (effect != null && effect._feedMode && effect.sourceCamera == camera)
                    return effect.CompositeFeed(display);
            }

            return false;
        }

        private bool CompositeFeed(RenderTexture display)
        {
            if (!isActiveAndEnabled || _renderLease == null || !_renderLease.IsActive || !HasActiveOutlines())
                return false;

            if (_feedCommandBuffer == null)
                _feedCommandBuffer = new CommandBuffer { name = "Y4NGZ CCTV Feed Outline" };

            CommandBuffer commandBuffer = _feedCommandBuffer;
            commandBuffer.Clear();
            if (!RenderOutlineBuffer(commandBuffer) || outlineShaderMaterial == null || renderTexture == null)
                return false;

            outlineShaderMaterial.SetTexture(OutlineSourceId, renderTexture);
            commandBuffer.GetTemporaryRT(CompositeTempId, display.width, display.height, 0, FilterMode.Bilinear,
                display.format, display.sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            commandBuffer.Blit(display, CompositeTempId);
            commandBuffer.Blit(CompositeTempId, display, outlineShaderMaterial, 1);
            commandBuffer.ReleaseTemporaryRT(CompositeTempId);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            TraceHdrpComposite(display.width, display.height);
            return true;
        }

        private void EnsureHdrpCustomPass()
        {
            if (GraphicsSettings.currentRenderPipeline == null)
                return;

            Camera camera = ResolveSourceCamera();
            if (camera == null)
                return;

            // A feed never runs custom passes; its outline is the baker's overlay.
            if (CameraRenderProfile.IsFeed(camera))
            {
                if (_customPassVolume != null)
                    _customPassVolume.enabled = false;
                return;
            }

            if (_customPassVolume != null && _customPass != null)
            {
                _customPass.Owner = this;
                _customPassVolume.targetCamera = camera;
                _customPassVolume.enabled = isActiveAndEnabled;
                return;
            }

            if (_customPassVolumeObject == null)
            {
                _customPassVolumeObject = new GameObject("Y4NGZ_OutlineCustomPassVolume")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(_customPassVolumeObject);
            }

            _customPassVolume = _customPassVolumeObject.GetComponent<CustomPassVolume>();
            if (_customPassVolume == null)
                _customPassVolume = _customPassVolumeObject.AddComponent<CustomPassVolume>();

            _customPassVolume.isGlobal = true;
            _customPassVolume.priority = 1000f;
            _customPassVolume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
            _customPassVolume.targetCamera = camera;
            _customPassVolume.customPasses.Clear();

            CustomPass pass = _customPassVolume.AddPassOfType(typeof(Y4NGZHDRPOutlinePass));
            _customPass = pass as Y4NGZHDRPOutlinePass;
            if (_customPass == null)
            {
                if (Time.unscaledTime >= _nextHdrpWarningAt)
                {
                    _nextHdrpWarningAt = Time.unscaledTime + 10f;
                    SurveillanceBootstrap.Log?.LogWarning("[LethalCCTV] OutlineEffect failed to register HDRP custom pass; CCTV outlines may be invisible.");
                }

                return;
            }

            _customPass.name = "Y4NGZ Outline Composite";
            _customPass.enabled = true;
            _customPass.targetColorBuffer = CustomPass.TargetBuffer.Camera;
            _customPass.targetDepthBuffer = CustomPass.TargetBuffer.Camera;
            _customPass.clearFlags = ClearFlag.None;
            _customPass.Owner = this;
            _customPassVolume.enabled = isActiveAndEnabled;
            SurveillanceBootstrap.Log?.LogInfo($"[LethalCCTV] OutlineEffect HDRP custom pass registered for '{camera.name}'.");
        }

        private void TraceHdrpComposite(int width, int height)
        {
            if (Time.unscaledTime < _nextHdrpTraceAt)
                return;

            _nextHdrpTraceAt = Time.unscaledTime + 5f;
            SurveillanceBootstrap.Log?.LogInfo(
                $"[LethalCCTV] OutlineEffect {(_feedMode ? "feed overlay" : "HDRP custom pass")} compositing {_outlines.Count} outline renderer(s) at {width}x{height}.");
        }

        public void UpdateMaterialsPublicProperties()
        {
            if (!CreateMaterialsIfNeeded() || outlineShaderMaterial == null)
                return;

            float scalingFactor = 1f;
            if (scaleWithScreenSize)
                scalingFactor = Screen.height / 360f;

            float thicknessScale = scaleWithScreenSize && scalingFactor >= 1f ? scalingFactor : 1f;
            ApplyMaterialProperties(outlineShaderMaterial, thicknessScale, legacyComposite: true);
            if (outlineOverlayMaterial != null)
                ApplyMaterialProperties(outlineOverlayMaterial, thicknessScale, legacyComposite: false);
            Shader.SetGlobalFloat("_OutlineAlphaCutoff", alphaCutoff);
        }

        private void ApplyMaterialProperties(Material material, float thicknessScale, bool legacyComposite)
        {
            if (material == null)
                return;

            material.SetFloat("_LineThicknessX", thicknessScale * (lineThickness / Mathf.Max(1f, Screen.width)));
            material.SetFloat("_LineThicknessY", thicknessScale * (lineThickness / Mathf.Max(1f, Screen.height)));
            material.SetFloat("_LineIntensity", lineIntensity);
            material.SetColor("_LineColor1", legacyComposite ? lineColor0 * lineColor0 : lineColor0);
            material.SetColor("_LineColor2", legacyComposite ? lineColor1 * lineColor1 : lineColor1);
            material.SetColor("_LineColor3", legacyComposite ? lineColor2 * lineColor2 : lineColor2);
            material.SetInt("_CornerOutlines", cornerOutlines ? 1 : 0);

            if (!legacyComposite)
                return;

            material.SetFloat("_FillAmount", fillAmount);
            material.SetColor("_FillColor", fillColor);
            material.SetFloat("_UseFillColor", useFillColor ? 1f : 0f);
            material.SetInt("_FlipY", flipY ? 1 : 0);
            material.SetInt("_Dark", additiveRendering ? 0 : 1);
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
                return;

            if (camera == null || camera != ResolveSourceCamera())
                return;

            CommandBuffer commandBuffer = CommandBufferPool.Get("Y4NGZ Outline Buffer");
            try
            {
                if (RenderOutlineBuffer(commandBuffer))
                    context.ExecuteCommandBuffer(commandBuffer);
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline != null)
                return;

            if (camera == null || camera != ResolveSourceCamera())
                return;
            if (outlineShaderMaterial == null || renderTexture == null || !_outlineTextureValid)
            {
                return;
            }

            int width = Mathf.Max(1, camera.pixelWidth);
            int height = Mathf.Max(1, camera.pixelHeight);
            CommandBuffer commandBuffer = CommandBufferPool.Get("Y4NGZ Outline Composite");
            try
            {
                outlineShaderMaterial.SetTexture("_OutlineSource", renderTexture);
                commandBuffer.GetTemporaryRT(CompositeTempId, width, height, 0, FilterMode.Bilinear, RenderTextureFormat.Default);
                commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, CompositeTempId, outlineShaderMaterial, 1);
                commandBuffer.Blit(CompositeTempId, BuiltinRenderTextureType.CameraTarget);
                commandBuffer.ReleaseTemporaryRT(CompositeTempId);
                context.ExecuteCommandBuffer(commandBuffer);
            }
            finally
            {
                CommandBufferPool.Release(commandBuffer);
            }
        }

        private bool RenderOutlineBuffer(CommandBuffer commandBuffer)
        {
            if (commandBuffer == null)
                return false;

            if (ResolveSourceCamera() == null)
                return false;

            if (_outlines.Count == 0)
            {
                if (!_renderNextFrame)
                    return false;

                _renderNextFrame = false;
            }
            else
            {
                _renderNextFrame = true;
            }

            if (!CreateMaterialsIfNeeded())
                return false;

            RecreateRenderTexturesIfNeeded();
            UpdateMaterialsPublicProperties();
            if (renderTexture == null)
                return false;

            commandBuffer.SetRenderTarget(renderTexture);
            commandBuffer.SetViewport(new Rect(0f, 0f, renderTexture.width, renderTexture.height));
            commandBuffer.ClearRenderTarget(true, true, Color.clear);
            commandBuffer.SetViewProjectionMatrices(sourceCamera.worldToCameraMatrix, sourceCamera.projectionMatrix);

            foreach (Outline outline in _outlines)
                DrawOutline(commandBuffer, outline);

            _outlineTextureValid = true;
            return true;
        }

        private Camera ResolveSourceCamera()
        {
            if (sourceCamera == null)
                sourceCamera = GetComponent<Camera>() ?? Camera.main;

            return sourceCamera;
        }

        private void RegisterPipelineCallbacks()
        {
            if (_pipelineCallbacksRegistered)
                return;

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            _pipelineCallbacksRegistered = true;
        }

        private void UnregisterPipelineCallbacks()
        {
            if (!_pipelineCallbacksRegistered)
                return;

            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            _pipelineCallbacksRegistered = false;
        }

        private void DrawOutline(CommandBuffer commandBuffer, Outline outline)
        {
            if (outline == null || outline.Renderer == null || !outline.enabled ||
                (SquadOnly && !outline.SquadHighlighted))
                return;

            Material[] sharedMaterials = outline.SharedMaterials;
            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                Material material = ResolveBufferMaterial(outline, sharedMaterials[i]);
                if (material == null)
                    continue;

                if (material.HasProperty("_Culling"))
                    material.SetInt("_Culling", backfaceCulling ? (int)CullMode.Back : (int)CullMode.Off);
                if (material.HasProperty("_Cull"))
                    material.SetInt("_Cull", backfaceCulling ? (int)CullMode.Back : (int)CullMode.Off);

                MeshFilter meshFilter = outline.MeshFilter;
                SkinnedMeshRenderer skinned = outline.SkinnedMeshRenderer;
                SpriteRenderer sprite = outline.SpriteRenderer;
                int passIndex = ResolveMaskMaterialPass(material);
                if (meshFilter != null && meshFilter.sharedMesh != null)
                {
                    if (i < meshFilter.sharedMesh.subMeshCount)
                        commandBuffer.DrawRenderer(outline.Renderer, material, i, passIndex);
                }
                else if (skinned != null && skinned.sharedMesh != null)
                {
                    if (i < skinned.sharedMesh.subMeshCount)
                        commandBuffer.DrawRenderer(outline.Renderer, material, i, passIndex);
                }
                else if (sprite != null)
                {
                    commandBuffer.DrawRenderer(outline.Renderer, material, i, passIndex);
                }
            }
        }

        private Material ResolveBufferMaterial(Outline outline, Material sourceMaterial)
        {
            if (outline == null)
                return null;

            return outline.eraseRenderer ? _outlineEraseMaterial : GetMaterialFromId(outline.color);
        }

        private Material GetMaterialFromId(int id)
        {
            switch (id)
            {
                case 1: return _outline2Material;
                case 2: return _outline3Material;
                default: return _outline1Material;
            }
        }

        private void EnsureOutlineCamera()
        {
            if (sourceCamera == null)
                return;

            if (outlineCamera != null)
                return;

            Camera[] children = GetComponentsInChildren<Camera>(includeInactive: true);
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] != null && children[i].name == "Outline Camera")
                {
                    outlineCamera = children[i];
                    outlineCamera.enabled = false;
                    return;
                }
            }

            GameObject cameraGameObject = new GameObject("Outline Camera");
            cameraGameObject.transform.SetParent(sourceCamera.transform, false);
            outlineCamera = cameraGameObject.AddComponent<Camera>();
            outlineCamera.enabled = false;
        }

        private void RecreateRenderTexturesIfNeeded()
        {
            if (sourceCamera == null)
                return;

            int width = Mathf.Max(1, sourceCamera.pixelWidth);
            int height = Mathf.Max(1, sourceCamera.pixelHeight);
            bool recreate = renderTexture == null
                || renderTexture.width != width
                || renderTexture.height != height;

            if (!recreate)
                return;

            if (renderTexture != null)
                renderTexture.Release();
            if (extraRenderTexture != null)
                extraRenderTexture.Release();

            _outlineTextureValid = false;
            renderTexture = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            // The between-colours pass is the only reader of the second texture; the CCTV and
            // squad-ping configurations never enable it, so they hold one camera-sized RT, not two.
            extraRenderTexture = addLinesBetweenColors
                ? new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                }
                : null;
            if (outlineCamera != null)
                outlineCamera.targetTexture = renderTexture;
        }

        private bool CreateMaterialsIfNeeded()
        {
            if (_outlineShader == null)
                _outlineShader = OutlineRuntimeAssets.LoadShader("OutlineShader");
            if (_outlineBufferShader == null)
                _outlineBufferShader = OutlineRuntimeAssets.LoadShader("OutlineBufferShader");
            if (_outlineOverlayShader == null)
                _outlineOverlayShader = OutlineRuntimeAssets.LoadShader("Y4NGZOutlineOverlayShader");
            if (_outlineShader == null)
                return false;
            if (GraphicsSettings.currentRenderPipeline != null && _outlineOverlayShader == null)
                return false;

            if (outlineShaderMaterial == null)
            {
                outlineShaderMaterial = new Material(_outlineShader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (outlineOverlayMaterial == null && _outlineOverlayShader != null)
            {
                outlineOverlayMaterial = new Material(_outlineOverlayShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (_outlineEraseMaterial == null)
                _outlineEraseMaterial = CreateMaterial(new Color(0f, 0f, 0f, 0f));
            if (_outline1Material == null)
                _outline1Material = CreateMaterial(new Color(1f, 0f, 0f, 1f));
            if (_outline2Material == null)
                _outline2Material = CreateMaterial(new Color(0f, 1f, 0f, 1f));
            if (_outline3Material == null)
                _outline3Material = CreateMaterial(new Color(0f, 0f, 1f, 1f));

            return outlineShaderMaterial != null
                && (GraphicsSettings.currentRenderPipeline == null || outlineOverlayMaterial != null)
                && _outlineEraseMaterial != null
                && _outline1Material != null
                && _outline2Material != null
                && _outline3Material != null;
        }

        private Material CreateMaterial(Color emissionColor)
        {
            Shader shader = ResolveMaskShader();
            if (shader == null)
                return null;

            Material material = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                renderQueue = 2500
            };
            ConfigureMaskMaterial(material, emissionColor);
            return material;
        }

        private Shader ResolveMaskShader()
        {
            if (_maskShader != null)
                return _maskShader;

            // Use the shipped emissive RGB mask in both pipelines. HDRP/Unlit
            // depends on camera/material state that the stripped CCTV pass does
            // not supply; its empty mask made an otherwise active ping invisible.
            _maskShader = _outlineBufferShader;

            return _maskShader;
        }

        private static void ConfigureMaskMaterial(Material material, Color color)
        {
            if (material == null)
                return;

            SetColorIfPresent(material, "_Color", color);
            SetColorIfPresent(material, "_BaseColor", color);
            SetColorIfPresent(material, "_UnlitColor", color);
            SetColorIfPresent(material, "_EmissiveColor", color);
            SetFloatIfPresent(material, "_SurfaceType", 0f);
            SetFloatIfPresent(material, "_AlphaCutoffEnable", 0f);
            SetFloatIfPresent(material, "_ZWrite", 0f);
            SetFloatIfPresent(material, "_ZTestDepthEqualForOpaque", (float)CompareFunction.Always);
            SetIntIfPresent(material, "_SrcBlend", (int)BlendMode.One);
            SetIntIfPresent(material, "_DstBlend", (int)BlendMode.Zero);
            SetIntIfPresent(material, "_Cull", (int)CullMode.Off);
            SetIntIfPresent(material, "_Culling", (int)CullMode.Off);
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        }

        private static readonly string[] MaskPassNames =
        {
            "ForwardOnly",
            "Forward",
            "SRPDefaultUnlit",
            "DepthForwardOnly"
        };

        private static int ResolveMaskMaterialPass(Material material)
        {
            if (material == null)
                return 0;

            string[] passNames = MaskPassNames;
            for (int i = 0; i < passNames.Length; i++)
            {
                int pass = material.FindPass(passNames[i]);
                if (pass >= 0)
                    return pass;
            }

            return 0;
        }

        private static void SetColorIfPresent(Material material, string name, Color value)
        {
            if (material.HasProperty(name))
                material.SetColor(name, value);
        }

        private static void SetFloatIfPresent(Material material, string name, float value)
        {
            if (material.HasProperty(name))
                material.SetFloat(name, value);
        }

        private static void SetIntIfPresent(Material material, string name, int value)
        {
            if (material.HasProperty(name))
                material.SetInt(name, value);
        }

        private void UpdateOutlineCameraFromSource()
        {
            if (outlineCamera == null || sourceCamera == null)
                return;

            outlineCamera.CopyFrom(sourceCamera);
            outlineCamera.renderingPath = RenderingPath.Forward;
            outlineCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            outlineCamera.clearFlags = CameraClearFlags.SolidColor;
            outlineCamera.rect = new Rect(0f, 0f, 1f, 1f);
            outlineCamera.cullingMask = 0;
            outlineCamera.targetTexture = renderTexture;
            outlineCamera.enabled = false;
            outlineCamera.allowHDR = false;
        }

        public sealed class Y4NGZHDRPOutlinePass : CustomPass
        {
            internal OutlineEffect Owner;

#if Y4NGZCOMPANY_CUSTOMPASS_PUBLIC
            public override void Execute(CustomPassContext ctx)
#else
            protected override void Execute(CustomPassContext ctx)
#endif
            {
                OutlineEffect owner = Owner;
                if (owner == null || ctx.cmd == null || ctx.hdCamera == null)
                    return;

                Camera camera = ctx.hdCamera.camera;
                if (camera == null || camera != owner.ResolveSourceCamera())
                    return;

                if (!owner.HasActiveOutlines())
                    return;

                int width = Mathf.Max(1, ctx.hdCamera.actualWidth);
                int height = Mathf.Max(1, ctx.hdCamera.actualHeight);
                owner.sourceCamera = camera;

                if (!owner.RenderOutlineBuffer(ctx.cmd))
                    return;
                if (owner.outlineOverlayMaterial == null || owner.renderTexture == null || !owner._outlineTextureValid)
                    return;

                owner.outlineOverlayMaterial.SetTexture(OutlineSourceId, owner.renderTexture);
                HDUtils.DrawFullScreen(
                    ctx.cmd,
                    owner.outlineOverlayMaterial,
                    ctx.cameraColorBuffer,
                    ctx.cameraDepthBuffer,
                    null,
                    0);

                owner.TraceHdrpComposite(width, height);
            }
        }

        private void DestroyMaterials()
        {
            for (int i = 0; i < _materialBuffer.Count; i++)
            {
                if (_materialBuffer[i] != null)
                    Destroy(_materialBuffer[i]);
            }

            _materialBuffer.Clear();
            if (outlineShaderMaterial != null) Destroy(outlineShaderMaterial);
            if (outlineOverlayMaterial != null) Destroy(outlineOverlayMaterial);
            if (_outlineEraseMaterial != null) Destroy(_outlineEraseMaterial);
            if (_outline1Material != null) Destroy(_outline1Material);
            if (_outline2Material != null) Destroy(_outline2Material);
            if (_outline3Material != null) Destroy(_outline3Material);
        }
    }
}

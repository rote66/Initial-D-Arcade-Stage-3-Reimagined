using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

// Unity owns every graphics object. The plugin publishes CPU scene records;
// this component never imports a native GPU texture or issues a plugin event.
public sealed class Idas3SceneRenderer : MonoBehaviour
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SceneCamera {
        public Vector3 eye, target, up;
        public float verticalFov, aspect, nearClip, farClip;
        public Vector4 viewport;
        public uint leftHanded;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SceneFrame {
        public uint size, version;
        public ulong frameGeneration, textureGeneration;
        public uint width, height, vertexCount, rangeCount, textureCount, overlayCount, viewCount, screenFadeArgb;
        public IntPtr vertices, ranges, textures, overlays, frameConstants, lightConstants, fogConstants;
        public SceneCamera mainCamera, mirrorCamera;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct SceneOverlay { public IntPtr surface; public uint width, height, flags, reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SceneRange {
        public uint first, count, texture, tsp, pcw, isp, gmp, flags, gloss, lightScope, viewMask, sourceIndex;
        public Vector4 lightDirection;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SceneVertex { public Vector3 position, normal; public Vector4 color; public Vector2 uv; public Vector4 offset; }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SceneTexture { public uint width, height; public ulong pixelCount; public IntPtr argb; }
    [DllImport(Idas3Native.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int Idas3SceneGetFrame(ref SceneFrame frame);
    [DllImport(Idas3Native.Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr Idas3SceneGetGeometryIds(ulong generation, uint rangeCount);

    public SceneFrame CurrentFrame { get; private set; }
    public Camera MainCamera => main;
    public Camera MirrorCamera => mirror;
    public Idas3GameOptions.Values HudOptions { get; set; } = new Idas3GameOptions.Values();
    public bool ShowGameHud { get; set; } = true;
    public Rect ViewportRect { get; private set; } = new Rect(0, 0, 1, 1);
    public int ActiveMeshCount { get; private set; }
    public int UploadedVertexCount { get; private set; }
    public int GeometryUploadCount { get; private set; }
    public int MaterialUpdateCount { get; private set; }
    internal int MaterialConfigurationCount {get;private set;}
    internal int QueueUpdateCount {get;private set;}
    internal int CreatedRangeObjects {get;private set;}
    internal int CreatedRenderers {get;private set;}
    internal int ResidentRenderers {get;private set;}
    internal int MeshBufferResizes {get;private set;}
    internal int VertexCacheResizes {get;private set;}
    internal long MeshBufferBytes {get;private set;}
    internal long VertexCacheBytes {get;private set;}
    internal long InactiveBufferBytes {get;private set;}
    internal long ActiveVertexBytes {get;private set;}
    internal long ScratchBufferBytes=>(indexScratch.IsCreated?(long)indexScratch.Length*4:0)+
        (metalScratch.IsCreated?(long)metalScratch.Length*UnsafeUtility.SizeOf<MetalVertex>():0);
    internal int TrimmedRanges {get;private set;}
    internal const long InactiveBufferBudget=16*1024*1024;
    internal int SourceRangeCount {get;private set;}
    internal int SceneCommandDraws {get;private set;}
    internal int SceneCommandRebuilds {get;private set;}
    internal int SharedMaterialEntries=>sharedMaterials.Count;
    internal int SharedMaterialRanges {get;private set;}
    internal int SharedMaterialConfigurations {get;private set;}
    internal bool SharedMaterialBaseline {get;set;}
    internal int SkippedQueueUpdates {get;private set;}
    internal int WarmupStates {get;private set;}
    internal int TotalWarmupStates=>warmedPipelines.Count;
    internal bool CanPrepareResources {get;set;}
    internal bool RendererReuse {get;set;}
    internal bool OrderedSubmission {get;set;}
    internal bool CanUseOrderedSubmission {get;set;}
    internal bool OrderedSubmissionActive {get;private set;}
    public int DepthCandidateCount => depthCandidates.Count;
    public int DepthDrawCount { get; private set; }
    public int DepthBufferRebuildCount { get; private set; }
    public int DepthMaterialCount => diagnosticDepthMaterialBaseline ? dedicatedDepthMaterials : depthMaterials.Count;
    public int DepthMaterialGroups { get; private set; }
    public int DepthSourceDrawCount { get; private set; }
    public int DepthBatchUploadVertices { get; private set; }
    public int DepthBatchRebuildCount { get; private set; }
    public int DepthBatchCapacityVertices { get; private set; }
    // Exclusions are before frustum culling, not measured GPU draw savings.
    public int MainViewExcludedRanges { get; private set; }
    public int MainViewExcludedVertices { get; private set; }
    public int MirrorViewExcludedRanges { get; private set; }
    public int MirrorViewExcludedVertices { get; private set; }
    public int UploadedTextureCount => textureCache.Count;
    internal int CachedRangeObjects=>objects.Count;
    private Camera main, mirror, backdrop, canvasClear;
    internal bool CameraStackBaseline { get; set; }
    private bool backdropRequired = true, canvasClearRequired = true;
    // Desired work is separate from Camera.enabled: capture tools temporarily
    // disable automatic rendering while manually rendering this same stack.
    internal bool RequiresCamera(Camera camera) => camera == backdrop ? backdropRequired :
        camera == canvasClear ? canvasClearRequired : camera != mirror || ShowGameHud && CurrentFrame.viewCount == 2;
    private static bool CoversOutput(Rect rect) => rect.xMin <= 0 && rect.yMin <= 0 && rect.xMax >= 1 && rect.yMax >= 1;
    private Shader sceneShader, directSceneShader, headlightShader, opaqueAlphaDepthShader;
    private bool metalGeometry;
    private VertexAttributeDescriptor[] meshLayout;
    private struct MetalVertex { public Vector3 position,normal; public Vector4 color,offset; public Vector3 face; }
    private bool geometryStageBaseline;
    private bool rangeReuseBaseline;
    private static unsafe void ScanPositionBounds(SceneVertex* data, int count, out Vector3 low, out Vector3 high) {
        float lx=data[0].position.x,ly=data[0].position.y,lz=data[0].position.z;
        float hx=lx,hy=ly,hz=lz;
        for(int i=0;i<count;++i){
            // Keep the exact float comparisons and +/- zero arithmetic of the
            // Vector3 path, including equal/NaN ordering. No SIMD reassociation.
            float x=data[i].position.x,y=data[i].position.y,z=data[i].position.z;
            float ax=x-0f,ay=y-0f,az=z-0f,bx=x+0f,by=y+0f,bz=z+0f;
            lx=lx<ax?lx:ax;ly=ly<ay?ly:ay;lz=lz<az?lz:az;
            hx=hx>bx?hx:bx;hy=hy>by?hy:by;hz=hz>bz?hz:bz;
        }
        low=new Vector3(lx,ly,lz);high=new Vector3(hx,hy,hz);
    }

    private bool sourceLightingBaseline, sourceCourseLightingReady;
    private CommandBuffer opaqueAlphaDepth, mirrorAlphaDepth;
    private readonly List<RangeObject> depthCandidates = new List<RangeObject>();
    private readonly List<DepthDraw> mainDepthDraws = new List<DepthDraw>(), mirrorDepthDraws = new List<DepthDraw>();
    private readonly List<RangeObject> depthScratch = new List<RangeObject>();
    private readonly Dictionary<DepthMaterialKey, DepthMaterialEntry> depthMaterials = new Dictionary<DepthMaterialKey, DepthMaterialEntry>();
    private bool diagnosticDepthMaterialBaseline;
    private bool diagnosticDepthBatchBaseline;
    private int dedicatedDepthMaterials;
    private static readonly Comparison<RangeObject> DepthMaterialOrder = (a, b) => {
        int order = a.depthMaterialOrder.CompareTo(b.depthMaterialOrder);
        return order != 0 ? order : a.depthGeometryOrder.CompareTo(b.depthGeometryOrder);
    };
    private readonly Dictionary<Material, DepthBatch> mainDepthBatches = new Dictionary<Material, DepthBatch>();
    private readonly Dictionary<Material, DepthBatch> mirrorDepthBatches = new Dictionary<Material, DepthBatch>();
    private readonly List<DepthBatch> mainBatchDraws = new List<DepthBatch>(), mirrorBatchDraws = new List<DepthBatch>();
    private readonly List<DepthBatch> depthBatchScratch = new List<DepthBatch>();
    private NativeArray<SceneVertex> depthVertexScratch;
    private readonly Vector4[] depthSidePlanes = new Vector4[4];
    private bool diagnosticDepthSubmissionBaseline;
    private bool diagnosticViewCullingBaseline;
    private ComputeBuffer framesBuffer, lightsBuffer, fogBuffer;
    private ComputeBuffer emptyLampCells, emptyLampLights;
    private readonly float[] frameWords = new float[184];
    private readonly int[] lightWords = new int[1248], fogWords = new int[136];
    [StructLayout(LayoutKind.Sequential)] private struct UInt4 { public uint x, y, z, w; }
    private readonly Vector4[] frameVectors = new Vector4[46];
    private readonly UInt4[] lightVectors = new UInt4[312], fogVectors = new UInt4[34];
    private readonly List<RangeObject> objects = new List<RangeObject>();
    private readonly Dictionary<ulong, Texture2D> textureCache = new Dictionary<ulong, Texture2D>();
    private readonly Transform[] owners = new Transform[4];
    private Transform worldRoot;
    private readonly Light[] lampObjects = new Light[8];
    private SceneTexture[] textureRecords = Array.Empty<SceneTexture>();
    private ulong textureGeneration = ulong.MaxValue;
    private bool initialized;
    private bool diagnosticFlipDepth, diagnosticNoCull, diagnosticCullBaseline, diagnosticAlphaDepthOff, diagnosticDepthCancellationBaseline;
    private bool diagnosticIntroFoliageBaseline;
    private bool diagnosticCarCull, diagnosticActorCullBaseline;
    private bool diagnosticPerfBaseline;
    private bool diagnosticCameraPose;
    private Vector3 diagnosticEye, diagnosticTarget;
    // Opaque, then punch-through, then translucent. Hoisted so the frame
    // loop allocates nothing.
    private static readonly uint[] SourceListOrder = { 0, 4, 2 };
    // Reused by SetCamera; it allocated one of these per camera per frame.
    private readonly float[] viewWordsScratch = new float[16];
    // Source depth-compare codes. Hoisted: this ran once per range per frame.
    private static readonly CompareFunction[] DepthComparisons = {
        CompareFunction.Never, CompareFunction.Greater, CompareFunction.Equal, CompareFunction.GreaterEqual,
        CompareFunction.Less, CompareFunction.NotEqual, CompareFunction.LessEqual, CompareFunction.Always };
    private const int SceneLayer = 30;
    private const int HiddenSceneLayer = 25, MainSceneLayer = 26, MirrorSceneLayer = 27;
    private const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
    private static readonly VertexAttributeDescriptor[] layout = {
        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4)
    };
    private readonly Dictionary<ulong, RangeObject> geometryLookup = new Dictionary<ulong, RangeObject>();
    private readonly List<RangeObject> selectedRanges = new List<RangeObject>();
    private int selectionStamp, trimCursor,freeRangeCursor;
    private bool appliedRendererReuse,releaseInactiveBuffers;
    private readonly RangeObject[] freeRanges=new RangeObject[23];
    private bool rangeCacheOff;
    private bool geometryIdsOff, validateGeometryIds;
    private IntPtr geometryIds;
    private CommandBuffer mainSceneCommands,mirrorSceneCommands;
    private readonly List<RangeObject> frameDraws=new List<RangeObject>();
    private readonly List<RangeObject> mainSceneDraws=new List<RangeObject>(),mirrorSceneDraws=new List<RangeObject>();
    private bool sceneCommandsDirty;
    private const int SharedMaterialLimit=1024,SharedMaterialRetained=512;
    private readonly Dictionary<SharedMaterialKey,SharedMaterialState> sharedMaterials=new Dictionary<SharedMaterialKey,SharedMaterialState>();
    private readonly List<SharedMaterialKey> retiredMaterials=new List<SharedMaterialKey>(16);
    private int materialStamp;
    private struct SharedMaterialKey : IEquatable<SharedMaterialKey> {
        internal SceneRange range;
        internal ulong generation;
        internal Shader shader;
        internal bool sourceLighting;
        internal SharedMaterialKey(SceneRange source,ulong textureGeneration,Shader materialShader,bool lighting=false){
            range=source;range.first=range.count=range.sourceIndex=0;
            generation=textureGeneration;shader=materialShader;sourceLighting=lighting;
        }
        public bool Equals(SharedMaterialKey other)=>generation==other.generation&&shader==other.shader&&
            sourceLighting==other.sourceLighting&&SameMaterial(range,other.range);
        public override bool Equals(object other)=>other is SharedMaterialKey value&&Equals(value);
        public override unsafe int GetHashCode(){
            var value=range;var words=(uint*)&value;
            unchecked{
                uint hash=2166136261;
                for(int i=0;i<16;++i)hash=(hash^words[i])*16777619;
                return (int)hash^generation.GetHashCode()^(shader!=null?shader.GetHashCode():0)^sourceLighting.GetHashCode();
            }
        }
    }
    private sealed class SharedMaterialState {
        internal Material scene;
        internal int usedStamp;
        internal bool retired;
    }
    private NativeArray<uint> indexScratch;
    private NativeArray<MetalVertex> metalScratch;
    private RenderTexture warmupTarget;
    private CommandBuffer warmupCommands;
    private bool warmupPending=true;
    private readonly HashSet<PipelineKey> warmedPipelines=new HashSet<PipelineKey>();
    private struct PipelineKey : IEquatable<PipelineKey> {
        internal Shader shader;
        internal uint state;
        public bool Equals(PipelineKey other)=>shader==other.shader&&state==other.state;
        public override bool Equals(object other)=>other is PipelineKey value&&Equals(value);
        public override int GetHashCode()=>unchecked((shader!=null?shader.GetHashCode():0)*397^(int)state);
    }
    internal bool SceneLayoutReused { get; private set; }
    internal int SceneLayoutRejectReason { get; private set; }
    private bool sceneLayoutBaseline, cachedSceneLayoutReady, cachedSceneLighting;
    private uint cachedSceneRangeCount, cachedSceneVertexCount, cachedSceneViewCount;
    private ulong cachedSceneTextureGeneration;
    private byte[] cachedSceneRanges = Array.Empty<byte>(), cachedSceneIdentities = Array.Empty<byte>();
    private readonly List<int> dynamicSceneRanges = new List<int>();

    private bool CanCacheSceneLayout => !RendererReuse && !OrderedSubmissionActive && !sceneLayoutBaseline && !diagnosticPerfBaseline && !rangeCacheOff &&
        !rangeReuseBaseline && !geometryIdsOff && !validateGeometryIds && !diagnosticNoCull && DepthCensus == null;

    private unsafe bool ReuseSceneLayout(SceneFrame frame) {
        SceneLayoutRejectReason = 1;
        if (!CanCacheSceneLayout || !cachedSceneLayoutReady || frame.rangeCount != cachedSceneRangeCount ||
            frame.vertexCount != cachedSceneVertexCount || frame.viewCount != cachedSceneViewCount ||
            frame.textureGeneration != cachedSceneTextureGeneration || sourceCourseLightingReady != cachedSceneLighting) return false;
        // Every source state word, offset, order, visibility bit and immutable
        // geometry identity must match. Cameras and lighting still update.
        fixed (byte* ranges = cachedSceneRanges, identities = cachedSceneIdentities) {
            SceneLayoutRejectReason = 2;
            if (UnsafeUtility.MemCmp(ranges, frame.ranges.ToPointer(), (long)frame.rangeCount * 64) != 0) return false;
            SceneLayoutRejectReason = 3;
            if (UnsafeUtility.MemCmp(identities, geometryIds.ToPointer(), (long)frame.rangeCount * 8) != 0) return false;
        }
        SceneLayoutRejectReason = 0;
        return true;
    }

    private unsafe void RememberSceneLayout(SceneFrame frame) {
        cachedSceneLayoutReady = false;
        if (!CanCacheSceneLayout || frame.rangeCount == 0) return;
        int rangeBytes = checked((int)frame.rangeCount * 64), identityBytes = checked((int)frame.rangeCount * 8);
        if (cachedSceneRanges.Length < rangeBytes) Array.Resize(ref cachedSceneRanges, rangeBytes);
        if (cachedSceneIdentities.Length < identityBytes) Array.Resize(ref cachedSceneIdentities, identityBytes);
        fixed (byte* ranges = cachedSceneRanges, identities = cachedSceneIdentities) {
            UnsafeUtility.MemCpy(ranges, frame.ranges.ToPointer(), rangeBytes);
            UnsafeUtility.MemCpy(identities, geometryIds.ToPointer(), identityBytes);
        }
        dynamicSceneRanges.Clear();
        for (int i = 0; i < frame.rangeCount; ++i) {
            var range = ((SceneRange*)frame.ranges.ToPointer())[i];
            uint list = (range.flags & 1) != 0 ? (range.pcw >> 24) & 7 : 0;
            if (range.count != 0 && range.viewMask != 0 && (list == 0 || list == 4 || list == 2) &&
                ((ulong*)geometryIds.ToPointer())[i] == 0) dynamicSceneRanges.Add(i);
        }
        cachedSceneRangeCount = frame.rangeCount; cachedSceneVertexCount = frame.vertexCount;
        cachedSceneViewCount = frame.viewCount; cachedSceneTextureGeneration = frame.textureGeneration;
        cachedSceneLighting = sourceCourseLightingReady; cachedSceneLayoutReady = true;
    }
    private sealed class RangeObject {
        public GameObject gameObject;
        public Mesh mesh;
        public MeshRenderer renderer;
        public Material material;
        public Material depthMaterial;
        public int depthMaterialOrder;
        public int depthGeometryOrder;
        public ulong geometryRevision;
        public int count, capacity, selectedStamp;
        public ulong geometryKey;
        public ulong geometryId;
        public RangeObject nextGeometry;
        public RangeObject nextFree;
        public NativeArray<SceneVertex> vertexCache;
        public bool cachedBillboard;
        public SceneRange materialRange;
        public ulong materialTextureGeneration;
        public int materialQueue;
        public bool materialConfigured, depthMaterialDirty, depthCandidate;
        public bool sharedMaterialDirty=true;
        public SharedMaterialState sharedMaterial;
        public Material commandMaterial;
        public uint parentScope = uint.MaxValue;
        public int layer = SceneLayer;
        public bool active = true;
        public Bounds bounds;
    }

    // The depth pass writes only nearest covered depth, with no colour,
    // blending or stencil side effects. Its submission order is immaterial.
    // Share only exactly equal source state; texture-generation changes clear
    // the cache. Geometry offsets/counts and colour render queues are not state.
    private readonly struct DepthMaterialKey : IEquatable<DepthMaterialKey> {
        private readonly SceneRange range;
        public DepthMaterialKey(SceneRange value) { value.first = value.count = value.sourceIndex = 0; range = value; }
        public bool Equals(DepthMaterialKey other) => SameRange(range, other.range);
        public override bool Equals(object other) => other is DepthMaterialKey key && Equals(key);
        public override int GetHashCode() {
            unchecked {
                uint h = range.texture;
                h = h * 31 + range.tsp; h = h * 31 + range.pcw; h = h * 31 + range.isp;
                h = h * 31 + range.gmp; h = h * 31 + range.flags; h = h * 31 + range.gloss;
                h = h * 31 + range.lightScope; h = h * 31 + range.viewMask;
                return (int)h ^ range.lightDirection.GetHashCode();
            }
        }
    }
    private sealed class DepthMaterialEntry {
        public Material material;
        public int order;
    }
    private readonly struct DepthDraw {
        public readonly RangeObject item;
        public readonly Material material;
        public DepthDraw(RangeObject range) { item = range; material = range.depthMaterial; }
    }
    private readonly struct DepthPart {
        public readonly RangeObject item;
        public readonly ulong revision;
        public DepthPart(RangeObject value) { item = value; revision = value.geometryRevision; }
    }
    private sealed class DepthBatch {
        public Mesh mesh, drawMesh;
        public Material material;
        public int capacity;
        public readonly List<DepthPart> parts = new List<DepthPart>();
    }

    private void ConfigureDepthMaterial(RangeObject item, SceneRange range) {
        if (diagnosticDepthMaterialBaseline) {
            if (item.depthMaterial == null) { item.depthMaterial = new Material(opaqueAlphaDepthShader); ++dedicatedDepthMaterials; }
            item.depthMaterial.CopyPropertiesFromMaterial(item.material);
            ConfigureDepthSourceWords(item.depthMaterial, range);
        } else {
            var key = new DepthMaterialKey(range);
            if (!depthMaterials.TryGetValue(key, out var entry)) {
                entry = new DepthMaterialEntry { material = new Material(opaqueAlphaDepthShader), order = depthMaterials.Count };
                entry.material.CopyPropertiesFromMaterial(item.material);
                ConfigureDepthSourceWords(entry.material, range);
                depthMaterials.Add(key, entry);
            }
            item.depthMaterial = entry.material; item.depthMaterialOrder = entry.order;
        }
        item.depthMaterialDirty = false;
    }
    private void ClearSharedDepthMaterials() {
        opaqueAlphaDepth?.Clear(); mirrorAlphaDepth?.Clear();
        foreach (var batch in mainDepthBatches.Values) if (batch.mesh != null) Destroy(batch.mesh);
        foreach (var batch in mirrorDepthBatches.Values) if (batch.mesh != null) Destroy(batch.mesh);
        mainDepthBatches.Clear(); mirrorDepthBatches.Clear();
        mainBatchDraws.Clear(); mirrorBatchDraws.Clear(); depthBatchScratch.Clear();
        if (depthVertexScratch.IsCreated) depthVertexScratch.Dispose();
        DepthBatchCapacityVertices = 0;
        foreach (var entry in depthMaterials.Values) Destroy(entry.material);
        depthMaterials.Clear(); mainDepthDraws.Clear(); mirrorDepthDraws.Clear();
    }

    public void Initialize(Camera camera)
    {
        rangeCacheOff = Array.IndexOf(Idas3LaunchArguments.GetArguments(), "-idas3-range-cache-off") >= 0;
        if (initialized) throw new InvalidOperationException("Scene renderer is already initialized.");
        if (Marshal.SizeOf<SceneFrame>() != 256 || Marshal.SizeOf<SceneCamera>() != 72 || Marshal.SizeOf<SceneRange>() != 64 || Marshal.SizeOf<SceneVertex>() != 64)
            throw new InvalidOperationException("Unity scene ABI mismatch.");
        main = camera != null ? camera : throw new ArgumentNullException(nameof(camera));
        string[] arguments = Idas3LaunchArguments.GetArguments();
        sceneLayoutBaseline = Array.IndexOf(arguments, "-idas3-scene-layout-baseline") >= 0;
        geometryIdsOff = Array.IndexOf(arguments, "-idas3-geometry-ids-off") >= 0;
        validateGeometryIds = Array.IndexOf(arguments, "-idas3-validate-geometry-ids") >= 0;
        diagnosticFlipDepth = Array.IndexOf(arguments, "-idas3-scene-depth-test-flip") >= 0;
        diagnosticNoCull = Array.IndexOf(arguments, "-idas3-scene-no-cull") >= 0;
        diagnosticCullBaseline = Array.IndexOf(arguments, "-idas3-scene-cull-baseline") >= 0;
        diagnosticAlphaDepthOff = Array.IndexOf(arguments, "-idas3-scene-alpha-depth-off") >= 0;
        diagnosticDepthCancellationBaseline = Array.IndexOf(arguments, "-idas3-scene-depth-cancellation-baseline") >= 0;
        diagnosticIntroFoliageBaseline = Array.IndexOf(arguments, "-idas3-scene-intro-foliage-baseline") >= 0;
        diagnosticCarCull = Array.IndexOf(arguments, "-idas3-scene-car-cull-check") >= 0 &&
            Array.IndexOf(arguments, "-idas3-scene-car-door-check") >= 0;
        diagnosticActorCullBaseline = Array.IndexOf(arguments, "-idas3-scene-actor-cull-baseline") >= 0;
        diagnosticPerfBaseline = Array.IndexOf(arguments, "-idas3-scene-perf-baseline") >= 0;
        diagnosticDepthSubmissionBaseline = Array.IndexOf(arguments, "-idas3-depth-submission-baseline") >= 0;
        diagnosticDepthMaterialBaseline = Array.IndexOf(arguments, "-idas3-depth-material-baseline") >= 0;
        diagnosticDepthBatchBaseline = Array.IndexOf(arguments, "-idas3-depth-batch-baseline") >= 0;
        diagnosticViewCullingBaseline = Array.IndexOf(arguments, "-idas3-scene-view-culling-baseline") >= 0;
        if (diagnosticFlipDepth || diagnosticNoCull)
            Debug.Log("IDAS3 scene diagnostics: depthTestFlip=" + diagnosticFlipDepth + ", noCull=" + diagnosticNoCull);
        metalGeometry=SystemInfo.graphicsDeviceType==GraphicsDeviceType.Metal;
        sceneShader = metalGeometry ? Resources.Load<Shader>("Idas3SceneDirect") : Shader.Find("IDAS3/Original Scene Material");
        if (sceneShader == null || !sceneShader.isSupported) throw new InvalidOperationException("Original Unity scene shader is unavailable.");
        meshLayout=layout;
        if(metalGeometry){
            var attributes=new List<VertexAttributeDescriptor>(layout);
            attributes.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord3,VertexAttributeFormat.Float32,3,1));
            attributes.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord4,VertexAttributeFormat.Float32,3,1));
            attributes.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord5,VertexAttributeFormat.Float32,4,1));
            attributes.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord6,VertexAttributeFormat.Float32,4,1));
            attributes.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord7,VertexAttributeFormat.Float32,3,1));
            meshLayout=attributes.ToArray();
        }
        directSceneShader = Resources.Load<Shader>("Idas3SceneDirect");
        if (directSceneShader == null || !directSceneShader.isSupported) throw new InvalidOperationException("Direct Unity scene shader is unavailable.");
        headlightShader = Resources.Load<Shader>("Idas3Headlight");
        if (headlightShader == null || !headlightShader.isSupported) throw new InvalidOperationException("Projected headlight shader is unavailable.");
        geometryStageBaseline = Array.IndexOf(arguments, "-idas3-geometry-stage-baseline") >= 0;
        rangeReuseBaseline = Array.IndexOf(arguments, "-idas3-range-reuse-baseline") >= 0;
        sourceLightingBaseline = Array.IndexOf(arguments, "-idas3-source-lighting-baseline") >= 0;
        opaqueAlphaDepthShader = metalGeometry ? Resources.Load<Shader>("Idas3OpaqueAlphaDepthDirect") : Shader.Find("Hidden/IDAS3/Opaque Alpha Depth");
        if (opaqueAlphaDepthShader == null || !opaqueAlphaDepthShader.isSupported) throw new InvalidOperationException("Original opaque-alpha depth shader is unavailable.");
        // This component shares the host camera GameObject. Captured vertices
        // already contain their complete world transform, so their identity
        // mesh hierarchy must not inherit that moving camera transform. Unity
        // uses Renderer.localToWorld for CPU bounds even when our shader uses
        // the authored world positions directly.
        worldRoot = new GameObject("Original world-space scene").transform;
        string[] names = { "World and effects", "Course", "Player car", "Opponent car" };
        for (int i = 0; i < owners.Length; ++i) { owners[i] = new GameObject(names[i]).transform; owners[i].SetParent(worldRoot, false); }
        backdrop = NewCamera("Scene clear", -1000); backdrop.cullingMask = 0; backdrop.clearFlags = CameraClearFlags.SolidColor;
        canvasClear = NewCamera("Original canvas clear", -2); canvasClear.cullingMask = 0; canvasClear.clearFlags = CameraClearFlags.SolidColor;
        // These cameras only clear solid colors. HDR would allocate a floating-point
        // intermediate even though the scene cameras render into an LDR target.
        // Keep the previous defaults available only for matched diagnostic captures.
        CameraStackBaseline = Array.IndexOf(arguments, "-idas3-camera-stack-baseline") >= 0;
        bool clearCameraBaseline = Array.IndexOf(arguments, "-idas3-clear-camera-hdr-baseline") >= 0;
        backdrop.allowHDR = canvasClear.allowHDR = clearCameraBaseline;
        backdrop.allowMSAA = canvasClear.allowMSAA = clearCameraBaseline;
        mirror = NewCamera("Original rear-view camera", 1);
        main.depth = 0; ConfigureCommonCamera(main, 1); ConfigureCommonCamera(mirror, 2);
        // The source translucent sorter lets a fully opaque texel hide every
        // farther fragment, even when its polygon is in the translucent list.
        // Establish only those fully opaque depths before scene color passes.
        // Each camera executes this buffer against its own cleared depth view.
        opaqueAlphaDepth = new CommandBuffer { name = "Original opaque-alpha occlusion" };
        mirrorAlphaDepth = new CommandBuffer { name = "Original mirror opaque-alpha occlusion" };
        main.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, opaqueAlphaDepth);
        mirror.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, mirrorAlphaDepth);
        framesBuffer = new ComputeBuffer(46, 16, ComputeBufferType.Structured);
        lightsBuffer = new ComputeBuffer(312, 16, ComputeBufferType.Structured);
        fogBuffer = new ComputeBuffer(34, 16, ComputeBufferType.Structured);
        // Metal requires every declared buffer, even for day/no-lamp branches.
        emptyLampCells = new ComputeBuffer(1, 8, ComputeBufferType.Structured);
        emptyLampCells.SetData(new[] { Vector2Int.zero });
        emptyLampLights = new ComputeBuffer(1, 16, ComputeBufferType.Structured);
        emptyLampLights.SetData(new[] { Vector4.zero });
        Shader.SetGlobalBuffer("_ImportedLampCells", emptyLampCells);
        Shader.SetGlobalBuffer("_ImportedLampLights", emptyLampLights);
        Camera.onPreRender += BeforeCamera;
        initialized = true;
    }
    private Camera NewCamera(string name, float depth)
    {
        var obj = new GameObject(name); obj.transform.SetParent(transform, false);
        var camera = obj.AddComponent<Camera>(); camera.depth = depth; return camera;
    }
    private static int LayerForViewMask(uint viewMask)
    {
        switch (viewMask & 3) {
            case 1: return MainSceneLayer;
            case 2: return MirrorSceneLayer;
            case 3: return SceneLayer;
            default: return HiddenSceneLayer;
        }
    }
    internal static bool IsSceneGeometryLayer(int layer) => layer == SceneLayer ||
        layer == MainSceneLayer || layer == MirrorSceneLayer || layer == HiddenSceneLayer;
    private static void ConfigureCommonCamera(Camera camera, uint viewBit)
    {
        // Imported scenery uses 28; challenger UI exclusively owns 29.
        // Both world cameras must see the course, including the rear-view.
        // View-only ranges used to be rejected in the fragment shader, after
        // submission, vertex lighting and geometry processing in both views.
        camera.cullingMask = (1 << SceneLayer) | (1 << 28) | (1 << LayerForViewMask(viewBit));
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.allowHDR = false; camera.allowMSAA = true; camera.useOcclusionCulling = false;
    }
    private void BeforeCamera(Camera camera)
    {
        if (camera != main && camera != mirror) return;
        Shader.SetGlobalFloat("_IdasTrackLighting", HudOptions.trackLighting);
        Shader.SetGlobalInteger("_IdasView", camera == mirror ? 1 : 0);
        Shader.SetGlobalBuffer("_IdasFrameWords", framesBuffer);
        Shader.SetGlobalBuffer("_IdasLightWords", lightsBuffer);
        Shader.SetGlobalBuffer("_IdasFogWords", fogBuffer);
        Shader.SetGlobalBuffer("_ImportedLampCells", emptyLampCells);
        Shader.SetGlobalBuffer("_ImportedLampLights", emptyLampLights);
        // Form reversed depth directly from clip W. Subtracting forward clip Z
        // from W loses the tiny near-plane term in distant-world intro shots.
        // Double precision here also keeps the coefficients accurate for the
        // opening camera's .01 .. 10000 range. Each mirror has its own planes.
        var source = camera == mirror ? CurrentFrame.mirrorCamera : CurrentFrame.mainCamera;
        double near = source.nearClip, far = source.farClip;
        Shader.SetGlobalVector("_IdasDepthProjection", new Vector4(
            (float)(near * far / (far - near)), (float)(near / (far - near)), 0,
            diagnosticDepthCancellationBaseline ? 0 : 1));
    }
    internal void ConfigureMobilePerformance(bool enabled)
    {
        RendererReuse=OrderedSubmission=enabled;
        if(!enabled)ReleaseWarmup();
    }
    private void ApplyReuseTransition()
    {
        if(appliedRendererReuse==RendererReuse)return;
        appliedRendererReuse=RendererReuse;
        cachedSceneLayoutReady=false;
        // Drop recorded references before changing buffer ownership. The next
        // submission rebuilds both camera paths from the same native frame.
        mainSceneCommands?.Clear();mirrorSceneCommands?.Clear();
        opaqueAlphaDepth.Clear();mirrorAlphaDepth.Clear();
        mainSceneDraws.Clear();mirrorSceneDraws.Clear();mainDepthDraws.Clear();mirrorDepthDraws.Clear();
        selectedRanges.Clear();geometryLookup.Clear();sceneCommandsDirty=true;
        foreach(var item in objects){
            item.geometryId=item.geometryKey=0;item.count=-1;item.materialConfigured=false;
            item.sharedMaterial=null;item.sharedMaterialDirty=true;
            item.commandMaterial=item.material;
            if(!RendererReuse&&item.vertexCache.IsCreated)item.vertexCache.Dispose();
            if(RendererReuse&&OrderedSubmission&&CanUseOrderedSubmission&&item.gameObject!=null){
                item.renderer.enabled=false;Destroy(item.gameObject);
                item.gameObject=null;item.renderer=null;--ResidentRenderers;
            }
        }
        if(!RendererReuse){
            if(indexScratch.IsCreated)indexScratch.Dispose();
            if(metalScratch.IsCreated)metalScratch.Dispose();
            foreach(var state in sharedMaterials.Values){
                state.retired=true;Destroy(state.scene);
            }
            sharedMaterials.Clear();retiredMaterials.Clear();
            ReleaseWarmup();releaseInactiveBuffers=true;
        }
    }
    private void ReleaseWarmup()
    {
        warmupCommands?.Release();warmupCommands=null;
        if(warmupTarget!=null){warmupTarget.Release();Destroy(warmupTarget);warmupTarget=null;}
        warmedPipelines.Clear();warmupPending=true;
    }
    private void CreateSceneCommands()
    {
        mainSceneCommands=new CommandBuffer{name="Original ordered scene submission"};
        mirrorSceneCommands=new CommandBuffer{name="Original ordered mirror submission"};
        main.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,mainSceneCommands);
        mirror.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,mirrorSceneCommands);
    }
    private void ReleaseSceneCommands()
    {
        if(mainSceneCommands!=null){main.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,mainSceneCommands);mainSceneCommands.Release();mainSceneCommands=null;}
        if(mirrorSceneCommands!=null){mirror.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,mirrorSceneCommands);mirrorSceneCommands.Release();mirrorSceneCommands=null;}
        mainSceneDraws.Clear();mirrorSceneDraws.Clear();
    }
    public unsafe void ApplyFrame()
    {
        if (!initialized) throw new InvalidOperationException("Initialize the Unity scene renderer first.");
        ApplyReuseTransition();
        UploadedVertexCount = GeometryUploadCount = MaterialUpdateCount = 0;
        MaterialConfigurationCount=QueueUpdateCount=SkippedQueueUpdates=CreatedRangeObjects=SourceRangeCount=WarmupStates=0;
        CreatedRenderers=MeshBufferResizes=VertexCacheResizes=TrimmedRanges=0;
        SceneCommandDraws=SceneCommandRebuilds=SharedMaterialRanges=SharedMaterialConfigurations=0;
        frameDraws.Clear();
        bool ordered=OrderedSubmission&&CanUseOrderedSubmission&&!diagnosticPerfBaseline;
        if(OrderedSubmissionActive!=ordered){
            cachedSceneLayoutReady=false;
            OrderedSubmissionActive=ordered;sceneCommandsDirty=true;
            if(ordered)CreateSceneCommands();else ReleaseSceneCommands();
            foreach(var item in objects){
                if(!ordered&&item.active)EnsureRenderer(item);
                if(item.renderer!=null)item.renderer.enabled=!ordered;
                if(!ordered&&item.materialConfigured){item.material.renderQueue=item.materialQueue;++MaterialUpdateCount;++QueueUpdateCount;}
            }
        }
        Idas3.Multiplayer.Idas3OnlineTransportPump.KeepAliveDuringNativeLoad();
        var frame = new SceneFrame { size = 256 };
        if (Idas3SceneGetFrame(ref frame) != 1 || frame.version != 1) throw new InvalidOperationException("Native scene frame is unavailable.");
        if (frame.frameGeneration == 0) return;
        if (frame.vertexCount > 2000000 || frame.rangeCount > 4000 || frame.viewCount < 1 || frame.viewCount > 2)
            throw new InvalidOperationException("Invalid Unity scene bounds.");
        CurrentFrame = frame;
        geometryIds = Idas3SceneGetGeometryIds(frame.frameGeneration, frame.rangeCount);
        if (geometryIds == IntPtr.Zero && frame.rangeCount != 0)
            throw new InvalidOperationException("Native geometry identities do not match the scene frame.");
        Marshal.Copy(frame.frameConstants, frameWords, 0, frameWords.Length);
        Marshal.Copy(frame.lightConstants, lightWords, 0, lightWords.Length);
        Marshal.Copy(frame.fogConstants, fogWords, 0, fogWords.Length);
        // Source records stay available in CurrentFrame. Only Unity's display
        // projection expands a fixed authored main aperture to the full output.
        // Use these same adjusted words for shader upload and CPU culling.
        SceneCamera outputMainCamera = ExpandMainViewport(frame.mainCamera, frame.width, frame.height, frameWords);
        if (diagnosticCameraPose) ApplyDiagnosticCamera(ref outputMainCamera);
        for (int i = 0; i < frameVectors.Length; ++i) frameVectors[i] = new Vector4(frameWords[i * 4], frameWords[i * 4 + 1], frameWords[i * 4 + 2], frameWords[i * 4 + 3]);
        PackWords(lightWords, lightVectors); PackWords(fogWords, fogVectors);
        framesBuffer.SetData(frameVectors); lightsBuffer.SetData(lightVectors); fogBuffer.SetData(fogVectors);
        // Scope one is the original course. The shared material must remain
        // valid for every active camera, including a reflected rear view.
        bool sourceLighting = !sourceLightingBaseline && frameWords[43] == 0 && lightWords[156 + 152] != 0 &&
            (frame.viewCount < 2 || (frameWords[92 + 43] == 0 && lightWords[5 * 156 + 152] != 0));
        if (sourceLighting != sourceCourseLightingReady) {
            sourceCourseLightingReady = sourceLighting;
            foreach (var item in objects) item.materialConfigured = false;
        }
        if (textureGeneration != frame.textureGeneration)
        {
            ClearSharedDepthMaterials();
            foreach (var texture in textureCache.Values) Destroy(texture); textureCache.Clear();
            textureRecords = new SceneTexture[frame.textureCount];
            for (int i = 0; i < textureRecords.Length; ++i) textureRecords[i] = Marshal.PtrToStructure<SceneTexture>(IntPtr.Add(frame.textures, i * 24));
            textureGeneration = frame.textureGeneration;
        }
        SceneLayoutReused = ReuseSceneLayout(frame);
        if (!SceneLayoutReused) MainViewExcludedRanges = MainViewExcludedVertices = MirrorViewExcludedRanges = MirrorViewExcludedVertices = 0;
        var source = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<SceneVertex>(frame.vertices.ToPointer(), (int)frame.vertexCount, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        var safety = AtomicSafetyHandle.Create(); NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref source, safety);
#endif
        try
        {
            if (!SceneLayoutReused) {
                ActiveMeshCount = 0; depthCandidates.Clear(); SelectCachedRanges(frame);
            }
            // Exact source list ordering, then exact submission ordering.
            // Distinct queues prevent Unity's distance sorting from changing
            // authored alpha and projected-light destination-color blending.
            int rank = 0;
            foreach (uint list in SourceListOrder)
            for (int cursor = 0, count = SceneLayoutReused ? dynamicSceneRanges.Count : (int)frame.rangeCount; cursor < count; ++cursor)
            {
                int i = SceneLayoutReused ? dynamicSceneRanges[cursor] : cursor;
                // First-time mesh/material uploads can span several seconds.
                // Pump only the encrypted route; never dispatch game callbacks
                // while native frame pointers are being consumed.
                if ((i & 63) == 0) Idas3.Multiplayer.Idas3OnlineTransportPump.KeepAliveDuringNativeLoad();
                var range = *(SceneRange*)IntPtr.Add(frame.ranges, i * 64);
                uint nativeList = (range.flags & 1) != 0 ? (range.pcw >> 24) & 7 : 0;
                if (nativeList != list || range.count == 0 || range.viewMask == 0) continue;
                if ((ulong)range.first + range.count > frame.vertexCount || range.lightScope > 3)
                    throw new InvalidOperationException("Scene range is outside its vertex/light bounds.");
                var item = diagnosticPerfBaseline || rangeCacheOff ? GetRangeObject(ActiveMeshCount) : selectedRanges[i] ?? ClaimRangeObject((int)range.count);
                if(!diagnosticPerfBaseline&&!rangeCacheOff)selectedRanges[i]=item;
                if(!OrderedSubmissionActive||!RendererReuse)EnsureRenderer(item);
                if(!SceneLayoutReused)++ActiveMeshCount;
                ++SourceRangeCount;
                if(OrderedSubmissionActive)frameDraws.Add(item);
                int layer = diagnosticViewCullingBaseline ? SceneLayer : LayerForViewMask(range.viewMask);
                if (item.layer != layer) { if(item.gameObject!=null)item.gameObject.layer = layer; item.layer = layer; }
                if (!SceneLayoutReused && !diagnosticViewCullingBaseline) {
                    if ((range.viewMask & 1) == 0) { ++MainViewExcludedRanges; MainViewExcludedVertices += (int)range.count; }
                    if (frame.viewCount == 2 && (range.viewMask & 2) == 0) { ++MirrorViewExcludedRanges; MirrorViewExcludedVertices += (int)range.count; }
                }
                int queue = SceneLayoutReused ? item.materialQueue : 1000 + rank++;
                CountDepthMode(range);
                ulong geometryId = geometryIdsOff ? 0 : ((ulong*)geometryIds.ToPointer())[i];
                // Native identities guarantee immutable vertex bytes. A cached
                // active renderer with identical material/ownership needs only
                // its source-order queue maintained. Camera/light globals still
                // update and Unity still culls/renders both views normally.
                if (!rangeReuseBaseline && !diagnosticPerfBaseline && !diagnosticNoCull && !validateGeometryIds &&
                    geometryId != 0 && item.geometryId == geometryId && item.active && item.vertexCache.IsCreated &&
                    item.parentScope == range.lightScope && item.materialConfigured &&
                    item.materialTextureGeneration == textureGeneration && SameRange(item.materialRange, range)) {
                    if (item.materialQueue != queue) {
                        SetQueue(item,queue);
                    }
                    if (!SceneLayoutReused && item.depthCandidate) depthCandidates.Add(item);
                    continue;
                }
                if (diagnosticPerfBaseline || !item.active) { if(item.gameObject!=null)item.gameObject.SetActive(true); item.active = true; }
                if (diagnosticPerfBaseline || item.parentScope != range.lightScope) {
                    if(item.gameObject!=null)item.gameObject.transform.SetParent(owners[range.lightScope], false);
                    item.parentScope = range.lightScope;
                }
                bool countChanged=SetRangeCount(item,(int)range.count);
                bool billboard = (range.flags & 4) != 0;
                var rangeVertices = (SceneVertex*)frame.vertices.ToPointer() + (int)range.first;
                long vertexBytes = (long)range.count * 64;
                // The scene ABI publishes complete world vertices each frame,
                // including static course geometry. Immutable cached ranges
                // have native identities; dynamic/untagged ranges still get
                // an exact byte comparison. Lighting/camera buffers update.
                bool immutableMatch = !countChanged&&geometryId != 0 && geometryId == item.geometryId && item.vertexCache.IsCreated && item.cachedBillboard == billboard;
                if (validateGeometryIds && immutableMatch &&
                    UnsafeUtility.MemCmp(rangeVertices, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(item.vertexCache), vertexBytes) != 0)
                    throw new InvalidOperationException("An immutable native geometry identity changed its vertices.");
                bool geometryChanged = diagnosticPerfBaseline || countChanged || !item.vertexCache.IsCreated || item.cachedBillboard != billboard || (!immutableMatch &&
                    UnsafeUtility.MemCmp(rangeVertices, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(item.vertexCache), vertexBytes) != 0);
                if (geometryChanged) {
                    ++item.geometryRevision;
                    item.mesh.SetVertexBufferData(source, (int)range.first, 0, (int)range.count, 0, UploadFlags);
                    if(metalGeometry)UploadMetalVertices(item.mesh,rangeVertices,(int)range.count);
                    UploadedVertexCount += (int)range.count; ++GeometryUploadCount;
                    var first = source[(int)range.first];
                    Vector3 low = billboard ? first.normal : first.position, high = low;
                    // Ordinary world geometry needs positions only. Avoid copying
                    // every 64-byte vertex and creating Vector3 temporaries for
                    // a zero billboard radius on each frame. Billboard bounds
                    // retain their conservative camera-independent expansion.
                    if (!billboard) ScanPositionBounds(rangeVertices, (int)range.count, out low, out high);
                    else {
                        for (int j = 0; j < range.count; ++j)
                        {
                            var vertex = source[(int)range.first + j]; Vector3 position = billboard ? vertex.normal : vertex.position;
                            // Lit spectator cards pack their source normal in the unused
                            // planar Z slot; this must not enlarge geometric bounds.
                            Vector3 local = vertex.position;
                            if (billboard && (range.gmp & 512) == 0) local.z = 0;
                            Vector3 radius = billboard ? Vector3.one * local.magnitude : Vector3.zero;
                            low = Vector3.Min(low, position - radius); high = Vector3.Max(high, position + radius);
                        }
                    }
                    item.bounds = diagnosticNoCull
                        ? new Bounds(frame.mainCamera.eye, Vector3.one * 20000000f)
                        : new Bounds((low + high) * .5f, Vector3.Max(high - low, Vector3.one * .001f));
                    item.mesh.bounds = item.bounds;
                    if (!diagnosticPerfBaseline) {
                        EnsureVertexCache(item,(int)range.count);
                        UnsafeUtility.MemCpy(NativeArrayUnsafeUtility.GetUnsafePtr(item.vertexCache), rangeVertices, vertexBytes);
                        item.cachedBillboard = billboard;
                        item.geometryKey = GeometryKey(rangeVertices, (int)range.count, billboard);
                    }
                }
                else if (diagnosticNoCull) { item.bounds = new Bounds(frame.mainCamera.eye, Vector3.one * 20000000f); item.mesh.bounds = item.bounds; }
                item.geometryId = geometryId;
                // Include all source words, override direction, view mask,
                // submission order and texture ownership in the exact key.
                // Lighting and diagnostic camera changes are shader globals.
                if (diagnosticPerfBaseline || !item.materialConfigured ||
                    item.materialTextureGeneration != textureGeneration ||
                    !(RendererReuse?SameMaterial(item.materialRange,range):SameRange(item.materialRange,range))) {
                    ConfigureMaterial(item.material, range, queue); ++MaterialUpdateCount;++MaterialConfigurationCount;
                    item.materialRange = range; item.materialQueue = queue; item.materialTextureGeneration = textureGeneration;
                    item.materialConfigured = true; item.depthMaterialDirty = item.sharedMaterialDirty = true;
                }
                else if (item.materialQueue != queue) {
                    SetQueue(item,queue);
                }
                item.materialRange=range;
                item.depthCandidate = !diagnosticAlphaDepthOff && (range.flags & 1) != 0 && IsCourseGeometry(range) && nativeList == 2 &&
                    ((range.tsp >> 29) & 7) == 4 && ((range.tsp >> 26) & 7) == 5;
                if (item.depthCandidate)
                {
                    if (diagnosticPerfBaseline || item.depthMaterialDirty || item.depthMaterial == null)
                        ConfigureDepthMaterial(item, range);
                    if (!SceneLayoutReused) depthCandidates.Add(item);
                }
            }
            if (!SceneLayoutReused) for (int i = 0; i < objects.Count; ++i) {
                var item = objects[i];
                if (diagnosticPerfBaseline || rangeCacheOff ? i < ActiveMeshCount : item.selectedStamp == selectionStamp) continue;
                if (diagnosticPerfBaseline || item.active) { if(item.gameObject!=null)item.gameObject.SetActive(false); item.active = false; }
            }
            if(RendererReuse&&CanPrepareResources){
                int target=Math.Min(4000,ActiveMeshCount+128),limit=Math.Min(target,objects.Count+32);
                while(objects.Count<limit)GetRangeObject(objects.Count);
            }
        }
        finally
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(safety);
#endif
        }
        if (!SceneLayoutReused) RememberSceneLayout(frame);
        SourceRangeCount=ActiveMeshCount;
        Color clear = new Color(frameWords[20], frameWords[21], frameWords[22], 1);
        // The host captures at the current output dimensions. Native race/demo
        // projection uses that aspect, so the complete canvas fills the window.
        // Fixed main apertures expand their frustum without changing the
        // authored center composition; only the 5:1 mirror stays a sub-viewport.
        ViewportRect = new Rect(0, 0, 1, 1);
        backdrop.targetTexture = canvasClear.targetTexture = mirror.targetTexture = main.targetTexture;
        backdrop.backgroundColor = Color.black; backdrop.rect = new Rect(0, 0, 1, 1);
        // Clear the complete output before the source background UI camera (-1).
        canvasClear.backgroundColor = clear; canvasClear.rect = ViewportRect;
        bool behind = false;
        for (int i = 0; i < frame.overlayCount; ++i) behind |= ((*(SceneOverlay*)IntPtr.Add(frame.overlays, i * 24)).flags & 1) != 0;
        main.clearFlags = behind ? CameraClearFlags.Depth : CameraClearFlags.SolidColor;
        SetCamera(main, outputMainCamera, 0, clear, frame.width, frame.height);
        // A full main color clear overwrites the earlier canvas clear. Retain
        // it for menu apertures and backgrounds drawn before the 3D scene.
        bool mainClearsOutput = main.clearFlags == CameraClearFlags.SolidColor && CoversOutput(main.rect);
        canvasClearRequired = CameraStackBaseline || !mainClearsOutput;
        // The canvas itself covers the complete target, so an earlier black
        // backdrop only has work if neither following clear covers the output.
        backdropRequired = CameraStackBaseline || !mainClearsOutput && !CoversOutput(canvasClear.rect);
        canvasClear.enabled = canvasClearRequired; backdrop.enabled = backdropRequired;
        mirror.enabled = ShowGameHud && frame.viewCount == 2;
        if (mirror.enabled) SetCamera(mirror, frame.mirrorCamera, 1, clear, frame.width, frame.height);
        if(OrderedSubmissionActive)UpdateCommandMaterials();
        DepthDrawCount = DepthSourceDrawCount = DepthBufferRebuildCount = DepthMaterialGroups = 0;
        DepthBatchUploadVertices = DepthBatchRebuildCount = 0;
        UpdateDepthCommands(main, 1, opaqueAlphaDepth, mainDepthDraws, true);
        UpdateDepthCommands(mirror, 2, mirrorAlphaDepth, mirrorDepthDraws, mirror.enabled);
        UpdateSceneCommands(main,1,mainSceneCommands,mainSceneDraws,OrderedSubmissionActive);
        UpdateSceneCommands(mirror,2,mirrorSceneCommands,mirrorSceneDraws,OrderedSubmissionActive&&mirror.enabled);
        sceneCommandsDirty=false;
        UpdateLampObjects();
        if(RendererReuse||releaseInactiveBuffers){
            UpdateBufferAccounting();
            TrimInactiveBuffers(RendererReuse?InactiveBufferBudget:0);
            releaseInactiveBuffers=!RendererReuse&&InactiveBufferBytes>0;
        }
        if(RendererReuse)TrimSharedMaterials();
        WarmPipelines();
    }
    internal void VerifyMobileKeys(Action<bool,string> check)
    {
        var range=new SceneRange{count=3,flags=1,lightScope=1,viewMask=3,tsp=1u<<29};
        var changed=range;changed.first=99;changed.count=6;changed.sourceIndex=37;
        check(SameMaterial(range,changed)&&!SameRange(range,changed),"Geometry size is not part of the material key");
        changed=range;changed.texture=1;check(!SameMaterial(range,changed),"Texture is part of the exact material key");
        changed=range;changed.viewMask=1;check(!SameMaterial(range,changed),"View mask is part of the exact material key");
        changed=range;changed.lightDirection.x=1;check(!SameMaterial(range,changed),"Light direction is part of the exact material key");
        changed=range;changed.flags|=4;check(!SameMaterial(range,changed),"Billboards retain their shader state");
        changed=range;changed.pcw=2u<<24;check(!SameMaterial(range,changed),"Transparent list retains its shader state");
        changed=range;changed.isp=1u<<26;check(!SameMaterial(range,changed),"Depth-write state is part of the exact material key");
        changed=range;changed.lightScope=2;check(!SameMaterial(range,changed),"Actor lighting ownership is part of the exact material key");
        var key=new SharedMaterialKey(range,7,directSceneShader);
        changed=range;changed.first=99;changed.count=6;changed.sourceIndex=37;
        var same=new SharedMaterialKey(changed,7,directSceneShader);
        check(key.Equals(same)&&key.GetHashCode()==same.GetHashCode(),"Shared state ignores geometry offsets and counts");
        check(!key.Equals(new SharedMaterialKey(range,8,directSceneShader)),"Shared state retains texture ownership");
        check(!key.Equals(new SharedMaterialKey(range,7,headlightShader)),"Shared state retains its shader");
        check(!key.Equals(new SharedMaterialKey(range,7,directSceneShader,true)),"Shared state retains source lighting variants");
        var variant=new Material(directSceneShader);
        try {
            var plain=Pipeline(variant);
            variant.EnableKeyword("IDAS_NATIVE_SOURCE_COURSE");var course=Pipeline(variant);
            variant.DisableKeyword("IDAS_NATIVE_SOURCE_COURSE");variant.EnableKeyword("IDAS_NATIVE_SOURCE_NOCLIP");
            var noClip=Pipeline(variant);
            check(!plain.Equals(course)&&!plain.Equals(noClip)&&!course.Equals(noClip),
                "Metal warmup distinguishes source lighting shader variants");
        } finally {Destroy(variant);}
        bool reuse=RendererReuse;
        var item=new RangeObject{mesh=new Mesh()};
        try {
            RendererReuse=true;
            check(SetRangeCount(item,6),"Initial mesh count is configured");EnsureVertexCache(item,6);
            int buffers=MeshBufferResizes,caches=VertexCacheResizes;
            check(item.capacity==8&&item.vertexCache.Length==8,"Range buffers use bounded power-of-two capacity");
            long stride=4;
            for(int stream=0;stream<item.mesh.vertexBufferCount;++stream)stride+=item.mesh.GetVertexBufferStride(stream);
            check(MeshBytes(item)==stride*item.capacity,"Requested mesh bytes match actual Unity vertex/index layouts");
            check(SetRangeCount(item,3),"A shrinking range updates its logical count");EnsureVertexCache(item,3);
            check(SetRangeCount(item,6),"A growing range within capacity updates its logical count");EnsureVertexCache(item,6);
            check(MeshBufferResizes==buffers&&VertexCacheResizes==caches,"Size changes within capacity preserve GPU and CPU buffers");
            check(item.mesh.GetIndexCount(0)==6,"Unused capacity does not enter the draw");
            check(SetRangeCount(item,9),"An oversized range grows capacity");EnsureVertexCache(item,9);
            check(item.capacity==16&&item.vertexCache.Length==16&&MeshBufferResizes==buffers+1&&VertexCacheResizes==caches+1,
                "Capacity growth replaces each buffer once");
            SetRangeCount(item,3000);EnsureVertexCache(item,3000);
            SetRangeCount(item,6);EnsureVertexCache(item,6);
            check(item.capacity==8&&item.vertexCache.Length==8,"Historical large buffers shrink when far above the current range");
            RendererReuse=false;SetRangeCount(item,3);EnsureVertexCache(item,3);
            check(item.capacity==3&&item.vertexCache.Length==3,"Baseline retains exact-size buffers");
        } finally {RendererReuse=reuse;if(item.vertexCache.IsCreated)item.vertexCache.Dispose();Destroy(item.mesh);}
    }
    internal void VerifyBufferPool(Action<bool,string> check)
    {
        var holder=new GameObject("Isolated range capacity regression");
        var probe=holder.AddComponent<Idas3SceneRenderer>();
        probe.sceneShader=sceneShader;probe.meshLayout=meshLayout;probe.metalGeometry=metalGeometry;
        probe.RendererReuse=probe.OrderedSubmissionActive=true;
        try {
            for(int frame=0;frame<256;++frame){
                ++probe.selectionStamp;
                foreach(var item in probe.objects)item.active=false;
                probe.BuildFreeRanges();
                for(int slot=0;slot<32;++slot){
                    int count=slot==frame%32?12288:3;
                    var item=probe.ClaimRangeObject(count);
                    check(item.selectedStamp==probe.selectionStamp,"Claimed range belongs to this selection");
                    probe.SetRangeCount(item,count);probe.EnsureVertexCache(item,count);item.active=true;
                    check(item.capacity>=count&&item.capacity<=(long)Mathf.NextPowerOfTwo(count)*4,
                        "Active capacity remains bounded by current geometry, not history");
                }
                probe.UpdateBufferAccounting();
                check(probe.objects.Count==32,"Size-aware selection does not grow the object pool");
                check(probe.MeshBufferBytes+probe.VertexCacheBytes<4*1024*1024,
                    "A moving large range must not inflate all 32 retained buffers");
            }
            foreach(var item in probe.objects)item.active=false;
            probe.UpdateBufferAccounting();
            for(int frame=0;frame<4&&probe.InactiveBufferBytes>0;++frame){
                probe.TrimmedRanges=0;probe.TrimInactiveBuffers(0);
                check(probe.TrimmedRanges<=16,"Inactive releases are bounded per frame");
            }
            check(probe.MeshBufferBytes==0&&probe.VertexCacheBytes==0&&probe.InactiveBufferBytes==0,
                "Inactive pool payload can be fully released");
            ++probe.selectionStamp;probe.BuildFreeRanges();
            var restored=probe.ClaimRangeObject(6);probe.SetRangeCount(restored,6);probe.EnsureVertexCache(restored,6);
            check(restored.mesh!=null&&restored.capacity==8&&restored.vertexCache.Length==8,
                "A trimmed range recreates its mesh and CPU cache on demand");
        } finally {Destroy(holder);}
    }
    private bool SetRangeCount(RangeObject item,int count)
    {
        int wanted=RendererReuse?Mathf.NextPowerOfTwo(count):count;
        bool resize=item.capacity<count||item.capacity>(long)wanted*4||!RendererReuse&&item.capacity!=count;
        if(item.count==count&&!resize)return false;
        if(!RendererReuse&&item.vertexCache.IsCreated)item.vertexCache.Dispose();
        if(resize){
            if(item.mesh==null){
                item.mesh=NewRangeMesh();
                if(item.gameObject!=null)item.gameObject.GetComponent<MeshFilter>().sharedMesh=item.mesh;
            }
            item.capacity=wanted;
            item.mesh.Clear();item.mesh.SetVertexBufferParams(item.capacity,meshLayout);
            item.mesh.SetIndexBufferParams(item.capacity,IndexFormat.UInt32);++MeshBufferResizes;
            if(RendererReuse){
                EnsureIndexScratch(item.capacity);
                item.mesh.SetIndexBufferData(indexScratch,0,0,item.capacity,UploadFlags);
            }else{
                var indices=new NativeArray<uint>(item.capacity,Allocator.Temp,NativeArrayOptions.UninitializedMemory);
                for(int j=0;j<indices.Length;++j)indices[j]=(uint)j;
                item.mesh.SetIndexBufferData(indices,0,0,indices.Length,UploadFlags);indices.Dispose();
            }
        }
        item.mesh.subMeshCount=1;
        item.mesh.SetSubMesh(0,new SubMeshDescriptor(0,count,MeshTopology.Triangles),UploadFlags);
        item.count=count;return true;
    }
    private void EnsureVertexCache(RangeObject item,int count)
    {
        int capacity=RendererReuse?item.capacity:count;
        if(item.vertexCache.IsCreated&&item.vertexCache.Length==capacity)return;
        if(item.vertexCache.IsCreated)item.vertexCache.Dispose();
        item.vertexCache=new NativeArray<SceneVertex>(capacity,
            Allocator.Persistent,NativeArrayOptions.UninitializedMemory);++VertexCacheResizes;
    }
    private void SetQueue(RangeObject item,int queue)
    {
        if(OrderedSubmissionActive)++SkippedQueueUpdates;
        else {item.material.renderQueue=queue;++MaterialUpdateCount;++QueueUpdateCount;}
        item.materialQueue=queue;
    }
    private void UpdateCommandMaterials()
    {
        ++materialStamp;
        bool share=RendererReuse&&OrderedSubmissionActive&&!SharedMaterialBaseline;
        foreach(var item in objects){
            if(!item.active)continue;
            Material scene=item.material;
            if(share){
                var state=item.sharedMaterial;
                if(state==null||state.retired||item.sharedMaterialDirty){
                    var key=new SharedMaterialKey(item.materialRange,textureGeneration,item.material.shader,sourceCourseLightingReady);
                    if(!sharedMaterials.TryGetValue(key,out state)&&sharedMaterials.Count<SharedMaterialLimit){
                        // Populate runtime-only uniforms from the source
                        // state explicitly, including true integer words.
                        state=new SharedMaterialState{scene=new Material(item.material.shader)};
                        ConfigureMaterial(state.scene,item.materialRange,item.materialQueue);
                        sharedMaterials.Add(key,state);++SharedMaterialConfigurations;
                    }
                    item.sharedMaterial=state;item.sharedMaterialDirty=false;
                }
                if(state!=null&&!state.retired){
                    state.usedStamp=materialStamp;scene=state.scene;++SharedMaterialRanges;
                }
            }
            if(item.commandMaterial!=scene)sceneCommandsDirty=true;
            item.commandMaterial=scene;
        }
    }
    private void TrimSharedMaterials()
    {
        // Current camera commands no longer reference retired states. Never
        // mutate a shared material still used by another cached command.
        retiredMaterials.Clear();
        foreach(var pair in sharedMaterials){
            if(pair.Value.usedStamp==materialStamp)continue;
            if(pair.Key.generation==textureGeneration&&sharedMaterials.Count-retiredMaterials.Count<=SharedMaterialRetained)continue;
            retiredMaterials.Add(pair.Key);if(retiredMaterials.Count==16)break;
        }
        foreach(var key in retiredMaterials){
            var state=sharedMaterials[key];state.retired=true;
            Destroy(state.scene);
            sharedMaterials.Remove(key);
        }
    }
    private void UpdateSceneCommands(Camera camera,uint viewBit,CommandBuffer commands,List<RangeObject> previous,bool enabled)
    {
        if(commands==null)return;
        depthScratch.Clear();SetSidePlanes(camera);
        if(enabled)foreach(var item in frameDraws)
            if((item.materialRange.viewMask&viewBit)!=0&&(diagnosticNoCull||!OutsideDepthView(item.bounds)))depthScratch.Add(item);
        SceneCommandDraws+=depthScratch.Count;
        bool changed=sceneCommandsDirty||previous.Count!=depthScratch.Count;
        if(!changed)for(int i=0;i<previous.Count;++i)
            if(previous[i]!=depthScratch[i]){changed=true;break;}
        if(!changed)return;
        commands.Clear();previous.Clear();
        foreach(var item in depthScratch){
            commands.DrawMesh(item.mesh,Matrix4x4.identity,item.commandMaterial,0,-1);previous.Add(item);
        }
        ++SceneCommandRebuilds;
    }
    private void UpdateDepthCommands(Camera camera, uint viewBit, CommandBuffer commands, List<DepthDraw> previous, bool enabled)
    {
        depthScratch.Clear();
        // Homogeneous side planes are conservative for custom reversed depth.
        // Use the color renderer's world/billboard bounds without reducing LOD
        // or distance. Keep all near/far candidates for the GPU to clip.
        SetSidePlanes(camera);
        if (enabled) foreach (var item in depthCandidates) {
            if (!diagnosticDepthSubmissionBaseline && !diagnosticNoCull &&
                ((item.materialRange.viewMask & viewBit) == 0 || OutsideDepthView(item.bounds))) continue;
            depthScratch.Add(item);
        }
        if (!diagnosticDepthMaterialBaseline) depthScratch.Sort(DepthMaterialOrder);
        DepthSourceDrawCount += depthScratch.Count;
        Material lastMaterial = null;
        foreach (var item in depthScratch) {
            if (item.depthMaterial != lastMaterial) { ++DepthMaterialGroups; lastMaterial = item.depthMaterial; }
        }
        if (!diagnosticDepthMaterialBaseline && !diagnosticDepthBatchBaseline && !diagnosticPerfBaseline) {
            UpdateDepthBatches(commands, viewBit == 1 ? mainDepthBatches : mirrorDepthBatches,
                viewBit == 1 ? mainBatchDraws : mirrorBatchDraws);
            return;
        }
        DepthDrawCount += depthScratch.Count;
        bool changed = sceneCommandsDirty || diagnosticDepthSubmissionBaseline || previous.Count != depthScratch.Count;
        if (!changed) for (int i = 0; i < previous.Count; ++i)
            if (previous[i].item != depthScratch[i] || previous[i].material != depthScratch[i].depthMaterial) { changed = true; break; }
        if (!changed) return;
        // Persistent mesh/material references stay valid when their contents
        // update. Re-record only when the ordered visible draw list changes.
        commands.Clear(); previous.Clear();
        foreach (var item in depthScratch) {
            commands.DrawMesh(item.mesh, Matrix4x4.identity, item.depthMaterial, 0, 0);
            previous.Add(new DepthDraw(item));
        }
        ++DepthBufferRebuildCount;
    }
    // Exact state groups in this depth-only pass are order independent. Retain
    // one mesh per group/view, updating only groups whose visible membership or
    // vertex bytes changed. The source colour meshes/queues are untouched.
    private void UpdateDepthBatches(CommandBuffer commands, Dictionary<Material, DepthBatch> cache, List<DepthBatch> previous)
    {
        depthBatchScratch.Clear();
        bool changed = false;
        for (int first = 0; first < depthScratch.Count;) {
            var material = depthScratch[first].depthMaterial;
            int end = first + 1;
            while (end < depthScratch.Count && depthScratch[end].depthMaterial == material) ++end;
            if (!cache.TryGetValue(material, out var batch)) {
                batch = new DepthBatch { material = material }; cache.Add(material, batch);
            }
            changed |= UploadDepthBatch(batch, first, end);
            depthBatchScratch.Add(batch); first = end;
        }
        DepthDrawCount += depthBatchScratch.Count;
        changed |= previous.Count != depthBatchScratch.Count;
        if (!changed) for (int i = 0; i < previous.Count; ++i)
            if (previous[i] != depthBatchScratch[i]) { changed = true; break; }
        if (!changed) return;
        commands.Clear(); previous.Clear();
        foreach (var batch in depthBatchScratch) {
            commands.DrawMesh(batch.drawMesh, Matrix4x4.identity, batch.material, 0, 0);
            previous.Add(batch);
        }
        ++DepthBufferRebuildCount;
    }
    private unsafe bool UploadDepthBatch(DepthBatch batch, int first, int end)
    {
        bool changed = batch.parts.Count != end - first;
        if (!changed) for (int i = first; i < end; ++i) {
            var part = batch.parts[i - first]; var item = depthScratch[i];
            if (part.item != item || part.revision != item.geometryRevision) { changed = true; break; }
        }
        if (!changed) return false;
        // A single range already is one draw. Reuse its existing vertex/index
        // buffers instead of allocating a duplicate that cannot save a draw.
        if (end - first == 1) {
            var item = depthScratch[first]; batch.drawMesh = item.mesh;
            batch.parts.Clear(); batch.parts.Add(new DepthPart(item));
            return true;
        }
        if (batch.mesh == null) {
            batch.mesh = new Mesh { name = "Original scenery depth group" }; batch.mesh.MarkDynamic();
        }
        batch.drawMesh = batch.mesh;
        int vertices = 0;
        for (int i = first; i < end; ++i) vertices = checked(vertices + depthScratch[i].count);
        if (!depthVertexScratch.IsCreated || depthVertexScratch.Length < vertices) {
            if (depthVertexScratch.IsCreated) depthVertexScratch.Dispose();
            depthVertexScratch = new NativeArray<SceneVertex>(Mathf.NextPowerOfTwo(vertices), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
        if (batch.capacity < vertices) {
            int capacity = Mathf.NextPowerOfTwo(vertices);
            batch.mesh.SetVertexBufferParams(capacity, meshLayout);
            batch.mesh.SetIndexBufferParams(capacity, IndexFormat.UInt32);
            var indices = new NativeArray<uint>(capacity, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try {
                for (int i = 0; i < capacity; ++i) indices[i] = (uint)i;
                batch.mesh.SetIndexBufferData(indices, 0, 0, capacity, UploadFlags);
            } finally { indices.Dispose(); }
            batch.mesh.subMeshCount = 1;
            DepthBatchCapacityVertices += capacity - batch.capacity; batch.capacity = capacity;
        }
        int offset = 0; Bounds bounds = depthScratch[first].bounds;
        var destination = (SceneVertex*)NativeArrayUnsafeUtility.GetUnsafePtr(depthVertexScratch);
        batch.parts.Clear();
        for (int i = first; i < end; ++i) {
            var item = depthScratch[i];
            if (!item.vertexCache.IsCreated || item.vertexCache.Length < item.count)
                throw new InvalidOperationException("Scenery depth group has no exact source vertices.");
            UnsafeUtility.MemCpy(destination + offset, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(item.vertexCache), (long)item.count * 64);
            offset += item.count; bounds.Encapsulate(item.bounds); batch.parts.Add(new DepthPart(item));
        }
        batch.mesh.SetVertexBufferData(depthVertexScratch, 0, 0, vertices, 0, UploadFlags);
        if(metalGeometry)UploadMetalVertices(batch.mesh,destination,vertices);
        batch.mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertices, MeshTopology.Triangles), UploadFlags);
        batch.mesh.bounds = bounds;
        DepthBatchUploadVertices += vertices; ++DepthBatchRebuildCount;
        return true;
    }
    private void SetSidePlanes(Camera camera){
        Matrix4x4 vp=camera.cullingMatrix;Vector4 w=vp.GetRow(3),x=vp.GetRow(0),y=vp.GetRow(1);
        depthSidePlanes[0]=w+x;depthSidePlanes[1]=w-x;depthSidePlanes[2]=w+y;depthSidePlanes[3]=w-y;
    }
    private bool OutsideDepthView(Bounds bounds)
    {
        Vector3 c = bounds.center, e = bounds.extents + Vector3.one * .05f;
        foreach (Vector4 p in depthSidePlanes) {
            float farthest = p.x*c.x+p.y*c.y+p.z*c.z+p.w + Mathf.Abs(p.x)*e.x+Mathf.Abs(p.y)*e.y+Mathf.Abs(p.z)*e.z;
            if (farthest < 0) return true;
        }
        return false;
    }
    // Called only by isolated smoke/performance checks, outside timed frames.
    private static readonly string[] MaterialIntegerWords={
        "pcw","tsp","gmp","original","emissive","billboard","courseLightRange","_LightScope","viewMask","courseCullMode"
    };
    private static readonly string[] MaterialFloatWords={
        "glossCoefficient","_SrcBlend","_DstBlend","_SrcBlendAlpha","_DstBlendAlpha","_TrackSurface","_ZTest","_ZWrite"
    };
    internal void VerifyViewCulling(Action<bool, string> check)
    {
        if (!diagnosticDepthMaterialBaseline && !diagnosticDepthBatchBaseline && !diagnosticPerfBaseline) {
            VerifyDepthBatches(mainBatchDraws, check); VerifyDepthBatches(mirrorBatchDraws, check);
            check(DepthDrawCount == mainBatchDraws.Count + mirrorBatchDraws.Count, "Depth command count differs from its visible groups.");
        }
        for (uint mask = 0; mask < 4; ++mask) {
            int bit = 1 << LayerForViewMask(mask);
            check(((main.cullingMask & bit) != 0) == ((mask & 1) != 0), "Main camera view-mask layer mismatch.");
            check(((mirror.cullingMask & bit) != 0) == ((mask & 2) != 0), "Mirror camera view-mask layer mismatch.");
            check(LayerForViewMask(mask | 0xfffffffcu) == LayerForViewMask(mask), "Unused view bits changed layer visibility.");
        }
        check((main.cullingMask & (1 << 28)) != 0 && (mirror.cullingMask & (1 << 28)) != 0,
            "View culling excluded imported scenery.");
        check(((main.cullingMask | mirror.cullingMask) & (1 << 29)) == 0, "World cameras include the challenger UI layer.");
        int active = 0, mainRanges = 0, mainVertices = 0, mirrorRanges = 0, mirrorVertices = 0;
        foreach (var item in objects) {
            if (!item.active) continue;
            ++active;
            check(OrderedSubmissionActive?item.renderer==null||!item.renderer.enabled:item.renderer!=null&&item.renderer.enabled,
                "Scene range renderer and command-buffer ownership overlap");
            uint mask = item.materialRange.viewMask;
            check(item.gameObject==null||item.gameObject.layer==item.layer,"Lazy renderer layer differs from range ownership");
            bool mainIncluded = (main.cullingMask & (1 << item.layer)) != 0;
            bool mirrorIncluded = (mirror.cullingMask & (1 << item.layer)) != 0;
            check(item.capacity>=item.count&&item.mesh.GetIndexCount(0)==(uint)item.count,"Mesh capacity changed the logical index count");
            check(!item.vertexCache.IsCreated||item.vertexCache.Length>=item.count,"Vertex cache cannot hold its logical range");
            check(mainIncluded == (diagnosticViewCullingBaseline || (mask & 1) != 0), "Active range has incorrect main-view ownership.");
            check(mirrorIncluded == (diagnosticViewCullingBaseline || (mask & 2) != 0), "Active range has incorrect mirror ownership.");
            check(unchecked((uint)item.material.GetInteger("viewMask")) == mask, "Source shader view-mask guard changed.");
            if(OrderedSubmissionActive&&item.commandMaterial!=null){
                check(item.commandMaterial.shader==item.material.shader&&item.commandMaterial.mainTexture==item.material.mainTexture,
                    "Shared commands retain the original shader and texture");
                foreach(string word in MaterialIntegerWords){
                    bool exact=item.commandMaterial.GetInteger(word)==item.material.GetInteger(word);
                    check(exact,exact?"Shared command integers match":"Shared command integer differs: "+word);
                }
                foreach(string word in MaterialFloatWords){
                    bool exact=item.commandMaterial.GetFloat(word)==item.material.GetFloat(word);
                    check(exact,exact?"Shared command scalars match":"Shared command scalar differs: "+word);
                }
                check(item.commandMaterial.GetVector("_LightOverride")==item.material.GetVector("_LightOverride"),
                    "Shared commands retain the exact light override");
                check(item.commandMaterial.IsKeywordEnabled("IDAS_NATIVE_SOURCE_COURSE")==item.material.IsKeywordEnabled("IDAS_NATIVE_SOURCE_COURSE")&&
                    item.commandMaterial.IsKeywordEnabled("IDAS_NATIVE_SOURCE_NOCLIP")==item.material.IsKeywordEnabled("IDAS_NATIVE_SOURCE_NOCLIP"),
                    "Shared commands retain source lighting shader variants");
            }
            if (!mainIncluded) { ++mainRanges; mainVertices += item.count; }
            if (mirror.enabled && !mirrorIncluded) { ++mirrorRanges; mirrorVertices += item.count; }
        }
        check(active == ActiveMeshCount, "View-culling check missed active scene ranges.");
        check(mainRanges == MainViewExcludedRanges && mainVertices == MainViewExcludedVertices &&
            mirrorRanges == MirrorViewExcludedRanges && mirrorVertices == MirrorViewExcludedVertices,
            "View-culling exclusion counters differ from active renderer ownership.");
    }
    // Called by the isolated scene checks outside measured frames. Inspect the
    // actual Unity mesh buffers, including every colour/UV/normal word, so stale
    // cached geometry cannot hide behind a successful draw/count assertion.
    private static unsafe void VerifyDepthBatches(List<DepthBatch> batches, Action<bool, string> check)
    {
        foreach (var batch in batches) {
            using var readback = Mesh.AcquireReadOnlyMeshData(batch.drawMesh);
            var mesh = readback[0]; var vertices = mesh.GetVertexData<SceneVertex>();
            var indices = mesh.GetIndexData<uint>(); int offset = 0;
            foreach (var part in batch.parts) {
                var item = part.item;
                check(part.revision == item.geometryRevision && item.depthMaterial == batch.material, "Scenery depth group retained stale geometry/material state.");
                check(item.vertexCache.IsCreated && offset + item.count <= vertices.Length, "Scenery depth group is missing source vertices.");
                check(UnsafeUtility.MemCmp((SceneVertex*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(vertices) + offset,
                    NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(item.vertexCache), (long)item.count * 64) == 0, "Scenery depth group changed source vertex bytes.");
                offset += item.count;
            }
            check(mesh.GetSubMesh(0).indexCount == offset, "Scenery depth group changed its triangle count.");
            bool sequential = true;
            for (int i = 0; i < offset; ++i) sequential &= indices[i] == (uint)i;
            check(sequential, "Scenery depth group changed source triangle ordering.");
        }
    }
    // Match exact vertex bytes before claiming any free renderer: an inserted
    // roadside range must not evict every unchanged range later in the list.
    // Hash collisions are resolved with a full comparison, never treated as hits.
    private static unsafe ulong GeometryKey(SceneVertex* data, int count, bool billboard) {
        ulong hash = (ulong)count ^ (billboard ? 0x9e3779b97f4a7c15UL : 0UL);
        var first=(ulong*)data;var last=(ulong*)(data+count-1);
        unchecked { for(int i=0;i<8;++i) { hash=(hash^first[i])*1099511628211UL; hash=(hash^last[i])*1099511628211UL; } }
        return hash;
    }
    private unsafe void SelectCachedRanges(SceneFrame frame) {
        if(diagnosticPerfBaseline || rangeCacheOff)return;
        ++selectionStamp;
        freeRangeCursor=0;
        while(selectedRanges.Count<frame.rangeCount)selectedRanges.Add(null);
        bool lookupNeeded=false;
        // Most frames keep the same ordering. Immutable identities avoid
        // touching vertex data; other candidates use cheap endpoint words
        // here and a complete byte comparison in the upload pass.
        for(int i=0;i<frame.rangeCount;++i) {
            var previous=selectedRanges[i];selectedRanges[i]=null;
            var r=*(SceneRange*)IntPtr.Add(frame.ranges,i*64);
            uint list=(r.flags&1)!=0?(r.pcw>>24)&7:0;
            if(r.count==0||r.viewMask==0||(list!=0&&list!=4&&list!=2))continue;
            if((ulong)r.first+r.count>frame.vertexCount)throw new InvalidOperationException("Invalid cached range bounds");
            var data=(SceneVertex*)frame.vertices.ToPointer()+(int)r.first;
            bool match=false;
            if(previous!=null&&previous.selectedStamp!=selectionStamp&&previous.count==r.count&&previous.vertexCache.IsCreated&&previous.cachedBillboard==((r.flags&4)!=0)) {
                ulong identity=geometryIdsOff?0:((ulong*)geometryIds.ToPointer())[i];
                if(identity!=0&&identity==previous.geometryId)match=true;
                else {
                var cached=(SceneVertex*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(previous.vertexCache);
                var a=(ulong*)data;var b=(ulong*)cached;var lastA=(ulong*)(data+r.count-1);var lastB=(ulong*)(cached+r.count-1);
                match=a[0]==b[0]&&a[1]==b[1]&&lastA[0]==lastB[0]&&lastA[1]==lastB[1];
                }
            }
            if(match){previous.selectedStamp=selectionStamp;selectedRanges[i]=previous;}
            else if(IsCourseGeometry(r))lookupNeeded=true;
        }
        if(!lookupNeeded){BuildFreeRanges();return;} // Moving actors alone do not need a world lookup.
        geometryLookup.Clear();
        foreach(var item in objects) {
            if(!item.vertexCache.IsCreated||item.selectedStamp==selectionStamp)continue;
            geometryLookup.TryGetValue(item.geometryKey,out item.nextGeometry);
            geometryLookup[item.geometryKey]=item;
        }
        for(int i=0;i<frame.rangeCount;++i) {
            if(selectedRanges[i]!=null)continue;
            var r=*(SceneRange*)IntPtr.Add(frame.ranges,i*64);
            uint list=(r.flags&1)!=0?(r.pcw>>24)&7:0;
            if(r.count==0||r.viewMask==0||(list!=0&&list!=4&&list!=2))continue;
            var data=(SceneVertex*)frame.vertices.ToPointer()+(int)r.first;
            bool billboard=(r.flags&4)!=0;
            geometryLookup.TryGetValue(GeometryKey(data,(int)r.count,billboard),out var candidate);
            while(candidate!=null) {
                // A moved list position does not change immutable vertex data.
                // Dynamic/untagged ranges still require a full comparison.
                if(candidate.selectedStamp!=selectionStamp && candidate.count==r.count && candidate.cachedBillboard==billboard &&
                   ((!geometryIdsOff && ((ulong*)geometryIds.ToPointer())[i]!=0 && ((ulong*)geometryIds.ToPointer())[i]==candidate.geometryId) ||
                    UnsafeUtility.MemCmp(data,NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(candidate.vertexCache),(long)r.count*64)==0)) {
                    candidate.selectedStamp=selectionStamp;selectedRanges[i]=candidate;break;
                }
                candidate=candidate.nextGeometry;
            }
        }
        BuildFreeRanges();
    }
    private static int CapacityBucket(int count){
        int bucket=0;while(count>1){count=(count+1)>>1;++bucket;}return bucket;
    }
    private void BuildFreeRanges(){
        if(!RendererReuse)return;
        Array.Clear(freeRanges,0,freeRanges.Length);
        for(int i=objects.Count-1;i>=0;--i){
            var item=objects[i];if(item.selectedStamp==selectionStamp)continue;
            int bucket=CapacityBucket(item.capacity);
            item.nextFree=freeRanges[bucket];freeRanges[bucket]=item;
        }
    }
    private RangeObject ClaimRangeObject(int count) {
        if(!RendererReuse){
            while(freeRangeCursor<objects.Count&&objects[freeRangeCursor].selectedStamp==selectionStamp)++freeRangeCursor;
            var original=GetRangeObject(freeRangeCursor++);
            original.selectedStamp=selectionStamp;return original;
        }
        int wanted=CapacityBucket(count),bucket=-1;
        // Preserve similarly sized buffers before growing a smaller slot. An
        // unrelated large draw must not ratchet every pool slot's high water.
        for(int i=wanted;i<=Math.Min(wanted+1,freeRanges.Length-1);++i)
            if(freeRanges[i]!=null){bucket=i;break;}
        if(bucket<0)for(int i=wanted-1;i>=0;--i)
            if(freeRanges[i]!=null){bucket=i;break;}
        if(bucket<0)for(int i=wanted+2;i<freeRanges.Length;++i)
            if(freeRanges[i]!=null){bucket=i;break;}
        RangeObject item;
        if(bucket<0)item=GetRangeObject(objects.Count);
        else {item=freeRanges[bucket];freeRanges[bucket]=item.nextFree;item.nextFree=null;}
        item.selectedStamp=selectionStamp;return item;
    }
    private int MeshPayloadStride=>68+(metalGeometry?UnsafeUtility.SizeOf<MetalVertex>():0);
    private long MeshBytes(RangeObject item)=>(long)item.capacity*MeshPayloadStride;
    private static long CacheBytes(RangeObject item)=>item.vertexCache.IsCreated?(long)item.vertexCache.Length*64:0;
    private void UpdateBufferAccounting(){
        MeshBufferBytes=VertexCacheBytes=InactiveBufferBytes=ActiveVertexBytes=0;
        foreach(var item in objects){
            long mesh=MeshBytes(item),cache=CacheBytes(item);
            MeshBufferBytes+=mesh;VertexCacheBytes+=cache;
            if(item.active)ActiveVertexBytes+=(long)item.count*(MeshPayloadStride+64);
            else InactiveBufferBytes+=mesh+cache;
        }
    }
    private void TrimInactiveBuffers(long budget){
        // Commands for this frame no longer refer to inactive ranges. Bound
        // release work per frame; Mesh destruction is deferred by Unity.
        int inspected=0;
        while(InactiveBufferBytes>budget&&inspected++<objects.Count&&TrimmedRanges<16){
            if(trimCursor>=objects.Count)trimCursor=0;
            var item=objects[trimCursor++];if(item.active||item.capacity==0)continue;
            long mesh=MeshBytes(item),cache=CacheBytes(item);
            if(item.vertexCache.IsCreated)item.vertexCache.Dispose();
            if(item.gameObject!=null)item.gameObject.GetComponent<MeshFilter>().sharedMesh=null;
            Destroy(item.mesh);item.mesh=null;item.count=item.capacity=0;item.geometryId=item.geometryKey=0;
            MeshBufferBytes-=mesh;VertexCacheBytes-=cache;InactiveBufferBytes-=mesh+cache;++TrimmedRanges;
        }
    }
    private static Mesh NewRangeMesh(){
        var mesh=new Mesh{name="Original authored geometry"};mesh.MarkDynamic();return mesh;
    }
    private RangeObject GetRangeObject(int index)
    {
        if (index < objects.Count) return objects[index];
        var mesh = NewRangeMesh();
        var result = new RangeObject {mesh=mesh,material=new Material(sceneShader),active=false,depthGeometryOrder=objects.Count};
        objects.Add(result);++CreatedRangeObjects;
        if(!OrderedSubmissionActive||!RendererReuse)EnsureRenderer(result);
        return result;
    }
    private void EnsureRenderer(RangeObject item)
    {
        if(item.renderer!=null)return;
        var obj=new GameObject("Original range");obj.layer=item.layer;
        obj.transform.SetParent(owners[item.parentScope<4?item.parentScope:0],false);
        obj.AddComponent<MeshFilter>().sharedMesh=item.mesh;
        var value=obj.AddComponent<MeshRenderer>();value.shadowCastingMode=ShadowCastingMode.Off;
        value.receiveShadows=false;value.lightProbeUsage=LightProbeUsage.Off;value.reflectionProbeUsage=ReflectionProbeUsage.Off;
        value.enabled=!OrderedSubmissionActive;value.sharedMaterial=item.material;
        obj.SetActive(item.active);item.gameObject=obj;item.renderer=value;
        ++CreatedRenderers;++ResidentRenderers;
    }
    private static void PackWords(int[] source, UInt4[] target)
    {
        for (int i = 0; i < target.Length; ++i) target[i] = new UInt4 {
            x = unchecked((uint)source[i * 4]), y = unchecked((uint)source[i * 4 + 1]),
            z = unchecked((uint)source[i * 4 + 2]), w = unchecked((uint)source[i * 4 + 3])
        };
    }
    private static unsafe bool SameRange(SceneRange a, SceneRange b) {
        // Source offsets/order do not alter shader state; queue is handled separately.
        a.first=b.first=0;a.sourceIndex=b.sourceIndex=0;
        return UnsafeUtility.MemCmp(&a, &b, 64) == 0;
    }
    private static unsafe bool SameMaterial(SceneRange a,SceneRange b){
        a.first=b.first=a.count=b.count=a.sourceIndex=b.sourceIndex=0;
        return UnsafeUtility.MemCmp(&a,&b,64)==0;
    }
    private void EnsureIndexScratch(int count){
        if(indexScratch.IsCreated&&indexScratch.Length>=count)return;
        if(indexScratch.IsCreated)indexScratch.Dispose();
        indexScratch=new NativeArray<uint>(Mathf.NextPowerOfTwo(count),Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
        for(int i=0;i<indexScratch.Length;++i)indexScratch[i]=(uint)i;
    }
    private unsafe void UploadMetalVertices(Mesh mesh,SceneVertex* vertices,int count)
    {
        if(count%3!=0)throw new InvalidOperationException("Native scene ranges must contain complete triangles.");
        if(RendererReuse&&(!metalScratch.IsCreated||metalScratch.Length<count)){
            if(metalScratch.IsCreated)metalScratch.Dispose();
            metalScratch=new NativeArray<MetalVertex>(Mathf.NextPowerOfTwo(count),Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
        }
        var data=RendererReuse?metalScratch:new NativeArray<MetalVertex>(count,Allocator.Temp,NativeArrayOptions.UninitializedMemory);
        try{
            for(int i=0;i<count;i+=3){
                var last=vertices[i+2];
                var triangle=new MetalVertex{position=last.position,normal=last.normal,color=last.color,offset=last.offset,
                    face=Vector3.Cross(vertices[i+1].position-vertices[i].position,last.position-vertices[i].position)};
                data[i]=data[i+1]=data[i+2]=triangle;
            }
            mesh.SetVertexBufferData(data,0,0,count,1,UploadFlags);
        }finally{if(!RendererReuse)data.Dispose();}
    }
    private static PipelineKey Pipeline(Material material)
    {
        uint state=0;
        string[] properties=PipelineProperties;
        for(int i=0;i<properties.Length;++i)
            if(material.HasProperty(properties[i]))state|=((uint)material.GetFloat(properties[i])&15u)<<(i*4);
        // Local shader variants compile distinct Metal pipelines.
        if(material.IsKeywordEnabled("IDAS_NATIVE_SOURCE_COURSE"))state|=1u<<(properties.Length*4);
        if(material.IsKeywordEnabled("IDAS_NATIVE_SOURCE_NOCLIP"))state|=1u<<(properties.Length*4+1);
        return new PipelineKey{shader=material.shader,state=state};
    }
    private static readonly string[] PipelineProperties={"_SrcBlend","_DstBlend","_SrcBlendAlpha","_DstBlendAlpha","_ZWrite","_ZTest"};
    private void WarmPipelines()
    {
        if(!metalGeometry||!CanPrepareResources||!Idas3MobilePerformance.Enabled)return;
        if(MaterialConfigurationCount!=0)warmupPending=true;
        int samples=main.targetTexture!=null?main.targetTexture.antiAliasing:Math.Max(1,QualitySettings.antiAliasing);
        if(!warmupPending&&warmupTarget!=null&&warmupTarget.antiAliasing==samples&&
            (main.targetTexture==null||warmupTarget.graphicsFormat==main.targetTexture.graphicsFormat&&
                warmupTarget.depthStencilFormat==main.targetTexture.depthStencilFormat))return;
        var descriptor=main.targetTexture!=null?main.targetTexture.descriptor:
            new RenderTextureDescriptor(16,16,RenderTextureFormat.Default,24);
        descriptor.width=descriptor.height=16;descriptor.msaaSamples=samples;
        descriptor.useMipMap=descriptor.autoGenerateMips=false;descriptor.memoryless=RenderTextureMemoryless.None;
        if(warmupTarget==null||warmupTarget.graphicsFormat!=descriptor.graphicsFormat||
            warmupTarget.depthStencilFormat!=descriptor.depthStencilFormat||warmupTarget.antiAliasing!=samples){
            if(warmupTarget!=null){warmupTarget.Release();Destroy(warmupTarget);}
            warmupTarget=new RenderTexture(descriptor){name="IDAS3 bounded pipeline warmup"};
            if(!warmupTarget.Create()){Destroy(warmupTarget);warmupTarget=null;return;}
            warmedPipelines.Clear();
        }
        if(warmupCommands==null)warmupCommands=new CommandBuffer{name="IDAS3 observed Metal pipeline warmup"};
        warmupCommands.Clear();
        warmupCommands.SetRenderTarget(warmupTarget);
        warmupCommands.ClearRenderTarget(true,true,Color.clear);
        warmupCommands.SetViewProjectionMatrices(main.worldToCameraMatrix,GL.GetGPUProjectionMatrix(main.projectionMatrix,true));
        BeforeCamera(main);
        foreach(var item in objects){
            if(!item.active||!item.materialConfigured)continue;
            WarmMaterial(item.mesh,item.material);
            if(item.depthCandidate&&item.depthMaterial!=null)WarmMaterial(item.mesh,item.depthMaterial);
            if(WarmupStates>=4||warmedPipelines.Count>=128)break;
        }
        if(WarmupStates!=0)Graphics.ExecuteCommandBuffer(warmupCommands);
        warmupCommands.Clear();
        warmupPending=WarmupStates>=4;
    }
    private void WarmMaterial(Mesh mesh,Material material)
    {
        if(WarmupStates>=4||warmedPipelines.Count>=128||!warmedPipelines.Add(Pipeline(material)))return;
        for(int pass=0;pass<material.passCount;++pass)warmupCommands.DrawMesh(mesh,Matrix4x4.identity,material,0,pass);
        ++WarmupStates;
    }
    private void ConfigureMaterial(Material m, SceneRange r, int queue)
    {
        bool original = (r.flags & 1) != 0;
        int cullMode = SourceCullMode(r);
        // The geometry stage only rejects authored backfaces or selects the
        // final vertex's flat colors. When neither operation applies its output
        // is exactly the vertex shader's output, including both camera views.
        Shader wanted = metalGeometry ? directSceneShader : !geometryStageBaseline && cullMode < 2 && (!original || (r.pcw & 2) != 0)
            ? directSceneShader : sceneShader;
        // lightobj chunk 7's original noLight, destination-color projector.
        // Keep other emissive effects and course materials on their own path.
        if ((r.flags & 7) == 3 && r.pcw == 0x8a00071eu && (r.gmp & 512) != 0 &&
            r.tsp >> 29 == 2 && ((r.tsp >> 26) & 7) == 1 && ((r.tsp >> 22) & 3) == 2)
            wanted = headlightShader;
        if (m.shader != wanted) {m.shader = wanted;sceneCommandsDirty=true;}
        if(!OrderedSubmissionActive)m.renderQueue = queue;
        ConfigureSourceWords(m, r, cullMode);
        uint source = original ? r.tsp >> 29 : 1, destination = original ? (r.tsp >> 26) & 7 : 0;
        m.SetInt("_SrcBlend", (int)Factor(source, true, false)); m.SetInt("_DstBlend", (int)Factor(destination, false, false));
        m.SetInt("_SrcBlendAlpha", (int)Factor(source, true, true)); m.SetInt("_DstBlendAlpha", (int)Factor(destination, false, true));
        uint list = original ? (r.pcw >> 24) & 7 : 0;
        // Keep cars, skies/unlit artwork, UI, projected headlights and
        // translucent shadow overlays out of the scenery exposure curve.
        m.SetFloat("_TrackSurface", IsCourseGeometry(r) && list != 2 &&
            (r.flags & 2) == 0 && (r.gmp & 512) == 0 ? 1 : 0);
        // The source translucent list is auto-sorted per pixel by the original
        // hardware, which compares greater-or-equal and does not write depth.
        // Punch-through is not auto-sorted on the hardware: only the translucent
        // list compares greater-or-equal. Forcing it on punch-through lets
        // coplanar fragments overwrite by submission order, which is what made
        // signs flicker under motion and let the road show through foliage.
        uint compare = !original || list == 2 ? 6 : r.isp >> 29;
        var depthTest = DepthComparisons[compare];
        // Opt-in actual-player diagnostic: isolate ShaderLab comparison
        // semantics from the existing reversed clip-space depth conversion.
        if (diagnosticFlipDepth) {
            if (depthTest == CompareFunction.Less) depthTest = CompareFunction.Greater;
            else if (depthTest == CompareFunction.Greater) depthTest = CompareFunction.Less;
            else if (depthTest == CompareFunction.LessEqual) depthTest = CompareFunction.GreaterEqual;
            else if (depthTest == CompareFunction.GreaterEqual) depthTest = CompareFunction.LessEqual;
        }
        m.SetInt("_ZTest", (int)depthTest); m.SetInt("_ZWrite", original && list == 2 ? 0 : (!original || list == 4 || ((r.isp >> 26) & 1) == 0 ? 1 : 0));
        uint sampler = original ? ((r.tsp >> 16) & 1) | (((r.tsp >> 15) & 1) << 1) | (((r.tsp >> 18) & 1) << 2) | (((r.tsp >> 17) & 1) << 3) | ((((r.tsp >> 13) & 3) != 0 ? 1u : 0u) << 4) : 16;
        m.mainTexture = Texture(r.texture, sampler);
        bool sourceLighting = sourceCourseLightingReady && original && r.lightScope == 1 && (r.flags & 8) == 0 &&
            (wanted == sceneShader || wanted == directSceneShader);
        bool noClip = sourceLighting && r.viewMask == 3 && list != 4;
        if (sourceLighting && !noClip) m.EnableKeyword("IDAS_NATIVE_SOURCE_COURSE"); else m.DisableKeyword("IDAS_NATIVE_SOURCE_COURSE");
        if (noClip) m.EnableKeyword("IDAS_NATIVE_SOURCE_NOCLIP"); else m.DisableKeyword("IDAS_NATIVE_SOURCE_NOCLIP");
    }
    private int SourceCullMode(SceneRange r) {
        bool original = (r.flags & 1) != 0;
        bool actorCull = !diagnosticActorCullBaseline && ((r.flags & 32) != 0 || diagnosticCarCull);
        return original && (IsCourseGeometry(r) || actorCull) && (r.flags & 4) == 0 && !diagnosticCullBaseline
            ? (int)((r.isp >> 27) & 3) : 0;
    }
    private static void ConfigureSourceWords(Material m, SceneRange r, int cullMode) {
        // SetInt aliases SetFloat in Unity. Source material words require the
        // true integer setter or their low bits are lost above2^24.
        m.SetInteger("pcw", unchecked((int)r.pcw)); m.SetInteger("tsp", unchecked((int)r.tsp)); m.SetInteger("gmp", unchecked((int)r.gmp));
        m.SetInteger("original", (int)(r.flags & 1)); m.SetInteger("emissive", (int)((r.flags >> 1) & 1)); m.SetInteger("billboard", (int)((r.flags >> 2) & 1));
        m.SetInteger("courseLightRange", r.lightScope != 0 ? 1 : 0); m.SetInteger("_LightScope", (int)r.lightScope); m.SetInteger("viewMask", (int)r.viewMask);
        uint g = r.gloss & 255; m.SetFloat("glossCoefficient", (1 + (g & 31) / 32f) * Mathf.Pow(2, (int)(g >> 5) - 1));
        var direction = r.lightDirection; direction.w = (r.flags & 8) != 0 ? 1 : 0; m.SetVector("_LightOverride", direction);
        m.SetInteger("courseCullMode", cullMode);
    }
    private void ConfigureDepthSourceWords(Material m, SceneRange r) {
        // Colour variants can compile out these uniforms. The alpha-depth
        // shader still needs all source words to compute original coverage.
        ConfigureSourceWords(m, r, SourceCullMode(r));
        m.DisableKeyword("IDAS_NATIVE_SOURCE_COURSE");
        m.DisableKeyword("IDAS_NATIVE_SOURCE_NOCLIP");
    }
    // Diagnostic only: when a dictionary is installed, count the depth modes
    // the source materials ask for in one frame.
    internal static System.Collections.Generic.Dictionary<string,int> DepthCensus;
    private static void CountDepthMode(SceneRange r)
    {
        // Count source submissions even when cached materials need no setters.
        if (DepthCensus == null || (r.flags & 1) == 0) return;
        uint list = (r.pcw >> 24) & 7, compare = list == 2 ? 6 : r.isp >> 29;
        uint zw = (uint)(list == 2 ? 0 : list == 4 || ((r.isp >> 26) & 1) == 0 ? 1 : 0);
        uint sb = (r.tsp >> 29) & 7, db = (r.tsp >> 26) & 7;
        string k = "list" + list + " compare" + compare + " zwrite" + zw + " src" + sb + " dst" + db
            + " emissive" + ((r.flags >> 1) & 1);
        DepthCensus.TryGetValue(k, out int n); DepthCensus[k] = n + 1;
    }
    private static BlendMode Factor(uint code, bool source, bool alpha)
    {
        switch (code) {
            case 0: return BlendMode.Zero; case 1: return BlendMode.One;
            case 2: return source ? (alpha ? BlendMode.DstAlpha : BlendMode.DstColor) : (alpha ? BlendMode.SrcAlpha : BlendMode.SrcColor);
            case 3: return source ? (alpha ? BlendMode.OneMinusDstAlpha : BlendMode.OneMinusDstColor) : (alpha ? BlendMode.OneMinusSrcAlpha : BlendMode.OneMinusSrcColor);
            case 4: return BlendMode.SrcAlpha; case 5: return BlendMode.OneMinusSrcAlpha; case 6: return BlendMode.DstAlpha; default: return BlendMode.OneMinusDstAlpha;
        }
    }
    private unsafe Texture Texture(uint id, uint sampler)
    {
        if (id >= textureRecords.Length) return Texture2D.whiteTexture;
        ulong key = ((ulong)id << 32) | sampler;
        if (textureCache.TryGetValue(key, out var ready)) return ready;
        Idas3.Multiplayer.Idas3OnlineTransportPump.KeepAliveDuringNativeLoad();
        var image = textureRecords[id];
        if (image.width == 0 || image.height == 0 || image.width > 2048 || image.height > 2048 || image.argb == IntPtr.Zero ||
            image.pixelCount != (ulong)image.width * image.height || image.pixelCount > 4194304)
            throw new InvalidOperationException("Invalid Unity source texture.");
        var texture = new Texture2D((int)image.width, (int)image.height, TextureFormat.BGRA32, true, true) { name = "Original texture " + id + " sampler " + sampler };
        // The original bank carries mip0 only; native D3D GenerateMips builds
        // the rest. LoadRawTextureData expects the ENTIRE allocated mip chain.
        var pixels = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<uint>(image.argb.ToPointer(), checked((int)image.pixelCount), Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        var safety = AtomicSafetyHandle.Create(); NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref pixels, safety);
#endif
        try { texture.SetPixelData(pixels, 0); }
        catch { Destroy(texture); throw; }
        finally
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(safety);
#endif
        }
        texture.wrapModeU = (sampler & 1) != 0 ? TextureWrapMode.Clamp : (sampler & 4) != 0 ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;
        texture.wrapModeV = (sampler & 2) != 0 ? TextureWrapMode.Clamp : (sampler & 8) != 0 ? TextureWrapMode.Mirror : TextureWrapMode.Repeat;
        // Point-sampled source materials crawl above the cabinet resolution,
        // which the per-pixel alternation map shows as a fine speckle over the
        // rock face, the guardrails and the road ahead.
        bool filtered = (sampler & 16) != 0 || Screen.height > 480;
        texture.filterMode = filtered ? FilterMode.Trilinear : FilterMode.Point;
        texture.anisoLevel = filtered ? 16 : 1;
        texture.Apply(true, true); textureCache.Add(key, texture);
        return texture;
    }
    private static Matrix4x4 NativeMatrix(float[] words, int offset)
    {
        var matrix = new Matrix4x4(); for (int row = 0; row < 4; ++row) for (int col = 0; col < 4; ++col) matrix[row, col] = words[offset + col * 4 + row]; return matrix;
    }
    // Geometry ownership is independent of the original light-array scope.
    // The opening has authored course meshes but uses its own fallback lights.
    private bool IsCourseGeometry(SceneRange r) => r.lightScope == 1 ||
        ((r.flags & 16) != 0 && !diagnosticIntroFoliageBaseline);
    // Opt-in visual probe: source geometry and its captured lighting stay fixed.
    // Only the main clip/culling camera changes, so opposing sides of the same
    // authored foliage can be compared without moving the physics actor.
    public void SetDiagnosticCameraPose(Vector3 eye, Vector3 target)
    {
        var args = Idas3LaunchArguments.GetArguments();
        if (Array.IndexOf(args, "-idas3-scene-cull-check") < 0 &&
            Array.IndexOf(args, "-idas3-scene-intro-check") < 0 &&
            Array.IndexOf(args, "-idas3-scene-car-door-check") < 0 &&
            Array.IndexOf(args, "-idas3-scene-intro-foliage-check") < 0 &&
            Array.IndexOf(args, "-idas3-sadamine-boundary-check") < 0)
            throw new InvalidOperationException("Diagnostic camera requires the isolated culling check.");
        if ((target-eye).sqrMagnitude < .001f) throw new ArgumentException("Degenerate diagnostic camera.");
        diagnosticEye=eye;diagnosticTarget=target;diagnosticCameraPose=true;
    }
    public void ClearDiagnosticCameraPose() { diagnosticCameraPose=false; }
    public void SetDiagnosticCourseLampVisibility(bool enabled,bool carOnly=false)
    {
        if(Array.IndexOf(Idas3LaunchArguments.GetArguments(),"-hakone-smoke")<0)
            throw new InvalidOperationException("Lamp capture requires the isolated Hakone smoke check.");
        if(enabled){framesBuffer.SetData(frameVectors);return;}
        var sample=(Vector4[])frameVectors.Clone();
        for(int view=0;view<2;view++){
            if(carOnly)sample[view*23+7].w=0;
            else for(int row=14;row<22;row++)sample[view*23+row].w=0;
        }
        framesBuffer.SetData(sample);
    }
    private void ApplyDiagnosticCamera(ref SceneCamera camera)
    {
        var oldView=NativeMatrix(Array.ConvertAll(lightWords, BitConverter.Int32BitsToSingle),0);
        var projection=NativeMatrix(frameWords,0)*oldView.inverse;
        Vector3 z=(diagnosticEye-diagnosticTarget).normalized;
        Vector3 x=Vector3.Cross(Vector3.up,z).normalized,y=Vector3.Cross(z,x);
        var view=Matrix4x4.identity;
        view.SetRow(0,new Vector4(x.x,x.y,x.z,-Vector3.Dot(x,diagnosticEye)));
        view.SetRow(1,new Vector4(y.x,y.y,y.z,-Vector3.Dot(y,diagnosticEye)));
        view.SetRow(2,new Vector4(z.x,z.y,z.z,-Vector3.Dot(z,diagnosticEye)));
        var vp=projection*view;
        for(int row=0;row<4;++row)for(int col=0;col<4;++col)frameWords[col*4+row]=vp[row,col];
        camera.eye=diagnosticEye;camera.target=diagnosticTarget;camera.up=y;
        for(int i=0;i<3;++i){frameWords[16+i]=diagnosticEye[i];frameWords[32+i]=x[i];frameWords[36+i]=y[i];}
    }
    private static SceneCamera ExpandMainViewport(SceneCamera c, uint width, uint height, float[] words)
    {
        if (width == 0 || height == 0 || c.viewport.z <= 0 || c.viewport.w <= 0)
            throw new InvalidOperationException("Invalid original main camera viewport.");
        if (c.viewport.x == 0 && c.viewport.y == 0 && c.viewport.z == width && c.viewport.w == height)
            return c; // Ordinary race/demo projection remains bit-identical.
        // A panel aperture is a hole in a menu screen, not a narrow window onto
        // the world: widening it would spill the car across the whole screen.
        if ((c.leftHanded & 2) != 0) return c;
        float sx = c.viewport.z / width, sy = c.viewport.w / height;
        float tx = (2 * c.viewport.x + c.viewport.z) / width - 1;
        float ty = 1 - (2 * c.viewport.y + c.viewport.w) / height;
        // Native row-vector VP: x' = sx*x + tx*w, y' = sy*y + ty*w.
        // Converting either result to output pixels produces the SAME position
        // and object proportions as the original smaller viewport. The extra
        // window area reveals additional world instead of stretching/cropping.
        // Including tx/ty also preserves an authored off-center aperture.
        for (int row = 0; row < 4; ++row) {
            int i = row * 4;
            words[i] = sx * words[i] + tx * words[i + 3];
            words[i + 1] = sy * words[i + 1] + ty * words[i + 3];
        }
        c.verticalFov = (float)(2 * Math.Atan(Math.Tan(c.verticalFov * .5) / sy));
        c.aspect *= sy / sx;
        c.viewport = new Vector4(0, 0, width, height);
        return c;
    }
    private void SetCamera(Camera camera, SceneCamera c, int view, Color clear, uint width, uint height)
    {
        camera.enabled = true; camera.backgroundColor = clear;
        camera.transform.SetPositionAndRotation(c.eye, Quaternion.LookRotation(c.target - c.eye, c.up));
        camera.fieldOfView = c.verticalFov * Mathf.Rad2Deg; camera.aspect = c.aspect; camera.nearClipPlane = c.nearClip; camera.farClipPlane = c.farClip;
        camera.rect = new Rect(ViewportRect.x + c.viewport.x / width * ViewportRect.width,
            ViewportRect.y + (1 - (c.viewport.y + c.viewport.w) / height) * ViewportRect.height,
            c.viewport.z / width * ViewportRect.width, c.viewport.w / height * ViewportRect.height);
        if(view==1){
            var rect=camera.rect;float scale=HudOptions.HudGroupScale(4);
            var offset=HudOptions.HudOffset(4);
            camera.rect=new Rect(.5f+(rect.x-.5f)*scale+offset.x,1f+(rect.y-1f)*scale-offset.y,rect.width*scale,rect.height*scale);
        }
        Buffer.BlockCopy(lightWords, view * 4 * 156 * 4, viewWordsScratch, 0, 64);
        var viewWords = viewWordsScratch;
        var matrix = NativeMatrix(viewWords, 0); var viewProjection = NativeMatrix(frameWords, view * 92);
        camera.worldToCameraMatrix = matrix; camera.projectionMatrix = viewProjection * matrix.inverse; camera.cullingMatrix = viewProjection;
    }
    private void UpdateLampObjects()
    {
        for (int i = 0; i < lampObjects.Length; ++i)
        {
            int row = (14 + i) * 4;
            if (lampObjects[i] == null) {
                var obj = new GameObject("Course lamp " + i); obj.transform.SetParent(owners[1], false);
                var light = obj.AddComponent<Light>(); light.type = LightType.Point; light.shadows = LightShadows.None;
                light.color = new Color(1, .91f, .76f); light.intensity = 1.6f; lampObjects[i] = light;
            }
            var lamp = lampObjects[i]; lamp.enabled = frameWords[row + 3] > 0;
            lamp.transform.position = new Vector3(frameWords[row], frameWords[row + 1], frameWords[row + 2]); lamp.range = lamp.enabled ? 1 / Mathf.Sqrt(frameWords[row + 3]) : 0;
            // Original materials evaluate these captured lights themselves;
            // Unity Light objects are inspectable scene controls, not a second
            // additive pass over source ELAN/light-projection illumination.
        }
    }
    private void OnDestroy()
    {
        Camera.onPreRender -= BeforeCamera;
        ClearSharedDepthMaterials();
        if (opaqueAlphaDepth != null) {
            if (main) main.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, opaqueAlphaDepth);
            opaqueAlphaDepth.Release(); opaqueAlphaDepth = null;
        }
        if (mirrorAlphaDepth != null) {
            if (mirror) mirror.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, mirrorAlphaDepth);
            mirrorAlphaDepth.Release(); mirrorAlphaDepth = null;
        }
        framesBuffer?.Release(); lightsBuffer?.Release(); fogBuffer?.Release();
        emptyLampCells?.Release(); emptyLampLights?.Release();
        if(indexScratch.IsCreated)indexScratch.Dispose();
        if(metalScratch.IsCreated)metalScratch.Dispose();
        if(mainSceneCommands!=null){if(main)main.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,mainSceneCommands);mainSceneCommands.Release();}
        if(mirrorSceneCommands!=null){if(mirror)mirror.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,mirrorSceneCommands);mirrorSceneCommands.Release();}
        warmupCommands?.Release();
        if(warmupTarget!=null){warmupTarget.Release();Destroy(warmupTarget);}
        foreach (var item in objects) {
            if (item.vertexCache.IsCreated) item.vertexCache.Dispose();
            if (item.material != null) Destroy(item.material);
            if (diagnosticDepthMaterialBaseline && item.depthMaterial != null) Destroy(item.depthMaterial);
            if (item.mesh != null) Destroy(item.mesh);
        }
        foreach(var state in sharedMaterials.Values){
            Destroy(state.scene);
        }
        sharedMaterials.Clear();
        foreach (var texture in textureCache.Values) if (texture != null) Destroy(texture);
        if (worldRoot != null) Destroy(worldRoot.gameObject);
        if (mirror != null) Destroy(mirror.gameObject); if (backdrop != null) Destroy(backdrop.gameObject);
        if (canvasClear != null) Destroy(canvasClear.gameObject);
    }
}

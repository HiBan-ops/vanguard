using System;
using System.Threading;
using System.Threading.Tasks;
using Vanguard.Client.Diagnostics;

#if SPT_CLIENT
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
#endif

// Responsibility: owns one native EFT PlayerModelLoader instance for one Squad Preview Operator body.
// Flow: the loader receives a native LastPlayerStateClass plus the smallest qualified inventory-controller dependency,
// invokes PlayerModelLoader.Load with animateWeapon=false, parents the loaded body under a Vanguard-owned host,
// and clears/cancels it deterministically.
// Authority boundary: renderer lifecycle only. The loader never owns persistent Operator state or menu host authority.
// Invariant: one instance owns at most one body; cancellation/stale generations cannot resurrect a cleared body.
namespace Vanguard.Client.UI.SquadPreview;

internal interface IVanguardSquadPreviewBodyLoader
{
    bool IsLoaded { get; }
    string? LoadedOperatorId { get; }

#if SPT_CLIENT
    Task<VanguardSquadPreviewLoadResult> LoadAsync(
        string operatorId,
        object nativeVisualState,
        object inventoryController,
        Transform host,
        Vector3 localPosition,
        CancellationToken cancellationToken);

    bool TrySetLocalPosition(Vector3 localPosition);
    bool TryApplyPresentation(VanguardSquadPreviewPresentationState presentationState);
#else
    Task<VanguardSquadPreviewLoadResult> LoadAsync(
        string operatorId,
        object nativeVisualState,
        object inventoryController,
        object host,
        object localPosition,
        CancellationToken cancellationToken);

    bool TrySetLocalPosition(object localPosition);
    bool TryApplyPresentation(VanguardSquadPreviewPresentationState presentationState);
#endif

    void Clear(string reason);
}

internal sealed class VanguardSquadPreviewBodyLoader : IVanguardSquadPreviewBodyLoader
{
    public const string LoadBeginTag = "SQUAD_PREVIEW_OPERATOR_LOAD_BEGIN";
    public const string LoadReadyTag = "SQUAD_PREVIEW_OPERATOR_LOAD_READY";
    public const string LoadCancelledTag = "SQUAD_PREVIEW_OPERATOR_LOAD_CANCELLED";
    public const string ClearedTag = "SQUAD_PREVIEW_OPERATOR_CLEARED";
    public const string PresentationTag = "SQUAD_PREVIEW_SHADOW_PRESENTATION";

    private readonly object gate = new();
    private object? playerModelLoader;
    private CancellationTokenSource? activeLoadCts;
    private int generation;
    private string? loadedOperatorId;
    private bool isLoaded;
#if SPT_CLIENT
    private GameObject? loadedModelGameObject;
    private readonly List<RendererPresentationSnapshot> presentationSnapshots = new();
    private VanguardSquadPreviewPresentationState? appliedPresentationState;
    private bool presentationCacheInitialized;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
    private static readonly int MainColorId = Shader.PropertyToID("_MainColor");
    private static readonly int Color0Id = Shader.PropertyToID("_Color0");
    private static readonly int Color1Id = Shader.PropertyToID("_Color1");
    private static readonly int Color2Id = Shader.PropertyToID("_Color2");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private const float ShadowColorFactor = 0.14f;

    private sealed class RendererPresentationSnapshot
    {
        public Renderer Renderer { get; init; } = null!;
        public int MaterialIndex { get; init; }
        public MaterialPropertyBlock OriginalBlock { get; init; } = null!;
        public MaterialPropertyBlock ShadowBlock { get; init; } = null!;
    }
#endif

    public bool IsLoaded
    {
        get
        {
            lock (gate)
            {
                return isLoaded;
            }
        }
    }

    public string? LoadedOperatorId
    {
        get
        {
            lock (gate)
            {
                return loadedOperatorId;
            }
        }
    }

#if SPT_CLIENT
    public async Task<VanguardSquadPreviewLoadResult> LoadAsync(
        string operatorId,
        object nativeVisualState,
        object inventoryController,
        Transform host,
        Vector3 localPosition,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return VanguardSquadPreviewLoadResult.Failed("operator_id_missing");
        }

        if (nativeVisualState == null || inventoryController == null || host == null)
        {
            return VanguardSquadPreviewLoadResult.Failed("renderer_dependency_missing");
        }

        Clear("replace_before_load");

        CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int currentGeneration;
        object loader;
        MethodInfo loadMethod;
        lock (gate)
        {
            currentGeneration = ++generation;
            activeLoadCts = linkedCts;
            loadedOperatorId = operatorId;
            isLoaded = false;
        }

        try
        {
            Type? loaderType = ResolveType("PlayerModelLoader");
            if (loaderType == null)
            {
                return FailAndClear(currentGeneration, operatorId, "player_model_loader_type_missing");
            }

            loader = Activator.CreateInstance(loaderType)
                ?? throw new InvalidOperationException("PlayerModelLoader constructor returned null.");
            loadMethod = ResolveLoadMethod(loaderType, nativeVisualState.GetType(), inventoryController.GetType())
                ?? throw new MissingMethodException(loaderType.FullName, "Load");

            lock (gate)
            {
                if (generation != currentGeneration || linkedCts.IsCancellationRequested)
                {
                    TryClearLoader(loader);
                    return VanguardSquadPreviewLoadResult.CancelledResult("cancelled_before_native_load");
                }

                playerModelLoader = loader;
            }

            VanguardClientDiagnosticsLog.Info(
                LoadBeginTag,
                $"operator={operatorId}; generation={currentGeneration}; nativeState={nativeVisualState.GetType().Name}; inventoryController={inventoryController.GetType().Name}; animateWeapon=false; host={host.name}; mutation=false");

            object?[] args = BuildLoadArguments(
                loadMethod,
                nativeVisualState,
                inventoryController,
                linkedCts.Token,
                localPosition);
            object? invocation = loadMethod.Invoke(loader, args);
            if (invocation is not Task nativeLoadTask)
            {
                throw new InvalidOperationException("PlayerModelLoader.Load did not return Task.");
            }

            await nativeLoadTask;

            lock (gate)
            {
                if (generation != currentGeneration || linkedCts.IsCancellationRequested)
                {
                    TryClearLoader(loader);
                    if (ReferenceEquals(playerModelLoader, loader))
                    {
                        playerModelLoader = null;
                    }
                    return VanguardSquadPreviewLoadResult.CancelledResult("cancelled_after_native_load");
                }
            }

            object? playerBody = ResolveMember(loader, "PlayerBody");
            object? modelPlayerPoser = ResolveMember(loader, "ModelPlayerPoser");
            GameObject? modelGameObject = ResolveGameObject(modelPlayerPoser) ?? ResolveGameObject(playerBody);
            if (playerBody == null || modelPlayerPoser == null || modelGameObject == null)
            {
                return FailAndClear(
                    currentGeneration,
                    operatorId,
                    $"native_load_incomplete_playerBody_{playerBody != null}_poser_{modelPlayerPoser != null}_gameObject_{modelGameObject != null}");
            }

            int previewLayer = LayerMask.NameToLayer("Weapon Preview");
            if (previewLayer < 0 || previewLayer > 31)
            {
                return FailAndClear(currentGeneration, operatorId, "weapon_preview_layer_missing");
            }

            Transform modelTransform = modelGameObject.transform;
            modelTransform.SetParent(host, false);
            modelTransform.localPosition = localPosition;
            modelTransform.localRotation = Quaternion.identity;
            SetLayerRecursively(modelTransform, previewLayer);
            modelGameObject.name = $"VanguardSquadPreviewOperator_{operatorId}";
            modelGameObject.SetActive(true);

            lock (gate)
            {
                if (generation != currentGeneration || linkedCts.IsCancellationRequested)
                {
                    TryClearLoader(loader);
                    if (ReferenceEquals(playerModelLoader, loader))
                    {
                        playerModelLoader = null;
                    }
                    return VanguardSquadPreviewLoadResult.CancelledResult("cancelled_before_ready_commit");
                }

                isLoaded = true;
                loadedModelGameObject = modelGameObject;
            }

            int rendererCount = modelGameObject.GetComponentsInChildren<Renderer>(true).Length;
            VanguardClientDiagnosticsLog.Info(
                LoadReadyTag,
                $"operator={operatorId}; generation={currentGeneration}; body=true; poser=true; gameObject=true; renderers={rendererCount}; animateWeapon=false; host={host.name}; mutation=false");
            return VanguardSquadPreviewLoadResult.Loaded(rendererCount);
        }
        catch (OperationCanceledException)
        {
            return CancelAndClear(currentGeneration, operatorId, "operation_cancelled");
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            return FailAndClear(currentGeneration, operatorId, root.GetType().Name + ":" + root.Message);
        }
    }

    public bool TrySetLocalPosition(Vector3 localPosition)
    {
        GameObject? model;
        lock (gate)
        {
            model = isLoaded ? loadedModelGameObject : null;
        }

        if (model == null)
        {
            return false;
        }

        model.transform.localPosition = localPosition;
        return true;
    }

    public bool TryApplyPresentation(VanguardSquadPreviewPresentationState presentationState)
    {
        GameObject? model;
        string operatorId;
        bool cacheInitialized;

        lock (gate)
        {
            if (!isLoaded || loadedModelGameObject == null || string.IsNullOrWhiteSpace(loadedOperatorId))
            {
                return false;
            }

            if (appliedPresentationState == presentationState)
            {
                return true;
            }

            model = loadedModelGameObject;
            operatorId = loadedOperatorId!;
            cacheInitialized = presentationCacheInitialized;
        }

        // Normal is the overwhelmingly common Off-Raid state. Do not enumerate renderers/materials,
        // allocate MaterialPropertyBlocks, or touch renderer overrides until Shadow is actually needed.
        // This preserves the lightweight Normal-state hot path for ordinary menu/profile navigation.
        if (presentationState == VanguardSquadPreviewPresentationState.Normal && !cacheInitialized)
        {
            lock (gate)
            {
                if (isLoaded
                    && ReferenceEquals(loadedModelGameObject, model)
                    && string.Equals(loadedOperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
                {
                    appliedPresentationState = VanguardSquadPreviewPresentationState.Normal;
                }
            }

            VanguardClientDiagnosticsLog.Diagnostic(
                PresentationTag,
                () => $"phase=applied; operator={operatorId}; state=Normal; cache=deferred_until_first_shadow; materialSlots=0; restoredSlots=0; shadowedSlots=0; factor={ShadowColorFactor:0.00}; propertyBlock=true; materialClone=false; mutation=presentation_only");
            return true;
        }

        if (!cacheInitialized)
        {
            if (!TryInitializePresentationCache(model, operatorId))
            {
                return false;
            }
        }

        RendererPresentationSnapshot[] snapshots;
        lock (gate)
        {
            if (!isLoaded
                || !ReferenceEquals(loadedModelGameObject, model)
                || !string.Equals(loadedOperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            snapshots = presentationSnapshots.ToArray();
        }

        long applyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        int restoredSlots = 0;
        int shadowedSlots = 0;

        foreach (RendererPresentationSnapshot snapshot in snapshots)
        {
            if (snapshot.Renderer == null)
            {
                continue;
            }

            try
            {
                if (presentationState == VanguardSquadPreviewPresentationState.Shadow)
                {
                    snapshot.Renderer.SetPropertyBlock(snapshot.ShadowBlock, snapshot.MaterialIndex);
                    shadowedSlots++;
                }
                else
                {
                    snapshot.Renderer.SetPropertyBlock(snapshot.OriginalBlock, snapshot.MaterialIndex);
                    restoredSlots++;
                }
            }
            catch (Exception exception)
            {
                VanguardClientDiagnosticsLog.Warning(
                    PresentationTag,
                    $"phase=apply_slot_failed; operator={operatorId}; state={presentationState}; renderer={Safe(snapshot.Renderer.name)}; materialIndex={snapshot.MaterialIndex}; reason={Safe(exception.GetType().Name + ":" + exception.Message)}; mutation=presentation_only");
            }
        }

        lock (gate)
        {
            if (isLoaded
                && ReferenceEquals(loadedModelGameObject, model)
                && string.Equals(loadedOperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
            {
                appliedPresentationState = presentationState;
            }
        }

        double applyMs = ElapsedMilliseconds(applyStarted);
        VanguardClientDiagnosticsLog.Diagnostic(
            PresentationTag,
            () => $"phase=applied; operator={operatorId}; state={presentationState}; cache=ready; materialSlots={snapshots.Length}; restoredSlots={restoredSlots}; shadowedSlots={shadowedSlots}; factor={ShadowColorFactor:0.00}; applyMs={applyMs:0.###}; propertyBlock=true; materialClone=false; mutation=presentation_only");
        return true;
    }
#else
    public Task<VanguardSquadPreviewLoadResult> LoadAsync(
        string operatorId,
        object nativeVisualState,
        object inventoryController,
        object host,
        object localPosition,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(VanguardSquadPreviewLoadResult.Failed("spt_client_not_defined"));
    }

    public bool TrySetLocalPosition(object localPosition) => false;
    public bool TryApplyPresentation(VanguardSquadPreviewPresentationState presentationState) => false;
#endif

    public void Clear(string reason)
    {
        object? loaderToClear;
        CancellationTokenSource? ctsToDispose;
        string? operatorId;
        int clearedGeneration;
        bool hadState;

        lock (gate)
        {
            hadState = playerModelLoader != null || activeLoadCts != null || isLoaded || !string.IsNullOrWhiteSpace(loadedOperatorId);
            generation++;
            clearedGeneration = generation;
            loaderToClear = playerModelLoader;
            ctsToDispose = activeLoadCts;
            operatorId = loadedOperatorId;
            playerModelLoader = null;
            activeLoadCts = null;
            loadedOperatorId = null;
            isLoaded = false;
#if SPT_CLIENT
            loadedModelGameObject = null;
            presentationSnapshots.Clear();
            appliedPresentationState = null;
            presentationCacheInitialized = false;
#endif
        }

        try
        {
            ctsToDispose?.Cancel();
        }
        catch
        {
            // Cancellation is best-effort; native Clear below remains mandatory.
        }

#if SPT_CLIENT
        TryClearLoader(loaderToClear);
#endif
        ctsToDispose?.Dispose();

        if (hadState)
        {
            VanguardClientDiagnosticsLog.Info(
                ClearedTag,
                $"operator={operatorId ?? "<none>"}; generation={clearedGeneration}; reason={reason}; nativeClearInvoked={loaderToClear != null}; mutation=false");
        }
    }

#if SPT_CLIENT
    private VanguardSquadPreviewLoadResult CancelAndClear(int currentGeneration, string operatorId, string reason)
    {
        VanguardClientDiagnosticsLog.Info(
            LoadCancelledTag,
            $"operator={operatorId}; generation={currentGeneration}; reason={reason}; mutation=false");
        Clear(reason);
        return VanguardSquadPreviewLoadResult.CancelledResult(reason);
    }

    private VanguardSquadPreviewLoadResult FailAndClear(int currentGeneration, string operatorId, string reason)
    {
        VanguardClientDiagnosticsLog.Warning(
            LoadCancelledTag,
            $"operator={operatorId}; generation={currentGeneration}; state=failed_closed; reason={reason}; mutation=false; persistentTruthUntouched=true");
        Clear("failed_" + reason);
        return VanguardSquadPreviewLoadResult.Failed(reason);
    }

    private static MethodInfo? ResolveLoadMethod(Type loaderType, Type visualStateType, Type inventoryControllerType)
    {
        return loaderType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "Load", StringComparison.Ordinal))
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
            .Where(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length >= 2
                    && parameters[0].ParameterType.IsAssignableFrom(visualStateType)
                    && parameters[1].ParameterType.IsAssignableFrom(inventoryControllerType);
            })
            .OrderByDescending(method => method.GetParameters().Length)
            .FirstOrDefault();
    }

    private static object?[] BuildLoadArguments(
        MethodInfo loadMethod,
        object nativeVisualState,
        object inventoryController,
        CancellationToken cancellationToken,
        Vector3 localPosition)
    {
        ParameterInfo[] parameters = loadMethod.GetParameters();
        object?[] args = new object?[parameters.Length];

        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            Type parameterType = parameter.ParameterType;

            if (index == 0)
            {
                args[index] = nativeVisualState;
                continue;
            }

            if (index == 1)
            {
                args[index] = inventoryController;
                continue;
            }

            if (parameterType == typeof(CancellationToken))
            {
                args[index] = cancellationToken;
                continue;
            }

            if (parameterType == typeof(Vector3))
            {
                // Native PlayerModelLoader call uses Vector3.zero; Vanguard positions the
                // completed model only after it is parented under the squad host.
                args[index] = Vector3.zero;
                continue;
            }

            if (parameterType == typeof(bool))
            {
                // PlayerModelLoader's known booleans include animateWeapon and bottom shadow.
                // Squad Preview intentionally keeps weapon animation off and does not request extra native shadow state.
                args[index] = false;
                continue;
            }

            if (parameterType == typeof(float))
            {
                args[index] = 0f;
                continue;
            }

            if (index == 3 && TryResolvePlayerModelViewFilter(parameterType, out object? viewFilter))
            {
                // EFT / SPT native call passes GClass1856.Default while the
                // PlayerModelLoader parameter is declared as its abstract GClass1854 base. Resolving
                // Default on the declared base type returns null and causes PlayerModelLoader to NRE.
                args[index] = viewFilter;
                continue;
            }

            if (index == 8 && TryResolveDefaultLayerMask(parameterType, out object? defaultLayerMask))
            {
                // EFT / SPT PlayerModelLoader.Load uses LayerMaskClass.DefaultLayerMask
                // at this position. Resolve it dynamically so the adapter remains isolated from the obfuscated type.
                args[index] = defaultLayerMask;
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                args[index] = parameter.DefaultValue;
                continue;
            }

            object? nativeDefault = ResolveNativeDefault(parameterType);
            if (nativeDefault != null)
            {
                args[index] = nativeDefault;
                continue;
            }

            if (TryCreateEmptyCollection(parameterType, out object? collection))
            {
                args[index] = collection;
                continue;
            }

            args[index] = parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
        }

        return args;
    }

    private static bool TryResolvePlayerModelViewFilter(Type targetType, out object? value)
    {
        value = null;
        Type? concreteType = ResolveType("GClass1856");
        if (concreteType == null)
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object? raw = concreteType.GetField("Default", flags)?.GetValue(null)
            ?? concreteType.GetProperty("Default", flags)?.GetValue(null);
        if (raw == null || !targetType.IsInstanceOfType(raw))
        {
            return false;
        }

        value = raw;
        return true;
    }

    private static bool TryResolveDefaultLayerMask(Type targetType, out object? value)
    {
        value = null;
        Type? layerMaskClass = ResolveType("LayerMaskClass");
        if (layerMaskClass == null)
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        object? raw = layerMaskClass.GetProperty("DefaultLayerMask", flags)?.GetValue(null)
            ?? layerMaskClass.GetField("DefaultLayerMask", flags)?.GetValue(null);
        if (raw == null)
        {
            return false;
        }

        if (targetType.IsInstanceOfType(raw))
        {
            value = raw;
            return true;
        }

        try
        {
            value = Convert.ChangeType(raw, targetType);
            return value != null;
        }
        catch
        {
            return false;
        }
    }

    private static object? ResolveNativeDefault(Type type)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetProperty("Default", flags)?.GetValue(null)
            ?? type.GetField("Default", flags)?.GetValue(null)
            ?? type.GetProperty("DefaultLayerMask", flags)?.GetValue(null)
            ?? type.GetField("DefaultLayerMask", flags)?.GetValue(null);
    }

    private static bool TryCreateEmptyCollection(Type type, out object? value)
    {
        value = null;
        if (!typeof(IEnumerable).IsAssignableFrom(type) || type.IsArray || type == typeof(string))
        {
            return false;
        }

        try
        {
            value = Activator.CreateInstance(type);
            return value != null;
        }
        catch
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                Type concrete = typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments());
                value = Activator.CreateInstance(concrete);
                return value != null;
            }

            return false;
        }
    }


    private bool TryInitializePresentationCache(GameObject modelGameObject, string operatorId)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        var snapshots = new List<RendererPresentationSnapshot>();
        var shaders = new HashSet<string>(StringComparer.Ordinal);
        int totalMaterialSlots = 0;
        int unsupportedSlots = 0;
        int colorSlots = 0;
        int baseColorSlots = 0;
        int tintColorSlots = 0;
        int mainColorSlots = 0;
        int color012Slots = 0;
        int emissionSlots = 0;

        Renderer[] renderers = modelGameObject.GetComponentsInChildren<Renderer>(true);
        foreach (Renderer renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;
            totalMaterialSlots += materials.Length;

            for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                Material? material = materials[materialIndex];
                if (material == null)
                {
                    unsupportedSlots++;
                    continue;
                }

                bool hasColor = material.HasProperty(ColorId);
                bool hasBaseColor = material.HasProperty(BaseColorId);
                bool hasTintColor = material.HasProperty(TintColorId);
                bool hasMainColor = material.HasProperty(MainColorId);
                bool hasColor0 = material.HasProperty(Color0Id);
                bool hasColor1 = material.HasProperty(Color1Id);
                bool hasColor2 = material.HasProperty(Color2Id);
                bool hasEmissionColor = material.HasProperty(EmissionColorId);
                bool controllable = hasColor
                    || hasBaseColor
                    || hasTintColor
                    || hasMainColor
                    || hasColor0
                    || hasColor1
                    || hasColor2
                    || hasEmissionColor;

                string shaderName = material.shader != null ? material.shader.name : "<none>";
                shaders.Add(shaderName);

                if (!controllable)
                {
                    unsupportedSlots++;
                    continue;
                }

                colorSlots += hasColor ? 1 : 0;
                baseColorSlots += hasBaseColor ? 1 : 0;
                tintColorSlots += hasTintColor ? 1 : 0;
                mainColorSlots += hasMainColor ? 1 : 0;
                color012Slots += hasColor0 || hasColor1 || hasColor2 ? 1 : 0;
                emissionSlots += hasEmissionColor ? 1 : 0;

                // Capture both blocks once. Unity copies a MaterialPropertyBlock when SetPropertyBlock
                // is called, so these cached blocks can be safely reused for later state transitions.
                var originalBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(originalBlock, materialIndex);

                var shadowBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(shadowBlock, materialIndex);

                if (hasColor)
                {
                    shadowBlock.SetColor(ColorId, Darken(material.GetColor(ColorId)));
                }
                if (hasBaseColor)
                {
                    shadowBlock.SetColor(BaseColorId, Darken(material.GetColor(BaseColorId)));
                }
                if (hasTintColor)
                {
                    shadowBlock.SetColor(TintColorId, Darken(material.GetColor(TintColorId)));
                }
                if (hasMainColor)
                {
                    shadowBlock.SetColor(MainColorId, Darken(material.GetColor(MainColorId)));
                }
                if (hasColor0)
                {
                    shadowBlock.SetColor(Color0Id, Darken(material.GetColor(Color0Id)));
                }
                if (hasColor1)
                {
                    shadowBlock.SetColor(Color1Id, Darken(material.GetColor(Color1Id)));
                }
                if (hasColor2)
                {
                    shadowBlock.SetColor(Color2Id, Darken(material.GetColor(Color2Id)));
                }
                if (hasEmissionColor)
                {
                    Color emission = material.GetColor(EmissionColorId);
                    shadowBlock.SetColor(EmissionColorId, new Color(0f, 0f, 0f, emission.a));
                }

                snapshots.Add(new RendererPresentationSnapshot
                {
                    Renderer = renderer,
                    MaterialIndex = materialIndex,
                    OriginalBlock = originalBlock,
                    ShadowBlock = shadowBlock
                });
            }
        }

        lock (gate)
        {
            if (!isLoaded
                || !ReferenceEquals(loadedModelGameObject, modelGameObject)
                || !string.Equals(loadedOperatorId, operatorId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            presentationSnapshots.Clear();
            presentationSnapshots.AddRange(snapshots);
            presentationCacheInitialized = true;
        }

        double initMs = ElapsedMilliseconds(started);
        string shaderSample = string.Join(",", shaders.OrderBy(name => name, StringComparer.Ordinal).Take(12).Select(Safe));
        VanguardClientDiagnosticsLog.Diagnostic(
            PresentationTag,
            () => $"phase=coverage; operator={operatorId}; cache=lazy_initialized; renderers={renderers.Length}; materialSlots={totalMaterialSlots}; controllableSlots={snapshots.Count}; unsupportedSlots={unsupportedSlots}; color={colorSlots}; baseColor={baseColorSlots}; tintColor={tintColorSlots}; mainColor={mainColorSlots}; color012={color012Slots}; emission={emissionSlots}; shaderCount={shaders.Count}; shaderSample={shaderSample}; initMs={initMs:0.###}; propertyBlock=true; materialClone=false; mutation=false");
        return true;
    }

    private static double ElapsedMilliseconds(long startedTimestamp)
    {
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp;
        return elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency;
    }

    private static Color Darken(Color source)
    {
        return new Color(
            source.r * ShadowColorFactor,
            source.g * ShadowColorFactor,
            source.b * ShadowColorFactor,
            source.a);
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        for (int index = 0; index < root.childCount; index++)
        {
            SetLayerRecursively(root.GetChild(index), layer);
        }
    }

    private static GameObject? ResolveGameObject(object? instance)
    {
        if (instance is GameObject gameObject)
        {
            return gameObject;
        }

        if (instance is Component component)
        {
            return component.gameObject;
        }

        return ResolveMember(instance, "gameObject") as GameObject;
    }

    private static object? ResolveMember(object? instance, string name)
    {
        if (instance == null)
        {
            return null;
        }

        Type type = instance.GetType();
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
            ?? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    }

    private static void TryClearLoader(object? loader)
    {
        if (loader == null)
        {
            return;
        }

        try
        {
            loader.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(loader, Array.Empty<object>());
        }
        catch (Exception exception)
        {
            Exception root = Unwrap(exception);
            VanguardClientDiagnosticsLog.Warning(
                ClearedTag,
                $"native_loader_clear_failed reason={root.GetType().Name}:{root.Message}; mutation=false");
        }
    }

    private static string Safe(string? value) => string.IsNullOrWhiteSpace(value)
        ? "none"
        : value.Trim().Replace(' ', '_').Replace(';', '_').Replace('|', '_').Replace('\r', '_').Replace('\n', '_');

    private static Type? ResolveType(string fullOrSimpleName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? exact = assembly.GetType(fullOrSimpleName, throwOnError: false, ignoreCase: false);
            if (exact != null)
            {
                return exact;
            }

            try
            {
                Type? simple = assembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, fullOrSimpleName, StringComparison.Ordinal));
                if (simple != null)
                {
                    return simple;
                }
            }
            catch (ReflectionTypeLoadException)
            {
                // Optional/mod assemblies may not fully enumerate. Continue bounded discovery.
            }
        }

        return null;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } target)
        {
            exception = target.InnerException;
        }

        return exception;
    }
#endif
}

internal readonly struct VanguardSquadPreviewLoadResult
{
    private VanguardSquadPreviewLoadResult(bool success, bool cancelled, string reason, int rendererCount)
    {
        Success = success;
        Cancelled = cancelled;
        Reason = reason;
        RendererCount = rendererCount;
    }

    public bool Success { get; }
    public bool Cancelled { get; }
    public string Reason { get; }
    public int RendererCount { get; }

    public static VanguardSquadPreviewLoadResult Loaded(int rendererCount) => new(true, false, "loaded", rendererCount);
    public static VanguardSquadPreviewLoadResult Failed(string reason) => new(false, false, reason, 0);
    public static VanguardSquadPreviewLoadResult CancelledResult(string reason) => new(false, true, reason, 0);
}

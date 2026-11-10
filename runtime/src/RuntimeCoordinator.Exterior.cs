using Godot;
using OpenNV.Runtime.Content;
using OpenNV.Runtime.Presentation.Rendering;
using OpenNV.Runtime.World.Cells;
using OpenNV.Runtime.World.Actors;
using System.Diagnostics;
using OpenNV.Runtime.Gameplay.State;
using OpenNV.Runtime.Formats.Gamebryo;

namespace OpenNV.Runtime;

public partial class RuntimeCoordinator
{
    private FalloutExteriorGrid? _nativeExteriorGrid;
    private readonly Dictionary<string, Texture2D> _nativeLandscapeTextures = new(StringComparer.OrdinalIgnoreCase);
    private Node3D? _nativeStreamRoot;
    private sealed record PreparedExteriorGrid(FalloutExteriorGridScene Grid, IReadOnlyDictionary<FalloutFormKey, FalloutLandscapeTransport> Landscapes,
        IReadOnlyDictionary<string, FalloutNifFile> Models);
    private Task<PreparedExteriorGrid>? _nativeGridRead;
    private CancellationTokenSource? _nativeGridReadCancellation;
    private FalloutExteriorGridScene? _nativeGridPending;
    private Queue<(string Source, Func<bool> Upload)>? _nativeGridUploads;
    private readonly List<Node3D> _nativeGridStaged = [];
    private readonly Dictionary<FalloutFormKey, Node3D> _nativeGridModels = [];
    private (int X, int Y)? _nativeGridFailed;
    private double _nativeGridUploadMilliseconds;
    private double _nativeGridMaximumUploadMilliseconds;
    private string? _nativeGridMaximumUploadSource;
    private double _nativeGridCommitMilliseconds;
    private IReadOnlyDictionary<string, double>? _nativeGridCommitPhases;
    private (int X, int Y)? _nativeGridTarget;
    private string? _nativeGridError;
    private readonly HashSet<(int X, int Y)> _nativeWalkableGrid = [];
    private readonly Dictionary<FalloutFormKey, ulong> _nativeLandLastUse = [];
    private readonly Queue<HashSet<string>> _nativeRecentModelSets = [];
    private ulong _nativeGridGeneration;
    private bool NativeCollisionResident(Vector3 position)
    {
        if (_nativeDoorLoading || _nativeActiveCell is null || _nativeCurrentCellRoot is null) return false;
        if (_nativeActiveCell.Cell.Worldspace is null) return true;
        var width = 4096 * _configuration.World.GameUnitsToMeters;
        var radius = _configuration.Player.CapsuleRadiusMeters;
        for (var x = (int)MathF.Floor((position.X - radius) / width); x <= (int)MathF.Floor((position.X + radius) / width); x++)
            for (var y = (int)MathF.Floor((-position.Z - radius) / width); y <= (int)MathF.Floor((-position.Z + radius) / width); y++)
                if (!_nativeWalkableGrid.Contains((x, y))) return false;
        return true;
    }
    private object NativeStreamingState => new
    {
        reading = _nativeGridRead is not null,
        uploadsRemaining = _nativeGridUploads?.Count ?? 0,
        residentCells = _nativeWalkableGrid.Count,
        warmReferences = _nativeReferencePresentation?.WarmNodeCount ?? 0,
        terrainCacheCells = _nativeCurrentCellRoot?.GetChildren().OfType<RuntimeNativeLandscapeTransport>().Count() ?? 0,
        target = _nativeGridTarget is { } target ? new[] { target.X, target.Y } : null,
        preparedAhead = _nativeGridUploads?.Count == 0 && _nativeGridPending is not null,
        lastUploadMilliseconds = _nativeGridUploadMilliseconds,
        maximumUploadMilliseconds = _nativeGridMaximumUploadMilliseconds,
        maximumUploadSource = _nativeGridMaximumUploadSource,
        preparingNpcs = _nativeGridNpcPreparations.Count,
        lastCommitMilliseconds = _nativeGridCommitMilliseconds,
        lastCommitPhasesMilliseconds = _nativeGridCommitPhases,
        error = _nativeGridError
    };

    private void AdvanceNativeExteriorStreaming(double delta)
    {
        if (_nativeStreamRoot is not null && _nativeStreamRoot != _nativeCurrentCellRoot)
        {
            foreach (var node in _nativeGridStaged.Where(GodotObject.IsInstanceValid)) node.QueueFree();
            _nativeGridStaged.Clear(); _nativeGridModels.Clear(); _nativeGridUploads = null; _nativeGridPending = null;
            CancelNativeGridRead(); _nativeStreamRoot = null; _nativeGridFailed = null; _nativeGridTarget = null;
            _nativeLandLastUse.Clear(); _nativeRecentModelSets.Clear();
        }
        if (_nativePlayer is not { } player || _nativeActiveCell?.Cell.Worldspace is not { } world ||
            _nativeDoorLoading || GetTree().Paused) return;
        var units = _configuration.World.GameUnitsToMeters;
        var position = player.GlobalPosition;
        var coordinates = ((int)MathF.Floor(position.X / (4096 * units)), (int)MathF.Floor(-position.Z / (4096 * units)));
        try
        {
            // Preparation may lead the player, but CELL/gameplay ownership may
            // change only after ordinary movement reaches the requested cell.
            if (_nativeGridTarget is { } requested && coordinates != requested && coordinates != _nativeActiveCell.Cell.Coordinates)
            {
                foreach (var node in _nativeGridStaged.Where(GodotObject.IsInstanceValid)) node.QueueFree();
                _nativeGridStaged.Clear(); _nativeGridModels.Clear(); _nativeGridUploads = null; _nativeGridPending = null;
                CancelNativeGridRead(); _nativeGridTarget = null;
            }
            if (_nativeGridRead is { IsCompleted: true } read)
            {
                _nativeGridRead = null;
                StageNativeExteriorGrid(read.GetAwaiter().GetResult());
            }
            if (_nativeGridUploads is { } uploads)
            {
                var started = Stopwatch.GetTimestamp();
                // A pending decode or partial body yields. Visit each queued
                // item at most once this frame, without waiting on a worker.
                var remaining = uploads.Count;
                while (remaining-- > 0 && uploads.TryDequeue(out var upload))
                {
                    if (!upload.Upload()) uploads.Enqueue(upload);
                    _nativeGridUploadMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (_nativeGridUploadMilliseconds > _nativeGridMaximumUploadMilliseconds)
                    { _nativeGridMaximumUploadMilliseconds = _nativeGridUploadMilliseconds; _nativeGridMaximumUploadSource = upload.Source; }
                    if (_nativeGridUploadMilliseconds >= 3) break;
                }
                if (uploads.Count == 0 && coordinates == _nativeGridPending!.Scene.Cell.Coordinates) CommitNativeExteriorGrid();
                return;
            }
            if (_nativeGridRead is not null) return;
            var target = coordinates;
            if (coordinates == _nativeActiveCell.Cell.Coordinates)
            {
                var ahead = FalloutExteriorStreamTarget.Predict(position.X, -position.Z, player.Velocity.X, -player.Velocity.Z, units);
                target = (ahead.X, ahead.Y);
            }
            if (target == _nativeActiveCell.Cell.Coordinates || target == _nativeGridFailed) return;
            _nativeStreamRoot = _nativeCurrentCellRoot;
            _nativeGridTarget = target;
            _nativeGridError = null;
            var resident = _nativeWalkableGrid.ToHashSet();
            var residentModels = _nativeNifPrototypes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var content = RuntimeLiveContentSource.Current!;
            _nativeGridReadCancellation = new();
            var cancellation = _nativeGridReadCancellation.Token;
            _nativeGridRead = Task.Run(async () =>
            {
                var (grid, lands) = await FalloutContentWorkers.Run(() =>
                {
                    var resolved = ResolveExterior(world, [(target.Item1 + .5f) * 4096, (target.Item2 + .5f) * 4096, position.Y / units]);
                    var landscapes = new Dictionary<FalloutFormKey, FalloutLandscapeTransport>();
                    foreach (var cell in resolved.Cells.Where(cell => !resident.Contains(cell.Coordinates!.Value)))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var landscape = FalloutLandscapeTransportResolver.ResolveCell(_nativePluginStack!, cell, resolved.PersistentCell);
                        if (landscape is not null) landscapes.Add(cell.FormKey, landscape);
                    }
                    foreach (var path in landscapes.Values.SelectMany(land => land.Textures.Values)
                        .SelectMany(texture => new[] { texture.DiffusePath, texture.NormalPath }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        _ = content.TryRead(path, null, out _, out _);
                    }
                    return (resolved, landscapes);
                }, cancellation);
                var models = new Dictionary<string, FalloutNifFile>(StringComparer.OrdinalIgnoreCase);
                var paths = grid.Scene.References.Select(reference => grid.Scene.BaseObjects[reference.Base])
                    .Where(model => model.Signature is not ("NPC_" or "CREA") && model.ModelPath is not null)
                    .Select(model => model.ModelPath!).Distinct(StringComparer.OrdinalIgnoreCase).Where(path => !residentModels.Contains(path)).ToArray();
                var prepared = await Task.WhenAll(paths.Select(path => FalloutContentWorkers.Run(() =>
                {
                    try
                    {
                        if (!content.TryRead(path, null, out var bytes, out _)) return (Path: path, Nif: (FalloutNifFile?)null);
                        var nif = FalloutNifFile.Read(bytes);
                        foreach (var block in nif.Blocks.Where(block => block.TypeName == "BSShaderTextureSet"))
                            foreach (var texture in ((FalloutNifShaderTextureSet)nif.ReadObject(block.Index)).Textures.Where(texture => texture.Length != 0))
                                _ = content.TryRead(FalloutNifSurfaceInputs.TexturePath(texture), null, out _, out _);
                        return (Path: path, Nif: (FalloutNifFile?)nif);
                    }
                    // Prefetch does not admit or substitute a model. The ordinary
                    // reference owner still reports any enabled source failure.
                    catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException) { return (Path: path, Nif: (FalloutNifFile?)null); }
                }, cancellation)));
                foreach (var item in prepared)
                    if (item.Nif is not null) models.Add(item.Path, item.Nif);
                return new PreparedExteriorGrid(grid, lands, models);
            });
        }
        catch (Exception error)
        {
            _nativeGridError = error.Message; _nativeGridFailed = _nativeGridTarget ?? coordinates;
            CancelNativeGridRead(); _nativeGridUploads = null; _nativeGridPending = null;
            _nativeGridTarget = null;
            foreach (var node in _nativeGridStaged.Where(GodotObject.IsInstanceValid)) node.QueueFree();
            _nativeGridStaged.Clear(); _nativeGridModels.Clear();
            GD.PushError($"OPENNV_EXTERIOR_STREAM_FAIL cell={coordinates}: {error.Message}");
        }
    }

    private void CancelNativeGridRead()
    {
        foreach (var preparation in _nativeGridNpcPreparations) preparation.Dispose();
        _nativeGridNpcPreparations.Clear();
        _nativeGridReadCancellation?.Cancel();
        _nativeGridReadCancellation?.Dispose(); _nativeGridReadCancellation = null;
        if (_nativeGridRead is { } abandoned)
            _ = abandoned.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _nativeGridRead = null;
    }

    private void RetireNativeGridPreparation()
    {
        CancelNativeGridRead();
        // Prepared models already own native callbacks, but are not yet in
        // the resident presentation index. Retire them before a replacement
        // CELL can bind the same references; deferred deletion is too late.
        foreach (var node in _nativeGridStaged.Where(GodotObject.IsInstanceValid))
        {
            NativeOwnedAnimationSoundPlayer.UnloadSourceLoops(node);
            node.Free();
        }
        _nativeGridStaged.Clear(); _nativeGridModels.Clear();
        _nativeGridUploads = null; _nativeGridPending = null; _nativeGridTarget = null;
    }

    public override void _ExitTree()
    {
        if (_retiringNativeSession) GD.Print("OPENNV_NATIVE_SESSION_RETIRE phase=enter");
        CancelNativeManualSave("Native session retired before the pending manual save could commit.");
        DetachNativeCloseRequest();
        CancelNativeLauncherEntry();
        CancelNativeGridRead();
        if (!_retiringNativeSession) return;
        // Session retirement has drained source workers. Detached prototypes are outside
        // the scene tree and must be released with the retired record owners.
        var retired = 0;
        foreach (var (path, prototype) in _nativeNifPrototypes)
        {
            if (_options.ContainsKey("live-harness") && retired % 16 == 0)
                GD.Print($"OPENNV_NATIVE_SESSION_RETIRE phase=prototype index={retired} source={path}");
            prototype.Scene.Root.Free(); retired++;
        }
        GD.Print($"OPENNV_NATIVE_SESSION_RETIRE phase=prototypes-freed count={retired}");
        _nativeNifPrototypes.Clear();
        _nativePrewarmedInitialCellRoot?.Free(); _nativePrewarmedInitialCellRoot = null;
        _nativeReferences?.Dispose(); _nativePluginStack?.Dispose();
        _nativeIndexCancellation.Dispose();
        GD.Print("OPENNV_NATIVE_SESSION_RETIRE phase=source-owners-disposed");
    }

    private void StageNativeExteriorGrid(PreparedExteriorGrid prepared)
    {
        var grid = prepared.Grid with { Scene = _nativeReferences!.ComposeResidency(prepared.Grid.Scene, _configuration.World.GameUnitsToMeters, prepared.Grid.Cells) };
        var root = _nativeCurrentCellRoot!;
        _nativeGridPending = grid; _nativeGridUploads = new();
        var oldLand = root.GetChildren().OfType<RuntimeNativeLandscapeTransport>().Select(land => land.Source.ActiveCell).ToHashSet();
        foreach (var cell in grid.Cells.Where(cell => !oldLand.Contains(cell.FormKey) && prepared.Landscapes.ContainsKey(cell.FormKey)))
            _nativeGridUploads.Enqueue((cell.FormKey.ToString(), () =>
            {
                var land = RuntimeNativeLandscapeTransportBuilder.Build(prepared.Landscapes[cell.FormKey],
                    _configuration.World.GameUnitsToMeters, _nativeLandscapeTextures);
                root.AddChild(land); GamebryoReferenceEnableRuntime.Apply(land, false); _nativeGridStaged.Add(land);
                return true;
            }
            ));
        var previous = _nativeActiveCell!.References.Select(reference => reference.FormKey).ToHashSet();
        foreach (var reference in grid.Scene.References.Where(reference => !previous.Contains(reference.FormKey) &&
            _nativeReferencePresentation?.HasPrepared(reference.FormKey) != true))
        {
            ExteriorNpcPreparation? npc = null;
            _nativeGridUploads.Enqueue((reference.FormKey.ToString(), () =>
            {
                // Decode/upload native resources without entering the live tree.
                // Ready callbacks can start authored sounds and gameplay bindings;
                // they belong to committed residency, never preparation ahead.
                var stagedReference = new Node3D();
                RuntimeNativeNpc? preparedNpc = null;
                try
                {
                    var model = grid.Scene.BaseObjects[reference.Base].ModelPath;
                    if (grid.Scene.BaseObjects[reference.Base].Signature == "NPC_" && _nativeReferences!.IsEnabled(reference.FormKey))
                    {
                        if (npc is null)
                        {
                            // Bound both jobs and completed bodies awaiting GPU
                            // publication; worker slots alone do not bound memory.
                            if (_nativeGridNpcPreparations.Count >= FalloutContentWorkers.Concurrency) return false;
                            npc = PrepareExteriorNpc(reference, _nativeGridReadCancellation!.Token);
                        }
                        if (npc is not null)
                        {
                            var armor = _nativeReferences.EquippedArmor(reference.FormKey,
                                _nativeOpeningStageDriver?.PlayerLevel ?? _nativeOpeningRestore?.State.Vitals?.Level ?? 1, _nativeGlobals);
                            if (!armor.SequenceEqual(npc.Appearance.EquippedArmor))
                            { ReleaseExteriorNpc(npc); npc = null; return false; }
                            if (!npc.Advance(this, grid.Scene, out preparedNpc)) return false;
                        }
                    }
                    PlaceNativeReference(stagedReference, grid.Scene, reference, observe: false,
                        preparedModel: model is null ? null : prepared.Models.GetValueOrDefault(model), preparedNpc: preparedNpc);
                    if (preparedNpc is not null && GodotObject.IsInstanceValid(preparedNpc) && preparedNpc.GetParent() is null) preparedNpc.Free();
                    var nodes = stagedReference.GetChildren().OfType<Node3D>().ToArray();
                    if (nodes.Length > 1) throw new InvalidDataException("A source reference must have one presentation root.");
                    foreach (var node in nodes)
                    {
                        stagedReference.RemoveChild(node);
                        _nativeGridStaged.Add(node); _nativeGridModels.Add(reference.FormKey, node);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or InvalidOperationException)
                {
                    if (preparedNpc is not null && GodotObject.IsInstanceValid(preparedNpc) && preparedNpc.GetParent() is null) preparedNpc.Free();
                    _nativeReferenceDivergences[reference.FormKey.ToString()] = error.Message;
                    if (grid.Scene.BaseObjects[reference.Base].Signature == "NPC_")
                        _nativeActorDivergences[reference.FormKey.ToString()] = error.Message;
                    GD.PushError($"OPENNV_NATIVE_REFERENCE_DIVERGENCE reference={reference.FormKey}: {error.Message}");
                }
                finally { stagedReference.Free(); }
                ReleaseExteriorNpc(npc); npc = null;
                return true;
            }
            ));
        }
    }

    private void CommitNativeExteriorGrid()
    {
        var started = Stopwatch.GetTimestamp();
        var phases = _options.ContainsKey("live-harness") ? new Dictionary<string, double>() : null;
        _nativeGridCommitPhases = phases;
        var phaseStarted = started;
        void Mark(string phase)
        {
            if (phases is null) return;
            var now = Stopwatch.GetTimestamp();
            phases.Add(phase, Stopwatch.GetElapsedTime(phaseStarted, now).TotalMilliseconds);
            phaseStarted = now;
        }
        var root = _nativeCurrentCellRoot!; var grid = _nativeGridPending!;
        var previous = _nativeActiveCell!.Cell.FormKey;
        var retained = grid.Cells.Select(cell => cell.FormKey).ToHashSet();
        ++_nativeGridGeneration;
        var sky = new FalloutSkyLightingState(_nativePluginStack!, _nativeSkyLighting!.DaytimeExtension);
        sky.Restore(_nativeSkyLighting.Capture());
        var position = _nativePlayer!.GlobalPosition / _configuration.World.GameUnitsToMeters;
        sky.EnterCell(grid.Scene.Cell, _nativeGlobals, [position.X, -position.Z, position.Y]);
        _nativeReferences!.LoadCell(grid.Scene);
        _nativeReferences.UnloadCell(previous);
        _nativeActiveCell = grid.Scene;
        _nativeSkyLighting.Restore(sky.Capture());
        Mark("world-and-weather");
        var center = grid.Scene.Cell.Coordinates!.Value;
        DiscoverNativeCellReferences(grid.Scene);
        Mark("source-discovery");
        _nativeReferencePresentation!.SetResidency(grid.Scene.References, reference => MaterializeNativeReference(root, grid.Scene, reference),
            reference => Math.Abs((int)MathF.Floor(reference.Position[0] / 4096) - center.X) <= grid.Radius + 1 &&
                Math.Abs((int)MathF.Floor(reference.Position[1] / 4096) - center.Y) <= grid.Radius + 1);
        Mark("reference-residency");
        foreach (var (key, model) in _nativeGridModels)
        {
            root.AddChild(model);
            _nativeReferencePresentation.Register(key, model);
        }
        Mark("reference-publication");
        foreach (var land in root.GetChildren().OfType<RuntimeNativeLandscapeTransport>())
        {
            var active = retained.Contains(land.Source.ActiveCell);
            GamebryoReferenceEnableRuntime.Apply(land, active);
            if (active) _nativeLandLastUse[land.Source.ActiveCell] = _nativeGridGeneration;
        }
        // Keep a bounded inactive terrain fringe for boundary reversals. It has
        // no collision or gameplay residency until the normal commit enables it.
        var terrain = root.GetChildren().OfType<RuntimeNativeLandscapeTransport>().ToArray();
        foreach (var land in terrain.Where(land => !retained.Contains(land.Source.ActiveCell))
            .OrderBy(land => _nativeLandLastUse.GetValueOrDefault(land.Source.ActiveCell))
            .Take(Math.Max(0, terrain.Length - Math.Max(96, retained.Count * 2))))
        {
            _nativeLandLastUse.Remove(land.Source.ActiveCell); land.QueueFree();
        }
        Mark("terrain-residency");
        _nativeReferenceEvents!.SetResidency(grid.Scene, root);
        Mark("reference-events");
        ObserveNativeResidentReferences(grid.Scene);
        Mark("runtime-observation");
        var environment = root.GetChildren().OfType<RuntimeNativeExteriorEnvironment>().Single();
        foreach (var node in _nativeGridStaged) environment.Register(node);
        Mark("environment-registration");
        root.GetChildren().OfType<RuntimeNativeExteriorLod>().Single().SetDetailGrid(grid);
        Mark("lod-detail-mask");
        foreach (var actor in _nativeGridModels.Values.OfType<RuntimeNativeNpc>())
            actor.BeginPackageDialogue = (package, completed) => _nativeOpeningStageDriver!.RequestPackageDialogue(actor.Appearance.Reference!.Value, package, completed);
        foreach (var actor in _nativeReferencePresentation.Actors) actor.UpdateResidentScene(grid.Scene);
        _nativeWalkableGrid.Clear(); foreach (var cell in grid.Cells) _nativeWalkableGrid.Add(cell.Coordinates!.Value);
        _nativeGridUploads = null; _nativeGridPending = null; _nativeGridStaged.Clear(); _nativeGridModels.Clear();
        _nativeGridFailed = null; _nativeGridTarget = null;
        _nativeGridReadCancellation?.Dispose(); _nativeGridReadCancellation = null;
        Mark("actor-residency");
        var models = grid.Scene.References.Select(reference => grid.Scene.BaseObjects[reference.Base])
            .Where(value => value.ModelPath is not null).Select(value => value.ModelPath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _nativeRecentModelSets.Enqueue(models);
        while (_nativeRecentModelSets.Count > 3) _nativeRecentModelSets.Dequeue();
        var warmModels = _nativeRecentModelSets.SelectMany(set => set).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _nativeNifPrototypes.Keys.Where(path => !warmModels.Contains(path)).ToArray())
        {
            _nativeNifPrototypes[path].Scene.Root.Free(); _nativeNifPrototypes.Remove(path);
        }
        Mark("prototype-eviction");
        var textures = root.GetChildren().OfType<RuntimeNativeLandscapeTransport>().Where(land => !land.IsQueuedForDeletion())
            .SelectMany(land => land.Source.Textures.Values).SelectMany(texture => new[] { texture.DiffusePath, texture.NormalPath })
            .Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _nativeLandscapeTextures.Keys.Where(path => !textures.Contains(path)).ToArray()) _nativeLandscapeTextures.Remove(path);
        Mark("texture-prune");
        _nativeGridCommitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        GD.Print($"OPENNV_EXTERIOR_STREAM_READY previous={previous} active={grid.Scene.Cell.FormKey} cells={grid.Cells.Count} overlap=retained commitMs={_nativeGridCommitMilliseconds:F3}");
    }

    private Node3D? MaterializeNativeReference(Node3D root, FalloutCellScene cell, FalloutPlacedReference reference)
    {
        Node3D? Find() => root.GetChildren().OfType<Node3D>().SingleOrDefault(node => !node.IsQueuedForDeletion() &&
            (node is RuntimeNativeNpc actor ? actor.Appearance.Reference == reference.FormKey :
                node.GetMeta("opennv_reference_form_key", "").AsString() == reference.FormKey.ToString()));
        if (Find() is { } existing) return existing;
        var before = root.GetChildCount();
        try { PlaceNativeReference(root, cell, reference, materializeDisabled: true); return Find(); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            while (root.GetChildCount() > before) root.GetChild(before).Free();
            _nativeReferenceDivergences[reference.FormKey.ToString()] = error.Message;
            GD.PushError($"OPENNV_NATIVE_REFERENCE_DIVERGENCE reference={reference.FormKey} base={reference.Base}: {error.Message}");
            return null;
        }
    }
    private FalloutExteriorGridScene ResolveExterior(FalloutFormKey world, float[] position)
    {
        _nativeExteriorGrid ??= new(_nativePluginStack!);
        var settings = FalloutInstallationSettings.Read(RuntimeLiveContentSource.Current!);
        var diameter = checked((int)settings.Unsigned("General", "uGridsToLoad"));
        diameter = Math.Max(diameter, _configuration.World.MinimumExteriorGridDiameter);
        return _nativeExteriorGrid.Resolve(world, _nativeExteriorGrid.PersistentCell(world),
            position[0], position[1], diameter);
    }

    private void AddExteriorLandscape(Node3D root, FalloutExteriorGridScene grid)
    {
        foreach (var cell in grid.Cells)
        {
            var source = FalloutLandscapeTransportResolver.ResolveCell(_nativePluginStack!, cell, grid.PersistentCell);
            if (source is null) continue;
            root.AddChild(RuntimeNativeLandscapeTransportBuilder.Build(
                source, _configuration.World.GameUnitsToMeters, _nativeLandscapeTextures));
        }
        AddExteriorWater(root, grid);
        CompleteExteriorLandscape(root, grid);
    }

    private ShaderMaterial? _nativeWaterMaterial;

    // Tier ladder for weaker fleet GPUs. Defaults to the current full-quality pass.
    private string EnvironmentTier() => _options.GetValueOrDefault("env-tier", "high").ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" or "med" => "medium",
        _ => "high",
    };

    private ShaderMaterial NativeWaterMaterial()
    {
        if (_nativeWaterMaterial is not null) return _nativeWaterMaterial;
        var tier = EnvironmentTier();
        var low = tier == "low";
        var medium = tier == "medium";
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/water.gdshader") };
        material.SetShaderParameter("shallow_color", new Color(0.14f, 0.42f, 0.46f));
        material.SetShaderParameter("deep_color", new Color(0.07f, 0.25f, 0.30f));
        material.SetShaderParameter("sky_color", new Color(0.55f, 0.68f, 0.80f));
        material.SetShaderParameter("water_noise_1", WaterNoise(11, 0.012f));
        material.SetShaderParameter("water_noise_2", WaterNoise(29, 0.021f));
        material.SetShaderParameter("depth_scale", 4.0f);
        material.SetShaderParameter("refraction_strength", 0.55f);
        material.SetShaderParameter("refraction_quality", low ? 0.5f : medium ? 0.8f : 1.0f);
        material.SetShaderParameter("normal_scale", 0.35f);
        material.SetShaderParameter("roughness_scale", 0.08f);
        material.SetShaderParameter("time_scale", 2.0f);
        material.SetShaderParameter("uv_scale", 1.2f);
        material.SetShaderParameter("fresnel_power", 5.0f);
        material.SetShaderParameter("reflection_strength", 0.60f);
        var hour = _nativeGameTime?.Hour ?? 12.0f;
        var solarAngle = (hour - 6.0f) * MathF.PI / 12.0f;
        material.SetShaderParameter("sun_direction", new Vector3(MathF.Cos(solarAngle), MathF.Sin(solarAngle), 0).Normalized());
        material.SetShaderParameter("water_waves", WaterWaves(7, 0.02f));
        material.SetShaderParameter("wave_height", low ? 0.06f : 0.12f);
        material.SetShaderParameter("wave_scale", 0.02f);
        material.SetShaderParameter("sun_color", new Color(1.0f, 0.96f, 0.88f));
        material.SetShaderParameter("specular_strength", 0.35f);
        material.SetShaderParameter("specular_power", 180.0f);
        material.SetShaderParameter("foam_strength", 0.35f);
        material.SetShaderParameter("caustics_quality", low ? 0.0f : medium ? 0.45f : 0.70f);
        material.SetShaderParameter("caustics_strength", medium ? 0.30f : 0.38f);
        material.SetShaderParameter("caustics_scale", 0.32f);
        material.SetShaderParameter("ssr_quality", low ? 0.0f : medium ? 0.35f : 0.5f);
        material.SetShaderParameter("ssr_strength", 0.45f);
        material.SetShaderParameter("ssr_distance", low ? 20.0f : 32.0f);
        _nativeWaterMaterial = material;
        return material;
    }

    private static NoiseTexture2D WaterNoise(int seed, float frequency) => new()
    {
        Width = 512,
        Height = 512,
        Seamless = true,
        AsNormalMap = true,
        GenerateMipmaps = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = frequency,
            Seed = seed,
            FractalOctaves = 3,
        },
    };

    private static NoiseTexture3D WaterWaves(int seed, float frequency) => new()
    {
        Width = 64,
        Height = 64,
        Depth = 64,
        Seamless = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            Frequency = frequency,
            Seed = seed,
            FractalOctaves = 3,
        },
    };

    private void AddExteriorWater(Node3D root, FalloutExteriorGridScene grid)
    {
        var units = _configuration.World.GameUnitsToMeters;
        var waterPlanes = 0;
        if (root.GetNodeOrNull<RuntimeNativeUnderwater>("UnderwaterTint") is not { } underwater)
        {
            underwater = new RuntimeNativeUnderwater { Name = "UnderwaterTint" };
            root.AddChild(underwater);
        }
        var heights = new Dictionary<(int, int), float>();
        foreach (var cell in grid.Cells)
            if (cell.WaterHeight is float level && float.IsFinite(level) && level < 1e30f && cell.Coordinates is { } coordinates)
                heights[coordinates] = level;
        underwater.Configure(
            () => _nativePlayer?.Camera?.GlobalPosition ?? Vector3.Zero,
            world =>
            {
                var gx = (int)MathF.Floor((world.X / units) / 4096f);
                var gy = (int)MathF.Floor((-world.Z / units) / 4096f);
                return heights.TryGetValue((gx, gy), out var level) ? level : null;
            },
            units,
            now => NativeWaterMaterial().RenderPriority = now ? 1 : 0,
            (now, belowMeters) => root.GetNodeOrNull<RuntimeNativeExteriorEnvironment>("ExteriorSkyOwner")
                ?.SetUnderwater(now, belowMeters),
            () =>
            {
                var hour = _nativeGameTime?.Hour ?? 12.0f;
                var angle = (hour - 6.0f) * MathF.PI / 12.0f;
                return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
            });
        var waterSubdivisions = EnvironmentTier() switch { "low" => 8, "medium" => 16, _ => 32 };
        foreach (var cell in grid.Cells)
        {
            if (cell.WaterHeight is not { } height || !float.IsFinite(height) || height > 1e30f) continue;
            var coordinates = cell.Coordinates;
            if (coordinates is not { } gridPosition) continue;
            var plane = new MeshInstance3D
            {
                Name = $"NativeWater_{cell.FormKey}",
                Mesh = new PlaneMesh
                {
                    Size = new Vector2(4096f, 4096f),
                    SubdivideWidth = waterSubdivisions,
                    SubdivideDepth = waterSubdivisions,
                },
                Position = new Vector3(
                    (gridPosition.X + 0.5f) * 4096f * units,
                    height * units,
                    -(gridPosition.Y + 0.5f) * 4096f * units),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            plane.MaterialOverride = NativeWaterMaterial();
            root.AddChild(plane);
            waterPlanes++;
            GD.Print($"OPENNV_NATIVE_WATER cell={cell.FormKey} grid={gridPosition} height={height} y={plane.Position.Y:F2}");
        }
        var worldspace = grid.Scene.Cell.Worldspace ?? throw new InvalidDataException("Exterior water has no worldspace.");
        var center = grid.Scene.Cell.Coordinates ?? throw new InvalidDataException("Exterior water has no grid coordinates.");
        var loadDistance = FalloutInstallationSettings.Read(RuntimeLiveContentSource.Current!).Number("TerrainManager", "fBlockLoadDistance");
        var distantRadius = Math.Clamp((int)MathF.Ceiling(loadDistance / 4096f), grid.Radius + 1, 64);
        var distantCells = _nativeExteriorGrid is null ? Array.Empty<(int X, int Y, float Height)>()
            : _nativeExteriorGrid.WaterCells(worldspace, center.X, center.Y, distantRadius)
                .Where(cell => Math.Abs(cell.X - center.X) > grid.Radius || Math.Abs(cell.Y - center.Y) > grid.Radius).ToArray();
        if (distantCells.Length != 0)
        {
            var vertices = new Vector3[distantCells.Length * 6];
            var normals = new Vector3[vertices.Length];
            Array.Fill(normals, Vector3.Up);
            for (var index = 0; index < distantCells.Length; index++)
            {
                var (x, y, level) = distantCells[index];
                var minX = x * 4096f * units; var maxX = (x + 1) * 4096f * units;
                var minZ = -(y + 1) * 4096f * units; var maxZ = -y * 4096f * units;
                var surface = level * units;
                var offset = index * 6;
                vertices[offset + 0] = new Vector3(minX, surface, minZ);
                vertices[offset + 1] = new Vector3(minX, surface, maxZ);
                vertices[offset + 2] = new Vector3(maxX, surface, maxZ);
                vertices[offset + 3] = new Vector3(minX, surface, minZ);
                vertices[offset + 4] = new Vector3(maxX, surface, maxZ);
                vertices[offset + 5] = new Vector3(maxX, surface, minZ);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            var distant = new MeshInstance3D
            {
                Name = "NativeWaterDistant",
                Mesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            distant.MaterialOverride = NativeWaterMaterial();
            root.AddChild(distant);
            GD.Print($"OPENNV_NATIVE_WATER_DISTANT cells={distantCells.Length} radiusCells={distantRadius} levels={string.Join(",", distantCells.Select(cell => cell.Height).Distinct().Order())}");
        }
        GD.Print($"OPENNV_NATIVE_WATER_TOTAL cells={grid.Cells.Count} planes={waterPlanes}");
    }

    private void CompleteExteriorLandscape(Node3D root, FalloutExteriorGridScene grid)
    {
        root.SetMeta("opennv_exterior_grid_cells", grid.Cells.Count);
        root.SetMeta("opennv_exterior_grid_radius", grid.Radius);
        var lod = new RuntimeNativeExteriorLod();
        lod.Configure(_nativePluginStack!, RuntimeLiveContentSource.Current!, grid, _configuration.World.GameUnitsToMeters);
        root.AddChild(lod);
    }

    private void AddExteriorEnvironment(Node3D root, FalloutCellDefinition cell)
    {
        var sky = new RuntimeNativeExteriorEnvironment { Name = "ExteriorSkyOwner" };
        var imageSpace = FalloutImageSpaceReader.ForCell(_nativePluginStack!, cell.FormKey) ??
            FalloutImageSpaceReader.ForWorld(_nativePluginStack!, cell.Worldspace ??
                throw new InvalidDataException("Exterior environment has no worldspace."));
        sky.Compositor = (_options.ContainsKey("sky-shader") || _options.ContainsKey("sky-simple")) ? null : AddNativeImageSpace(root, imageSpace);
        sky.ImageSpace = imageSpace;
        var diameter = Math.Max(checked((int)FalloutInstallationSettings.Read(RuntimeLiveContentSource.Current!).Unsigned("General", "uGridsToLoad")),
            _configuration.World.MinimumExteriorGridDiameter);
        // Cover the complete resident square, including its diagonal when the
        // player crosses an anchor boundary. Engine defaults cut this at 100m.
        var shadowDistance = (diameter / 2 + 1) * 4096 * _configuration.World.GameUnitsToMeters * MathF.Sqrt(2);
        sky.Configure(_nativePluginStack!, _nativeSkyLighting!, () => _nativeGameTime!.Hour, _configuration.World.GameUnitsToMeters, this, shadowDistance, _options.ContainsKey("sky-shader") || _options.ContainsKey("sky-simple"), _options.ContainsKey("sky-simple") ? "res://shaders/sky_simple.gdshader" : "res://shaders/sky_sorta.gdshader", _options.ContainsKey("sky-dome"), EnvironmentTier() != "low");
        root.AddChild(sky);
        if (_options.ContainsKey("sky-dome"))
        {
            var dome = new RuntimeNativeShaderSky { Name = "ShaderSkyDome" };
            var tier = EnvironmentTier();
            dome.Configure(() => _nativePlayer?.Camera?.GlobalPosition ?? Vector3.Zero, _configuration.World.GameUnitsToMeters,
                () => _nativeSkyLighting?.ActiveWeather.EditorId ?? "",
                () =>
                {
                    var hour = _nativeGameTime?.Hour ?? 12.0f;
                    var angle = (hour - 6.0f) * MathF.PI / 12.0f;
                    return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
                },
                tier == "low" ? 3 : tier == "medium" ? 4 : 5,
                tier == "low" ? 1.0f : tier == "medium" ? 1.25f : 1.5f);
            GD.Print($"OPENNV_ENV_TIER tier={tier}");
            root.AddChild(dome);
        }
        if (_options.ContainsKey("weather-particles"))
        {
            var precipitation = new RuntimeNativePrecipitation { Name = "WeatherParticles" };
            precipitation.Configure(() => _nativeSkyLighting!.ActiveWeather.EditorId,
                () => _nativePlayer?.Camera?.GlobalPosition ?? Vector3.Zero);
            root.AddChild(precipitation);
            GD.Print($"OPENNV_NATIVE_WEATHER_PARTICLES weather={_nativeSkyLighting!.ActiveWeather.EditorId}");
        }
        var wind = new RuntimeNativeWind { Name = "ExteriorWindOwner" };
        FalloutFormKey? windWeather = null;
        float windSpeed = 0;
        wind.Configure(() =>
        {
            var weather = _nativeSkyLighting!.ActiveWeather.Form;
            if (windWeather != weather)
            {
                windSpeed = FalloutWeatherMotion.Read(_nativePluginStack!.GetEffective(weather),
                    FalloutGameSettingFloats.ReadRetained(_nativePluginStack, "fWeatherCloudSpeedMax", nameof(RuntimeNativeWind))).WindSpeed;
                windWeather = weather;
            }
            return (windSpeed, FalloutWindForce.InitialHeading);
        }, _configuration.World.GameUnitsToMeters);
        root.AddChild(wind);
    }

    private Color NativeAmbient(FalloutCellDefinition cell)
    {
        if (cell.Lighting is { } lighting) return ByteColor(lighting.AmbientRgb);
        var sky = _nativeSkyLighting ?? throw new InvalidOperationException("Exterior ambient has no sky owner.");
        var weather = sky.ActiveWeather;
        var weights = FalloutWeatherTimeWeights.Sample(sky.Climate, _nativeGameTime!.Hour, sky.DaytimeExtension, weather.TimeSamples);
        var rgb = weather.Sample(weights, 3);
        return new(rgb[0], rgb[1], rgb[2]);
    }
}

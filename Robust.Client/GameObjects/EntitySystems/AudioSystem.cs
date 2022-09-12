using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using JetBrains.Annotations;
using Robust.Client.Audio;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.Threading;

namespace Robust.Client.GameObjects;

[UsedImplicitly]
public sealed class AudioSystem : SharedAudioSystem
{
    [Dependency] private readonly IResourceCache _resourceCache = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IClydeAudio _clyde = default!;
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPhysicsSystem _broadPhaseSystem = default!;
    [Dependency] private readonly SharedTransformSystem _xformSys = default!;

    private readonly List<PlayingStream> _playingClydeStreams = new();

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<PlayAudioEntityMessage>(PlayAudioEntityHandler);
        SubscribeNetworkEvent<PlayAudioGlobalMessage>(PlayAudioGlobalHandler);
        SubscribeNetworkEvent<PlayAudioPositionalMessage>(PlayAudioPositionalHandler);
        SubscribeNetworkEvent<StopAudioMessageClient>(StopAudioMessageHandler);
    }

    private void StopAudioMessageHandler(StopAudioMessageClient ev)
    {
        var stream = _playingClydeStreams.Find(p => p.NetIdentifier == ev.Identifier);
        if (stream == null)
        {
            return;
        }

        StreamDone(stream);
        _playingClydeStreams.Remove(stream);
    }

    private void PlayAudioPositionalHandler(PlayAudioPositionalMessage ev)
    {
        var mapId = ev.Coordinates.GetMapId(EntityManager);

        if (!_mapManager.MapExists(mapId))
        {
            Logger.Error(
                $"Server tried to play sound on map {mapId}, which does not exist. Ignoring.");
            return;
        }

        var stream = (PlayingStream?)Play(ev.FileName, ev.Coordinates, ev.FallbackCoordinates, ev.AudioParams);
        if (stream != null)
        {
            stream.NetIdentifier = ev.Identifier;
        }
    }

    private void PlayAudioGlobalHandler(PlayAudioGlobalMessage ev)
    {
        var stream = (PlayingStream?)Play(ev.FileName, ev.AudioParams);
        if (stream != null)
        {
            stream.NetIdentifier = ev.Identifier;
        }
    }

    private void PlayAudioEntityHandler(PlayAudioEntityMessage ev)
    {
        var stream = EntityManager.EntityExists(ev.EntityUid) ?
            (PlayingStream?)Play(ev.FileName, ev.EntityUid, ev.FallbackCoordinates, ev.AudioParams)
            : (PlayingStream?)Play(ev.FileName, ev.Coordinates, ev.FallbackCoordinates, ev.AudioParams);

        if (stream != null)
        {
            stream.NetIdentifier = ev.Identifier;
        }

    }

    public override void FrameUpdate(float frameTime)
    {
        // Update positions of streams every frame.
        // Start with an initial pass to cull streams that need to be removed, and sort stuff out.
        Span<int> validIndices = stackalloc int[_playingClydeStreams.Count];
        int validCount = 0;

        // Initial clearing pass
        try
        {
            int streamIndexOut = 0;
            foreach (var stream in _playingClydeStreams)
            {
                // Note: continue; in here is expected to have one of two outcomes:
                // + StreamDone
                // + streamIndexOut++

                // Occlusion recalculation parallel needs a way to know which targets to actually recalculate for.
                // That in mind start by setting this to false (it's set to true later when relevant)
                stream.OcclusionValidTemporary = false;

                if (!stream.Source.IsPlaying)
                {
                    StreamDone(stream);
                    continue;
                }

                MapCoordinates? mapPos = null;
                if (stream.TrackingCoordinates != null)
                {
                    var coords = stream.TrackingCoordinates.Value;
                    if (_mapManager.MapExists(coords.GetMapId(EntityManager)))
                    {
                        mapPos = stream.TrackingCoordinates.Value.ToMap(EntityManager);
                    }
                    else
                    {
                        // Map no longer exists, delete stream.
                        StreamDone(stream);
                        continue;
                    }
                }
                else if (stream.TrackingEntity != default)
                {
                    if (EntityManager.Deleted(stream.TrackingEntity))
                    {
                        StreamDone(stream);
                        continue;
                    }

                    mapPos = EntityManager.GetComponent<TransformComponent>(stream.TrackingEntity).MapPosition;
                }

                // TODO Remove when coordinates can't be NaN
                if (mapPos == null || !float.IsFinite(mapPos.Value.X) || !float.IsFinite(mapPos.Value.Y))
                    mapPos = stream.TrackingFallbackCoordinates?.ToMap(EntityManager);

                if (mapPos != null)
                {
                    stream.MapCoordinatesTemporary = mapPos.Value;
                    // this has a map position so it's good to go to the other processes
                    validIndices[validCount] = streamIndexOut;
                    // check for occlusion recalc
                    stream.OcclusionValidTemporary = mapPos.Value.MapId == _eyeManager.CurrentMap;
                    validCount++;
                }

                // This stream gets to live!
                streamIndexOut++;
            }
        }
        finally
        {
            // if this doesn't get ran (exception...) then the list can fill up with disposed garbage.
            // that will then throw on IsPlaying.
            // meaning it'll break the entire audio system.
            _playingClydeStreams.RemoveAll(p => p.Done);
        }

        var ourPos = _eyeManager.CurrentEye.Position.Position;

        // Occlusion calculation pass

        Parallel.For(
            0, _playingClydeStreams.Count,
            (i) =>
            {
                var stream = _playingClydeStreams[i];
                // As set earlier.
                if (stream.OcclusionValidTemporary)
                {
                    var pos = stream.MapCoordinatesTemporary;
                    var sourceRelative = ourPos - pos.Position;
                    var occlusion = 0f;
                    if (sourceRelative.Length > 0)
                    {
                        occlusion = _broadPhaseSystem.IntersectRayPenetration(
                            pos.MapId,
                            new CollisionRay(
                                pos.Position,
                                sourceRelative.Normalized,
                                OcclusionCollisionMask),
                            sourceRelative.Length,
                            stream.TrackingEntity);
                    }
                    stream.OcclusionTemporary = occlusion;
                }
            }
        );

        // Occlusion apply / Attenuation / position / velocity pass
        // Note that for streams for which MapCoordinatesTemporary isn't updated, they don't get here
        for (var i = 0; i < validCount; i++)
        {
            var stream = _playingClydeStreams[validIndices[i]];
            var pos = stream.MapCoordinatesTemporary;

            if (stream.OcclusionValidTemporary)
            {
                stream.Source.SetOcclusion(stream.OcclusionTemporary);
            }

            if (pos.MapId != _eyeManager.CurrentMap)
            {
                stream.Source.SetVolume(-10000000);
            }
            else
            {
                var sourceRelative = ourPos - pos.Position;
                // OpenAL uses MaxDistance to limit how much attenuation can *reduce* the gain,
                // and doesn't do any culling. We however cull based on MaxDistance, because
                // this is what all current code that uses MaxDistance expects and because
                // we don't need the OpenAL behaviour.
                if (sourceRelative.Length > stream.MaxDistance)
                {
                    stream.Source.SetVolume(-10000000);
                }
                else
                {
                    // OpenAL also limits the distance to <= AL_MAX_DISTANCE, but since we cull
                    // sources that are further away than stream.MaxDistance, we don't do that.
                    var distance = MathF.Max(stream.ReferenceDistance, sourceRelative.Length);
                    float gain;

                    // Technically these are formulas for gain not decibels but EHHHHHHHH.
                    switch (stream.Attenuation)
                    {
                        case Attenuation.Default:
                            gain = 1f;
                            break;
                        // You thought I'd implement clamping per source? Hell no that's just for the overall OpenAL setting
                        // I didn't even wanna implement this much for linear but figured it'd be cleaner.
                        case Attenuation.InverseDistanceClamped:
                        case Attenuation.InverseDistance:
                            gain = stream.ReferenceDistance /
                                   (stream.ReferenceDistance + stream.RolloffFactor *
                                       (distance - stream.ReferenceDistance));

                            break;
                        case Attenuation.LinearDistanceClamped:
                        case Attenuation.LinearDistance:
                            gain = 1f - stream.RolloffFactor * (distance - stream.ReferenceDistance) /
                                (stream.MaxDistance - stream.ReferenceDistance);

                            break;
                        case Attenuation.ExponentDistanceClamped:
                        case Attenuation.ExponentDistance:
                            gain = MathF.Pow((distance / stream.ReferenceDistance),
                                (-stream.RolloffFactor));
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(
                                $"No implemented attenuation for {stream.Attenuation.ToString()}");
                    }

                    var volume = MathF.Pow(10, stream.Volume / 10);
                    var actualGain = MathF.Max(0f, volume * gain);

                    stream.Source.SetVolumeDirect(actualGain);
                    var audioPos = stream.Attenuation != Attenuation.NoAttenuation ? pos.Position : ourPos;

                    if (!stream.Source.SetPosition(audioPos))
                    {
                        Logger.Warning($"Interrupting positional audio, can't set position.");
                        stream.Source.StopPlaying();
                    }

                    if (stream.TrackingEntity != default)
                    {
                        stream.Source.SetVelocity(stream.TrackingEntity.GlobalLinearVelocity());
                    }
                }
            }
        }
    }

    private static void StreamDone(PlayingStream stream)
    {
        stream.Source.Dispose();
        stream.Done = true;
    }

    /// <summary>
    ///     Play an audio file globally, without position.
    /// </summary>
    /// <param name="filename">The resource path to the OGG Vorbis file to play.</param>
    /// <param name="audioParams"></param>
    private IPlayingAudioStream? Play(string filename, AudioParams? audioParams = null)
    {
        if (_resourceCache.TryGetResource<AudioResource>(new ResourcePath(filename), out var audio))
        {
            return Play(audio, audioParams);
        }

        Logger.Error($"Server tried to play audio file {filename} which does not exist.");
        return default;
    }

    /// <summary>
    ///     Play an audio stream globally, without position.
    /// </summary>
    /// <param name="stream">The audio stream to play.</param>
    /// <param name="audioParams"></param>
    private IPlayingAudioStream? Play(AudioStream stream, AudioParams? audioParams = null)
    {
        var source = _clyde.CreateAudioSource(stream);

        if (source == null)
        {
            return null;
        }

        ApplyAudioParams(audioParams, source);

        source.SetGlobal();
        source.StartPlaying();
        // These defaults differ from AudioParams.Default
        var playing = new PlayingStream
        {
            Source = source,
            Attenuation = audioParams?.Attenuation ?? Attenuation.Default,
            MaxDistance = audioParams?.MaxDistance ?? float.MaxValue,
            ReferenceDistance = audioParams?.ReferenceDistance ?? 1f,
            RolloffFactor = audioParams?.RolloffFactor ?? 1f,
            Volume = audioParams?.Volume ?? 0
        };
        _playingClydeStreams.Add(playing);
        return playing;
    }

    /// <summary>
    ///     Play an audio file following an entity.
    /// </summary>
    /// <param name="filename">The resource path to the OGG Vorbis file to play.</param>
    /// <param name="entity">The entity "emitting" the audio.</param>
    /// <param name="fallbackCoordinates">The map or grid coordinates at which to play the audio when entity is invalid.</param>
    /// <param name="audioParams"></param>
    private IPlayingAudioStream? Play(string filename, EntityUid entity, EntityCoordinates fallbackCoordinates,
        AudioParams? audioParams = null)
    {
        if (_resourceCache.TryGetResource<AudioResource>(new ResourcePath(filename), out var audio))
        {
            return Play(audio, entity, fallbackCoordinates, audioParams);
        }

        Logger.Error($"Server tried to play audio file {filename} which does not exist.");
        return default;
    }

    /// <summary>
    ///     Play an audio stream following an entity.
    /// </summary>
    /// <param name="stream">The audio stream to play.</param>
    /// <param name="entity">The entity "emitting" the audio.</param>
    /// <param name="fallbackCoordinates">The map or grid coordinates at which to play the audio when entity is invalid.</param>
    /// <param name="audioParams"></param>
    private IPlayingAudioStream? Play(AudioStream stream, EntityUid entity, EntityCoordinates? fallback = null,
        AudioParams? audioParams = null)
    {
        var source = _clyde.CreateAudioSource(stream);

        if (source == null)
        {
            return null;
        }

        var query = GetEntityQuery<TransformComponent>();
        var xform = query.GetComponent(entity);
        var worldPos = _xformSys.GetWorldPosition(xform, query);
        fallback ??= GetFallbackCoordinates(new(worldPos, xform.MapID));

        if (!source.SetPosition(worldPos))
        {
            return Play(stream, fallback.Value, fallback.Value, audioParams);
        }

        ApplyAudioParams(audioParams, source);

        source.StartPlaying();
        var playing = new PlayingStream
        {
            Source = source,
            TrackingEntity = entity,
            TrackingFallbackCoordinates = fallback != EntityCoordinates.Invalid ? fallback : null,
            Attenuation = audioParams?.Attenuation ?? Attenuation.Default,
            MaxDistance = audioParams?.MaxDistance ?? float.MaxValue,
            ReferenceDistance = audioParams?.ReferenceDistance ?? 1f,
            RolloffFactor = audioParams?.RolloffFactor ?? 1f,
            Volume = audioParams?.Volume ?? 0
        };
        _playingClydeStreams.Add(playing);
        return playing;
    }

    /// <summary>
    ///     Play an audio file at a static position.
    /// </summary>
    /// <param name="filename">The resource path to the OGG Vorbis file to play.</param>
    /// <param name="coordinates">The coordinates at which to play the audio.</param>
    /// <param name="fallbackCoordinates">The map or grid coordinates at which to play the audio when coordinates are invalid.</param>
    /// <param name="audioParams"></param>
    private IPlayingAudioStream? Play(string filename, EntityCoordinates coordinates, EntityCoordinates fallbackCoordinates,
        AudioParams? audioParams = null)
    {
        if (_resourceCache.TryGetResource<AudioResource>(new ResourcePath(filename), out var audio))
        {
            return Play(audio, coordinates, fallbackCoordinates, audioParams);
        }

        Logger.Error($"Server tried to play audio file {filename} which does not exist.");
        return default;
    }

    /// <summary>
    ///     Play an audio stream at a static position.
    /// </summary>
    /// <param name="stream">The audio stream to play.</param>
    /// <param name="coordinates">The coordinates at which to play the audio.</param>
    /// <param name="fallbackCoordinates">The map or grid coordinates at which to play the audio when coordinates are invalid.</param>
    /// <param name="audioParams"></param>
    private IPlayingAudioStream? Play(AudioStream stream, EntityCoordinates coordinates,
        EntityCoordinates fallbackCoordinates, AudioParams? audioParams = null)
    {
        var source = _clyde.CreateAudioSource(stream);

        if (source == null)
        {
            return null;
        }

        if (!source.SetPosition(fallbackCoordinates.Position))
        {
            source.Dispose();
            Logger.Warning($"Can't play positional audio \"{stream.Name}\", can't set position.");
            return null;
        }

        if (!coordinates.IsValid(EntityManager))
        {
            coordinates = fallbackCoordinates;
        }

        ApplyAudioParams(audioParams, source);

        source.StartPlaying();
        var playing = new PlayingStream
        {
            Source = source,
            TrackingCoordinates = coordinates,
            TrackingFallbackCoordinates = fallbackCoordinates != EntityCoordinates.Invalid ? fallbackCoordinates : null,
            Attenuation = audioParams?.Attenuation ?? Attenuation.Default,
            MaxDistance = audioParams?.MaxDistance ?? float.MaxValue,
            ReferenceDistance = audioParams?.ReferenceDistance ?? 1f,
            RolloffFactor = audioParams?.RolloffFactor ?? 1f,
            Volume = audioParams?.Volume ?? 0
        };
        _playingClydeStreams.Add(playing);
        return playing;
    }

    /// <inheritdoc />
    public override IPlayingAudioStream? PlayPredicted(SoundSpecifier? sound, EntityUid source, EntityUid? user, AudioParams? audioParams = null)
    {
        if (_timing.IsFirstTimePredicted || sound == null)
            return Play(sound, Filter.Local(), source, audioParams);
        else
            return null; // uhh Lets hope predicted audio never needs to somehow store the playing audio....
    }

    private void ApplyAudioParams(AudioParams? audioParams, IClydeAudioSource source)
    {
        if (!audioParams.HasValue)
        {
            return;
        }

        if (audioParams.Value.Variation.HasValue)
            source.SetPitch(audioParams.Value.PitchScale * (float)RandMan.NextGaussian(1, audioParams.Value.Variation.Value));
        else
            source.SetPitch(audioParams.Value.PitchScale);

        source.SetVolume(audioParams.Value.Volume);
        source.SetRolloffFactor(audioParams.Value.RolloffFactor);
        source.SetMaxDistance(audioParams.Value.MaxDistance);
        source.SetReferenceDistance(audioParams.Value.ReferenceDistance);
        source.SetPlaybackPosition(audioParams.Value.PlayOffsetSeconds);
        source.IsLooping = audioParams.Value.Loop;
    }

    public sealed class PlayingStream : IPlayingAudioStream
    {
        public uint? NetIdentifier;
        public IClydeAudioSource Source = default!;
        public EntityUid TrackingEntity = default!;
        public EntityCoordinates? TrackingCoordinates;
        public EntityCoordinates? TrackingFallbackCoordinates;
        public bool Done;
        public float Volume;

        /// <summary>
        /// Temporary holding value to determine if calculating occlusion for this stream is a good idea.
        /// Because some of this stuff is parallelized for performance, these can't be stackalloc'd arrays.
        /// </summary>
        public bool OcclusionValidTemporary;
        /// <summary>
        /// Temporary holding value containing the occlusion value of the stream.
        /// Because some of this stuff is parallelized for performance, these can't be stackalloc'd arrays.
        /// </summary>
        public float OcclusionTemporary;
        /// <summary>
        /// Temporary holding value containing the map coordinates of the stream.
        /// Because some of this stuff is parallelized for performance, these can't be stackalloc'd arrays.
        /// Note that if the map coordinates aren't available, this isn't updated.
        /// Only streams for which map coordinates are available go into the "valid" stackalloc'd array.
        /// (Occlusion uses the OcclusionValidTemporary field as it can't access stackalloc'd arrays.)
        /// </summary>
        public MapCoordinates MapCoordinatesTemporary;

        public float MaxDistance;
        public float ReferenceDistance;
        public float RolloffFactor;

        public Attenuation Attenuation
        {
            get => _attenuation;
            set
            {
                if (value == _attenuation) return;
                _attenuation = value;
                if (_attenuation != Attenuation.Default)
                {
                    // Need to disable default attenuation when using a custom one
                    // Damn Sloth wanting linear ambience sounds so they smoothly cut-off and are short-range
                    Source.SetRolloffFactor(0f);
                }
            }
        }
        private Attenuation _attenuation = Attenuation.Default;

        public void Stop()
        {
            Source.StopPlaying();
        }
    }

    /// <inheritdoc />
    public override IPlayingAudioStream? PlayGlobal(string filename, Filter playerFilter, AudioParams? audioParams = null)
    {
        return Play(filename, audioParams);
    }

    /// <inheritdoc />
    public override IPlayingAudioStream? Play(string filename, Filter playerFilter, EntityUid entity,
        AudioParams? audioParams = null)
    {
        if (_resourceCache.TryGetResource<AudioResource>(new ResourcePath(filename), out var audio))
        {
            return Play(audio, entity, null, audioParams);
        }

        Logger.Error($"Server tried to play audio file {filename} which does not exist.");
        return default;
    }

    /// <inheritdoc />
    public override IPlayingAudioStream? Play(string filename, Filter playerFilter, EntityCoordinates coordinates,
        AudioParams? audioParams = null)
    {
        return Play(filename, coordinates, GetFallbackCoordinates(coordinates.ToMap(EntityManager)), audioParams);
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using CommandSystem;
using LabApi.Features;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Paths;
using Mirror;
using PlayerRoles;
using UnityEngine;
using LabLogger = LabApi.Features.Console.Logger;
using UnityObject = UnityEngine.Object;

namespace Woltano.SCP939.ClawSwing
{
    /// <summary>
    /// Independent, always-on, server-side-only SCP-939 missed-claw animation and 3D sound.
    /// Based on the tested V17 always-on implementation and V16 FAST timing. Never sends the synthetic packet to the attacker.
    /// Does not call attack/damage/cooldown setters, edit hitboxes or alter client files.
    /// </summary>
    public sealed class Scp939ClawSyncPlugin : Plugin
    {
        private const string Prefix = "[SCP939_CLAWSYNC_RC1]";
        private const string ClawTypeName = "PlayerRoles.PlayableScps.Scp939.Scp939ClawAbility";
        private const string AttackBaseName = "PlayerRoles.PlayableScps.Subroutines.ScpAttackAbilityBase`1";
        private const string SubroutineBaseName = "PlayerRoles.Subroutines.SubroutineBase";
        private const string ResultFieldName = "<LastAttackResult>k__BackingField";
        private const string TriggerFieldName = "<AttackTriggered>k__BackingField";
        private const int SilentSignal = 8; // V13/V16: client plays Claw, no hit sound or hit flags.
        private const float MinVisualInterval = 0.65f;
        private const float DiscoveryInterval = 0.50f;
        private const float SummaryInterval = 120f;
        private const string AudioKey = "Woltano.SCP939.ClawSync";
        private const string AudioFile = "939_claw_beta.mp3";
        private const float AudioMinDistance = 1f;
        private const float AudioMaxDistance = 16f;
        private const float AudioVolume = 0.85f;

        private sealed class PendingEdge
        {
            public float Due;
            public float DetectedAt;
        }
        private sealed class PendingResult
        {
            public float Due;
            public float DetectedAt;
            public float StartAt;
            public int[] RecipientIds;
        }
        private sealed class ClawState
        {
            public int PlayerId;
            public Player Attacker;
            public ReferenceHub Hub;
            public GameObject PlayerObject;
            public Component Claw;
            public FieldInfo Result;
            public FieldInfo Trigger;
            public MethodInfo SendToOne;
            public object Cooldown;
            public PropertyInfo CooldownReady;
            public bool WasReady;
            public float LastPoll = -1000f;
            public float LastVisual = -1000f;
            public PendingEdge Edge;
            public PendingResult ResultPending;
            public int Misses;
            public int Pairs;
        }

        public static Scp939ClawSyncPlugin Instance { get; private set; }
        public override string Name => "SCP-939 Claw Sync";
        public override string Description => "Shows missed SCP-939 claw attacks and plays nearby 3D swing audio; server-side LabAPI plugin by Woltano.";
        public override string Author => "Woltano";
        public override Version Version => new Version(1, 0, 0, 0);
        public override Version RequiredApiVersion => new Version(LabApiProperties.CompiledVersion);
        public override bool IsTransparent => true;

        private readonly Dictionary<int, ClawState> _tracked = new Dictionary<int, ClawState>();
        private readonly List<int> _removeIds = new List<int>();
        private readonly List<ClawState> _frameStates = new List<ClawState>();
        private readonly Dictionary<int, float> _bindFailures = new Dictionary<int, float>();
        private GameObject _runnerObject;
        private ClawSyncRunner _runner;
        private bool _enabled = true;
        private bool _spatialSound = true;
        private bool _debug;
        private string _timing = "fast";
        private float _sampleInterval = 0.016f;
        private float _settleSeconds = 0.030f;
        private float _resultGapSeconds = 0.050f;
        private float _lastDiscovery = -1000f;
        private float _lastSummary;
        private float _nextAudioAttempt;

        private bool _audioReady;
        private string _audioStatus = "NOT_INITIALIZED";
        private object _audioManager;
        private MethodInfo _audioPlayMethod;
        private int _attackEdges;
        private int _misses;
        private int _pairs;
        private int _sentMessages;
        private int _hitsSkipped;
        private int _skipped;
        private int _errors;
        private int _audioSessions;
        private int _audioFailures;
        private float _totalDispatchMs;
        private float _maxDispatchMs;

        public override void Enable()
        {
            try
            {
                Instance = this;
                _enabled = true;
                _runnerObject = new GameObject("Woltano_SCP939_ClawSync_Runner");
                UnityObject.DontDestroyOnLoad(_runnerObject);
                _runner = _runnerObject.AddComponent<ClawSyncRunner>();
                _runner.Tick = Tick;
                _lastSummary = Time.realtimeSinceStartup;
                LabLogger.Info(Prefix + " ENABLED_AUTO alwaysOn=true timing=fast mode=spatial " +
                    "unlimitedRounds=true multiScp=true clientMods=false damageCalls=0 hitboxChanges=0 " +
                    "commands='clawsync status|on|off|mode|timing|debug'.");
            }
            catch (Exception ex)
            {
                LabLogger.Error(Prefix + " ENABLE_ERROR " + Clean(ex.Message));
                TearDown();
            }
        }

        public override void Disable()
        {
            TearDown();
            LabLogger.Info(Prefix + " UNLOADED pendingPacketsCancelled=true.");
        }

        private void TearDown()
        {
            _enabled = false;
            _tracked.Clear();
            _removeIds.Clear();
            _frameStates.Clear();
            _bindFailures.Clear();
            if (_runner != null) _runner.Tick = null;
            if (_runnerObject != null) UnityObject.Destroy(_runnerObject);
            _runner = null;
            _runnerObject = null;
            if (Instance == this) Instance = null;
        }

        public bool SetEnabled(bool enable, out string reply)
        {
            if (_runner == null) { reply = "Plugin runner is not available."; return false; }
            _enabled = enable;
            _tracked.Clear();
            _bindFailures.Clear();
            _lastDiscovery = -1000f;
            _nextAudioAttempt = 0f;
            LabLogger.Info(Prefix + (enable ? " ADMIN_ENABLED" : " ADMIN_DISABLED") + " pendingPacketsCancelled=true");
            reply = enable ? "SCP939 claws ENABLED until manually disabled; automatic across every round." :
                "SCP939 claws DISABLED for this server process. Restart server to restore automatic ON.";
            return true;
        }

        public bool SetMode(string mode, out string reply)
        {
            if (mode == "quiet")
            {
                _spatialSound = false;
                reply = "Mode QUIET (animation only).";
            }
            else if (mode == "spatial")
            {
                _spatialSound = true;
                reply = "Mode SPATIAL: animation + 3D claw swing sound for nearby observers, no extra sound for attacker.";
                _nextAudioAttempt = 0f;
            }
            else { reply = "Use clawsync mode quiet | spatial"; return false; }
            LabLogger.Info(Prefix + " MODE_CHANGED " + mode);
            return true;
        }

        public bool SetTiming(string preset, out string reply)
        {
            if (preset == "fast")
            {
                _sampleInterval = 0.016f;
                _settleSeconds = 0.030f;
                _resultGapSeconds = 0.050f;
            }
            else if (preset == "balanced")
            {
                _sampleInterval = 0.025f;
                _settleSeconds = 0.050f;
                _resultGapSeconds = 0.080f;
            }
            else if (preset == "legacy")
            {
                _sampleInterval = 0.050f;
                _settleSeconds = 0.120f;
                _resultGapSeconds = 0.200f;
            }
            else { reply = "Use clawsync timing fast | balanced | legacy"; return false; }
            _timing = preset;
            // Avoid mixing two different timings inside one START/RESULT sequence.
            foreach (ClawState s in _tracked.Values)
            {
                s.Edge = null;
                s.ResultPending = null;
                s.LastPoll = -1000f;
            }
            reply = "Timing " + preset + " (poll=" + (int)(_sampleInterval * 1000f) +
                "ms, settle=" + (int)(_settleSeconds * 1000f) + "ms, START->RESULT=" +
                (int)(_resultGapSeconds * 1000f) + "ms). Pending cosmetic sequences cancelled.";
            LabLogger.Info(Prefix + " TIMING_CHANGED " + reply);
            return true;
        }

        public bool SetDebug(string mode, out string reply)
        {
            if (mode != "on" && mode != "off") { reply = "Use clawsync debug on | off"; return false; }
            _debug = mode == "on";
            reply = "Per-attack debug logging " + mode + ".";
            LabLogger.Info(Prefix + " DEBUG_CHANGED " + mode);
            return true;
        }

        public string Status()
        {
            string avg = _pairs > 0 ? (_totalDispatchMs / _pairs).ToString("0", CultureInfo.InvariantCulture) : "n/a";
            return "ClawSync 1.0.0-rc1 auto=" + _enabled + " tracked939=" + _tracked.Count + " mode=" +
                (_spatialSound ? "spatial" : "quiet") + " timing=" + _timing +
                " edges=" + _attackEdges + " misses=" + _misses + " visualPairs=" + _pairs +
                " skippedHits=" + _hitsSkipped + " skipped=" + _skipped + " packets=" + _sentMessages +
                " dispatchAvgMs=" + avg + " maxMs=" + _maxDispatchMs.ToString("0", CultureInfo.InvariantCulture) +
                " audioSessions=" + _audioSessions + " audioFailures=" + _audioFailures +
                " audioStatus=" + _audioStatus + " errors=" + _errors + " debug=" + _debug +
                " activeForWholeServer=true (until manually disabled or server stops).";
        }

        private void Tick()
        {
            if (!_enabled) return;
            try
            {
                if (!NetworkServer.active)
                {
                    if (_tracked.Count != 0)
                    {
                        _tracked.Clear();
                        LabLogger.Info(Prefix + " NETWORK_STOP statesCleared=true");
                    }
                    return;
                }
                float now = Time.realtimeSinceStartup;
                if (_spatialSound && !_audioReady && now >= _nextAudioAttempt)
                {
                    _nextAudioAttempt = now + 20f;
                    string detail;
                    if (!EnsureAudioReady(out detail))
                        LabLogger.Warn(Prefix + " AUDIO_WAIT " + detail + "; visuals remain active. Next retry in 20s.");
                }
                if (now - _lastDiscovery >= DiscoveryInterval)
                {
                    _lastDiscovery = now;
                    SyncAttackers(now);
                }
                _frameStates.Clear();
                _frameStates.AddRange(_tracked.Values);
                foreach (ClawState state in _frameStates)
                {
                    try
                    {
                        if (IsCurrentAttacker(state)) TickAttacker(state, now);
                        else DropState(state.PlayerId, "ROLE_OR_OBJECT_CHANGED");
                    }
                    catch (Exception ex)
                    {
                        _errors++;
                        Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                        LabLogger.Warn(Prefix + " ATTACKER_ERROR id=" + state.PlayerId + " " +
                            inner.GetType().Name + " " + Clean(inner.Message));
                        DropState(state.PlayerId, "EXCEPTION");
                    }
                }
                if (now - _lastSummary >= SummaryInterval)
                {
                    _lastSummary = now;
                    LabLogger.Info(Prefix + " PERIODIC_STATUS " + Status());
                }
            }
            catch (Exception ex)
            {
                _errors++;
                LabLogger.Warn(Prefix + " LOOP_ERROR " + ex.GetType().Name + " " + Clean(ex.Message));
            }
        }

        private void SyncAttackers(float now)
        {
            var seen = new HashSet<int>();
            foreach (Player player in Player.ReadyList)
            {
                if (player == null || player.GameObject == null || player.ReferenceHub == null ||
                    player.Role != RoleTypeId.Scp939 || !(player.IsPlayer || player.IsDummy)) continue;
                int id = player.PlayerId;
                seen.Add(id);
                ClawState existing;
                if (_tracked.TryGetValue(id, out existing))
                {
                    if (IsCurrentAttacker(existing)) continue;
                    DropState(id, "ROLE_REBIND");
                }
                float lastFail;
                if (_bindFailures.TryGetValue(id, out lastFail) && now - lastFail < 5f) continue;
                ClawState state;
                string reason;
                if (TryBind(player, out state, out reason))
                {
                    _tracked[id] = state;
                    _bindFailures.Remove(id);
                    LabLogger.Info(Prefix + " ATTACKER_BOUND id=" + id + " dummy=" + player.IsDummy +
                        " cooldownReady=" + state.WasReady);
                }
                else
                {
                    _bindFailures[id] = now;
                    if (_debug) LabLogger.Warn(Prefix + " ATTACKER_BIND_WAIT id=" + id + " " + Clean(reason));
                }
            }
            _removeIds.Clear();
            foreach (KeyValuePair<int, ClawState> pair in _tracked)
                if (!seen.Contains(pair.Key) || !IsCurrentAttacker(pair.Value)) _removeIds.Add(pair.Key);
            foreach (int id in _removeIds) DropState(id, "DISCONNECT_OR_ROLE_CHANGE");
            _removeIds.Clear();
            // Clear retry throttles for disconnected players so reused IDs are not delayed.
            _removeIds.AddRange(_bindFailures.Keys);
            foreach (int id in _removeIds)
                if (!seen.Contains(id)) _bindFailures.Remove(id);
            _removeIds.Clear();
        }

        private void DropState(int id, string reason)
        {
            ClawState s;
            if (!_tracked.TryGetValue(id, out s)) return;
            if (s.Edge != null || s.ResultPending != null) _skipped++;
            if (_debug) LabLogger.Info(Prefix + " ATTACKER_RELEASED id=" + id + " reason=" + reason +
                " misses=" + s.Misses + " visuals=" + s.Pairs);
            _tracked.Remove(id);
        }

        private static bool IsCurrentAttacker(ClawState s)
        {
            return s != null && s.Attacker != null && s.Attacker.GameObject != null &&
                s.Attacker.ReferenceHub != null && s.Attacker.PlayerId == s.PlayerId &&
                s.Attacker.Role == RoleTypeId.Scp939 && s.Attacker.ReferenceHub == s.Hub &&
                s.Attacker.GameObject == s.PlayerObject && s.Claw != null &&
                (s.Claw as Behaviour) != null && ((Behaviour)s.Claw).isActiveAndEnabled;
        }

        private void TickAttacker(ClawState s, float now)
        {
            // Phase 2: result is only dispatched to observers that received phase 1.
            if (s.ResultPending != null && now >= s.ResultPending.Due)
            {
                PendingResult pending = s.ResultPending;
                s.ResultPending = null;
                if (!ClawIsIdle(s))
                {
                    _skipped++;
                    if (_debug) LabLogger.Info(Prefix + " RESULT_CANCELLED id=" + s.PlayerId + " realAttackStateChanged=true");
                }
                else
                {
                    List<Player> recipients = GetObservers(s.PlayerId, pending.RecipientIds);
                    if (recipients.Count == 0) _skipped++;
                    else
                    {
                        int[] resultSentIds;
                        int sent = SendPhase(s, recipients, false, SilentSignal, out resultSentIds);
                        if (sent > 0)
                        {
                            _pairs++;
                            s.Pairs++;
                            s.LastVisual = now;
                            float ms = (now - pending.DetectedAt) * 1000f;
                            _totalDispatchMs += ms;
                            if (ms > _maxDispatchMs) _maxDispatchMs = ms;
                            if (_spatialSound && _audioReady) TryPlaySpatialSwing(s.Attacker, resultSentIds);
                            // Production logs: first event and sparse milestones; never one log per every swing.
                            if (_pairs == 1 || (_pairs <= 20 && _pairs % 5 == 0) || _pairs % 100 == 0)
                                LabLogger.Info(Prefix + " VISUAL_MILESTONE pairs=" + _pairs +
                                    " attacker=" + s.PlayerId + " recipients=" + sent +
                                    " audioSessions=" + _audioSessions + " errors=" + _errors);
                            if (_debug) LabLogger.Info(Prefix + " VISUAL_SENT attacker=" + s.PlayerId +
                                " recipients=" + sent + " edgeToResultMs=" + ms.ToString("0", CultureInfo.InvariantCulture));
                        }
                        else _skipped++;
                    }
                }
            }

            // Verify that the genuine attack result remains None after the FAST settle time.
            if (s.Edge != null && now >= s.Edge.Due)
            {
                PendingEdge edge = s.Edge;
                s.Edge = null;
                if (!ClawIsIdle(s))
                {
                    _hitsSkipped++;
                    if (_debug) LabLogger.Info(Prefix + " MISS_REJECTED attacker=" + s.PlayerId + " resultNotNone=true");
                }
                else
                {
                    _misses++;
                    s.Misses++;
                    List<Player> observers = GetObservers(s.PlayerId, null);
                    if (observers.Count > 0 && s.ResultPending == null && now - s.LastVisual >= MinVisualInterval)
                    {
                        int[] ids;
                        int sent = SendPhase(s, observers, true, 0, out ids);
                        if (sent > 0)
                        {
                            s.ResultPending = new PendingResult
                            {
                                Due = now + _resultGapSeconds,
                                DetectedAt = edge.DetectedAt,
                                StartAt = now,
                                RecipientIds = ids
                            };
                            if (_debug) LabLogger.Info(Prefix + " MISS_START attacker=" + s.PlayerId +
                                " recipients=" + sent + " gapMs=" + (int)(_resultGapSeconds * 1000f));
                        }
                        else _skipped++;
                    }
                    // No observers is normal; do not send cosmetic network packets unnecessarily.
                }
            }

            if (now - s.LastPoll < _sampleInterval) return;
            s.LastPoll = now;
            bool ready = (bool)s.CooldownReady.GetValue(s.Cooldown, null);
            bool falling = s.WasReady && !ready;
            s.WasReady = ready;
            if (!falling) return;
            _attackEdges++;
            bool triggered = (bool)s.Trigger.GetValue(s.Claw);
            int result = Convert.ToInt32(s.Result.GetValue(s.Claw), CultureInfo.InvariantCulture);
            if (!triggered && result == 0 && s.Edge == null && s.ResultPending == null)
                s.Edge = new PendingEdge { Due = now + _settleSeconds, DetectedAt = now };
            else
            {
                _hitsSkipped++;
                if (_debug) LabLogger.Info(Prefix + " EDGE_NON_MISS attacker=" + s.PlayerId +
                    " result=" + result + " triggered=" + triggered);
            }
        }

        private static List<Player> GetObservers(int attackerId, int[] eligibleIds)
        {
            var list = new List<Player>();
            foreach (Player player in Player.ReadyList)
            {
                if (player == null || player.GameObject == null || player.ReferenceHub == null ||
                    !player.IsPlayer || player.IsDummy || player.PlayerId == attackerId ||
                    player.Role == RoleTypeId.Spectator || player.Role == RoleTypeId.Overwatch) continue;
                if (eligibleIds != null)
                {
                    bool included = false;
                    for (int i = 0; i < eligibleIds.Length; i++)
                        if (eligibleIds[i] == player.PlayerId) { included = true; break; }
                    if (!included) continue;
                }
                list.Add(player);
            }
            return list;
        }

        private int SendPhase(ClawState s, List<Player> observers, bool start, int signal, out int[] actualRecipientIds)
        {
            actualRecipientIds = Array.Empty<int>();
            if (!NetworkServer.active || observers == null || observers.Count == 0 || !ClawIsIdle(s)) return 0;
            object originalTrigger = s.Trigger.GetValue(s.Claw);
            object originalResult = s.Result.GetValue(s.Claw);
            int sent = 0;
            var delivered = new List<int>();
            try
            {
                if (start) s.Trigger.SetValue(s.Claw, true);
                else s.Result.SetValue(s.Claw, Enum.ToObject(s.Result.FieldType, signal));
                foreach (Player player in observers)
                {
                    try
                    {
                        // A single failing recipient must not interrupt all other players.
                        s.SendToOne.Invoke(s.Claw, new object[] { player.ReferenceHub });
                        sent++;
                        delivered.Add(player.PlayerId);
                    }
                    catch (Exception ex)
                    {
                        _errors++;
                        Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                        LabLogger.Warn(Prefix + " PACKET_ERROR attacker=" + s.PlayerId +
                            " receiver=" + player.PlayerId + " " + Clean(inner.Message));
                    }
                }
                _sentMessages += sent;
                actualRecipientIds = delivered.ToArray();
                return sent;
            }
            finally
            {
                // Restore the REAL server attack state immediately, including on exceptions.
                s.Result.SetValue(s.Claw, originalResult);
                s.Trigger.SetValue(s.Claw, originalTrigger);
            }
        }

        private static bool ClawIsIdle(ClawState state)
        {
            if (state == null || state.Claw == null) return false;
            object trigger = state.Trigger.GetValue(state.Claw);
            object result = state.Result.GetValue(state.Claw);
            return trigger is bool && !(bool)trigger &&
                Convert.ToInt32(result, CultureInfo.InvariantCulture) == 0;
        }

        private static bool TryBind(Player attacker, out ClawState state, out string reason)
        {
            state = null;
            reason = "UNKNOWN";
            Component claw = null;
            int matches = 0;
            foreach (Component c in attacker.GameObject.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().FullName != ClawTypeName) continue;
                Behaviour behaviour = c as Behaviour;
                if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                matches++;
                claw = c;
            }
            if (matches != 1) { reason = "ACTIVE_CLAW_COUNT=" + matches; return false; }
            Type baseAttack = FindType(claw.GetType(), AttackBaseName);
            Type subroutine = FindType(claw.GetType(), SubroutineBaseName);
            if (baseAttack == null || subroutine == null)
            { reason = "TYPE_LAYOUT_CHANGED"; return false; }
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            FieldInfo resultField = baseAttack.GetField(ResultFieldName, flags);
            FieldInfo triggerField = baseAttack.GetField(TriggerFieldName, flags);
            FieldInfo cooldownField = baseAttack.GetField("_serverCooldown", flags);
            MethodInfo send = subroutine.GetMethod("ServerSendRpc", flags, null,
                new Type[] { typeof(ReferenceHub) }, null);
            if (resultField == null || triggerField == null || cooldownField == null || send == null ||
                !resultField.FieldType.IsEnum || triggerField.FieldType != typeof(bool))
            { reason = "MEMBER_LAYOUT_CHANGED"; return false; }
            if (Enum.GetName(resultField.FieldType, 0) != "None" ||
                Enum.GetName(resultField.FieldType, 1) != "AttackedObject" ||
                Enum.IsDefined(resultField.FieldType, SilentSignal))
            { reason = "ATTACK_RESULT_LAYOUT_CHANGED"; return false; }
            object cooldown = cooldownField.GetValue(claw);
            PropertyInfo ready = cooldown == null ? null :
                cooldown.GetType().GetProperty("IsReady", BindingFlags.Public | BindingFlags.Instance);
            if (ready == null || ready.PropertyType != typeof(bool))
            { reason = "COOLDOWN_MEMBER_CHANGED"; return false; }
            object first = ready.GetValue(cooldown, null);
            if (!(first is bool)) { reason = "COOLDOWN_VALUE_CHANGED"; return false; }
            state = new ClawState
            {
                PlayerId = attacker.PlayerId,
                Attacker = attacker,
                Hub = attacker.ReferenceHub,
                PlayerObject = attacker.GameObject,
                Claw = claw,
                Result = resultField,
                Trigger = triggerField,
                Cooldown = cooldown,
                CooldownReady = ready,
                SendToOne = send,
                WasReady = (bool)first
            };
            reason = "OK";
            return true;
        }

        private bool EnsureAudioReady(out string detail)
        {
            if (_audioReady) { detail = "READY"; return true; }
            string stage = "RESOLVE_LABAPI_PATHS";
            try
            {

                // LabAPI resolves these directories on Windows AND Linux, independent of port.
                // The original V17 had hard-coded APPDATA and port 7777 paths.
                string pluginsRoot = PathManager.Plugins.FullName;
                string dependenciesRoot = PathManager.Dependencies.FullName;
                string port = Server.Port.ToString(CultureInfo.InvariantCulture);
                string portPlugins = Path.Combine(pluginsRoot, port);
                string globalPlugins = Path.Combine(pluginsRoot, "global");
                string[] audioCandidates = new[]
                {
                    Path.Combine(portPlugins, "Woltano_SCP939_ClawSwing", AudioFile),
                    Path.Combine(globalPlugins, "Woltano_SCP939_ClawSwing", AudioFile),
                    Path.Combine(portPlugins, "Woltano_SCP939_ClawSync", AudioFile),
                    Path.Combine(globalPlugins, "Woltano_SCP939_ClawSync", AudioFile),
                    Path.Combine(portPlugins, "SCP939_ClawV15", AudioFile)
                };
                string mp3 = null;
                for (int i = 0; i < audioCandidates.Length; i++)
                    if (File.Exists(audioCandidates[i])) { mp3 = audioCandidates[i]; break; }
                stage = "MP3";
                if (mp3 == null)
                {
                    _audioStatus = "MP3_MISSING";
                    detail = "Audio not provided. Visual animation continues. Place licensed 939_claw_beta.mp3 in the documented asset folder.";
                    return false;
                }
                long fileSize = new FileInfo(mp3).Length;
                if (fileSize < 4096 || fileSize > 2000000)
                {
                    _audioStatus = "MP3_INVALID";
                    detail = "Audio file size outside safety bounds";
                    return false;
                }
                stage = "AUDIO_LIB";
                // LabAPI defaults: dependencies/global and dependencies/$port.
                // Check the old dependencies root as a backward-compatible fallback.
                string[] libraryCandidates = new[]
                {
                    Path.Combine(dependenciesRoot, "global", "AudioManagerAPI.dll"),
                    Path.Combine(dependenciesRoot, port, "AudioManagerAPI.dll"),
                    Path.Combine(dependenciesRoot, "AudioManagerAPI.dll")
                };
                Type facade = Type.GetType("AudioManagerAPI.Defaults.DefaultAudioManager, AudioManagerAPI", false);
                if (facade == null)
                {
                    string lib = null;
                    for (int i = 0; i < libraryCandidates.Length; i++)
                        if (File.Exists(libraryCandidates[i])) { lib = libraryCandidates[i]; break; }
                    if (lib == null)
                    {
                        _audioStatus = "AUDIO_LIBRARY_MISSING";
                        detail = "AudioManagerAPI.dll not found in LabAPI dependencies/global, dependencies/$port or root";
                        return false;
                    }
                    Assembly asm = Assembly.LoadFrom(lib);
                    facade = asm.GetType("AudioManagerAPI.Defaults.DefaultAudioManager", false);
                }
                if (facade == null) throw new MissingMemberException("AudioManagerAPI facade missing");
                stage = "API_METHODS";
                MethodInfo register = facade.GetMethod("RegisterAudio", BindingFlags.Public | BindingFlags.Static);
                PropertyInfo instance = facade.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (register == null || instance == null) throw new MissingMethodException("AudioManager facade API changed");
                object manager = instance.GetValue(null, null);
                MethodInfo play = null;
                foreach (MethodInfo candidate in manager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (candidate.Name == "PlayAudio" && !candidate.IsGenericMethodDefinition &&
                        candidate.GetParameters().Length == 14)
                    { play = candidate; break; }
                }
                if (play == null) throw new MissingMethodException("AudioManager PlayAudio(14) missing");
                stage = "REGISTER_MP3";
                register.Invoke(null, new object[] { AudioKey, (Func<Stream>)(() => File.OpenRead(mp3)) });
                _audioManager = manager;
                _audioPlayMethod = play;
                _audioReady = true;
                _audioStatus = "REGISTERED";
                LabLogger.Info(Prefix + " AUDIO_REGISTERED size=" + fileSize +
                    " asset=PORT_OR_GLOBAL pathRedacted=true spatialRadius=16m");
                detail = "REGISTERED";
                return true;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                _audioStatus = "FAILED_" + stage + "_" + inner.GetType().Name;
                detail = _audioStatus + " " + Clean(inner.Message);
                return false;
            }
        }

        private void TryPlaySpatialSwing(Player attacker, int[] actualRecipientIds)
        {
            if (!_audioReady || _audioPlayMethod == null || _audioManager == null || attacker == null) return;
            try
            {
                int attackerId = attacker.PlayerId;
                Vector3 origin = attacker.Position + Vector3.up * 1.25f;
                // Play only to players that really received the animation RESULT packet.
                // This also avoids audio-only ghost swings during observer joins/leaves.
                Func<Player, bool> filter = p => p != null && p.IsPlayer && !p.IsDummy &&
                    p.PlayerId != attackerId && p.Role != RoleTypeId.Spectator &&
                    p.Role != RoleTypeId.Overwatch && ContainsId(actualRecipientIds, p.PlayerId) &&
                    Vector3.Distance(p.Position, origin) <= AudioMaxDistance;
                object priority = Enum.Parse(_audioPlayMethod.GetParameters()[7].ParameterType, "Medium");
                object[] args = { AudioKey, origin, false, AudioVolume, AudioMinDistance,
                    AudioMaxDistance, true, priority, filter, false, 0f, false, (float?)2.5f, true };
                object answer = _audioPlayMethod.Invoke(_audioManager, args);
                int sessionId = answer == null ? 0 : Convert.ToInt32(answer, CultureInfo.InvariantCulture);
                if (sessionId != 0) _audioSessions++;
                else
                {
                    _audioFailures++;
                    if (_audioFailures < 4 || _audioFailures % 20 == 0)
                        LabLogger.Warn(Prefix + " AUDIO_SESSION_ZERO count=" + _audioFailures +
                            " (decoder/speaker resource unavailable); visuals unaffected");
                }
                if (_debug) LabLogger.Info(Prefix + " SPATIAL_PLAY attacker=" + attackerId + " session=" + sessionId);
            }
            catch (Exception ex)
            {
                _audioFailures++;
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                LabLogger.Warn(Prefix + " AUDIO_PLAY_ERROR " + inner.GetType().Name + " " + Clean(inner.Message));
            }
        }

        private static bool ContainsId(int[] ids, int wanted)
        {
            if (ids == null) return false;
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] == wanted) return true;
            return false;
        }

        private static Type FindType(Type type, string fullName)
        {
            for (Type t = type; t != null; t = t.BaseType)
                if (t.FullName == fullName || (t.IsGenericType &&
                    t.GetGenericTypeDefinition().FullName == fullName)) return t;
            return null;
        }
        private static string Clean(string text)
        {
            if (text == null) return "null";
            string cleaned = text.Replace('\r', ' ').Replace('\n', ' ');
            return cleaned.Length <= 180 ? cleaned : cleaned.Substring(0, 180);
        }
    }

    public sealed class ClawSyncRunner : MonoBehaviour
    {
        public Action Tick;
        private void Update() { Action cb = Tick; if (cb != null) cb(); }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public sealed class ClawSyncCommand : ICommand
    {
        public string Command => "clawsync";
        public string[] Aliases => new[] { "clawfix" };
        public string Description => "Woltano SCP-939 Claw Sync 1.0.0-rc1: status/on/off/mode/timing/debug.";
        public bool Execute(ArraySegment<string> args, ICommandSender sender, out string response)
        {
            if (sender == null || !sender.CheckPermission(PlayerPermissions.PlayersManagement))
            { response = "Remote Admin PlayersManagement permission required."; return false; }
            Scp939ClawSyncPlugin plugin = Scp939ClawSyncPlugin.Instance;
            if (plugin == null) { response = "ClawSync plugin not loaded."; return false; }
            if (args.Count == 0) { response = "clawsync status | on | off | mode spatial|quiet | timing fast|balanced|legacy | debug on|off"; return true; }
            string sub = (args.Array[args.Offset] ?? "").ToLowerInvariant();
            if (sub == "status" && args.Count == 1) { response = plugin.Status(); return true; }
            if ((sub == "on" || sub == "start") && args.Count == 1) return plugin.SetEnabled(true, out response);
            if ((sub == "off" || sub == "stop") && args.Count == 1) return plugin.SetEnabled(false, out response);
            if (sub == "mode" && args.Count == 2)
                return plugin.SetMode((args.Array[args.Offset + 1] ?? "").ToLowerInvariant(), out response);
            if (sub == "timing" && args.Count == 2)
                return plugin.SetTiming((args.Array[args.Offset + 1] ?? "").ToLowerInvariant(), out response);
            if (sub == "debug" && args.Count == 2)
                return plugin.SetDebug((args.Array[args.Offset + 1] ?? "").ToLowerInvariant(), out response);
            response = "clawsync status | on | off | mode spatial|quiet | timing fast|balanced|legacy | debug on|off";
            return false;
        }
    }
}

// MavLinkUdpListener.cs
// Minimal MAVLink GCS client for Unity with support for up to 10 independent vehicles.
//
// Goal:
// - Receive telemetry from ALL configured vehicles all the time.
// - Control ONE selected vehicle via the same UI buttons (ARM/DISARM/GUIDED/TAKEOFF/LAND + 3 local points).
// - Each vehicle slot can use a different transport (UDP or TCP) or be disabled (None).
//
// Design (simple + robust):
// - Each slot is its own "link" (own UDP listen port OR own TCP connection).
// - That avoids the complexity of demuxing multiple vehicles on a single UDP port.
//   (UDP *can* carry multiple sysids on one port, but then you need routing by sysid/endpoint. We'll keep it simple.)
//
// UDP notes (recommended for SITL):
// - For each SITL instance, configure MAVProxy/SITL to send to Unity slot.listenPort, e.g. --out udp:127.0.0.1:14551
// - If MAVProxy uses an ephemeral source port (common), keep replyToLastSender = true so commands go back to the correct port.
//
// TCP notes:
// - Unity connects as a TCP client to slot.tcpHost:slot.tcpPort.
// - On the SITL side, there must be a TCP server endpoint on that port (SITL usually prints it: "SERIAL0 on TCP port XXXX").
//
// Supported RX messages (if autopilot sends them):
// - HEARTBEAT / COMMAND_ACK / STATUSTEXT
// - GLOBAL_POSITION_INT / LOCAL_POSITION_NED / VFR_HUD
// - ATTITUDE (roll/pitch/yaw)
// - SCALED_PRESSURE (baro)
// - WIND
//
// TX:
// - GCS HEARTBEAT
// - ARM/DISARM, GUIDED mode, TAKEOFF, LAND
// - Optional telemetry auto-config via MAV_CMD_SET_MESSAGE_INTERVAL
// - GUIDED local setpoints via SET_POSITION_TARGET_LOCAL_NED (resent ~10Hz while active)
//
// Packet build uses reflection to survive different MAVLink C# generator overloads.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class MavLinkUdpListener : MonoBehaviour
{
    public enum LinkType
    {
        None = 0,
        UDP = 1,
        TCP = 2
    }

    [Serializable]
    public class VehicleSlot
    {
        [Header("Slot")]
        public string label = "Drone";
        public LinkType linkType = LinkType.None;

        [Header("UDP (per-slot)")]
        [Tooltip("Unity listens here. Configure SITL/MAVProxy to send to this port: --out udp:127.0.0.1:<listenPort>")]
        public int listenPort = 14551;

        [Tooltip("Fallback TX if we haven't learned the sender endpoint yet.")]
        public string defaultTargetIp = "127.0.0.1";
        public int defaultTargetPort = 14550;

        [Tooltip("If true: send outgoing packets to the last UDP endpoint seen FROM this slot (recommended for SITL/MAVProxy).")]
        public bool replyToLastSender = true;

        [Header("TCP (per-slot)")]
        public string tcpHost = "127.0.0.1";
        public int tcpPort = 5770;
        public bool tcpAutoReconnect = true;
        public float tcpReconnectDelay = 1.0f;

        [Header("Runtime (read-only)")]
        public byte vehSysId = 1;
        public byte vehCompId = 1;
        public byte baseMode;
        public uint customMode;
        public bool armed;
        public bool guided;

        public double gpsLatDeg;
        public double gpsLonDeg;
        public float gpsRelAltM;

        public Vector3 localNed;
        public Vector3 localUnity;

        public float groundspeedMps;
        public float airspeedMps;
        public float headingDeg;

        public float rollDeg;
        public float pitchDeg;
        public float yawDeg;

        public float pressureHpa;
        public float windSpeedMps;
        public float windDirDeg;
        public float windSpeedZ;

        public long lastRxAgeMs;

        // ---------------- internal (not serialized) ----------------
        [NonSerialized] public MAVLink.MavlinkParse parser;

        [NonSerialized] public UdpClient udp;
        [NonSerialized] public IPEndPoint udpDefaultTx;
        [NonSerialized] public IPEndPoint udpLastSender;

        [NonSerialized] public TcpClient tcp;
        [NonSerialized] public NetworkStream tcpStream;
        [NonSerialized] public bool tcpConnected;
        [NonSerialized] public readonly byte[] tcpReadTmp = new byte[8192];
        [NonSerialized] public readonly List<byte> tcpRxBuffer = new List<byte>(16384);

        [NonSerialized] public Thread rxThread;
        [NonSerialized] public readonly ConcurrentQueue<MAVLink.MAVLinkMessage> rxQueue = new ConcurrentQueue<MAVLink.MAVLinkMessage>();

        [NonSerialized] public byte seq;
        [NonSerialized] public long lastRxMs;
        [NonSerialized] public bool telemetryConfigured;

        // Guided streaming state
        [NonSerialized] public Vector3 activeTargetNed;
        [NonSerialized] public bool hasTarget;
        [NonSerialized] public long lastSetpointMs;

        // Ping tracking (optional)
        [NonSerialized] public uint pingSeq = 1;
        [NonSerialized] public readonly Dictionary<uint, ulong> pingSentUsec = new Dictionary<uint, ulong>();

        public bool Enabled => linkType != LinkType.None;
    }

    [Header("Multi-vehicle")]
    [Tooltip("Slot index selected for control commands (ARM/TAKEOFF/etc). Telemetry continues for all enabled slots.")]
    public int activeSlotIndex = 0;

    [Tooltip("Up to 10 slots. Set linkType=None for unused slots.")]
    public VehicleSlot[] slots = new VehicleSlot[10];

    [Header("GCS identity")]
    public byte gcsSysId = 255;
    public byte gcsCompId = 190;

    [Header("GUIDED points (LOCAL NED, meters)")]
    [Tooltip("x=North, y=East, z=Down. For example z=-5 means 5 meters UP.")]
    public Vector3 pointA = new Vector3(5, 0, -5);
    public Vector3 pointB = new Vector3(0, 5, -5);
    public Vector3 pointC = new Vector3(-5, 0, -5);

    [Header("Behavior")]
    public bool preferMavlink2 = true;

    [Header("Telemetry auto-config (recommended)")]
    public bool autoConfigureTelemetry = true;

    [Tooltip("GLOBAL_POSITION_INT Hz")]
    public int rateGlobalPosHz = 5;
    [Tooltip("LOCAL_POSITION_NED Hz")]
    public int rateLocalPosHz = 10;
    [Tooltip("VFR_HUD Hz")]
    public int rateVfrHudHz = 5;
    [Tooltip("ATTITUDE Hz")]
    public int rateAttitudeHz = 10;
    [Tooltip("SCALED_PRESSURE Hz")]
    public int ratePressureHz = 2;
    [Tooltip("WIND Hz")]
    public int rateWindHz = 2;

    [Header("Debug")]
    public bool logTx = true;
    public bool logRxHeartbeat = true;
    public bool logRxAck = true;
    public bool logRxPosition = false;
    public bool showOnScreenHud = true;

    // ============================ INTERNAL ============================

    private volatile bool _running;

    private bool _dumpedPacketBuilders;
    private bool _warnedMavlink2Fallback;

    private static readonly System.Diagnostics.Stopwatch _monoClock = System.Diagnostics.Stopwatch.StartNew();
    private static long NowMs() => _monoClock.ElapsedMilliseconds;

    // ============================ UNITY ============================

    void Awake()
    {
        // Ensure array size = 10 and not null elements.
        if (slots == null || slots.Length != 10)
        {
            var arr = new VehicleSlot[10];
            for (int i = 0; i < 10; i++) arr[i] = new VehicleSlot { label = $"Drone {i + 1}" };
            slots = arr;
        }
        else
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) slots[i] = new VehicleSlot { label = $"Drone {i + 1}" };
                if (string.IsNullOrWhiteSpace(slots[i].label)) slots[i].label = $"Drone {i + 1}";
            }
        }

        activeSlotIndex = Mathf.Clamp(activeSlotIndex, 0, slots.Length - 1);
    }

    void Start()
    {
        _running = true;

        // Start each enabled slot.
        for (int i = 0; i < slots.Length; i++)
            StartSlot(i);

        DumpPacketBuildersOnce();

        InvokeRepeating(nameof(SendGcsHeartbeatsAll), 0f, 1f);

        if (autoConfigureTelemetry)
            InvokeRepeating(nameof(ConfigureTelemetryAll), 1.0f, 1.0f);

        Debug.Log($"[MAV] Multi-client started. slots={slots.Length} activeSlotIndex={activeSlotIndex}");
    }

    void OnDestroy()
    {
        _running = false;

        // Stop all slots.
        for (int i = 0; i < slots.Length; i++)
            StopSlot(i);
    }

    void Update()
    {
        // Pull RX queues for all slots.
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s == null || !s.Enabled) continue;

            s.lastRxAgeMs = NowMs() - s.lastRxMs;

            while (s.rxQueue.TryDequeue(out var msg))
                HandleMessage(i, s, msg);

            // Resend GUIDED setpoint ~10Hz if active.
            if (s.hasTarget && s.guided)
            {
                long now = NowMs();
                if (now - s.lastSetpointMs >= 100)
                {
                    SendLocalSetpoint(i, s, s.activeTargetNed);
                    s.lastSetpointMs = now;
                }
            }
        }

        // Keep active index within bounds.
        activeSlotIndex = Mathf.Clamp(activeSlotIndex, 0, slots.Length - 1);
    }

    void OnGUI()
    {
        if (!showOnScreenHud) return;

        var a = GetActiveSlot();
        string activeName = a != null ? a.label : "(none)";

        GUILayout.BeginArea(new Rect(10, 10, 620, 260), GUI.skin.box);
        GUILayout.Label($"Active slot: #{activeSlotIndex + 1}  {activeName}");

        // Quick selector (works without Unity UI). If you use a Dropdown, call SetActiveSlot(index) instead.
        GUILayout.BeginHorizontal();
        GUILayout.Label("Select:", GUILayout.Width(50));
        for (int i = 0; i < slots.Length; i++)
        {
            if (GUILayout.Button((i + 1).ToString(), GUILayout.Width(36)))
                SetActiveSlot(i);
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);

        // Show active slot telemetry.
        if (a != null && a.Enabled)
        {
            GUILayout.Label($"Link: {a.linkType}  RX age={a.lastRxAgeMs} ms");
            if (a.linkType == LinkType.UDP)
            {
                string tx = a.replyToLastSender && a.udpLastSender != null
                    ? $"{a.udpLastSender.Address}:{a.udpLastSender.Port}"
                    : $"{a.defaultTargetIp}:{a.defaultTargetPort}";
                GUILayout.Label($"UDP: listen={a.listenPort}  TX->{tx}");
            }
            else if (a.linkType == LinkType.TCP)
            {
                GUILayout.Label($"TCP: {a.tcpHost}:{a.tcpPort}  connected={a.tcpConnected}");
            }

            GUILayout.Label($"Vehicle: sys={a.vehSysId} comp={a.vehCompId}  armed={a.armed} guided={a.guided}  base={a.baseMode} custom={a.customMode}");
            GUILayout.Label($"GPS: lat={a.gpsLatDeg:F7} lon={a.gpsLonDeg:F7} relAlt={a.gpsRelAltM:F1} m");
            GUILayout.Label($"LOCAL: N={a.localNed.x:F2}  E={a.localNed.y:F2}  D={a.localNed.z:F2}  (Unity up={a.localUnity.z:F2})");
            GUILayout.Label($"ATT: roll={a.rollDeg:F1}° pitch={a.pitchDeg:F1}° yaw={a.yawDeg:F0}°");
            GUILayout.Label($"BARO: {a.pressureHpa:F1} hPa   WIND: {a.windSpeedMps:F1} m/s dir={a.windDirDeg:F0}° vz={a.windSpeedZ:F1}");
        }
        else
        {
            GUILayout.Label("Active slot is disabled (None). Enable it in Inspector.");
        }

        GUILayout.Space(8);

        // Compact overview of all slots.
        GUILayout.Label("Slots overview:");
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s == null) continue;
            string en = s.Enabled ? "ON" : "OFF";
            string link = s.linkType.ToString();
            string rx = s.Enabled ? $"age={s.lastRxAgeMs}ms" : "";
            string arm = s.Enabled ? (s.armed ? "ARM" : "DIS") : "";
            GUILayout.Label($"#{i + 1}: {s.label}  [{en}] {link}  {rx}  {arm}");
        }

        GUILayout.EndArea();
    }

    // ============================ PUBLIC (Unity UI hooks) ============================

    // For a Unity UI Dropdown (0..9): hook OnValueChanged -> SetActiveSlot
    public void SetActiveSlot(int index)
    {
        activeSlotIndex = Mathf.Clamp(index, 0, slots.Length - 1);
        Debug.Log($"[UI] Active slot set to #{activeSlotIndex + 1} ({slots[activeSlotIndex].label})");
    }

    // Buttons: these always control ACTIVE slot.
    public void Arm() => Arm(activeSlotIndex);
    public void Disarm() => Disarm(activeSlotIndex);
    public void SetModeGuided() => SetModeGuided(activeSlotIndex);
    public void Takeoff() => Takeoff(activeSlotIndex);
    public void Land() => Land(activeSlotIndex);

    public void FlyPointA() => FlyToLocal(activeSlotIndex, pointA);
    public void FlyPointB() => FlyToLocal(activeSlotIndex, pointB);
    public void FlyPointC() => FlyToLocal(activeSlotIndex, pointC);

    // ============================ SLOT LIFECYCLE ============================

    private void StartSlot(int slotIndex)
    {
        var s = slots[slotIndex];
        if (s == null || !s.Enabled) return;

        s.parser = new MAVLink.MavlinkParse();
        s.seq = 0;
        s.telemetryConfigured = false;
        s.hasTarget = false;
        s.lastSetpointMs = 0;

        if (s.linkType == LinkType.UDP)
        {
            try
            {
                s.udp = new UdpClient(s.listenPort);
                s.udpDefaultTx = new IPEndPoint(IPAddress.Parse(s.defaultTargetIp), s.defaultTargetPort);
            }
            catch (Exception e)
            {
                Debug.LogError($"[S{slotIndex}] UDP bind failed on port {s.listenPort}: {e.Message}");
                s.linkType = LinkType.None;
                return;
            }

            s.rxThread = new Thread(() => RxLoopUdp(slotIndex, s)) { IsBackground = true };
            s.rxThread.Start();

            Debug.Log($"[S{slotIndex}] UDP started. RX={s.listenPort} defaultTX={s.defaultTargetIp}:{s.defaultTargetPort} replyToLastSender={s.replyToLastSender}");
        }
        else if (s.linkType == LinkType.TCP)
        {
            s.rxThread = new Thread(() => RxLoopTcp(slotIndex, s)) { IsBackground = true };
            s.rxThread.Start();

            Debug.Log($"[S{slotIndex}] TCP starting. Will connect to {s.tcpHost}:{s.tcpPort} autoReconnect={s.tcpAutoReconnect}");
        }
    }

    private void StopSlot(int slotIndex)
    {
        var s = slots[slotIndex];
        if (s == null) return;

        try { s.udp?.Close(); } catch { }

        try
        {
            s.tcpStream?.Close();
            s.tcp?.Close();
        }
        catch { }

        try
        {
            if (s.rxThread != null && s.rxThread.IsAlive)
                s.rxThread.Join(200);
        }
        catch { }

        s.udp = null;
        s.tcpStream = null;
        s.tcp = null;
        s.rxThread = null;
        s.tcpConnected = false;
    }

    private VehicleSlot GetActiveSlot()
    {
        if (slots == null || slots.Length == 0) return null;
        int i = Mathf.Clamp(activeSlotIndex, 0, slots.Length - 1);
        return slots[i];
    }

    // ============================ CONTROL (per-slot) ============================

    private void Arm(int slotIndex)
    {
        var s = slots[slotIndex];
        if (!EnsureSlotReady(slotIndex, s)) return;
        SendCommandLong(slotIndex, s, MavCmd.COMPONENT_ARM_DISARM, p1: 1f);
        Debug.Log($"[UI] ARM -> slot #{slotIndex + 1}");
    }

    private void Disarm(int slotIndex)
    {
        var s = slots[slotIndex];
        if (!EnsureSlotReady(slotIndex, s)) return;
        SendCommandLong(slotIndex, s, MavCmd.COMPONENT_ARM_DISARM, p1: 0f);
        Debug.Log($"[UI] DISARM -> slot #{slotIndex + 1}");
    }

    private void SetModeGuided(int slotIndex)
    {
        var s = slots[slotIndex];
        if (!EnsureSlotReady(slotIndex, s)) return;
        // ArduCopter GUIDED mode = 4 (custom_mode)
        SendCommandLong(slotIndex, s, MavCmd.DO_SET_MODE, p1: 1f, p2: 4f);
        Debug.Log($"[UI] SetMode GUIDED -> slot #{slotIndex + 1}");
    }

    private void Takeoff(int slotIndex)
    {
        var s = slots[slotIndex];
        if (!EnsureSlotReady(slotIndex, s)) return;
        SendCommandLong(slotIndex, s, MavCmd.NAV_TAKEOFF, p7: 5f);
        Debug.Log($"[UI] TAKEOFF(5m) -> slot #{slotIndex + 1}");
    }

    private void Land(int slotIndex)
    {
        var s = slots[slotIndex];
        if (!EnsureSlotReady(slotIndex, s)) return;
        SendCommandLong(slotIndex, s, MavCmd.NAV_LAND);
        Debug.Log($"[UI] LAND -> slot #{slotIndex + 1}");
    }

    private void FlyToLocal(int slotIndex, Vector3 ned)
    {
        var s = slots[slotIndex];
        if (!EnsureSlotReady(slotIndex, s)) return;

        s.activeTargetNed = ned;
        s.hasTarget = true;
        s.lastSetpointMs = 0;

        SendLocalSetpoint(slotIndex, s, ned);

        if (!s.guided)
            Debug.LogWarning($"[S{slotIndex}] GUIDED not active yet (base_mode={s.baseMode}, custom_mode={s.customMode}). Still sending setpoint.");

        Debug.Log($"[S{slotIndex}] GUIDED Target NED: N={ned.x:F1} E={ned.y:F1} D={ned.z:F1}");
    }

    private bool EnsureSlotReady(int slotIndex, VehicleSlot s)
    {
        if (s == null || !s.Enabled)
        {
            Debug.LogWarning($"[S{slotIndex}] Slot disabled (None)");
            return false;
        }

        if (s.linkType == LinkType.TCP && !s.tcpConnected)
        {
            Debug.LogWarning($"[S{slotIndex}] TCP not connected.");
            return false;
        }

        // UDP can still send with fallback target if last sender isn't learned yet.
        return true;
    }

    // ============================ HEARTBEATS + TELEMETRY SETUP (all slots) ============================

    private void SendGcsHeartbeatsAll()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s == null || !s.Enabled) continue;
            SendGcsHeartbeat(i, s);
        }
    }

    private void SendGcsHeartbeat(int slotIndex, VehicleSlot s)
    {
        var hb = new MAVLink.mavlink_heartbeat_t
        {
            type = (byte)MAVLink.MAV_TYPE.GCS,
            autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
            base_mode = 0,
            custom_mode = 0,
            system_status = (byte)MAVLink.MAV_STATE.ACTIVE
        };

        bool ok = SendMessage(slotIndex, s, MAVLink.MAVLINK_MSG_ID.HEARTBEAT, hb);
        if (ok && logTx) Debug.Log($"[S{slotIndex}][TX] HEARTBEAT (GCS)");
    }

    private void ConfigureTelemetryAll()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            if (s == null || !s.Enabled) continue;
            if (s.telemetryConfigured) continue;

            // Require link to be usable.
            if (s.linkType == LinkType.TCP)
            {
                if (!s.tcpConnected) continue;
            }
            else if (s.linkType == LinkType.UDP)
            {
                if (s.replyToLastSender && s.udpLastSender == null)
                    continue; // wait until we learned sender endpoint
            }

            s.telemetryConfigured = true;

            SetMessageInterval(i, s, MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, rateGlobalPosHz);
            SetMessageInterval(i, s, MAVLink.MAVLINK_MSG_ID.LOCAL_POSITION_NED, rateLocalPosHz);
            SetMessageInterval(i, s, MAVLink.MAVLINK_MSG_ID.VFR_HUD, rateVfrHudHz);
            SetMessageInterval(i, s, MAVLink.MAVLINK_MSG_ID.ATTITUDE, rateAttitudeHz);
            SetMessageInterval(i, s, MAVLink.MAVLINK_MSG_ID.SCALED_PRESSURE, ratePressureHz);
            SetMessageInterval(i, s, MAVLink.MAVLINK_MSG_ID.WIND, rateWindHz);

            Debug.Log($"[S{i}] Telemetry configured.");
        }
    }

    private void SetMessageInterval(int slotIndex, VehicleSlot s, MAVLink.MAVLINK_MSG_ID msgId, int rateHz)
    {
        if (rateHz <= 0) rateHz = 1;
        float intervalUs = 1_000_000f / rateHz;

        SendCommandLong(slotIndex, s, MavCmd.SET_MESSAGE_INTERVAL,
            p1: (float)(int)msgId,
            p2: intervalUs,
            p3: 0f);

        if (logTx)
            Debug.Log($"[S{slotIndex}][TX] SET_MESSAGE_INTERVAL msg={(int)msgId} ({msgId}) interval={intervalUs:F0}us (~{rateHz}Hz)");
    }

    // ============================ MAVLINK TX (per-slot) ============================

    private void SendCommandLong(int slotIndex, VehicleSlot s, ushort command,
        float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        byte ts = (s.vehSysId != 0) ? s.vehSysId : (byte)0;
        byte tc = (s.vehCompId != 0) ? s.vehCompId : (byte)0;

        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = ts,
            target_component = tc,
            command = command,
            confirmation = 0,
            param1 = p1,
            param2 = p2,
            param3 = p3,
            param4 = p4,
            param5 = p5,
            param6 = p6,
            param7 = p7
        };

        SendMessage(slotIndex, s, MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        if (logTx) Debug.Log($"[S{slotIndex}][TX] COMMAND_LONG cmd={command} target={ts}:{tc}");
    }

    private void SendLocalSetpoint(int slotIndex, VehicleSlot s, Vector3 ned)
    {
        var sp = new MAVLink.mavlink_set_position_target_local_ned_t
        {
            time_boot_ms = (uint)(Time.time * 1000),
            target_system = s.vehSysId,
            target_component = s.vehCompId,
            coordinate_frame = (byte)MAVLink.MAV_FRAME.LOCAL_NED,

            // Mask ignores velocities/accelerations/yaw. We only set position.
            type_mask = (ushort)(
                (1 << 3) | (1 << 4) | (1 << 5) |
                (1 << 6) | (1 << 7) | (1 << 8) |
                (1 << 10) | (1 << 11)
            ),

            x = ned.x,
            y = ned.y,
            z = ned.z
        };

        SendMessage(slotIndex, s, MAVLink.MAVLINK_MSG_ID.SET_POSITION_TARGET_LOCAL_NED, sp);
        if (logTx) Debug.Log($"[S{slotIndex}][TX] SET_POSITION_TARGET_LOCAL_NED N={ned.x:F1} E={ned.y:F1} D={ned.z:F1}");
    }

    private bool SendMessage(int slotIndex, VehicleSlot s, MAVLink.MAVLINK_MSG_ID id, object payload)
    {
        if (s == null || !s.Enabled || s.parser == null) return false;

        byte[] packet = BuildPacket(s.parser, ref s.seq, id, payload, preferMavlink2, out byte usedSeq);
        if (packet == null || packet.Length == 0)
        {
            Debug.LogWarning($"[S{slotIndex}][TX] Failed to build packet for {id}");
            return false;
        }

        if (s.linkType == LinkType.TCP)
        {
            if (!s.tcpConnected || s.tcpStream == null)
            {
                if (logTx) Debug.LogWarning($"[S{slotIndex}][TX] TCP not connected. Dropping {id}.");
                return false;
            }

            try
            {
                s.tcpStream.Write(packet, 0, packet.Length);
                s.tcpStream.Flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[S{slotIndex}][TCP] Send error: {e.Message}");
                s.tcpConnected = false;
                return false;
            }

            if (logTx)
                Debug.Log($"[S{slotIndex}][TX] {id} bytes={packet.Length} seq={usedSeq}");

            return true;
        }

        // UDP
        IPEndPoint dst = s.udpDefaultTx;
        if (s.replyToLastSender)
        {
            if (s.udpLastSender != null) dst = s.udpLastSender;
        }

        try
        {
            s.udp.Send(packet, packet.Length, dst);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[S{slotIndex}][UDP] Send error: {e.Message}");
            return false;
        }

        if (logTx)
            Debug.Log($"[S{slotIndex}][TX] {id} bytes={packet.Length} seq={usedSeq} dst={dst.Address}:{dst.Port}");

        return true;
    }

    // ============================ MAVLINK RX (per-slot threads) ============================

    private void RxLoopUdp(int slotIndex, VehicleSlot s)
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running && s != null && s.udp != null)
        {
            try
            {
                byte[] data = s.udp.Receive(ref ep);

                s.udpLastSender = new IPEndPoint(ep.Address, ep.Port);
                s.lastRxMs = NowMs();

                ParseDatagram(s, data);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (!_running) break;
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    private void RxLoopTcp(int slotIndex, VehicleSlot s)
    {
        while (_running && s != null)
        {
            if (!s.tcpConnected)
            {
                TryConnectTcp(slotIndex, s);

                if (!s.tcpConnected)
                {
                    if (!s.tcpAutoReconnect) return;
                    Thread.Sleep((int)(Mathf.Max(0.2f, s.tcpReconnectDelay) * 1000f));
                    continue;
                }
            }

            try
            {
                int n = s.tcpStream.Read(s.tcpReadTmp, 0, s.tcpReadTmp.Length);
                if (n <= 0)
                {
                    s.tcpConnected = false;
                    continue;
                }

                s.lastRxMs = NowMs();

                lock (s.tcpRxBuffer)
                {
                    for (int i = 0; i < n; i++)
                        s.tcpRxBuffer.Add(s.tcpReadTmp[i]);

                    ParseStreamBuffer(s, s.tcpRxBuffer);
                }
            }
            catch
            {
                s.tcpConnected = false;
            }

            if (!s.tcpConnected)
            {
                try { s.tcpStream?.Close(); } catch { }
                try { s.tcp?.Close(); } catch { }
                s.tcpStream = null;
                s.tcp = null;
            }
        }
    }

    private void TryConnectTcp(int slotIndex, VehicleSlot s)
    {
        try
        {
            s.tcp = new TcpClient();
            s.tcp.NoDelay = true;
            s.tcp.Connect(s.tcpHost, s.tcpPort);
            s.tcpStream = s.tcp.GetStream();
            s.tcpConnected = true;
            Debug.Log($"[S{slotIndex}] TCP connected to {s.tcpHost}:{s.tcpPort}");
        }
        catch (Exception e)
        {
            s.tcpConnected = false;
            if (_running)
                Debug.LogWarning($"[S{slotIndex}] TCP connect failed: {e.Message}");
        }
    }

    private void ParseDatagram(VehicleSlot s, byte[] data)
    {
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b != 0xFE && b != 0xFD) { i++; continue; }

            using (var ms = new MemoryStream(data, i, data.Length - i, false))
            {
                var msg = s.parser.ReadPacket(ms);
                if (msg != null)
                {
                    s.rxQueue.Enqueue(msg);
                    i += (int)ms.Position;
                    continue;
                }
            }

            i++;
        }
    }

    private void ParseStreamBuffer(VehicleSlot s, List<byte> buf)
    {
        if (buf.Count == 0) return;

        byte[] arr = buf.ToArray();
        int i = 0;

        while (i < arr.Length)
        {
            byte b = arr[i];
            if (b != 0xFE && b != 0xFD) { i++; continue; }

            if (arr.Length - i < 8) break;

            using (var ms = new MemoryStream(arr, i, arr.Length - i, false))
            {
                var msg = s.parser.ReadPacket(ms);
                if (msg != null)
                {
                    s.rxQueue.Enqueue(msg);
                    i += (int)ms.Position;
                    continue;
                }
            }

            i++;
        }

        if (i > 0)
            buf.RemoveRange(0, Mathf.Min(i, buf.Count));

        if (buf.Count > 200000)
            buf.RemoveRange(0, buf.Count - 50000);
    }

    // ============================ RX HANDLER ============================

    private void HandleMessage(int slotIndex, VehicleSlot s, MAVLink.MAVLinkMessage msg)
    {
        // Ignore own loopback
        if (msg.sysid == gcsSysId && msg.compid == gcsCompId)
            return;

        switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
        {
            case MAVLink.MAVLINK_MSG_ID.HEARTBEAT:
            {
                s.vehSysId = msg.sysid;
                s.vehCompId = msg.compid;

                var hb = (MAVLink.mavlink_heartbeat_t)msg.data;
                s.baseMode = hb.base_mode;
                s.customMode = hb.custom_mode;

                s.armed = (s.baseMode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                s.guided = ((s.baseMode & (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED) != 0) && (s.customMode == 4u);

                if (logRxHeartbeat)
                    Debug.Log($"[S{slotIndex}][RX] HEARTBEAT sys={s.vehSysId} comp={s.vehCompId} base={s.baseMode} custom={s.customMode} armed={s.armed} guided={s.guided}");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.COMMAND_ACK:
            {
                var ack = (MAVLink.mavlink_command_ack_t)msg.data;
                if (logRxAck)
                    Debug.Log($"[S{slotIndex}][RX] COMMAND_ACK cmd={ack.command} result={ack.result} from {msg.sysid}:{msg.compid}");
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
            {
                var gp = (MAVLink.mavlink_global_position_int_t)msg.data;
                s.gpsLatDeg = gp.lat / 1e7;
                s.gpsLonDeg = gp.lon / 1e7;
                s.gpsRelAltM = gp.relative_alt / 1000f;

                if (logRxPosition)
                    Debug.Log($"[S{slotIndex}][RX] GLOBAL lat={s.gpsLatDeg:F7} lon={s.gpsLonDeg:F7} relAlt={s.gpsRelAltM:F1}m");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.LOCAL_POSITION_NED:
            {
                var lp = (MAVLink.mavlink_local_position_ned_t)msg.data;
                s.localNed = new Vector3(lp.x, lp.y, lp.z);
                s.localUnity = new Vector3(lp.x, lp.y, -lp.z);

                if (logRxPosition)
                    Debug.Log($"[S{slotIndex}][RX] LOCAL NED N={lp.x:F2} E={lp.y:F2} D={lp.z:F2} (Unity up={-lp.z:F2})");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.VFR_HUD:
            {
                var v = (MAVLink.mavlink_vfr_hud_t)msg.data;
                s.groundspeedMps = v.groundspeed;
                s.airspeedMps = v.airspeed;
                s.headingDeg = v.heading;

                if (logRxPosition)
                    Debug.Log($"[S{slotIndex}][RX] VFR gs={s.groundspeedMps:F1} as={s.airspeedMps:F1} hdg={s.headingDeg:F0} alt={v.alt:F1}");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.ATTITUDE:
            {
                var a = (MAVLink.mavlink_attitude_t)msg.data;
                s.rollDeg = a.roll * Mathf.Rad2Deg;
                s.pitchDeg = a.pitch * Mathf.Rad2Deg;
                s.yawDeg = a.yaw * Mathf.Rad2Deg;
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.SCALED_PRESSURE:
            {
                var p = (MAVLink.mavlink_scaled_pressure_t)msg.data;
                s.pressureHpa = p.press_abs;
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.WIND:
            {
                var w = (MAVLink.mavlink_wind_t)msg.data;
                s.windDirDeg = w.direction;
                s.windSpeedMps = w.speed;
                s.windSpeedZ = w.speed_z;
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.STATUSTEXT:
            {
                try
                {
                    var st = (MAVLink.mavlink_statustext_t)msg.data;
                    string text = BytesToNullTerminatedAscii(st.text);
                    Debug.Log($"[S{slotIndex}][RX] STATUSTEXT: {text}");
                }
                catch { }
                break;
            }
        }
    }

    private static string BytesToNullTerminatedAscii(byte[] bytes)
    {
        if (bytes == null) return string.Empty;
        int len = 0;
        while (len < bytes.Length && bytes[len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, len);
    }

    // ============================ PACKET BUILDER (REFLECTION) ============================

    private void DumpPacketBuildersOnce()
    {
        if (_dumpedPacketBuilders) return;
        _dumpedPacketBuilders = true;

        try
        {
            var methods = typeof(MAVLink.MavlinkParse).GetMethods(BindingFlags.Instance | BindingFlags.Public);
            Debug.Log("[MAV] MavlinkParse public methods returning byte[] (possible packet builders):");
            foreach (var m in methods)
            {
                if (m.ReturnType != typeof(byte[])) continue;
                var ps = m.GetParameters();
                var sig = new System.Text.StringBuilder();
                sig.Append(m.Name).Append("(");
                for (int i = 0; i < ps.Length; i++)
                {
                    if (i > 0) sig.Append(", ");
                    sig.Append(ps[i].ParameterType.Name).Append(" ").Append(ps[i].Name);
                }
                sig.Append(")");
                Debug.Log("[MAV]   " + sig);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[MAV] Failed to dump packet builder methods: " + e.Message);
        }
    }

    private byte[] BuildPacket(MAVLink.MavlinkParse parser, ref byte seq, MAVLink.MAVLINK_MSG_ID id, object payload, bool preferV2, out byte usedSeq)
    {
        usedSeq = seq;

        if (TryBuildPacket(parser, ref seq, id, payload, preferV2, out var pkt, out usedSeq))
            return pkt;

        if (preferV2 && TryBuildPacket(parser, ref seq, id, payload, false, out pkt, out usedSeq))
        {
            if (!_warnedMavlink2Fallback)
            {
                _warnedMavlink2Fallback = true;
                Debug.LogWarning("[MAV] MAVLink2 packet builder not found; falling back to MAVLink1.");
            }
            return pkt;
        }

        DumpPacketBuildersOnce();
        Debug.LogError("[MAV] No compatible packet builder overload found in this MAVLink library fork.");
        return null;
    }

    private bool TryBuildPacket(MAVLink.MavlinkParse parser, ref byte seq, MAVLink.MAVLINK_MSG_ID id, object payload, bool mavlink2, out byte[] packet, out byte usedSeq)
    {
        packet = null;
        usedSeq = seq;

        var methods = typeof(MAVLink.MavlinkParse).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        if (TryBuildByNameHints(parser, methods, ref seq, id, payload, mavlink2, out packet, out usedSeq))
            return true;

        foreach (var m in methods)
        {
            if (m.ReturnType != typeof(byte[])) continue;
            if (!LooksLikePacketBuilder(m)) continue;

            if (TryInvokePacketBuilder(parser, m, ref seq, id, payload, mavlink2, out packet, out usedSeq))
                return true;
        }

        return false;
    }

    private bool TryBuildByNameHints(MAVLink.MavlinkParse parser, MethodInfo[] methods, ref byte seq, MAVLink.MAVLINK_MSG_ID id, object payload, bool mavlink2, out byte[] packet, out byte usedSeq)
    {
        packet = null;
        usedSeq = seq;

        string[] preferredNames = mavlink2
            ? new[] { "GenerateMAVLinkPacket20", "GenerateMAVLinkPacket2", "GenerateMavlinkPacket20", "GenerateMavlinkPacket2", "GenerateMAVLinkPacket" }
            : new[] { "GenerateMAVLinkPacket10", "GenerateMAVLinkPacket1", "GenerateMavlinkPacket10", "GenerateMavlinkPacket1", "GenerateMAVLinkPacket" };

        foreach (var name in preferredNames)
        {
            foreach (var m in methods)
            {
                if (m.ReturnType != typeof(byte[])) continue;
                if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                if (TryInvokePacketBuilder(parser, m, ref seq, id, payload, mavlink2, out packet, out usedSeq))
                    return true;
            }
        }

        return false;
    }

    private bool LooksLikePacketBuilder(MethodInfo m)
    {
        var p = m.GetParameters();
        if (p.Length < 2) return false;

        bool okMsgId = (p[0].ParameterType == typeof(MAVLink.MAVLINK_MSG_ID))
            || (p[0].ParameterType == typeof(byte))
            || (p[0].ParameterType == typeof(ushort))
            || (p[0].ParameterType == typeof(int));

        if (!okMsgId) return false;

        return true;
    }

    private bool TryInvokePacketBuilder(MAVLink.MavlinkParse parser, MethodInfo m, ref byte seq, MAVLink.MAVLINK_MSG_ID id, object payload, bool mavlink2, out byte[] packet, out byte usedSeq)
    {
        packet = null;
        usedSeq = seq;

        var p = m.GetParameters();
        var args = new object[p.Length];

        // --- param 0: msgid ---
        if (p[0].ParameterType == typeof(MAVLink.MAVLINK_MSG_ID))
            args[0] = id;
        else if (p[0].ParameterType == typeof(byte))
            args[0] = (byte)((int)id & 0xFF);
        else if (p[0].ParameterType == typeof(ushort))
            args[0] = (ushort)((int)id & 0xFFFF);
        else if (p[0].ParameterType == typeof(int))
            args[0] = (int)id;
        else
            return false;

        // --- param 1: payload ---
        if (p[1].ParameterType == typeof(object) || p[1].ParameterType.IsInstanceOfType(payload))
            args[1] = payload;
        else
            return false;

        // NOTE: can't capture ref param (seq) in lambdas/local functions.
        bool assignedSeq = false;
        byte usedSeqLocal = seq;

        for (int i = 2; i < p.Length; i++)
        {
            var t = p[i].ParameterType;
            string n = (p[i].Name ?? string.Empty).ToLowerInvariant();

            if (t == typeof(bool))
            {
                if (n.Contains("mavlink2") || n.Contains("v2")) args[i] = mavlink2;
                else args[i] = false;
                continue;
            }

            if (t == typeof(byte))
            {
                if (n.Contains("sys")) args[i] = gcsSysId;
                else if (n.Contains("comp")) args[i] = gcsCompId;
                else if (n.Contains("seq"))
                {
                    byte cur = NextSeq8(ref seq);
                    args[i] = cur;
                    usedSeqLocal = cur;
                    assignedSeq = true;
                }
                else args[i] = (byte)0;
                continue;
            }

            if (t == typeof(ushort))
            {
                if (n.Contains("seq"))
                {
                    byte cur = NextSeq8(ref seq);
                    args[i] = (ushort)cur;
                    usedSeqLocal = cur;
                    assignedSeq = true;
                }
                else args[i] = (ushort)0;
                continue;
            }

            if (t == typeof(int))
            {
                if (n.Contains("seq"))
                {
                    byte cur = NextSeq8(ref seq);
                    args[i] = (int)cur;
                    usedSeqLocal = cur;
                    assignedSeq = true;
                }
                else args[i] = 0;
                continue;
            }

            // Unknown type -> fail this method
            return false;
        }

        // If we didn't match 'seq' by name, try a few common overload shapes.
        if (!assignedSeq)
        {
            int tail = p.Length - 2;

            // Pattern A: sysid, compid, seq
            if (tail == 3 && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte))
            {
                args[2] = gcsSysId;
                args[3] = gcsCompId;

                byte cur = NextSeq8(ref seq);
                if (p[4].ParameterType == typeof(byte)) args[4] = cur;
                else if (p[4].ParameterType == typeof(ushort)) args[4] = (ushort)cur;
                else if (p[4].ParameterType == typeof(int)) args[4] = (int)cur;
                else return false;

                usedSeqLocal = cur;
                assignedSeq = true;
            }
            // Pattern B: sysid, compid, incompat, compat, seq
            else if (tail == 5 && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte))
            {
                args[2] = gcsSysId;
                args[3] = gcsCompId;
                args[4] = (byte)0;
                args[5] = (byte)0;

                byte cur = NextSeq8(ref seq);
                if (p[6].ParameterType == typeof(byte)) args[6] = cur;
                else if (p[6].ParameterType == typeof(ushort)) args[6] = (ushort)cur;
                else if (p[6].ParameterType == typeof(int)) args[6] = (int)cur;
                else return false;

                usedSeqLocal = cur;
                assignedSeq = true;
            }
            // Pattern C: sign, sysid, compid, seq
            else if (tail == 4 && p[2].ParameterType == typeof(bool) && p[3].ParameterType == typeof(byte) && p[4].ParameterType == typeof(byte))
            {
                args[2] = false;
                args[3] = gcsSysId;
                args[4] = gcsCompId;

                byte cur = NextSeq8(ref seq);
                if (p[5].ParameterType == typeof(byte)) args[5] = cur;
                else if (p[5].ParameterType == typeof(ushort)) args[5] = (ushort)cur;
                else if (p[5].ParameterType == typeof(int)) args[5] = (int)cur;
                else return false;

                usedSeqLocal = cur;
                assignedSeq = true;
            }
            // Pattern D: sysid, compid, seq, sign
            else if (tail == 4 && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte) && p[p.Length - 1].ParameterType == typeof(bool))
            {
                args[2] = gcsSysId;
                args[3] = gcsCompId;

                byte cur = NextSeq8(ref seq);
                if (p[4].ParameterType == typeof(byte)) args[4] = cur;
                else if (p[4].ParameterType == typeof(ushort)) args[4] = (ushort)cur;
                else if (p[4].ParameterType == typeof(int)) args[4] = (int)cur;
                else return false;

                args[p.Length - 1] = false;

                usedSeqLocal = cur;
                assignedSeq = true;
            }
        }

        try
        {
            if (assignedSeq) usedSeq = usedSeqLocal;
            packet = (byte[])m.Invoke(parser, args);
            return packet != null && packet.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ============================ CONSTANTS ============================

    private static byte NextSeq8(ref byte seqRef)
    {
        byte cur = seqRef;
        unchecked { seqRef++; }
        return cur;
    }

    private static class MavCmd
    {
        public const ushort DO_SET_MODE = 176;
        public const ushort COMPONENT_ARM_DISARM = 400;
        public const ushort NAV_LAND = 21;
        public const ushort NAV_TAKEOFF = 22;
        public const ushort SET_MESSAGE_INTERVAL = 511;
    }
}

// MavLinkUdpListener.cs
// Minimal ArduCopter MAVLink GCS client for Unity.
// Transport: UDP (default) or TCP.
//
// UDP notes (recommended for SITL):
// - Configure SITL/MAVProxy to send to Unity listenPort, e.g. --out udp:127.0.0.1:14551
// - If your MAVProxy uses an ephemeral source port, enable replyToLastSender so commands go back to the correct port.
//
// TCP notes:
// - Unity connects as a TCP client to tcpHost:tcpPort.
// - On the SITL side you must expose a TCP server endpoint (often called tcpin).
//
// Features:
// - RX: HEARTBEAT / COMMAND_ACK / STATUSTEXT / GLOBAL_POSITION_INT / LOCAL_POSITION_NED / VFR_HUD
// - TX: GCS HEARTBEAT, (optional) REQUEST_DATA_STREAM, ARM/DISARM, GUIDED mode, TAKEOFF, LAND
// - GUIDED navigation to 3 local NED setpoints (SET_POSITION_TARGET_LOCAL_NED) resent ~10Hz
// - Packet build uses reflection to survive different MissionPlanner MAVLink C# generator overloads.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class MavLinkUdpListener : MonoBehaviour
{
    public enum Transport
    {
        UDP = 0,
        TCP = 1
    }

    [Header("Transport")]
    public Transport transport = Transport.UDP;

    [Header("UDP")]
    public int listenPort = 14551;               // Unity listens here (SITL --out udp:127.0.0.1:14551)
    public string targetIp = "127.0.0.1";
    public int targetPort = 14550;               // Default commands target (SITL --out udp:127.0.0.1:14550)

    [Tooltip("If true, send ALL outgoing packets to the UDP endpoint we last received from (QGC-style). Required for SITL because MAVProxy often uses an ephemeral source port. Recommended = true.")]
    public bool replyToLastSender = true;

    [Header("TCP")]
    [Tooltip("Unity connects as a TCP client to this host.")]
    public string tcpHost = "127.0.0.1";

    [Tooltip("TCP port on the autopilot/SITL side (Unity dials this).")]
    public int tcpPort = 5760;

    [Tooltip("Auto-reconnect if TCP link drops.")]
    public bool tcpAutoReconnect = true;

    [Tooltip("Reconnect delay in seconds.")]
    public float tcpReconnectDelay = 1.0f;

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

    [Header("SITL helpers")]
    [Tooltip("Unity serializes inspector values. If you changed defaults in code but your scene has old values, this will override them at runtime (recommended for SITL/debug).")]
    public bool forceSitlDefaults = true;

    [Tooltip("Log the first UDP datagram source endpoint (helps confirm correct TX port).")]
    public bool logFirstRxDatagram = true;

    [Tooltip("Legacy ArduPilot method (deprecated in MAVLink2). Keep false unless you know you need it.")]
    public bool requestStreamsOnStart = false;
    public int requestStreamRateHz = 5;

    [Header("Telemetry (recommended)")]
    [Tooltip("QGC-style: send MAV_CMD_SET_MESSAGE_INTERVAL for key telemetry messages.")]
    public bool autoConfigureTelemetry = true;
    public int rateGlobalPosHz = 5;
    public int rateLocalPosHz = 10;
    public int rateVfrHudHz = 5;

    [Header("Debug")]
    public bool logTx = true;
    public bool logRxHeartbeat = true;
    public bool logRxAck = true;

    [Tooltip("Print position telemetry to Console (can be spammy).")]
    public bool logRxPosition = true;

    [Tooltip("Show a small HUD with latest telemetry in the Game view.")]
    public bool showOnScreenHud = true;

    // ============================ TELEMETRY (public, updated from RX) ============================

    [Header("Live Telemetry (read-only)")]
    public double gpsLatDeg;
    public double gpsLonDeg;
    public float gpsRelAltM;

    public Vector3 localNed;         // N,E,D (meters)
    public Vector3 localUnity;       // N,E,Up (meters) convenient for Unity world

    public float groundspeedMps;
    public float airspeedMps;
    public float headingDeg;

    public long lastRxAgeMs;         // updated each frame (how old last RX is)

    // ============================ INTERNAL STATE ============================

    private MAVLink.MavlinkParse _parser;

    // UDP
    private UdpClient _udp;
    private IPEndPoint _defaultTx;
    private volatile IPEndPoint _lastSender;
    private bool _loggedFirstRx;

    // TCP
    private TcpClient _tcp;
    private NetworkStream _tcpStream;
    private volatile bool _tcpConnected;
    private readonly byte[] _tcpReadTmp = new byte[8192];
    private readonly List<byte> _tcpRxBuffer = new List<byte>(16384);

    // Threads
    private Thread _rxThread;
    private volatile bool _running;

    private readonly ConcurrentQueue<MAVLink.MAVLinkMessage> _rxQueue = new ConcurrentQueue<MAVLink.MAVLinkMessage>();

    // TX builder diagnostics
    private bool _dumpedPacketBuilders;
    private bool _warnedMavlink2Fallback;

    private byte _seq;

    // Discovered vehicle
    private byte _vehSysId = 1;
    private byte _vehCompId = 1;

    // Mode/state
    private byte _baseMode;
    private uint _customMode;
    private bool _armed;
    private bool _guided; // ArduCopter GUIDED = custom_mode 4

    // Guided setpoint stream
    private Vector3 _activeTargetNed;
    private bool _hasTarget;
    private long _lastSetpointMs;

    // Ping (optional)
    private uint _pingSeq = 1;
    private readonly Dictionary<uint, ulong> _pingSentUsec = new Dictionary<uint, ulong>();

    // Link status (thread-safe monotonic clock)
    private static readonly Stopwatch _monoClock = Stopwatch.StartNew();
    private static long NowMs() => _monoClock.ElapsedMilliseconds;
    private long _lastRxMs;

    // Telemetry setup
    private bool _telemetryConfigured;

    private string CurrentTxLabel()
    {
        if (transport == Transport.TCP)
            return _tcpConnected ? $"tcp://{tcpHost}:{tcpPort}" : $"tcp://{tcpHost}:{tcpPort} (disconnected)";

        var ep = (replyToLastSender && _lastSender != null) ? _lastSender : _defaultTx;
        return ep == null ? "(none)" : $"{ep.Address}:{ep.Port}";
    }

    // ============================ UNITY ============================

    void Start()
    {
        _parser = new MAVLink.MavlinkParse();

        if (forceSitlDefaults)
        {
            replyToLastSender = true;
            autoConfigureTelemetry = true;
            requestStreamsOnStart = false;
        }

        _running = true;

        if (transport == Transport.UDP)
        {
            _udp = new UdpClient(listenPort);
            _defaultTx = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);

            _rxThread = new Thread(RxLoopUdp) { IsBackground = true };
            _rxThread.Start();

            Debug.Log($"[MAV] UDP started. RX={listenPort} defaultTX={targetIp}:{targetPort} replyToLastSender={replyToLastSender} autoTelemetry={autoConfigureTelemetry} forceSitlDefaults={forceSitlDefaults}");
        }
        else
        {
            _rxThread = new Thread(RxLoopTcp) { IsBackground = true };
            _rxThread.Start();

            Debug.Log($"[MAV] TCP starting. Will connect to {tcpHost}:{tcpPort} autoReconnect={tcpAutoReconnect}");
        }

        InvokeRepeating(nameof(SendGcsHeartbeat), 0f, 1f);

        DumpPacketBuildersOnce();

        if (requestStreamsOnStart)
            Invoke(nameof(RequestStreams), 1.0f);

        if (autoConfigureTelemetry)
            Invoke(nameof(ConfigureTelemetry), 1.2f);
    }

    void OnDestroy()
    {
        _running = false;

        try { _udp?.Close(); } catch { }

        try
        {
            _tcpStream?.Close();
            _tcp?.Close();
        }
        catch { }

        try
        {
            if (_rxThread != null && _rxThread.IsAlive)
                _rxThread.Join(300);
        }
        catch { }

        _udp = null;
        _tcpStream = null;
        _tcp = null;
        _rxThread = null;
    }

    void Update()
    {
        lastRxAgeMs = NowMs() - _lastRxMs;

        while (_rxQueue.TryDequeue(out var msg))
            HandleMessage(msg);

        // Ensure telemetry gets configured as soon as link is usable.
        if (autoConfigureTelemetry && !_telemetryConfigured)
        {
            if (transport == Transport.TCP)
            {
                if (_tcpConnected) ConfigureTelemetry();
            }
            else
            {
                if (!replyToLastSender || _lastSender != null) ConfigureTelemetry();
            }
        }

        // Resend GUIDED target ~10Hz (required for smooth offboard control)
        if (_hasTarget && _guided)
        {
            long now = NowMs();
            if (now - _lastSetpointMs >= 100)
            {
                SendLocalSetpoint(_activeTargetNed);
                _lastSetpointMs = now;
            }
        }
    }

    void OnGUI()
    {
        if (!showOnScreenHud) return;

        GUILayout.BeginArea(new Rect(10, 10, 460, 170), GUI.skin.box);
        GUILayout.Label($"MAVLink link: {(IsLinkAlive() ? "OK" : "NO RX")}  age={lastRxAgeMs} ms");
        GUILayout.Label($"Transport: {transport}  TX -> {CurrentTxLabel()}  (replyToLastSender={replyToLastSender})");
        GUILayout.Label($"Vehicle: sys={_vehSysId} comp={_vehCompId}  armed={_armed} guided={_guided}  base={_baseMode} custom={_customMode}");
        GUILayout.Label($"GPS: lat={gpsLatDeg:F7} lon={gpsLonDeg:F7} relAlt={gpsRelAltM:F1} m");
        GUILayout.Label($"LOCAL: N={localNed.x:F2}  E={localNed.y:F2}  D={localNed.z:F2}  (Unity up={localUnity.z:F2})");
        GUILayout.Label($"VFR: gs={groundspeedMps:F1} m/s  as={airspeedMps:F1} m/s  hdg={headingDeg:F0}°");
        GUILayout.EndArea();
    }

    // ============================ UI CALLBACKS ============================

    public bool IsLinkAlive(float timeoutSeconds = 3f)
    {
        long ageMs = NowMs() - _lastRxMs;
        return ageMs <= (long)(timeoutSeconds * 1000f);
    }

    public void Ping()
    {
        // NOTE: Not all stacks respond to MAVLink PING. Use HEARTBEAT/ACK to judge link health.
        ulong nowUsec = UnixEpochUsec();
        uint seq = _pingSeq++;

        var ping = new MAVLink.mavlink_ping_t
        {
            time_usec = nowUsec,
            seq = seq,
            target_system = 0,
            target_component = 0
        };

        _pingSentUsec[seq] = nowUsec;
        if (!SendMessage(MAVLink.MAVLINK_MSG_ID.PING, ping))
            Debug.LogWarning("[UI] PING not sent (packet builder mismatch or link down)");
        Debug.Log($"[UI] PING sent seq={seq}");
    }

    public void SetModeGuided()
    {
        // ArduCopter GUIDED mode = 4 (custom_mode)
        SendCommandLong(MavCmd.DO_SET_MODE, p1: 1f, p2: 4f);
        Debug.Log("[UI] SetMode GUIDED");
    }

    public void Arm()
    {
        SendCommandLong(MavCmd.COMPONENT_ARM_DISARM, p1: 1f);
        Debug.Log("[UI] ARM");
    }

    public void Disarm()
    {
        SendCommandLong(MavCmd.COMPONENT_ARM_DISARM, p1: 0f);
        Debug.Log("[UI] DISARM");
    }

    public void Takeoff()
    {
        SendCommandLong(MavCmd.NAV_TAKEOFF, p7: 5f);
        Debug.Log("[UI] TAKEOFF alt=5m");
    }

    public void Land()
    {
        SendCommandLong(MavCmd.NAV_LAND);
        Debug.Log("[UI] LAND");
    }

    public void FlyPointA() => FlyToLocal(pointA);
    public void FlyPointB() => FlyToLocal(pointB);
    public void FlyPointC() => FlyToLocal(pointC);

    private void FlyToLocal(Vector3 ned)
    {
        _activeTargetNed = ned;
        _hasTarget = true;
        _lastSetpointMs = 0;

        SendLocalSetpoint(ned);

        if (!_guided)
            Debug.LogWarning($"[GUIDED] Not active yet (base_mode={_baseMode}, custom_mode={_customMode}). Still sending setpoint.");

        Debug.Log($"[GUIDED] Target NED: N={ned.x:F1} E={ned.y:F1} D={ned.z:F1}");
    }

    // ============================ MAVLINK TX ============================

    private void SendGcsHeartbeat()
    {
        var hb = new MAVLink.mavlink_heartbeat_t
        {
            type = (byte)MAVLink.MAV_TYPE.GCS,
            autopilot = (byte)MAVLink.MAV_AUTOPILOT.INVALID,
            base_mode = 0,
            custom_mode = 0,
            system_status = (byte)MAVLink.MAV_STATE.ACTIVE
        };

        bool ok = SendMessage(MAVLink.MAVLINK_MSG_ID.HEARTBEAT, hb);
        if (ok && logTx) Debug.Log("[TX] HEARTBEAT (GCS)");
    }

    private void RequestStreams()
    {
        var req = new MAVLink.mavlink_request_data_stream_t
        {
            target_system = _vehSysId,
            target_component = _vehCompId,
            req_stream_id = (byte)MAVLink.MAV_DATA_STREAM.ALL,
            req_message_rate = (ushort)Mathf.Clamp(requestStreamRateHz, 1, 50),
            start_stop = 1
        };

        bool ok = SendMessage(MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM, req);
        if (ok)
            Debug.Log($"[GCS] REQUEST_DATA_STREAM ALL rate={requestStreamRateHz}Hz");
        else
            Debug.LogWarning("[GCS] REQUEST_DATA_STREAM not sent (packet builder mismatch)");
    }

    private void ConfigureTelemetry()
    {
        if (_telemetryConfigured) return;

        if (transport == Transport.UDP)
        {
            if (replyToLastSender && _lastSender == null)
            {
                Debug.Log("[GCS] Waiting for RX endpoint before configuring telemetry...");
                Invoke(nameof(ConfigureTelemetry), 0.5f);
                return;
            }
        }
        else
        {
            if (!_tcpConnected)
            {
                Debug.Log("[GCS] Waiting for TCP connect before configuring telemetry...");
                Invoke(nameof(ConfigureTelemetry), 0.5f);
                return;
            }
        }

        _telemetryConfigured = true;

        SetMessageInterval(MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, rateGlobalPosHz);
        SetMessageInterval(MAVLink.MAVLINK_MSG_ID.LOCAL_POSITION_NED, rateLocalPosHz);
        SetMessageInterval(MAVLink.MAVLINK_MSG_ID.VFR_HUD, rateVfrHudHz);

        Debug.Log($"[GCS] Telemetry configured: GLOBAL={rateGlobalPosHz}Hz LOCAL={rateLocalPosHz}Hz VFR={rateVfrHudHz}Hz");
    }

    private void SetMessageInterval(MAVLink.MAVLINK_MSG_ID msgId, int rateHz)
    {
        if (rateHz <= 0) rateHz = 1;
        float intervalUs = 1_000_000f / rateHz;

        SendCommandLong(MavCmd.SET_MESSAGE_INTERVAL,
            p1: (float)(int)msgId,
            p2: intervalUs,
            p3: 0f);

        if (logTx)
            Debug.Log($"[TX] SET_MESSAGE_INTERVAL msg={(int)msgId} ({msgId}) interval={intervalUs:F0}us (~{rateHz}Hz)");
    }

    private void SendCommandLong(ushort command,
        float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        byte ts = (_vehSysId != 0) ? _vehSysId : (byte)0;
        byte tc = (_vehCompId != 0) ? _vehCompId : (byte)0;

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

        SendMessage(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        if (logTx) Debug.Log($"[TX] COMMAND_LONG cmd={command} target={ts}:{tc}");
    }

    private void SendLocalSetpoint(Vector3 ned)
    {
        var sp = new MAVLink.mavlink_set_position_target_local_ned_t
        {
            time_boot_ms = (uint)(Time.time * 1000),
            target_system = _vehSysId,
            target_component = _vehCompId,
            coordinate_frame = (byte)MAVLink.MAV_FRAME.LOCAL_NED,

            type_mask = (ushort)(
                (1 << 3) | (1 << 4) | (1 << 5) |
                (1 << 6) | (1 << 7) | (1 << 8) |
                (1 << 10) | (1 << 11)
            ),

            x = ned.x,
            y = ned.y,
            z = ned.z
        };

        SendMessage(MAVLink.MAVLINK_MSG_ID.SET_POSITION_TARGET_LOCAL_NED, sp);
        if (logTx) Debug.Log($"[TX] SET_POSITION_TARGET_LOCAL_NED N={ned.x:F1} E={ned.y:F1} D={ned.z:F1}");
    }

    private bool SendMessage(MAVLink.MAVLINK_MSG_ID id, object payload)
    {
        byte[] packet = BuildPacket(id, payload, preferMavlink2);
        if (packet == null || packet.Length == 0)
        {
            Debug.LogWarning($"[TX] Failed to build packet for {id}");
            return false;
        }

        if (transport == Transport.TCP)
        {
            if (!_tcpConnected || _tcpStream == null)
            {
                if (logTx) Debug.LogWarning($"[TX] TCP not connected. Dropping {id}.");
                return false;
            }

            try
            {
                _tcpStream.Write(packet, 0, packet.Length);
                _tcpStream.Flush();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[TCP] Send error: {e.Message}");
                _tcpConnected = false;
                return false;
            }

            if (logTx)
                Debug.Log($"[TX] {id} bytes={packet.Length} seq={(byte)(_seq - 1)}");

            return true;
        }

        IPEndPoint dst = _defaultTx;
        if (replyToLastSender)
        {
            if (_lastSender != null) dst = _lastSender;
            else
                Debug.LogWarning($"[TX] No RX endpoint learned yet. Using default TX {targetIp}:{targetPort}. (If commands don't work, wait for RX or keep replyToLastSender=true.)");
        }

        if (logTx)
            Debug.Log($"[TX] dst={dst.Address}:{dst.Port}");

        try
        {
            _udp.Send(packet, packet.Length, dst);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UDP] Send error: {e.Message}");
            return false;
        }

        if (logTx)
            Debug.Log($"[TX] {id} bytes={packet.Length} seq={(byte)(_seq - 1)}");

        return true;
    }

    // ============================ MAVLINK RX ============================

    private void RxLoopUdp()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                var learned = new IPEndPoint(ep.Address, ep.Port);
                bool senderChanged = (_lastSender == null) || !_lastSender.Equals(learned);

                _lastSender = learned;
                _lastRxMs = NowMs();

                if (logFirstRxDatagram && !_loggedFirstRx)
                {
                    _loggedFirstRx = true;
                    Debug.Log($"[RX] First datagram from {learned.Address}:{learned.Port} len={data.Length}");
                }

                if (senderChanged && replyToLastSender)
                    Debug.Log($"[MAV] Learned TX endpoint from RX: {_lastSender.Address}:{_lastSender.Port}");

                ParseDatagram(data);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException se)
            {
                if (_running)
                    Debug.LogWarning($"[UDP] RX SocketException: {se.SocketErrorCode}");
            }
            catch (Exception e)
            {
                if (_running)
                    Debug.LogWarning($"[UDP] RX error: {e.Message}");
            }
        }
    }

    private void RxLoopTcp()
    {
        while (_running)
        {
            if (!_tcpConnected)
            {
                TryConnectTcp();

                if (!_tcpConnected)
                {
                    if (!tcpAutoReconnect) return;
                    Thread.Sleep((int)(Mathf.Max(0.2f, tcpReconnectDelay) * 1000f));
                    continue;
                }
            }

            try
            {
                int n = _tcpStream.Read(_tcpReadTmp, 0, _tcpReadTmp.Length);
                if (n <= 0)
                {
                    _tcpConnected = false;
                    continue;
                }

                _lastRxMs = NowMs();

                lock (_tcpRxBuffer)
                {
                    for (int i = 0; i < n; i++)
                        _tcpRxBuffer.Add(_tcpReadTmp[i]);

                    ParseStreamBuffer(_tcpRxBuffer);
                }
            }
            catch (IOException)
            {
                _tcpConnected = false;
            }
            catch (ObjectDisposedException)
            {
                _tcpConnected = false;
            }
            catch (Exception e)
            {
                if (_running)
                    Debug.LogWarning($"[TCP] RX error: {e.Message}");
                _tcpConnected = false;
            }

            if (!_tcpConnected)
            {
                try { _tcpStream?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
                _tcpStream = null;
                _tcp = null;
            }
        }
    }

    private void TryConnectTcp()
    {
        try
        {
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.Connect(tcpHost, tcpPort);
            _tcpStream = _tcp.GetStream();
            _tcpConnected = true;
            Debug.Log($"[MAV] TCP connected to {tcpHost}:{tcpPort}");
        }
        catch (Exception e)
        {
            _tcpConnected = false;
            if (_running)
                Debug.LogWarning($"[MAV] TCP connect failed: {e.Message}");
        }
    }

    private void ParseDatagram(byte[] data)
    {
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b != 0xFE && b != 0xFD) { i++; continue; }

            using (var ms = new MemoryStream(data, i, data.Length - i, false))
            {
                var msg = _parser.ReadPacket(ms);
                if (msg != null)
                {
                    _rxQueue.Enqueue(msg);
                    i += (int)ms.Position;
                    continue;
                }
            }

            i++;
        }
    }

    private void ParseStreamBuffer(List<byte> buf)
    {
        if (buf.Count == 0) return;

        // Copy to array for fast scanning; remove consumed bytes afterwards.
        byte[] arr = buf.ToArray();
        int i = 0;

        while (i < arr.Length)
        {
            byte b = arr[i];
            if (b != 0xFE && b != 0xFD) { i++; continue; }

            if (arr.Length - i < 8) break;

            using (var ms = new MemoryStream(arr, i, arr.Length - i, false))
            {
                var msg = _parser.ReadPacket(ms);
                if (msg != null)
                {
                    _rxQueue.Enqueue(msg);
                    i += (int)ms.Position;
                    continue;
                }
            }

            i++;
        }

        if (i > 0)
            buf.RemoveRange(0, Mathf.Min(i, buf.Count));

        // Safety: keep buffer bounded if stream is noisy.
        if (buf.Count > 200000)
            buf.RemoveRange(0, buf.Count - 50000);
    }

    private void HandleMessage(MAVLink.MAVLinkMessage msg)
    {
        // Ignore own loopback
        if (msg.sysid == gcsSysId && msg.compid == gcsCompId)
            return;

        switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
        {
            case MAVLink.MAVLINK_MSG_ID.HEARTBEAT:
            {
                _vehSysId = msg.sysid;
                _vehCompId = msg.compid;

                var hb = (MAVLink.mavlink_heartbeat_t)msg.data;
                _baseMode = hb.base_mode;
                _customMode = hb.custom_mode;

                _armed = (_baseMode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                _guided = ((_baseMode & (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED) != 0) && (_customMode == 4u);

                if (logRxHeartbeat)
                    Debug.Log($"[RX] HEARTBEAT sys={_vehSysId} comp={_vehCompId} base={_baseMode} custom={_customMode} armed={_armed} guided={_guided}");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.COMMAND_ACK:
            {
                var ack = (MAVLink.mavlink_command_ack_t)msg.data;
                if (logRxAck)
                    Debug.Log($"[RX] COMMAND_ACK cmd={ack.command} result={ack.result} from {msg.sysid}:{msg.compid}");
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.PING:
            {
                var p = (MAVLink.mavlink_ping_t)msg.data;
                if (_pingSentUsec.TryGetValue(p.seq, out var sentUsec))
                {
                    ulong nowUsec = UnixEpochUsec();
                    double rttMs = (nowUsec - sentUsec) / 1000.0;
                    _pingSentUsec.Remove(p.seq);
                    Debug.Log($"[RX] PING response seq={p.seq} RTT={rttMs:F1}ms from {msg.sysid}:{msg.compid}");
                }
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
            {
                var gp = (MAVLink.mavlink_global_position_int_t)msg.data;

                gpsLatDeg = gp.lat / 1e7;
                gpsLonDeg = gp.lon / 1e7;
                gpsRelAltM = gp.relative_alt / 1000f;

                if (logRxPosition)
                    Debug.Log($"[RX] GLOBAL lat={gpsLatDeg:F7} lon={gpsLonDeg:F7} relAlt={gpsRelAltM:F1}m");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.LOCAL_POSITION_NED:
            {
                var lp = (MAVLink.mavlink_local_position_ned_t)msg.data;

                localNed = new Vector3(lp.x, lp.y, lp.z);
                localUnity = new Vector3(lp.x, lp.y, -lp.z);

                if (logRxPosition)
                    Debug.Log($"[RX] LOCAL NED N={lp.x:F2} E={lp.y:F2} D={lp.z:F2}  (Unity up={-lp.z:F2})");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.VFR_HUD:
            {
                var v = (MAVLink.mavlink_vfr_hud_t)msg.data;
                groundspeedMps = v.groundspeed;
                airspeedMps = v.airspeed;
                headingDeg = v.heading;

                if (logRxPosition)
                    Debug.Log($"[RX] VFR gs={groundspeedMps:F1}m/s as={airspeedMps:F1}m/s hdg={headingDeg:F0}° alt={v.alt:F1}m");

                break;
            }

            case MAVLink.MAVLINK_MSG_ID.STATUSTEXT:
            {
                try
                {
                    var st = (MAVLink.mavlink_statustext_t)msg.data;
                    string text = BytesToNullTerminatedAscii(st.text);
                    Debug.Log($"[RX] STATUSTEXT: {text}");
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

    private byte[] BuildPacket(MAVLink.MAVLINK_MSG_ID id, object payload, bool preferV2)
    {
        if (TryBuildPacket(id, payload, preferV2, out var pkt))
            return pkt;

        if (preferV2 && TryBuildPacket(id, payload, false, out pkt))
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

    private bool TryBuildPacket(MAVLink.MAVLINK_MSG_ID id, object payload, bool mavlink2, out byte[] packet)
    {
        packet = null;

        var methods = typeof(MAVLink.MavlinkParse).GetMethods(BindingFlags.Instance | BindingFlags.Public);

        if (TryBuildByNameHints(methods, id, payload, mavlink2, out packet))
            return true;

        foreach (var m in methods)
        {
            if (m.ReturnType != typeof(byte[])) continue;
            if (!LooksLikePacketBuilder(m)) continue;

            if (TryInvokePacketBuilder(m, id, payload, mavlink2, out packet))
                return true;
        }

        return false;
    }

    private bool TryBuildByNameHints(MethodInfo[] methods, MAVLink.MAVLINK_MSG_ID id, object payload, bool mavlink2, out byte[] packet)
    {
        packet = null;

        string[] preferredNames = mavlink2
            ? new[] { "GenerateMAVLinkPacket20", "GenerateMAVLinkPacket2", "GenerateMavlinkPacket20", "GenerateMavlinkPacket2", "GenerateMAVLinkPacket" }
            : new[] { "GenerateMAVLinkPacket10", "GenerateMAVLinkPacket1", "GenerateMavlinkPacket10", "GenerateMavlinkPacket1", "GenerateMAVLinkPacket" };

        foreach (var name in preferredNames)
        {
            foreach (var m in methods)
            {
                if (m.ReturnType != typeof(byte[])) continue;
                if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                if (TryInvokePacketBuilder(m, id, payload, mavlink2, out packet))
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

    private bool TryInvokePacketBuilder(MethodInfo m, MAVLink.MAVLINK_MSG_ID id, object payload, bool mavlink2, out byte[] packet)
    {
        packet = null;

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

        Func<byte> nextSeq8 = () => _seq++;
        Func<ushort> nextSeq16 = () => (ushort)(_seq++);
        Func<int> nextSeq32 = () => (int)(_seq++);

        bool assignedSeq = false;
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
                else if (n.Contains("seq")) { args[i] = nextSeq8(); assignedSeq = true; }
                else args[i] = (byte)0;
                continue;
            }

            if (t == typeof(ushort))
            {
                if (n.Contains("seq")) { args[i] = nextSeq16(); assignedSeq = true; }
                else args[i] = (ushort)0;
                continue;
            }

            if (t == typeof(int))
            {
                if (n.Contains("seq")) { args[i] = nextSeq32(); assignedSeq = true; }
                else args[i] = 0;
                continue;
            }

            // Unknown type -> fail this method
            return false;
        }

        if (!assignedSeq)
        {
            int tail = p.Length - 2;

            // Pattern A: sysid, compid, seq
            if (tail == 3 && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte))
            {
                args[2] = gcsSysId;
                args[3] = gcsCompId;
                args[4] = (p[4].ParameterType == typeof(byte)) ? (object)nextSeq8()
                    : (p[4].ParameterType == typeof(ushort)) ? (object)nextSeq16()
                    : (p[4].ParameterType == typeof(int)) ? (object)nextSeq32()
                    : null;
                if (args[4] == null) return false;
            }
            // Pattern B: sysid, compid, incompat, compat, seq
            else if (tail == 5 && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte))
            {
                args[2] = gcsSysId;
                args[3] = gcsCompId;
                args[4] = (byte)0;
                args[5] = (byte)0;
                args[6] = (p[6].ParameterType == typeof(byte)) ? (object)nextSeq8()
                    : (p[6].ParameterType == typeof(ushort)) ? (object)nextSeq16()
                    : (p[6].ParameterType == typeof(int)) ? (object)nextSeq32()
                    : null;
                if (args[6] == null) return false;
            }
            // Pattern C: sign, sysid, compid, seq
            else if (tail == 4 && p[2].ParameterType == typeof(bool) && p[3].ParameterType == typeof(byte) && p[4].ParameterType == typeof(byte))
            {
                args[2] = false;
                args[3] = gcsSysId;
                args[4] = gcsCompId;
                args[5] = (p[5].ParameterType == typeof(byte)) ? (object)nextSeq8()
                    : (p[5].ParameterType == typeof(ushort)) ? (object)nextSeq16()
                    : (p[5].ParameterType == typeof(int)) ? (object)nextSeq32()
                    : null;
                if (args[5] == null) return false;
            }
            // Pattern D: sysid, compid, seq, sign
            else if (tail == 4 && p[2].ParameterType == typeof(byte) && p[3].ParameterType == typeof(byte) && p[p.Length - 1].ParameterType == typeof(bool))
            {
                args[2] = gcsSysId;
                args[3] = gcsCompId;
                args[4] = (p[4].ParameterType == typeof(byte)) ? (object)nextSeq8()
                    : (p[4].ParameterType == typeof(ushort)) ? (object)nextSeq16()
                    : (p[4].ParameterType == typeof(int)) ? (object)nextSeq32()
                    : null;
                args[p.Length - 1] = false;
                if (args[4] == null) return false;
            }
        }

        try
        {
            packet = (byte[])m.Invoke(_parser, args);
            return packet != null && packet.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ============================ CONSTANTS ============================

    private static class MavCmd
    {
        public const ushort DO_SET_MODE = 176;
        public const ushort COMPONENT_ARM_DISARM = 400;
        public const ushort NAV_LAND = 21;
        public const ushort NAV_TAKEOFF = 22;
        public const ushort SET_MESSAGE_INTERVAL = 511;
    }

    private static ulong UnixEpochUsec()
    {
        return (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
    }
}

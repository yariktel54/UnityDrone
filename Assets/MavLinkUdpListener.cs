// UPDATED VERSION with button debug + correct GUIDED detection
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;

public class MavLinkUdpListener : MonoBehaviour
{
    [Header("UDP")]
    public int listenPort = 14551;
    public string targetIp = "127.0.0.1";
    public int targetPort = 14550;

    [Header("GUIDED points (LOCAL NED, meters)")]
    public Vector3 pointA = new Vector3(5, 0, -5);
    public Vector3 pointB = new Vector3(0, 5, -5);
    public Vector3 pointC = new Vector3(-5, 0, -5);

    private UdpClient _udp;
    private IPEndPoint _txEp;
    private Thread _rxThread;
    private volatile bool _running;

    private MAVLink.MavlinkParse _parser;
    private ConcurrentQueue<MAVLink.MAVLinkMessage> _queue = new ConcurrentQueue<MAVLink.MAVLinkMessage>();

    // GCS identity
    private byte gcsSysId = 253; // НЕ 255: щоб уникнути GCS-конфліктів навіть без QGC
    private byte gcsCompId = 190;
    private int seq = 0;

    // Vehicle state
    private byte vehicleSysId = 1;
    private byte vehicleCompId = 1;
    private bool guidedActive = false;
    private uint customMode = 0;
    private byte baseMode = 0;

    // GUIDED streaming
    private float lastSetpointTime = 0f;
    private Vector3 activeTarget;
    private bool hasActiveTarget = false;

    void Start()
    {
        Debug.Log("MavLinkUdpListener START");

        _parser = new MAVLink.MavlinkParse();
        _udp = new UdpClient(listenPort);
        _txEp = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);

        _running = true;
        _rxThread = new Thread(RecvLoop) { IsBackground = true };
        _rxThread.Start();

        Debug.Log($"MAVLink UDP RX:{listenPort} TX:{targetPort}");
    }

    void OnDestroy()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
        try { _rxThread?.Join(300); } catch { }
    }

    void Update()
    {
        while (_queue.TryDequeue(out var msg))
            HandleMessage(msg);

        // resend guided setpoint at ~10 Hz
        if (hasActiveTarget && guidedActive)
        {
            if (Time.time - lastSetpointTime > 0.1f)
            {
                SendPositionSetpoint();
                lastSetpointTime = Time.time;
            }
        }
    }

    // ================== UI CALLBACKS ==================

    public void Ping()
    {
        ulong t = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        var ping = new MAVLink.mavlink_ping_t
        {
            time_usec = t,
            seq = (uint)seq,
            target_system = 0,
            target_component = 0
        };
        Debug.Log($"[UI] Ping pressed seq={seq}");
        SendPacket(MAVLink.MAVLINK_MSG_ID.PING, ping);
    }


    public void SetModeGuided()
    {
        Debug.Log("[UI] SetModeGuided pressed");

        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = vehicleSysId,
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE,
            param1 = (float)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED,
            param2 = 4 // GUIDED (Copter)
        };
        SendCommand(cmd);
    }

    public void Arm()
    {
        Debug.Log("[UI] Arm pressed");

        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = vehicleSysId,
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            param1 = 1
        };
        SendCommand(cmd);
    }

    public void Takeoff()
    {
        Debug.Log("[UI] Takeoff pressed");

        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = vehicleSysId,
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.TAKEOFF,
            param7 = 5
        };
        SendCommand(cmd);
    }

    public void Land()
    {
        Debug.Log("[UI] Land pressed");

        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = vehicleSysId,
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.LAND
        };
        SendCommand(cmd);
    }

    public void FlyPointA()
    {
        Debug.Log("[UI] FlyPointA pressed");
        FlyToLocal(pointA);
    }

    public void FlyPointB()
    {
        Debug.Log("[UI] FlyPointB pressed");
        FlyToLocal(pointB);
    }

    public void FlyPointC()
    {
        Debug.Log("[UI] FlyPointC pressed");
        FlyToLocal(pointC);
    }

    // ================== GUIDED NAV ==================

    void FlyToLocal(Vector3 ned)
    {
        if (!guidedActive)
        {
            Debug.LogWarning($"GUIDED not active yet (base_mode={baseMode}, custom_mode={customMode})");
            return;
        }

        activeTarget = ned;
        hasActiveTarget = true;
        SendPositionSetpoint();

        Debug.Log($"[GUIDED] FlyToLocal x={ned.x:F1} y={ned.y:F1} z={ned.z:F1}");
    }

    // ================== MAVLINK SEND ==================

    void SendCommand(MAVLink.mavlink_command_long_t cmd)
    {
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
    }

    void SendPositionSetpoint()
    {
        var pos = new MAVLink.mavlink_set_position_target_local_ned_t
        {
            time_boot_ms = (uint)(Time.time * 1000),
            target_system = vehicleSysId,
            target_component = 0,
            coordinate_frame = (byte)MAVLink.MAV_FRAME.LOCAL_OFFSET_NED,

            type_mask = (ushort)(
                (1 << 3) | (1 << 4) | (1 << 5) |
                (1 << 6) | (1 << 7) | (1 << 8) |
                (1 << 10) | (1 << 11)
            ),

            x = activeTarget.x,
            y = activeTarget.y,
            z = activeTarget.z
        };

        SendPacket(MAVLink.MAVLINK_MSG_ID.SET_POSITION_TARGET_LOCAL_NED, pos);

        Debug.Log($"[GUIDED STREAM] x={activeTarget.x:F1} y={activeTarget.y:F1} z={activeTarget.z:F1}");
    }

    void SendPacket(MAVLink.MAVLINK_MSG_ID id, object payload)
    {
        byte[] packet = _parser.GenerateMAVLinkPacket10(
            id,
            payload,
            gcsSysId,
            gcsCompId,
            seq++
        );

        Debug.Log($"[TX] msg={id} len={packet.Length} seq={seq - 1} sys={gcsSysId} comp={gcsCompId}");
        _udp.Send(packet, packet.Length, _txEp);
    }

    // ================== RX ==================

    private void RecvLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                // Robust resync parser: scan for MAVLink1 STX (0xFE) and parse packets one by one.
                // This avoids dropping whole datagrams if there is any non-0xFE content or MAVLink2 frames (0xFD).
                int i = 0;
                while (i < data.Length)
                {
                    byte b = data[i];

                    // Optional: detect MAVLink2 frames for debugging
                    if (b == 0xFD)
                    {
                        // We can't decode MAVLink2 with this trimmed MissionPlanner parser.
                        // Skip one byte and continue scanning for 0xFE.
                        // (If you see this often, we must add MAVLink2 decode or force MAVLink1 output on SITL.)
                        // Debug.Log("[RX] Detected MAVLink2 STX 0xFD (skipping)");
                        i++;
                        continue;
                    }

                    if (b != 0xFE)
                    {
                        i++;
                        continue;
                    }

                    // Try to parse a MAVLink1 frame starting at i
                    using (var ms = new MemoryStream(data, i, data.Length - i, false))
                    {
                        var msg = _parser.ReadPacket(ms);
                        if (msg != null)
                        {
                            _queue.Enqueue(msg);
                            i += (int)ms.Position; // advance by consumed bytes
                            continue;
                        }
                    }

                    // If parse failed, advance one byte and resync
                    i++;
                }
            }
            catch { }
        }
    }

    private void HandleMessage(MAVLink.MAVLinkMessage msg)
    {
        vehicleSysId = msg.sysid;
        vehicleCompId = msg.compid;

        switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
        {
            case MAVLink.MAVLINK_MSG_ID.HEARTBEAT:
            {
                var hb = (MAVLink.mavlink_heartbeat_t)msg.data;
                baseMode = hb.base_mode;
                customMode = hb.custom_mode;

                bool armed = (baseMode & (byte)MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;

                guidedActive =
                    ((baseMode & (byte)MAVLink.MAV_MODE_FLAG.CUSTOM_MODE_ENABLED) != 0)
                    && customMode == 4;

                Debug.Log($"HEARTBEAT base={baseMode} custom={customMode} armed={armed} guided={guidedActive}");
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.COMMAND_ACK:
            {
                var ack = (MAVLink.mavlink_command_ack_t)msg.data;
                Debug.Log($"[ACK] cmd={(MAVLink.MAV_CMD)ack.command} result={(MAVLink.MAV_RESULT)ack.result}");
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
            {
                var gp = (MAVLink.mavlink_global_position_int_t)msg.data;
                double lat = gp.lat / 1e7;
                double lon = gp.lon / 1e7;
                float alt = gp.relative_alt / 1000f;
                Debug.Log($"GLOBAL lat={lat:F6} lon={lon:F6} alt={alt:F1}m");
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.PING:
            {
                var p = (MAVLink.mavlink_ping_t)msg.data;
                Debug.Log($"[RX PING] from sys={msg.sysid} comp={msg.compid} seq={p.seq} time_usec={p.time_usec}");
                break;
            }

            case MAVLink.MAVLINK_MSG_ID.LOCAL_POSITION_NED:
            {
                var lp = (MAVLink.mavlink_local_position_ned_t)msg.data;
                float x = lp.x;
                float y = lp.y;
                float z = -lp.z;
                Debug.Log($"LOCAL NED x={x:F2} y={y:F2} z={z:F2}");
                break;
            }
        }
    }
}

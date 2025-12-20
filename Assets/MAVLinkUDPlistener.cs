using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Concurrent;
using System.IO;
using UnityEngine;

public class MAVLinkUDPlistener : MonoBehaviour
{
    public int listenPort = 14551;

    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;

    private MAVLink.MavlinkParse _parser;
    private ConcurrentQueue<MAVLink.MAVLinkMessage> _queue =
        new ConcurrentQueue<MAVLink.MAVLinkMessage>();

    void Start()
    {
        _parser = new MAVLink.MavlinkParse();

        _udp = new UdpClient(listenPort);
        _udp.Client.ReceiveTimeout = 1000;

        _running = true;
        _rxThread = new Thread(RecvLoop);
        _rxThread.IsBackground = true;
        _rxThread.Start();

        Debug.Log($"MAVLink UDP listener started on port {listenPort}");
    }

    void OnDestroy()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
        try { _rxThread?.Join(300); } catch { }
    }

    private void RecvLoop()
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref ep);

                using (var ms = new MemoryStream(data))
                {
                    while (ms.Position < ms.Length)
                    {
                        MAVLink.MAVLinkMessage msg = _parser.ReadPacket(ms);
                        if (msg != null)
                            _queue.Enqueue(msg);
                        else
                            break;
                    }
                }
            }
            catch (SocketException) { }
            catch (Exception) { }
        }
    }

    void Update()
    {
        while (_queue.TryDequeue(out var msg))
        {
            HandleMessage(msg);
        }
    }

    private void HandleMessage(MAVLink.MAVLinkMessage msg)
    {
        switch ((MAVLink.MAVLINK_MSG_ID)msg.msgid)
        {
            case MAVLink.MAVLINK_MSG_ID.HEARTBEAT:
            {
                var hb = (MAVLink.mavlink_heartbeat_t)msg.data;
                Debug.Log(
                    $"HEARTBEAT sys={msg.sysid} mode={hb.base_mode} status={hb.system_status}"
                );
                break;
            }

            // ====== ПОВОРОТ ======
            case MAVLink.MAVLINK_MSG_ID.ATTITUDE:
            {
                var att = (MAVLink.mavlink_attitude_t)msg.data;

                float rollDeg  = att.roll  * Mathf.Rad2Deg;
                float pitchDeg = att.pitch * Mathf.Rad2Deg;
                float yawDeg   = att.yaw   * Mathf.Rad2Deg;

                Debug.Log(
                    $"ATTITUDE roll={rollDeg:F1}° pitch={pitchDeg:F1}° yaw={yawDeg:F1}°"
                );
                break;
            }

            // ====== ГЛОБАЛЬНА ПОЗИЦІЯ ======
            case MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT:
            {
                var gp = (MAVLink.mavlink_global_position_int_t)msg.data;

                double lat = gp.lat / 1e7;
                double lon = gp.lon / 1e7;
                float altRel = gp.relative_alt / 1000f; // мм → м

                Debug.Log(
                    $"GLOBAL POS lat={lat:F6} lon={lon:F6} alt={altRel:F1} m"
                );
                break;
            }

            // ====== ЛОКАЛЬНА ПОЗИЦІЯ (NED) ======
            case MAVLink.MAVLINK_MSG_ID.LOCAL_POSITION_NED:
            {
                var lp = (MAVLink.mavlink_local_position_ned_t)msg.data;

                float x = lp.x;
                float y = lp.y;
                float z = -lp.z; // NED → Unity (вгору +)

                Debug.Log(
                    $"LOCAL POS x={x:F2} y={y:F2} z={z:F2}"
                );
                break;
            }
        }
    }

}

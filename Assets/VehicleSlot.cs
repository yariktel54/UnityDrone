using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class VehicleSlot : MonoBehaviour
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

    [Tooltip("If replyToLastSender=true but no RX yet (udpLastSender==null): allow sending to defaultTargetIp:defaultTargetPort anyway.")]
    public bool allowFallbackBeforeRx = false;


    [Header("TCP (per-slot)")]
    public string tcpHost = "127.0.0.1";
    public int tcpPort = 6000;
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

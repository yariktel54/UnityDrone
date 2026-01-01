using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    // =========================
    // SITL .parm generation
    // =========================

    [Header("SITL .parm generation (per-slot)")]
    [Tooltip("If true, this slot will generate a .parm file on Awake() (only if linkType != None).")]
    public bool generateParmOnStart = true;

    [Tooltip("Generate also in build (not only in Unity Editor).")]
    public bool generateParmInBuild = false;

    [Tooltip("Folder in project root where .parm files are written.")]
    public string parmFolderInProjectRoot = "SITLParms";

    [Tooltip("Optional explicit file name. If empty -> auto name based on slot + sysid.")]
    public string parmFileNameOverride = "";

    [Header("SITL identity")]
    [Tooltip("SYSID_THISMAV value for this vehicle (must be unique across vehicles).")]
    [Range(1, 255)]
    public int sitlSysId = 1;

    [Tooltip("Optional: if true, slot number is derived from sibling index (Vehicle_1..Vehicle_10).")]
    public bool autoSlotNumberFromSibling = true;

    [Tooltip("Used only if autoSlotNumberFromSibling=false.")]
    public int slotNumber = 1;

    // =========================
    // "Drone characteristics" (simple)
    // =========================

    [Header("SITL Drone characteristics (simple)")]
    [Tooltip("Write frame type params (FRAME_CLASS / FRAME_TYPE) into .parm")]
    public bool writeFrameParams = true;

    [Tooltip("FRAME_CLASS (Copter). Typical: 1=Quad, 2=Hexa, 3=Octa ...")]
    public int frameClass = 1; // Quad

    [Tooltip("FRAME_TYPE (Copter). Typical: 1=X, 0=Plus (depends on ArduCopter)")]
    public int frameType = 1;  // X

    [Tooltip("Write battery sim params (SIM_BATT_*) into .parm")]
    public bool writeBatterySimParams = true;

    [Tooltip("SIM_BATT_CAP_AH (Ah). Example: 5Ah = 5000mAh")]
    public float simBattCapacityAh = 5.2f; // 5200 mAh

    [Tooltip("SIM_BATT_VOLTAGE (V). Example: 16.8V for 4S full, 12.6V for 3S full")]
    public float simBattVoltage = 16.8f;   // 4S full

    // =========================
    // Environment
    // =========================

    [Header("SITL Wind (SIM_WIND_*)")]
    public bool sitlWindEnabled = true;

    [Tooltip("SIM_WIND_SPD (m/s)")]
    public float sitlWindSpeedMps = 2.0f;

    [Tooltip("SIM_WIND_DIR (deg)")]
    public float sitlWindDirDeg = 90.0f;

    [Tooltip("SIM_WIND_TURB")]
    public float sitlWindTurb = 0.3f;

    [Header("SITL GPS jamming")]
    [Tooltip("If true -> SIM_GPS_DISABLE 1 (GPS disabled/jammed).")]
    public bool sitlGpsJammed = false;

    [Serializable]
    public struct ParmKV
    {
        public bool enabled;
        public string name;
        public float value;
    }

    [Header("Extra params (optional)")]
    [Tooltip("Any additional params you want in the .parm file (NAME VALUE).")]
    public List<ParmKV> extraParams = new List<ParmKV>();

    // =========================
    // Runtime (read-only) - with explicit defaults
    // NOTE: these are just initial values for HUD before first telemetry.
    // =========================

    [Header("Runtime (read-only)")]
    public byte vehSysId = 1;
    public byte vehCompId = 1;

    public byte baseMode = 0;
    public uint customMode = 0;

    public bool armed = false;
    public bool guided = false;

    // Default HUD position: Boryspil (UKBB)
    public double gpsLatDeg = 50.3450;
    public double gpsLonDeg = 30.8947;
    public float gpsRelAltM = 130.0f;

    public Vector3 localNed = Vector3.zero;
    public Vector3 localUnity = Vector3.zero;

    public float groundspeedMps = 0.0f;
    public float airspeedMps = 0.0f;
    public float headingDeg = 90.0f;

    public float rollDeg = 0.0f;
    public float pitchDeg = 0.0f;
    public float yawDeg = 90.0f;

    public float pressureHpa = 1013.25f;

    // Match HUD wind with SITL wind defaults
    public float windSpeedMps = 2.0f;
    public float windDirDeg = 90.0f;
    public float windSpeedZ = 0.0f;

    public long lastRxAgeMs = 0;

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

    [NonSerialized] public byte seq = 0;
    [NonSerialized] public long lastRxMs = 0;
    [NonSerialized] public bool telemetryConfigured = false;

    // Guided streaming state
    [NonSerialized] public Vector3 activeTargetNed = Vector3.zero;
    [NonSerialized] public bool hasTarget = false;
    [NonSerialized] public long lastSetpointMs = 0;

    // Ping tracking (optional)
    [NonSerialized] public uint pingSeq = 1;
    [NonSerialized] public readonly Dictionary<uint, ulong> pingSentUsec = new Dictionary<uint, ulong>();

    public bool Enabled => linkType != LinkType.None;

    private void Awake()
    {
        if (!generateParmOnStart) return;
        if (!Enabled) return; // якщо слот None — файл не генеримо
        if (!Application.isEditor && !generateParmInBuild) return;

        try
        {
            string path = GenerateParmFile();
            Debug.Log($"[SITL] Wrote .parm for '{name}' -> {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SITL] Failed to generate .parm for '{name}': {ex}");
        }
    }

    public int GetEffectiveSlotNumber()
    {
        if (autoSlotNumberFromSibling)
            return transform.GetSiblingIndex() + 1;
        return Mathf.Max(1, slotNumber);
    }

    public string GetParmOutputDirectory()
    {
        // Project root = parent of Assets
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.Combine(projectRoot, parmFolderInProjectRoot);
    }

    public string GetParmFileName()
    {
        if (!string.IsNullOrWhiteSpace(parmFileNameOverride))
            return parmFileNameOverride.Trim();

        int sn = GetEffectiveSlotNumber();
        return $"drone_slot{sn}_sysid{sitlSysId}.parm";
    }

    public string GenerateParmFile()
    {
        string dir = GetParmOutputDirectory();
        Directory.CreateDirectory(dir);

        string filePath = Path.Combine(dir, GetParmFileName());

        using (var sw = new StreamWriter(filePath, false))
        {
            sw.NewLine = "\n";

            sw.WriteLine("# Auto-generated by Unity VehicleSlot");
            sw.WriteLine($"# GameObject: {name}");
            sw.WriteLine($"# Label: {label}");
            sw.WriteLine($"# LinkType: {linkType}");
            sw.WriteLine($"# Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sw.WriteLine("");

            // Identity
            sw.WriteLine($"SYSID_THISMAV {sitlSysId}");

            // Drone characteristics (simple)
            if (writeFrameParams)
            {
                sw.WriteLine($"FRAME_CLASS {frameClass}");
                sw.WriteLine($"FRAME_TYPE {frameType}");
            }

            if (writeBatterySimParams)
            {
                sw.WriteLine($"SIM_BATT_CAP_AH {simBattCapacityAh.ToString(CultureInfo.InvariantCulture)}");
                sw.WriteLine($"SIM_BATT_VOLTAGE {simBattVoltage.ToString(CultureInfo.InvariantCulture)}");
            }

            // Wind
            if (sitlWindEnabled)
            {
                sw.WriteLine($"SIM_WIND_SPD {sitlWindSpeedMps.ToString(CultureInfo.InvariantCulture)}");
                sw.WriteLine($"SIM_WIND_DIR {sitlWindDirDeg.ToString(CultureInfo.InvariantCulture)}");
                sw.WriteLine($"SIM_WIND_TURB {sitlWindTurb.ToString(CultureInfo.InvariantCulture)}");
            }

            // GPS jam
            sw.WriteLine($"SIM_GPS_DISABLE {(sitlGpsJammed ? 1 : 0)}");

            // Extra params
            if (extraParams != null && extraParams.Count > 0)
            {
                sw.WriteLine("");
                sw.WriteLine("# Extra params");
                foreach (var p in extraParams)
                {
                    if (!p.enabled) continue;
                    if (string.IsNullOrWhiteSpace(p.name)) continue;

                    string pname = p.name.Trim();
                    sw.WriteLine($"{pname} {p.value.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        return filePath;
    }

#if UNITY_EDITOR
    [ContextMenu("SITL/Generate .parm now")]
    private void EditorGenerateParmNow()
    {
        if (!Enabled)
        {
            Debug.LogWarning($"[SITL] Slot '{name}' is None -> no .parm generated.");
            return;
        }

        string path = GenerateParmFile();
        Debug.Log($"[SITL] Wrote .parm for '{name}' -> {path}");
    }
#endif
}

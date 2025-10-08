using HidSharp;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace dieukhientayps5
{
    #region Enums
    public enum CiA402State
    {
        NotReadyToSwitchOn = 0, SwitchOnDisabled = 1, ReadyToSwitchOn = 2,
        SwitchedOn = 3, OperationEnabled = 4, QuickStopActive = 5,
        FaultReactionActive = 6, Fault = 7
    }

    public enum OperationMode : sbyte
    {
        ProfilePosition = 1, VelocityMode = 2, ProfileVelocity = 3,
        Homing = 6, CyclicSynchronousPosition = 8,
        CyclicSynchronousVelocity = 9, CyclicSynchronousTorque = 10
    }
    #endregion

    #region PDO Data Structures
    public struct TPDO1Data
    {
        public ushort StatusWord;
        public int ActualPosition;
        public short ActualTorque;

        public TPDO1Data(byte[] data)
        {
            StatusWord = 0; ActualPosition = 0; ActualTorque = 0;
            if (data.Length >= 2) StatusWord = (ushort)(data[0] | data[1] << 8);
            if (data.Length >= 6) ActualPosition = BitConverter.ToInt32(data, 2);
            if (data.Length >= 8) ActualTorque = BitConverter.ToInt16(data, 6);
        }

        public readonly bool IsOperationEnabled() => (StatusWord & 0x6F) == 0x27;
        public readonly bool HasFault() => (StatusWord & 0x08) != 0;
    }

    public struct TPDO2Data
    {
        public int ActualVelocity;
        public byte ModesOfOperationDisplay;

        public TPDO2Data(byte[] data)
        {
            if (data.Length >= 5)
            {
                ActualVelocity = BitConverter.ToInt32(data, 0);
                ModesOfOperationDisplay = data[4];
            }
            else { ActualVelocity = 0; ModesOfOperationDisplay = 0; }
        }
    }
    #endregion

    #region PS5 Controller
    public class PS5Controller
    {
        private HidDevice? device;
        private HidStream? stream;
        private volatile bool isRunning = false;

        private const int PS5_VENDOR_ID = 0x054C;
        private const int PS5_PRODUCT_ID = 0x0CE6;

        public struct ControllerState
        {
            public byte LeftStickX;
            public byte LeftStickY;
            public byte RightStickX;
            public byte RightStickY;
            public byte L2Trigger;
            public byte R2Trigger;
            public bool Square;
            public bool X;
            public bool Circle;
            public bool Triangle;
            public bool L1;
            public bool R1;
            public bool Share;
            public bool Options;
            public bool PS;
        }

        public event Action<ControllerState>? OnControllerUpdate;

        public bool Connect()
        {
            try
            {
                var devices = DeviceList.Local.GetHidDevices(PS5_VENDOR_ID, PS5_PRODUCT_ID);
                device = devices.FirstOrDefault();

                if (device == null)
                {
                    Console.WriteLine("[ERROR] Khong tim thay PS5 Controller!");
                    return false;
                }

                Console.WriteLine($"[OK] Da tim thay: {device.GetProductName()}");

                if (device.TryOpen(out stream))
                {
                    Console.WriteLine("[OK] Ket noi thanh cong voi PS5 Controller!");
                    return true;
                }
                else
                {
                    Console.WriteLine("[ERROR] Khong the mo ket noi. Thu chay voi sudo.");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Loi ket noi: {ex.Message}");
                return false;
            }
        }

        public void StartReading()
        {
            if (stream == null) return;

            isRunning = true;
            var readThread = new Thread(ReadControllerData) { IsBackground = true };
            readThread.Start();
        }

        private void ReadControllerData()
        {
            byte[] buffer = new byte[64];

            while (isRunning)
            {
                try
                {
                    int bytesRead = stream!.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        var state = ParseControllerData(buffer);
                        OnControllerUpdate?.Invoke(state);
                    }
                    Thread.Sleep(1);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Loi doc du lieu: {ex.Message}");
                    break;
                }
            }
        }

        private static ControllerState ParseControllerData(byte[] data)
        {
            var state = new ControllerState();
            if (data.Length < 10) return state;

            byte rawLX = data[1];
            byte rawLY = data[2];
            byte rawRX = data[3];
            byte rawRY = data[4];

            state.LeftStickX = rawLX;
            state.LeftStickY = rawLY;
            state.RightStickX = rawRX;
            state.RightStickY = rawRY;

            // ===== KHÓA TRỤC KHÔNG DÙNG =====
            // Vô hiệu hóa X joystick trái (chỉ dùng Y trái cho tiến/lùi)
            state.LeftStickX = 127;   // hoặc 128 tùy tay cầm của bạn

            // Vô hiệu hóa Y joystick phải (chỉ dùng X phải cho quay)
            state.RightStickY = 127;  // hoặc 128

            // =================================

            // Triggers
            state.L2Trigger = data[5];
            state.R2Trigger = data[6];

            // Nút mặt
            byte faceButtons = data[8];
            state.Square = (faceButtons & 0x10) != 0;
            state.X = (faceButtons & 0x20) != 0;
            state.Circle = (faceButtons & 0x40) != 0;
            state.Triangle = (faceButtons & 0x80) != 0;

            // Shoulder + Share/Options
            if (data.Length > 9)
            {
                byte shoulderButtons = data[9];
                state.L1 = (shoulderButtons & 0x01) != 0;
                state.R1 = (shoulderButtons & 0x02) != 0;
                state.Share = (shoulderButtons & 0x10) != 0;
                state.Options = (shoulderButtons & 0x20) != 0;
            }

            // PS button
            if (data.Length > 10)
            {
                state.PS = (data[10] & 0x01) != 0;
            }

            return state;
        }


        public void Stop()
        {
            isRunning = false;
            stream?.Close();
        }
    }
    #endregion

    #region UbuntuCANInterface - With PDO Support
    public class UbuntuCANInterface
    {
        private string canInterface = "";
        private bool isConnected = false;
        private readonly object sdoLock = new();
        private readonly object nodeLock = new();
        private Process? monitorProcess;
        private readonly ConcurrentQueue<string> canFrames = new();
        private volatile bool isMonitoring = false;

        private const int MAX_QUEUE_SIZE = 500;

        // PDO tracking per node
        private readonly ConcurrentDictionary<byte, TPDO1Data> latestTPDO1 = new();
        private readonly ConcurrentDictionary<byte, TPDO2Data> latestTPDO2 = new();
        private readonly ConcurrentDictionary<byte, DateTime> lastTPDO1Update = new();
        private readonly ConcurrentDictionary<byte, DateTime> lastTPDO2Update = new();

        public event EventHandler<string>? CANFrameReceived;
        public event EventHandler<(byte nodeId, TPDO1Data data)>? TPDO1Received;
        public event EventHandler<(byte nodeId, TPDO2Data data)>? TPDO2Received;

        public string GetInterfaceName() => canInterface;

        public TPDO1Data GetLatestTPDO1(byte nodeId) =>
            latestTPDO1.TryGetValue(nodeId, out var data) ? data : new TPDO1Data();

        public TPDO2Data GetLatestTPDO2(byte nodeId) =>
            latestTPDO2.TryGetValue(nodeId, out var data) ? data : new TPDO2Data();

        public bool Connect(string interfaceName, int baudrate)
        {
            try
            {
                canInterface = interfaceName;
                Console.WriteLine($"Thiết lập CAN {interfaceName} với baudrate {baudrate}");

                ExecuteCommand($"sudo ip link set {interfaceName} down");
                ExecuteCommand($"sudo ip link set {interfaceName} type can bitrate {baudrate}");
                var result = ExecuteCommand($"sudo ip link set {interfaceName} up");

                if (result.Contains("error") || result.Contains("Error"))
                {
                    Console.WriteLine($"[ERROR] Loi thiet lap CAN: {result}");
                    return false;
                }

                var status = ExecuteCommand($"ip -details link show {interfaceName}");
                if (status.Contains("UP") && status.Contains("can"))
                {
                    isConnected = true;
                    Console.WriteLine($"[OK] Ket noi thanh cong {interfaceName}");
                    StartCANMonitoring();
                    Thread.Sleep(200);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Loi ket noi CAN: {ex.Message}");
                return false;
            }
        }

        public bool SendNMT(byte command, byte targetNodeId)
        {
            if (!isConnected) return false;
            try
            {
                uint cobId = 0x000;
                byte[] data = [command, targetNodeId];
                string frameData = BitConverter.ToString(data).Replace("-", "");
                ExecuteCommand($"cansend {canInterface} {cobId:X3}#{frameData}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Loi SendNMT: {ex.Message}");
                return false;
            }
        }

        // RPDO2: Target Velocity (32-bit) + Mode of Operation (8-bit)
        public bool SendRPDO2(byte nodeId, int targetVelocity, sbyte modesOfOperation)
        {
            if (!isConnected) return false;
            try
            {
                uint cobId = (uint)(0x300 + nodeId);
                byte[] data = new byte[5];
                byte[] velBytes = BitConverter.GetBytes(targetVelocity);
                Array.Copy(velBytes, 0, data, 0, 4);
                data[4] = (byte)modesOfOperation;
                string frameData = BitConverter.ToString(data).Replace("-", "");
                ExecuteCommand($"cansend {canInterface} {cobId:X3}#{frameData}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Loi SendRPDO2: {ex.Message}");
                return false;
            }
        }

        private void StartCANMonitoring()
        {
            if (isMonitoring) return;
            isMonitoring = true;
            Task.Run(() =>
            {
                try
                {
                    monitorProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "candump",
                            Arguments = $"{canInterface}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true
                        }
                    };
                    monitorProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data) && isMonitoring)
                        {
                            while (canFrames.Count > MAX_QUEUE_SIZE)
                                canFrames.TryDequeue(out _);

                            canFrames.Enqueue(e.Data);
                            CANFrameReceived?.Invoke(this, e.Data);
                            ProcessPDOMessage(e.Data);
                        }
                    };
                    monitorProcess.Start();
                    monitorProcess.BeginOutputReadLine();
                    monitorProcess.WaitForExit();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Loi monitor CAN: {ex.Message}");
                }
            });
        }

        private void ProcessPDOMessage(string candumpLine)
        {
            if (!TryParseCandumpLine(candumpLine, out uint cobId, out string dataHex))
                return;

            // Detect node ID from COB-ID
            // TPDO1: 0x180 + nodeId
            // TPDO2: 0x280 + nodeId
            if (cobId >= 0x180 && cobId <= 0x1FF)
            {
                byte nodeId = (byte)(cobId - 0x180);
                byte[] data = HexStringToByteArray(dataHex);
                var tpdo1 = new TPDO1Data(data);
                latestTPDO1[nodeId] = tpdo1;
                lastTPDO1Update[nodeId] = DateTime.Now;
                TPDO1Received?.Invoke(this, (nodeId, tpdo1));
            }
            else if (cobId >= 0x280 && cobId <= 0x2FF)
            {
                byte nodeId = (byte)(cobId - 0x280);
                byte[] data = HexStringToByteArray(dataHex);
                var tpdo2 = new TPDO2Data(data);
                latestTPDO2[nodeId] = tpdo2;
                lastTPDO2Update[nodeId] = DateTime.Now;
                TPDO2Received?.Invoke(this, (nodeId, tpdo2));
            }
        }

        private static byte[] HexStringToByteArray(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return [];
            int length = hex.Length / 2;
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public void Disconnect()
        {
            if (isConnected)
            {
                isMonitoring = false;
                try
                {
                    monitorProcess?.Kill();
                    monitorProcess?.Dispose();
                }
                catch { }
                ExecuteCommand($"sudo ip link set {canInterface} down");
                isConnected = false;
                Console.WriteLine("[OK] Ngat ket noi CAN");
            }
        }

        public bool WriteSDO(byte nodeId, ushort index, byte subindex, uint data, byte dataSize)
        {
            if (!isConnected) return false;
            lock (nodeLock)
            {
                lock (sdoLock)
                {
                    try
                    {
                        while (canFrames.TryDequeue(out _)) { }

                        byte command = dataSize switch
                        {
                            1 => 0x2F,
                            2 => 0x2B,
                            4 => 0x23,
                            _ => throw new ArgumentException($"Kích thước không hỗ trợ: {dataSize}")
                        };

                        string frameData = $"{command:X2}{index & 0xFF:X2}{index >> 8 & 0xFF:X2}{subindex:X2}";
                        frameData += $"{data & 0xFF:X2}{data >> 8 & 0xFF:X2}{data >> 16 & 0xFF:X2}{data >> 24 & 0xFF:X2}";
                        uint cobId = (uint)(0x600 + nodeId);
                        ExecuteCommand($"cansend {canInterface} {cobId:X3}#{frameData}");
                        return WaitForSDOResponse((uint)(0x580 + nodeId), true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Loi WriteSDO Node {nodeId}: {ex.Message}");
                        return false;
                    }
                }
            }
        }

        public uint ReadSDO(byte nodeId, ushort index, byte subindex)
        {
            if (!isConnected) return 0;
            lock (nodeLock)
            {
                lock (sdoLock)
                {
                    try
                    {
                        while (canFrames.TryDequeue(out _)) { }

                        string frameData = $"40{index & 0xFF:X2}{index >> 8 & 0xFF:X2}{subindex:X2}00000000";
                        uint cobId = (uint)(0x600 + nodeId);
                        ExecuteCommand($"cansend {canInterface} {cobId:X3}#{frameData}");
                        return WaitForSDOReadResponse((uint)(0x580 + nodeId));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Loi ReadSDO Node {nodeId}: {ex.Message}");
                        return 0;
                    }
                }
            }
        }

        private static bool TryParseCandumpLine(string line, out uint cobId, out string dataHex)
        {
            cobId = 0; dataHex = "";
            var idMatch = Regex.Match(line, @"\b([0-9A-Fa-f]{3})\b");
            if (!idMatch.Success) return false;
            string idStr = idMatch.Groups[1].Value;
            if (!uint.TryParse(idStr, System.Globalization.NumberStyles.HexNumber, null, out cobId))
                return false;
            var dataMatches = Regex.Matches(line, @"\b([0-9A-Fa-f]{2})\b");
            if (dataMatches.Count == 0) return true;
            var sb = new System.Text.StringBuilder();
            foreach (Match m in dataMatches) sb.Append(m.Groups[1].Value);
            dataHex = sb.ToString();
            return true;
        }

        private bool WaitForSDOResponse(uint expectedCOBID, bool isWrite, int timeoutMs = 2000)
        {
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (canFrames.TryDequeue(out string? frame) && !string.IsNullOrEmpty(frame))
                {
                    if (TryParseCandumpLine(frame, out uint cobId, out string dataHex))
                    {
                        if (cobId == expectedCOBID && !string.IsNullOrEmpty(dataHex))
                        {
                            try
                            {
                                byte responseCmd = Convert.ToByte(dataHex[..2], 16);
                                if (responseCmd == 0x80) return false;
                                if (isWrite && responseCmd == 0x60) return true;
                            }
                            catch { }
                        }
                    }
                }
                Thread.Sleep(1);
            }
            return false;
        }

        private uint WaitForSDOReadResponse(uint expectedCOBID, int timeoutMs = 2000)
        {
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                if (canFrames.TryDequeue(out string? frame) && !string.IsNullOrEmpty(frame))
                {
                    if (TryParseCandumpLine(frame, out uint cobId, out string dataHex))
                    {
                        if (cobId == expectedCOBID && !string.IsNullOrEmpty(dataHex))
                        {
                            try
                            {
                                byte responseCmd = Convert.ToByte(dataHex[..2], 16);
                                if (responseCmd == 0x80) return 0;
                                return responseCmd switch
                                {
                                    0x4F => Convert.ToByte(dataHex.Substring(8, 2), 16),
                                    0x4B => (uint)(Convert.ToByte(dataHex.Substring(8, 2), 16) |
                                                  Convert.ToByte(dataHex.Substring(10, 2), 16) << 8),
                                    0x43 => (uint)(Convert.ToByte(dataHex.Substring(8, 2), 16) |
                                                   Convert.ToByte(dataHex.Substring(10, 2), 16) << 8 |
                                                   Convert.ToByte(dataHex.Substring(12, 2), 16) << 16 |
                                                   Convert.ToByte(dataHex.Substring(14, 2), 16) << 24),
                                    _ => 0u
                                };
                            }
                            catch { }
                        }
                    }
                }
                Thread.Sleep(1);
            }
            return 0;
        }

        private static string ExecuteCommand(string command)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{command}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(error) && !error.Contains("RTNETLINK"))
                    return error;
                return output;
            }
            catch (Exception ex) { return $"Lỗi: {ex.Message}"; }
        }
    }
    #endregion

    #region CiA402Motor - PDO Mode
    public class CiA402Motor(UbuntuCANInterface canInterface, byte nodeId)
    {
        private CiA402State currentState = CiA402State.NotReadyToSwitchOn;
        private bool usePDO = false;

        private const ushort CONTROL_WORD = 0x6040;
        private const ushort STATUS_WORD = 0x6041;
        private const ushort TARGET_VELOCITY = 0x60FF;
        private const ushort VELOCITY_ACTUAL = 0x606C;
        private const double ENCODER_RES = 10000.0;

        public bool ConfigurePDO()
        {
            Console.WriteLine($"  Cấu hình PDO cho Node {nodeId}...");

            // RPDO2: TargetVelocity (0x60FF,32bit) + ModeOfOperation (0x6060,8bit)
            canInterface.WriteSDO(nodeId, 0x1401, 1, 0x80000300 + nodeId, 4);
            canInterface.WriteSDO(nodeId, 0x1601, 0, 0, 1);
            canInterface.WriteSDO(nodeId, 0x1601, 1, 0x60FF0020, 4);
            canInterface.WriteSDO(nodeId, 0x1601, 2, 0x60600008, 4);
            canInterface.WriteSDO(nodeId, 0x1601, 0, 2, 1);
            canInterface.WriteSDO(nodeId, 0x1401, 1, (uint)(0x00000300 + nodeId), 4);

            // TPDO1: StatusWord + PositionActual + TorqueActual
            canInterface.WriteSDO(nodeId, 0x1800, 1, 0x80000180 + nodeId, 4);
            canInterface.WriteSDO(nodeId, 0x1A00, 0, 0, 1);
            canInterface.WriteSDO(nodeId, 0x1A00, 1, 0x60410010, 4);
            canInterface.WriteSDO(nodeId, 0x1A00, 2, 0x60640020, 4);
            canInterface.WriteSDO(nodeId, 0x1A00, 3, 0x60770010, 4);
            canInterface.WriteSDO(nodeId, 0x1A00, 0, 3, 1);
            canInterface.WriteSDO(nodeId, 0x1800, 1, (uint)(0x00000180 + nodeId), 4);

            // TPDO2: VelocityActual + ModesOfOperationDisplay
            canInterface.WriteSDO(nodeId, 0x1801, 1, 0x80000280 + nodeId, 4);
            canInterface.WriteSDO(nodeId, 0x1A01, 0, 0, 1);
            canInterface.WriteSDO(nodeId, 0x1A01, 1, 0x606C0020, 4);
            canInterface.WriteSDO(nodeId, 0x1A01, 2, 0x60610008, 4);
            canInterface.WriteSDO(nodeId, 0x1A01, 0, 2, 1);
            canInterface.WriteSDO(nodeId, 0x1801, 1, (uint)(0x00000280 + nodeId), 4);

            Console.WriteLine($"  ✓ PDO đã được cấu hình cho Node {nodeId}!");
            Thread.Sleep(500);
            usePDO = true;
            return true;
        }

        public void EnablePDOMode(bool enable)
        {
            usePDO = enable;
        }

        public bool Initialize()
        {
            Console.WriteLine($"  Khởi tạo motor Node {nodeId}...");

            UpdateState();
            if (currentState == CiA402State.Fault)
            {
                Console.WriteLine("  Phát hiện lỗi, đang reset...");
                ResetFault();
                Thread.Sleep(500);
                UpdateState();
            }

            if (!EnableOperation())
                return false;

            Console.WriteLine($"  Đặt Velocity mode cho Node {nodeId}...");
            canInterface.WriteSDO(nodeId, 0x6060, 0, (byte)OperationMode.CyclicSynchronousVelocity, 1);
            Thread.Sleep(100);

            // QUAN TRỌNG: Đặt velocity = 0 trước khi enable PDO
            Console.WriteLine($"  Reset target velocity = 0 cho Node {nodeId}...");
            canInterface.WriteSDO(nodeId, TARGET_VELOCITY, 0, 0, 4);
            Thread.Sleep(100);

            Console.WriteLine($"  ✓ Motor Node {nodeId} sẵn sàng!");
            return true;
        }

        public bool SetVelocityRpm(double rpm)
        {
            int countsPerSec = (int)(rpm * ENCODER_RES / 60.0);
            return SetVelocity(countsPerSec);
        }

        public double GetActualVelocityRpm()
        {
            int countsPerSec = GetActualVelocity();
            return countsPerSec * 60.0 / ENCODER_RES;
        }

        public bool Disable()
        {
            Console.WriteLine($"  Tắt motor Node {nodeId}...");
            SetVelocity(0);
            Thread.Sleep(100);
            return canInterface.WriteSDO(nodeId, CONTROL_WORD, 0, 0x07, 2);
        }

        private void UpdateState()
        {
            uint statusWord = usePDO ?
                canInterface.GetLatestTPDO1(nodeId).StatusWord :
                canInterface.ReadSDO(nodeId, STATUS_WORD, 0);
            currentState = DecodeState(statusWord);
        }

        private static CiA402State DecodeState(uint statusWord)
        {
            return (statusWord & 0x4F) switch
            {
                0x00 => CiA402State.NotReadyToSwitchOn,
                0x40 => CiA402State.SwitchOnDisabled,
                0x08 => CiA402State.Fault,
                0x0F => CiA402State.FaultReactionActive,
                _ => (statusWord & 0x6F) switch
                {
                    0x21 => CiA402State.ReadyToSwitchOn,
                    0x23 => CiA402State.SwitchedOn,
                    0x27 => CiA402State.OperationEnabled,
                    0x07 => CiA402State.QuickStopActive,
                    _ => CiA402State.NotReadyToSwitchOn
                }
            };
        }

        private bool ResetFault()
        {
            return canInterface.WriteSDO(nodeId, CONTROL_WORD, 0, 0x80, 2);
        }

        private bool EnableOperation()
        {
            for (int i = 0; i < 10; i++)
            {
                UpdateState();
                switch (currentState)
                {
                    case CiA402State.SwitchOnDisabled:
                        canInterface.WriteSDO(nodeId, CONTROL_WORD, 0, 0x06, 2);
                        Thread.Sleep(200);
                        break;
                    case CiA402State.ReadyToSwitchOn:
                        canInterface.WriteSDO(nodeId, CONTROL_WORD, 0, 0x07, 2);
                        Thread.Sleep(200);
                        break;
                    case CiA402State.SwitchedOn:
                        canInterface.WriteSDO(nodeId, CONTROL_WORD, 0, 0x0F, 2);
                        Thread.Sleep(200);
                        break;
                    case CiA402State.OperationEnabled:
                        return true;
                    case CiA402State.Fault:
                        ResetFault();
                        Thread.Sleep(500);
                        break;
                    default:
                        Thread.Sleep(200);
                        break;
                }
            }
            return false;
        }

        private bool SetVelocity(int targetVelocity)
        {
            if (usePDO)
            {
                // Sử dụng RPDO2 để gửi velocity nhanh hơn
                return canInterface.SendRPDO2(nodeId, targetVelocity, (sbyte)OperationMode.CyclicSynchronousVelocity);
            }
            else
            {
                // Fallback về SDO
                uint velocityData = targetVelocity < 0 ?
                    (uint)(targetVelocity + 0x100000000L) : (uint)targetVelocity;

                canInterface.WriteSDO(nodeId, TARGET_VELOCITY, 0, velocityData, 4);
                return canInterface.WriteSDO(nodeId, CONTROL_WORD, 0, 0x0F, 2);
            }
        }

        private int GetActualVelocity()
        {
            if (usePDO)
            {
                return canInterface.GetLatestTPDO2(nodeId).ActualVelocity;
            }
            else
            {
                uint rawValue = canInterface.ReadSDO(nodeId, VELOCITY_ACTUAL, 0);
                return rawValue > 0x7FFFFFFF ? (int)(rawValue - 0x100000000L) : (int)rawValue;
            }
        }
    }
    #endregion

    #region TwoWheelRobot - PDO Optimized
    public class TwoWheelRobot(CiA402Motor left, CiA402Motor right)
    {
        private const double MAX_RPM = 1500.0;
        private const double DEADZONE = 35.0;
        private const double JOY_CENTER = 127.5;
        private volatile bool isRunning = false;

        private double lastLeftRpm = 0;
        private double lastRightRpm = 0;
        private DateTime lastUpdateTime = DateTime.Now;
        private const int UPDATE_INTERVAL_MS = 20; // Cập nhật mỗi 20ms với PDO

        public void Start()
        {
            isRunning = true;
            Console.WriteLine("[OK] Robot da san sang dieu khien!");
        }

        public void Stop()
        {
            isRunning = false;
            SetMotorSpeeds(0, 0);
            Console.WriteLine("[STOP] Robot da dung");
        }

        public void UpdateFromController(PS5Controller.ControllerState state)
        {
            if (!isRunning) return;

            // Bắt buộc giữ R1 để kích hoạt điều khiển (giữ nguyên hành vi cũ)
            if (!state.R1)
            {
                if (Math.Abs(lastLeftRpm) > 0.1 || Math.Abs(lastRightRpm) > 0.1)
                {
                    SetMotorSpeeds(0, 0);
                }
                return;
            }

            // Giới hạn tần suất update (giữ nguyên hành vi cũ)
            var now = DateTime.Now;
            if ((now - lastUpdateTime).TotalMilliseconds < UPDATE_INTERVAL_MS) return;
            lastUpdateTime = now;

            // Đọc trục: trái-Y = tiến/lùi, phải-X = quay
            double forward = JOY_CENTER - state.LeftStickY;  // tiến(+)/lùi(-)
            double turn = state.RightStickX - JOY_CENTER; // quay phải(+)/trái(-)

            // Deadzone
            if (Math.Abs(forward) < DEADZONE) forward = 0;
            if (Math.Abs(turn) < DEADZONE) turn = 0;

            // Chuẩn hóa [-1, 1]
            forward = Math.Max(-1.0, Math.Min(1.0, forward / JOY_CENTER));
            turn = Math.Max(-1.0, Math.Min(1.0, turn / JOY_CENTER));

            double leftSpeed, rightSpeed;

            // --- TÁCH BIỆT CHẾ ĐỘ ---
            // Nếu độ lệch tiến/lùi lớn hơn quay -> chỉ chạy thẳng
            // Ngược lại -> chỉ quay tại chỗ
            if (Math.Abs(forward) >= Math.Abs(turn))
            {
                leftSpeed = forward;
                rightSpeed = forward;
            }
            else
            {
                leftSpeed = turn;
                rightSpeed = -turn;
            }

            // Kẹp biên
            leftSpeed = Math.Max(-1.0, Math.Min(1.0, leftSpeed));
            rightSpeed = Math.Max(-1.0, Math.Min(1.0, rightSpeed));

            // Đổi sang RPM và gửi cho motor (PDO)
            double leftRpm = leftSpeed * MAX_RPM;
            double rightRpm = rightSpeed * MAX_RPM;

            SetMotorSpeeds(leftRpm, rightRpm);
        }


        private void SetMotorSpeeds(double leftRpm, double rightRpm)
        {
            // Với PDO mode, luôn gửi lệnh (PDO rất nhanh)
            // Chỉ check thay đổi nhỏ để tránh spam không cần thiết
            if (Math.Abs(leftRpm - lastLeftRpm) > 0.5)
            {
                left.SetVelocityRpm(leftRpm);
                lastLeftRpm = leftRpm;
            }

            if (Math.Abs(rightRpm - lastRightRpm) > 0.5)
            {
                right.SetVelocityRpm(rightRpm);
                lastRightRpm = rightRpm;
            }
        }

        public string GetStatusString()
        {
            double leftVel = left.GetActualVelocityRpm();
            double rightVel = right.GetActualVelocityRpm();
            return $"L: {leftVel,6:F1} | R: {rightVel,6:F1}";
        }

        public (double left, double right) GetTargetSpeeds()
        {
            return (lastLeftRpm, lastRightRpm);
        }
    }
    #endregion

    #region Main Program
    class Program
    {
        static void Main(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            Console.WriteLine("====================================");
            Console.WriteLine("  PS5 Two-Wheel Robot with PDO     ");
            Console.WriteLine("====================================\n");

            // Cấu hình
            string canInterface = "can0";
            int baudrate = 500000;
            byte leftNodeId = 1;
            byte rightNodeId = 2;

            Console.WriteLine("Cau hinh:");
            Console.WriteLine($"   CAN Interface: {canInterface}");
            Console.WriteLine($"   Baudrate: {baudrate}");
            Console.WriteLine($"   Left Motor: Node {leftNodeId}");
            Console.WriteLine($"   Right Motor: Node {rightNodeId}");
            Console.WriteLine($"   Mode: PDO (Real-time control)\n");

            var sharedCANInterface = new UbuntuCANInterface();

            try
            {
                // Kết nối CAN interface
                Console.WriteLine("Thiet lap CAN interface...");
                if (!sharedCANInterface.Connect(canInterface, baudrate))
                {
                    Console.WriteLine("[ERROR] Khong the ket noi CAN interface!");
                    ShowCANTroubleshooting();
                    return;
                }
                Console.WriteLine("[OK] CAN interface da san sang!\n");

                // Khởi động NMT cho cả 2 node
                Console.WriteLine("Khoi dong NMT...");
                sharedCANInterface.SendNMT(0x81, leftNodeId);
                sharedCANInterface.SendNMT(0x81, rightNodeId);
                Thread.Sleep(1000);
                sharedCANInterface.SendNMT(0x01, leftNodeId);
                sharedCANInterface.SendNMT(0x01, rightNodeId);
                Thread.Sleep(500);

                // Khởi tạo motor trái
                Console.WriteLine($"Khoi tao Motor Trai (Node {leftNodeId}):");
                var leftMotor = new CiA402Motor(sharedCANInterface, leftNodeId);
                if (!leftMotor.Initialize())
                {
                    Console.WriteLine("[ERROR] Khong the khoi tao motor trai!");
                    return;
                }

                // Cấu hình PDO cho motor trái
                if (!leftMotor.ConfigurePDO())
                {
                    Console.WriteLine("[ERROR] Khong the cau hinh PDO cho motor trai!");
                    return;
                }
                leftMotor.EnablePDOMode(true);

                // Khởi tạo motor phải
                Console.WriteLine($"\nKhoi tao Motor Phai (Node {rightNodeId}):");
                var rightMotor = new CiA402Motor(sharedCANInterface, rightNodeId);
                if (!rightMotor.Initialize())
                {
                    Console.WriteLine("[ERROR] Khong the khoi tao motor phai!");
                    return;
                }

                // Cấu hình PDO cho motor phải
                if (!rightMotor.ConfigurePDO())
                {
                    Console.WriteLine("[ERROR] Khong the cau hinh PDO cho motor phai!");
                    return;
                }
                rightMotor.EnablePDOMode(true);

                Console.WriteLine("\n[OK] Ca 2 motor da san sang voi PDO mode!\n");

                // Đăng ký event để monitor PDO
                int pdoCounter = 0;
                sharedCANInterface.TPDO1Received += (sender, data) =>
                {
                    // Debug mỗi 100 frames
                    if (++pdoCounter % 100 == 0)
                    {
                        Console.WriteLine($"[PDO] Node {data.nodeId} TPDO1: Status=0x{data.data.StatusWord:X4} Pos={data.data.ActualPosition}");
                    }
                };

                sharedCANInterface.TPDO2Received += (sender, data) =>
                {
                    // Debug velocity data
                    if (pdoCounter % 100 == 0)
                    {
                        double rpm = data.data.ActualVelocity * 60.0 / 10000.0;
                        Console.WriteLine($"[PDO] Node {data.nodeId} TPDO2: Vel={data.data.ActualVelocity} ({rpm:F1} RPM) Mode={data.data.ModesOfOperationDisplay}");
                    }
                };

                // Khởi tạo PS5 Controller
                Console.WriteLine("Ket noi PS5 Controller...");
                var ps5Controller = new PS5Controller();
                if (!ps5Controller.Connect())
                {
                    ShowPS5Troubleshooting();
                    return;
                }

                // Khởi tạo robot
                var robot = new TwoWheelRobot(leftMotor, rightMotor);
                robot.Start();

                // Đăng ký event
                ps5Controller.OnControllerUpdate += (state) =>
                {
                    try
                    {
                        robot.UpdateFromController(state);

                        // Nhấn PS button để thoát
                        if (state.PS)
                        {
                            Console.WriteLine("\n[STOP] Dang dung robot...");
                            robot.Stop();
                            Thread.Sleep(500);
                            leftMotor.Disable();
                            rightMotor.Disable();
                            sharedCANInterface.Disconnect();
                            Console.WriteLine("[OK] Da thoat an toan!");
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] Loi: {ex.Message}");
                    }
                };

                ps5Controller.StartReading();

                Console.WriteLine("\n[OK] He thong da san sang!");
                Console.WriteLine("Dieu khien:");
                Console.WriteLine("   R1: GIU de kich hoat dieu khien");
                Console.WriteLine("   Joystick TRAI Y:  Tien/Lui");
                Console.WriteLine("   Joystick PHAI X:  Quay tai cho/Re");
                Console.WriteLine("   PS button: Thoat");
                Console.WriteLine("\nMode: PDO - Real-time control\n");

                // Monitor status với debug info
                var statusThread = new Thread(() =>
                {
                    int counter = 0;
                    while (true)
                    {
                        try
                        {
                            var status = robot.GetStatusString();
                            var target = robot.GetTargetSpeeds();

                            // Lấy TPDO data để kiểm tra
                            var tpdo1_left = sharedCANInterface.GetLatestTPDO1(leftNodeId);
                            var tpdo1_right = sharedCANInterface.GetLatestTPDO1(rightNodeId);

                            if (counter % 10 == 0) // Mỗi 5 giây in chi tiết
                            {
                                Console.WriteLine($"\n[DETAIL] Node {leftNodeId}: Status=0x{tpdo1_left.StatusWord:X4} Pos={tpdo1_left.ActualPosition}");
                                Console.WriteLine($"[DETAIL] Node {rightNodeId}: Status=0x{tpdo1_right.StatusWord:X4} Pos={tpdo1_right.ActualPosition}");
                            }

                            Console.WriteLine($"[STATUS] Target: L={target.left:F1} R={target.right:F1} | Actual: {status}");
                            Thread.Sleep(500);
                            counter++;
                        }
                        catch { }
                    }
                })
                { IsBackground = true };
                statusThread.Start();

                // Giữ chương trình chạy
                while (true)
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[ERROR] Loi: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
            }
            finally
            {
                try
                {
                    sharedCANInterface?.Disconnect();
                }
                catch { }
            }
        }

        static void ShowCANTroubleshooting()
        {
            Console.WriteLine("\n[ERROR] Khong the ket noi CAN!");
            Console.WriteLine("\nCac buoc khac phuc:");
            Console.WriteLine("1. Kiem tra interface:");
            Console.WriteLine("   ip link show");
            Console.WriteLine("\n2. Cai dat can-utils:");
            Console.WriteLine("   sudo apt install can-utils");
            Console.WriteLine("\n3. Load CAN modules:");
            Console.WriteLine("   sudo modprobe can");
            Console.WriteLine("   sudo modprobe can_raw");
            Console.WriteLine("\n4. Setup CAN interface:");
            Console.WriteLine("   sudo ip link set can0 type can bitrate 500000");
            Console.WriteLine("   sudo ip link set can0 up");
            Console.WriteLine("\n5. Kiem tra:");
            Console.WriteLine("   ip -details link show can0");
        }

        static void ShowPS5Troubleshooting()
        {
            Console.WriteLine("\n[ERROR] Khong the ket noi PS5 Controller!");
            Console.WriteLine("\nCac buoc khac phuc:");
            Console.WriteLine("1. Ket noi tay cam qua USB");
            Console.WriteLine("\n2. Kiem tra:");
            Console.WriteLine("   lsusb | grep 054c:0ce6");
            Console.WriteLine("\n3. Chay voi sudo:");
            Console.WriteLine("   sudo dotnet run");
            Console.WriteLine("\n4. Them udev rule (de khong can sudo):");
            Console.WriteLine("   sudo nano /etc/udev/rules.d/99-ps5-controller.rules");
            Console.WriteLine("   Them dong:");
            Console.WriteLine("   SUBSYSTEM==\"hidraw\", ATTRS{idVendor}==\"054c\", ATTRS{idProduct}==\"0ce6\", MODE=\"0666\"");
            Console.WriteLine("\n5. Reload udev:");
            Console.WriteLine("   sudo udevadm control --reload-rules");
            Console.WriteLine("   sudo udevadm trigger");
        }
    }
    #endregion
}
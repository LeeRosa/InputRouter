using System.Collections.Concurrent;
using System.IO.Ports;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace InputRouter;

internal enum TargetProgram
{
    None,
    ProgramA,
    ProgramB
}

internal sealed class RoutedInputMessage
{
    public string PortName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string DataHex { get; set; } = "";
    public int Length { get; set; }
}

internal sealed class Program
{
    private static readonly object ActiveLock = new();
    private static TargetProgram _activeProgram = TargetProgram.ProgramA;

    private static readonly ConcurrentDictionary<string, PipeClientConnection> Clients = new();

    private const string ProgramAPipeName = "InputRouter.ProgramA";
    private const string ProgramBPipeName = "InputRouter.ProgramB";
    private const string ControlPipeName = "InputRouter.Control";

    private static readonly List<SerialPort> SerialPorts = new();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== InputRouter 시작 ===");
        Console.WriteLine("기본 활성 대상: ProgramA");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => CloseAllPorts();

        // 파이프 서버 시작
        _ = RunClientPipeServerAsync(ProgramAPipeName, TargetProgram.ProgramA);
        _ = RunClientPipeServerAsync(ProgramBPipeName, TargetProgram.ProgramB);
        _ = RunControlPipeServerAsync();

        // COM 포트 시작
        StartSerialPort("COM1", 9600, Parity.None, 8, StopBits.One);
        StartSerialPort("COM2", 9600, Parity.None, 8, StopBits.One);

        Console.WriteLine("[System] Router 대기 중...");
        Console.WriteLine("[System] 활성 프로그램 변경은 ControlPipe 명령으로만 처리.");

        // 프로세스 유지
        await Task.Delay(Timeout.Infinite);
    }

    private static void StartSerialPort(
        string portName,
        int baudRate,
        Parity parity,
        int dataBits,
        StopBits stopBits)
    {
        try
        {
            var port = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = false,
                RtsEnable = false,
                Encoding = Encoding.ASCII
            };

            port.DataReceived += OnSerialDataReceived;
            port.Open();

            SerialPorts.Add(port);

            Console.WriteLine($"[Serial] {portName} Open 성공 (Baud={baudRate})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Serial] {portName} Open 실패: {ex.Message}");
        }
    }

    private static void CloseAllPorts()
    {
        foreach (var port in SerialPorts)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.DataReceived -= OnSerialDataReceived;
                    port.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Serial] {port.PortName} Close 예외: {ex.Message}");
            }
        }
    }

    private static void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port)
            return;

        try
        {
            int bytesToRead = port.BytesToRead;
            if (bytesToRead <= 0)
                return;

            byte[] buffer = new byte[bytesToRead];
            int read = port.Read(buffer, 0, buffer.Length);

            if (read <= 0)
                return;

            if (read != buffer.Length)
            {
                Array.Resize(ref buffer, read);
            }

            var message = new RoutedInputMessage
            {
                PortName = port.PortName,
                Timestamp = DateTime.Now,
                DataHex = BitConverter.ToString(buffer).Replace("-", " "),
                Length = buffer.Length
            };

            RouteMessage(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Serial] {port.PortName} 수신 예외: {ex.Message}");
        }
    }

    private static void RouteMessage(RoutedInputMessage message)
    {
        TargetProgram active;
        lock (ActiveLock)
        {
            active = _activeProgram;
        }

        string json = JsonSerializer.Serialize(message);

        string? pipeName = active switch
        {
            TargetProgram.ProgramA => ProgramAPipeName,
            TargetProgram.ProgramB => ProgramBPipeName,
            _ => null
        };

        Console.WriteLine(
            $"[Route] {message.Timestamp:HH:mm:ss.fff} " +
            $"RX from {message.PortName}, Len={message.Length}, Active={active}, Data={message.DataHex}");

        if (pipeName is null)
        {
            Console.WriteLine("[Route] 활성 프로그램이 없어 폐기됨");
            return;
        }

        if (!Clients.TryGetValue(pipeName, out var client))
        {
            Console.WriteLine($"[Route] {active} 클라이언트 미연결. 메시지 폐기.");
            return;
        }

        _ = client.SendAsync(json);
    }

    private static void SetActiveProgram(TargetProgram target, string source)
    {
        lock (ActiveLock)
        {
            if (_activeProgram == target)
            {
                Console.WriteLine($"[Active] {DateTime.Now:HH:mm:ss.fff} 변경 없음: {_activeProgram} (Source={source})");
                return;
            }

            _activeProgram = target;
        }

        Console.WriteLine($"[Active] {DateTime.Now:HH:mm:ss.fff} 활성 대상 변경 -> {_activeProgram} (Source={source})");
    }

    private static void PrintStatus()
    {
        lock (ActiveLock)
        {
            Console.WriteLine($"[Status] Active={_activeProgram}");
        }

        Console.WriteLine($"[Status] ProgramA Connected={Clients.ContainsKey(ProgramAPipeName)}");
        Console.WriteLine($"[Status] ProgramB Connected={Clients.ContainsKey(ProgramBPipeName)}");
        Console.WriteLine($"[Status] Open Ports={string.Join(", ", SerialPorts.Where(p => p.IsOpen).Select(p => p.PortName))}");
    }

    private static async Task RunClientPipeServerAsync(string pipeName, TargetProgram target)
    {
        while (true)
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);

            try
            {
                Console.WriteLine($"[Pipe] 클라이언트 대기중: {pipeName}");
                await server.WaitForConnectionAsync();

                var client = new PipeClientConnection(pipeName, server);
                Clients[pipeName] = client;

                Console.WriteLine($"[Pipe] 클라이언트 연결됨: {pipeName}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await client.ReceiveLoopAsync(target);
                    }
                    finally
                    {
                        Clients.TryRemove(pipeName, out _);
                        client.Dispose();
                        Console.WriteLine($"[Pipe] 클라이언트 연결 해제: {pipeName}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Pipe] {pipeName} 서버 예외: {ex.Message}");
                server.Dispose();
                await Task.Delay(1000);
            }
        }
    }

    private static async Task RunControlPipeServerAsync()
    {
        while (true)
        {
            var server = new NamedPipeServerStream(
                ControlPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Message,
                PipeOptions.Asynchronous);

            try
            {
                Console.WriteLine($"[Control] 제어 연결 대기중: {ControlPipeName}");
                await server.WaitForConnectionAsync();

                Console.WriteLine($"[Control] 제어 클라이언트 연결됨");

                using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, Encoding.UTF8, 1024, leaveOpen: true)
                {
                    AutoFlush = true
                };

                while (server.IsConnected)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line is null)
                        break;

                    line = line.Trim();
                    if (line.Length == 0)
                        continue;

                    Console.WriteLine($"[Control] RX: {line}");

                    if (line.Equals("ACTIVE:ProgramA", StringComparison.OrdinalIgnoreCase))
                    {
                        SetActiveProgram(TargetProgram.ProgramA, "ControlPipe");
                        await writer.WriteLineAsync("OK");
                    }
                    else if (line.Equals("ACTIVE:ProgramB", StringComparison.OrdinalIgnoreCase))
                    {
                        SetActiveProgram(TargetProgram.ProgramB, "ControlPipe");
                        await writer.WriteLineAsync("OK");
                    }
                    else if (line.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                    {
                        TargetProgram active;
                        lock (ActiveLock)
                        {
                            active = _activeProgram;
                        }

                        await writer.WriteLineAsync($"ACTIVE:{active}");
                    }
                    else if (line.Equals("PING", StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("PONG");
                    }
                    else
                    {
                        await writer.WriteLineAsync("UNKNOWN_COMMAND");
                    }
                }

                Console.WriteLine($"[Control] 제어 클라이언트 연결 해제");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] 서버 예외: {ex.Message}");
                await Task.Delay(1000);
            }
            finally
            {
                server.Dispose();
            }
        }
    }
}

internal sealed class PipeClientConnection : IDisposable
{
    private readonly string _pipeName;
    private readonly NamedPipeServerStream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public PipeClientConnection(string pipeName, NamedPipeServerStream stream)
    {
        _pipeName = pipeName;
        _stream = stream;
        _reader = new StreamReader(_stream, Encoding.UTF8, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(_stream, Encoding.UTF8, 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
    }

    public async Task SendAsync(string text)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (!_stream.IsConnected)
                return;

            await _writer.WriteLineAsync(text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PipeSend] {_pipeName} 전송 실패: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task ReceiveLoopAsync(TargetProgram target)
    {
        while (_stream.IsConnected)
        {
            string? line = await _reader.ReadLineAsync();
            if (line is null)
                break;

            line = line.Trim();
            if (line.Length == 0)
                continue;

            Console.WriteLine($"[PipeRecv] {target}: {line}");

            // 나중에 클라이언트가 직접 활성 통지 보내는 용도로 확장 가능
            // 예: ProgramA 파이프에서 "ACTIVE" 수신 시 ProgramA 활성화
        }
    }

    public void Dispose()
    {
        try { _reader.Dispose(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _sendLock.Dispose(); } catch { }
    }
}
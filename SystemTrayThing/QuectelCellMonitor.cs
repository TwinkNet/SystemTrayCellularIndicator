using System.Text.RegularExpressions;

// ReSharper disable InconsistentNaming

namespace SystemTrayThing;

using System;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

public enum QuectelNetworkState
{
    UNK,
    LTE,
    LTE_A,
    NR,
    NR_UC
}

public class QuectelCellMonitor : IDisposable
{
    private static readonly int[] _nrUcBands = new[] { 79, 78, 77, 41, 48, 38, 7 }; // bands roughly >2500 mhz
    private static QuectelNetworkState _cachedQuectelState = QuectelNetworkState.UNK;
    private static string _lastKnownOperator = "Searching...";
    public static LockableOperatorSchema[] lockableOperators = [];
    private static DateTime _lastCellCheck = DateTime.MinValue;

    // Fields relating to scanning available operators
    public static bool scanOperator = false;
    private static bool activelyScanningOperator = false;

    // Fields relating to switching to a specified operator
    public static bool pendingSwitchOperator = false;
    public static bool activelySwitchingOperator = false;
    public static string pendingOperatorMccncc = "";

    private readonly SerialPort _port;
    private readonly SemaphoreSlim _portLock = new SemaphoreSlim(1, 1);

    public QuectelCellMonitor(string portName = "COM3", int baudRate = 9600)
    {
        
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 200,
            WriteTimeout = 500
        };

        // Open the port once during initialization
        _port.Open();
    }

    public async Task PollCellularStateIfNeededAsync()
    {
        if ((DateTime.UtcNow - _lastCellCheck).TotalMilliseconds > 700)
        {
            _lastCellCheck = DateTime.UtcNow;
            if (scanOperator)
            {
                scanOperator = false;
                lockableOperators = await GetAvailableOperatorsAsync();
            }

            if (pendingSwitchOperator)
            {
                pendingSwitchOperator = false;
                bool result = await SwitchOperator();
            }

            _cachedQuectelState = await GetActiveCellStateAsync();
            _lastKnownOperator = await GetRegisteredOperatorAsync();
        }
    }

    public static QuectelNetworkState CachedQuectelState => _cachedQuectelState;

    public async Task<bool> SwitchOperator(int timeoutMs = 30000)
    {
        if (activelyScanningOperator) return false;
        activelySwitchingOperator = true;
        await _portLock.WaitAsync();
        try
        {
            if (!_port.IsOpen || String.IsNullOrEmpty(pendingOperatorMccncc))
            {
                return false;
            }
            _port.DiscardInBuffer();
            _port.Write("AT+COPS=4,2,\"" + pendingOperatorMccncc + "\"\r");

            var responseBuilder = new StringBuilder();
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                await Task.Delay(1);

                string chunk = _port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    responseBuilder.Append(chunk);
                    string currentText = responseBuilder.ToString();
                    if (currentText.Contains("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (currentText.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
        }
        catch
        {
            // Driver busy, port locked, or device disconnected
        }
        finally
        {
            // Always release the lock so the next 700ms tick can proceed
            _portLock.Release();
            activelySwitchingOperator = false;
        }

        return false;
    }

    public async Task<LockableOperatorSchema[]> GetAvailableOperatorsAsync(int timeoutMs = 30000)
    {
        if (activelyScanningOperator) return [];
        activelyScanningOperator = true;
        // Wait asynchronously if another call is currently using the port
        await _portLock.WaitAsync();

        try
        {
            if (!_port.IsOpen)
            {
                return [];
            }

            // Clear out any stale data from previous polling cycles before sending a new command
            _port.DiscardInBuffer();
            _port.Write("AT+COPS=?\r");
            var responseBuilder = new StringBuilder();
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                await Task.Delay(1);

                string chunk = _port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    responseBuilder.Append(chunk);
                    string currentText = responseBuilder.ToString();

                    if (currentText.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
                        currentText.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            string response = responseBuilder.ToString();
            string[] lines = response.Split("\r\n");
            List<LockableOperatorSchema> operators = new List<LockableOperatorSchema>();
            string pattern = @"\((\d+),""([^""]+)"",""([^""]+)"",""([^""]+)""(?:,(\d+))?\)"; // slop
            foreach (string line in lines)
            {
                if (line.StartsWith("+COPS: "))
                {
                    MatchCollection matches = Regex.Matches(line, pattern);

                    foreach (Match match in matches)
                    {
                        var op = new LockableOperatorSchema()
                        {
                            Status = int.Parse(match.Groups[1].Value),
                            LongName = match.Groups[2].Value,
                            ShortName = match.Groups[3].Value,
                            NumericId = match.Groups[4].Value
                        };
                        // Group 5 contains the Access Technology (AcT) if present
                        if (match.Groups[5].Success)
                        {
                            op.AccessTechnology = int.Parse(match.Groups[5].Value);
                        }
                        bool dup = false;
                        foreach (LockableOperatorSchema lockableOperatorSchema in operators)
                        {
                            if (lockableOperatorSchema.ToString().Equals(op.ToString()))
                            {
                                dup = true;
                                break;
                            }
                        }
                        if (!dup) operators.Add(op);
                    }

                    return [.. operators];
                }
            }
        }
        catch
        {
            // bleh
        }
        finally
        {
            _portLock.Release();
            activelyScanningOperator = false;
        }

        return [];
    }

    public async Task<QuectelNetworkState> GetActiveCellStateAsync(int timeoutMs = 1000)
    {
        await _portLock.WaitAsync();
        try
        {
            if (!_port.IsOpen)
            {
                return QuectelNetworkState.UNK;
            }

            _port.DiscardInBuffer();
            _port.Write("AT+QCAINFO\r");
            var responseBuilder = new StringBuilder();
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                await Task.Delay(1); // probably useless wait
                string chunk = _port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    responseBuilder.Append(chunk);
                    string currentText = responseBuilder.ToString();

                    if (currentText.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
                        currentText.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            string response = responseBuilder.ToString();
            string[] lines = response.Split("\r\n");

            if (response.Contains("NR5G", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string line in lines)
                {
                    if (line.Equals("")) continue;
                    if (QcaInfo5gSchema.IsMaybeCompatibleWithSchema(line))
                    {
                        QcaInfo5gSchema qcaInfo = new QcaInfo5gSchema(line);
                        foreach (var nrUcBand in _nrUcBands)
                        {
                            if (qcaInfo.Band == nrUcBand)
                            {
                                return QuectelNetworkState.NR_UC;
                            }
                        }
                    }
                }

                return QuectelNetworkState.NR;
            }
            else if (response.Contains("LTE", StringComparison.OrdinalIgnoreCase))
            {
                int sccCount = 0;
                foreach (string line in lines)
                {
                    if (line.Equals("")) continue;
                    if (QcaInfoLteSchema.IsMaybeCompatibleWithSchema(line))
                    {
                        QcaInfoLteSchema qcaInfo = new QcaInfoLteSchema(line);
                        if (qcaInfo.ComponentCarrier.Equals("SCC")) sccCount++;
                    }
                }

                if (sccCount >= 2)
                {
                    return QuectelNetworkState.LTE_A;
                }

                return QuectelNetworkState.LTE;
            }
        }
        catch(Exception e)
        {
            Console.WriteLine(e.ToString());
        }
        finally
        {
            _portLock.Release();
        }

        return QuectelNetworkState.UNK;
    }

    public async Task<string> GetRegisteredOperatorAsync(int timeoutMs = 1000)
    {
        await _portLock.WaitAsync();
        try
        {
            if (!_port.IsOpen)
            {
                return _lastKnownOperator;
            }
            _port.DiscardInBuffer();
            _port.Write("AT+COPS=3,1\r");
            _port.Write("AT+COPS?\r");
            var responseBuilder = new StringBuilder();
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                await Task.Delay(1); // probably useless
                string chunk = _port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    responseBuilder.Append(chunk);
                    string currentText = responseBuilder.ToString();

                    if (currentText.Contains("OK", StringComparison.OrdinalIgnoreCase) ||
                        currentText.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }
            string response = responseBuilder.ToString();
            var match = Regex.Match(response, @"\+COPS: \d+,\d+,\\?\""([^\""]+)\""");
            if (match.Success) _lastKnownOperator = match.Groups[1].Value;
            return _lastKnownOperator;
        }
        catch
        {
            // 
        }
        finally
        {
            _portLock.Release();
        }

        return _lastKnownOperator;
    }

    public static bool ActivelyScanningOperator => activelyScanningOperator;
    public static bool ActivelySwitchingOperator => activelySwitchingOperator;

    public string LastKnownOperator => _lastKnownOperator;

    public void Dispose()
    {
        if (_port != null)
        {
            if (_port.IsOpen)
            {
                _port.Close();
            }

            _port.Dispose();
        }

        _portLock?.Dispose();
    }
}
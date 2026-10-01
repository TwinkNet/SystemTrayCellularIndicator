using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;
using ManagedNativeWifi;
using SystemTrayThing;
using Timer = System.Windows.Forms.Timer;

namespace NetworkTrayMonitor
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApplicationContext());
        }
    }

    public class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private NetworkTechnology _currentNetworkTechnology = new("X", "Disabled");
        private AnimationTracker _animationTracker = new AnimationTracker();
        private int _noCellularConnCounter = 0;
        private readonly Timer _timer;
        private readonly QuectelCellMonitor _cellMonitor = new QuectelCellMonitor();
        private readonly ToolStripMenuItem _operatorsMenu;
        private readonly PersistantOperatorsManager _operatorsManager;

        public TrayApplicationContext()
        {
            _operatorsManager = new PersistantOperatorsManager();
            QuectelCellMonitor.lockableOperators = _operatorsManager.LoadOperators();
            foreach (LockableOperatorSchema lockableOperatorSchema in QuectelCellMonitor.lockableOperators)
            {
                Console.WriteLine("Loaded cached operator " + lockableOperatorSchema.ToString() + " from disk.");
            }
            _trayIcon = new NotifyIcon()
            {
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
            };
            _operatorsMenu = new ToolStripMenuItem("Operators");
            _operatorsMenu.Enabled = false;
            _trayIcon.ContextMenuStrip.Items.Add(_operatorsMenu);
            _trayIcon.ContextMenuStrip.Items.Add("Scan Operators", null, Scan);
            _trayIcon.ContextMenuStrip.Items.Add("Exit", null, Exit);

            _timer = new Timer { Interval = 500 }; // Check every .5 second
            _timer.Tick += UpdateNetworkIcon;
            _timer.Start();

            UpdateNetworkIcon(null, null);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        extern static bool DestroyIcon(IntPtr handle);

        private async void UpdateNetworkIcon(object sender, EventArgs e)
        {
            await _cellMonitor.PollCellularStateIfNeededAsync();
            NetworkTechnology networkType = await GetActiveNetworkTechnology();
            _currentNetworkTechnology = networkType;
            _trayIcon.Icon = networkType.CurrentIcon();
            _trayIcon.Text = $"{networkType.FullTechnology}";
            foreach (LockableOperatorSchema lockableOperatorSchema in QuectelCellMonitor.lockableOperators)
            {
                string name = lockableOperatorSchema.ToString();
                bool skip = false;
                foreach (ToolStripItem operatorsMenuDropDownItem in _operatorsMenu.DropDownItems)
                {
                    if (operatorsMenuDropDownItem.Text != null && operatorsMenuDropDownItem.Text.Equals(name))
                    {
                        skip = true;
                        break;
                    }
                }
                if (!skip)
                {
                    _operatorsMenu.DropDownItems.Add(name, null, (s, ev) => Switch(lockableOperatorSchema.NumericId));
                    if (!_operatorsMenu.Enabled) _operatorsMenu.Enabled = true;
                }
            }
        }

        private async Task<NetworkTechnology> GetActiveNetworkTechnology()
        {
            return await Task.Run(() =>
            {
                try
                {
                    // if (!NetworkInterface.GetIsNetworkAvailable())
                    // {
                    //     return new NetworkTechnology(_currentNetworkTechnology, "X", "Disconnected");
                    // }

                    NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                    // Ethernet
                    foreach (var ni in interfaces)
                    {
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                            ni.OperationalStatus == OperationalStatus.Up)
                        {
                            resetNoCellularConnCounter();
                            return new NetworkTechnology(_currentNetworkTechnology, "ETH", "Ethernet");
                        }
                    }

                    try
                    {
                        var connectedWifi = NativeWifi.EnumerateConnectedNetworkSsids().FirstOrDefault();
                        if (connectedWifi != null)
                        {
                            var wifiInterface = NativeWifi.EnumerateBssNetworks()
                                .FirstOrDefault(b => b.Ssid.ToString() == connectedWifi.ToString());

                            string band = "2.4";
                            if (wifiInterface != null)
                            {
                                int khz = wifiInterface.Frequency;
                                if (khz > 5000000) band = "5.0";
                                else band = "2.4";
                            }
                            resetNoCellularConnCounter();
                            return new NetworkTechnology(_currentNetworkTechnology, band, "Wi-Fi\n" + band + " GHz");
                        }
                    }
                    catch
                    {
                        // Wi-Fi ain't there
                    }

                    // Check Cellular
                    var connectionProfile = NetworkInformation.GetConnectionProfiles()
                        .FirstOrDefault(p => p.IsWwanConnectionProfile);

                    if (connectionProfile == null ||
                        connectionProfile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None
                        || QuectelCellMonitor.ActivelyScanningOperator
                        || QuectelCellMonitor.ActivelySwitchingOperator)
                    {
                        if (_noCellularConnCounter > 60)
                        {
                            return new NetworkTechnology(_currentNetworkTechnology, "X", "Not Connected");
                        }
                        _noCellularConnCounter++;
                        return new NetworkTechnology(_currentNetworkTechnology, "ANT", "Searching...");
                    }
                    resetNoCellularConnCounter();
                    long bytesReceived = 0;
                    long bytesSent = 0;
                    foreach (var ni in interfaces)
                    {
                        if (ni.OperationalStatus == OperationalStatus.Up)
                        {
                            var stats = ni.GetIPv4Statistics();
                            bytesReceived += stats.BytesReceived;
                            bytesSent += stats.BytesSent;
                        }
                    }

                    bool doAnimation = _animationTracker.Update(bytesSent, bytesReceived);
                    string animationSuffix = !doAnimation ? "" : "_S" + _animationTracker.AnimStage;
                    string provider = _cellMonitor.LastKnownOperator;

                    var wwanDetails = connectionProfile.WwanConnectionProfileDetails;
                    if (wwanDetails != null)
                    {
                        string dataClass = wwanDetails.GetCurrentDataClass().ToString();
                        bool is5GCapable = dataClass.Contains("Nr", StringComparison.OrdinalIgnoreCase) ||
                                           dataClass.Contains("5G", StringComparison.OrdinalIgnoreCase) ||
                                           dataClass.Contains("64", StringComparison.OrdinalIgnoreCase);
                        // we can send at commands to the quectel modem to check if it's actually attached to NR
                        // windows will report that 5G is in use if a nearby cell even has NR on it
                        // the modem also tends to detach from 5G when you're not doing anything data intensive.
                        bool hasReal5G = is5GCapable && (QuectelCellMonitor.CachedQuectelState == QuectelNetworkState.NR ||
                                                         QuectelCellMonitor.CachedQuectelState == QuectelNetworkState.NR_UC);
                        // true if connected to a 2500mhz+ band
                        bool has5GUC = hasReal5G && QuectelCellMonitor.CachedQuectelState == QuectelNetworkState.NR_UC;
                        // true if connected to 3 or more SCCs
                        bool hasLteA = QuectelCellMonitor.CachedQuectelState == QuectelNetworkState.LTE_A;

                        if (hasReal5G)
                        {
                            if (has5GUC)
                            {
                                return new NetworkTechnology(_currentNetworkTechnology, "5G+" + animationSuffix,
                                    provider + "\n5G+ NR");
                            }
                            return new NetworkTechnology(_currentNetworkTechnology, "5G" + animationSuffix,
                                provider + "\n5G NR");
                        }

                        if (dataClass.Contains("Lte", StringComparison.OrdinalIgnoreCase) ||
                            dataClass.Contains("32", StringComparison.OrdinalIgnoreCase) ||
                            dataClass.Contains("64", StringComparison.OrdinalIgnoreCase))
                        {
                            string capabilitySuffix = is5GCapable ? " (5G Possible)" : "";
                            if (hasLteA)
                            {
                                return new NetworkTechnology(_currentNetworkTechnology, "LTE+" + animationSuffix,
                                    provider + "\n4G LTE+" + capabilitySuffix);
                            }
                            return new NetworkTechnology(_currentNetworkTechnology, "LTE" + animationSuffix,
                                provider + "\n4G LTE" + capabilitySuffix);
                        }
                    }
                    return new NetworkTechnology(_currentNetworkTechnology, "H" + animationSuffix,
                        provider + "\n3G HSPA"); // RM520NGL doesn't support anything older than 3G UMTS or HSPA
                }
                catch (Exception exception)
                {
                    //
                }

                return new NetworkTechnology(_currentNetworkTechnology, "X", "Not Connected");
            });
        }

        private void resetNoCellularConnCounter()
        {
            _noCellularConnCounter = 0;
        }

        private void Exit(object sender, EventArgs e)
        {
            _trayIcon.Visible = false;
            _operatorsManager.SaveOperators(QuectelCellMonitor.lockableOperators);
            Application.Exit();
        }

        private void Scan(object sender, EventArgs e)
        {
            QuectelCellMonitor.scanOperator = true;
        }
        
        private void Switch(string numericId)
        {
            QuectelCellMonitor.pendingOperatorMccncc = numericId;
            QuectelCellMonitor.pendingSwitchOperator = true;
        }
    }
}
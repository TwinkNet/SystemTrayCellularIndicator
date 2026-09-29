using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SystemTrayThing;

public class NetworkTechnology
{
    private static readonly ConcurrentDictionary<string, Icon> IconCache = new();
    
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
    
    private readonly string _shortTechnology;
    private readonly string _fullTechnology;
    private Icon? _currentIcon;
    
    public NetworkTechnology(string shortTechnology, string fullTechnology)
    {
        _shortTechnology = shortTechnology;
        _fullTechnology = fullTechnology;
    }

    public NetworkTechnology(NetworkTechnology prevTechnology, string shortTechnology, string fullTechnology)
        : this(shortTechnology, fullTechnology)
    {
    }

    public string ShortTechnology => _shortTechnology;

    public string FullTechnology => _fullTechnology;

    public Icon CurrentIcon() => GetOrLoadIcon(ShortTechnology);
    
    private static Icon GetOrLoadIcon(string imageName)
    {
        return IconCache.GetOrAdd(imageName, key => LoadPngAsIcon(key));
    }
    
    private static Icon LoadPngAsIcon(string imageName)
    {
        string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", $"{imageName}.png");

        if (File.Exists(filePath))
        {
            try
            {
                using var bmp = new Bitmap(filePath);
                using var resizedBmp = new Bitmap(bmp, new Size(16, 16));
                
                IntPtr hIcon = resizedBmp.GetHicon();
                try
                {
                    using var tempIcon = Icon.FromHandle(hIcon);
                    return (Icon)tempIcon.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
            catch
            {
                //
            }
        }

        // Fallback if image file is missing
        if (!imageName.Equals("X", StringComparison.OrdinalIgnoreCase))
        {
            return GetOrLoadIcon("X");
        }

        return SystemIcons.Shield;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not NetworkTechnology) return base.Equals(obj);
        var nt = obj as NetworkTechnology;
        return nt != null && nt.ShortTechnology.Equals(this.ShortTechnology);
    }
    
    public override int GetHashCode()
    {
        return ShortTechnology.GetHashCode();
    }
}
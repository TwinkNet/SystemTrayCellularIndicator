using System.Text.RegularExpressions;

namespace SystemTrayThing;

public partial class QcaInfo5gSchema
{
    private readonly string _componentCarrier;
    private readonly int _earfcn;
    private readonly int _bandwidth;
    private readonly int _band;
    private readonly int _cellId;

    public QcaInfo5gSchema(string line)
    {
        string data = line.Replace("+QCAINFO:", "").Trim();
        string[] parts = data.Split(',');

        if (parts.Length >= 5)
        {
            _componentCarrier = parts[0].Trim('"');
            _earfcn = int.Parse(parts[1]);
            _bandwidth = int.Parse(parts[2]);
            // Clean quotes and extract just the numeric band value from "NR5G BAND 41"
            string bandString = parts[3].Trim('"');
            _band = int.Parse(BandRegex().Match(bandString).Value);
            
            _cellId = int.Parse(parts[4]);
        }
        else
        {
            _componentCarrier = "NCC";
            _earfcn = -1;
            _bandwidth = -1;
            _band = -1;
            _cellId = -1;
        }
    }

    public string ComponentCarrier => _componentCarrier;
    public int Band => _band;

    public static bool IsMaybeCompatibleWithSchema(string line)
    {
        return line.Contains("SCC") && line.Contains("NR5G");
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex BandRegex();
}
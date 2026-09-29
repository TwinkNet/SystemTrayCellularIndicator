using System.Text.Json;

namespace SystemTrayThing;

public class PersistantOperatorsManager
{
    private readonly string _filePath;
    private readonly string _directoryPath;

    public PersistantOperatorsManager()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directoryPath = Path.Combine(appDataPath, "SystemTrayThing");
        _filePath = Path.Combine(_directoryPath, "cached_operators.json");
    }
    
    
    public void SaveOperators(LockableOperatorSchema[] operators)
    {
        Directory.CreateDirectory(_directoryPath);
        var options = new JsonSerializerOptions { WriteIndented = true };
        
        string jsonString = JsonSerializer.Serialize(operators, options);
        File.WriteAllText(_filePath, jsonString);
    }

    public LockableOperatorSchema[] LoadOperators()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        string jsonString = File.ReadAllText(_filePath);
        return (JsonSerializer.Deserialize<List<LockableOperatorSchema>>(jsonString)
                ?? new List<LockableOperatorSchema>()).ToArray();
    }
}
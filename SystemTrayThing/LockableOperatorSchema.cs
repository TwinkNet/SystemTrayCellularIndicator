namespace SystemTrayThing;

public class LockableOperatorSchema
{
    public int Status { get; set; }
    public string? LongName { get; set; }
    public string? ShortName { get; set; }
    public string NumericId { get; set; }
    public int? AccessTechnology { get; set; }

    public override string ToString()
    {
        const string unnamed = "Unnamed Operator";
        string shortName = string.IsNullOrEmpty(this.ShortName)
            ? unnamed
            : this.ShortName;
        string longName = string.IsNullOrEmpty(this.LongName)
            ? unnamed
            : this.LongName;
        string name = !shortName.Equals(unnamed) ? shortName : longName;
        name = name.Replace(" ", "").Equals(this.NumericId) ? unnamed : name;
        name = this.NumericId + " - " + name;
        return name;
    }
}
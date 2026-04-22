namespace plugins;

[AttributeUsage(AttributeTargets.Property)]
public class OptionAttribute : Attribute
{
    public string LongName { get; }
    public char? ShortName { get; }
    public string? Category { get; }
    public string Description { get; }

    public OptionAttribute(string name, string description, string? category = null)
    {
        string[] parts = name.Split('|');
        if (parts.Length == 0 || parts.Length > 2) 
            throw new ArgumentException($"Formato 'name' non valido: {name}. Usa \"longname|l\" o \"longname\"."); 

        LongName = parts[0];
        if (parts.Length == 2 && parts[1].Length > 0) 
            ShortName = parts[1][0];

        Description = description;
        Category = category;
    }
}

[AttributeUsage(AttributeTargets.Property)]
public class FixedAttribute(int position, string name, string description) : Attribute
{
    public int Position { get; } = position;
    public string Name { get; } = name;
    public string Description { get; } = description;
}
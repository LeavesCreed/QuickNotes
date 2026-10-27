namespace WindowsWindowTrayPrototype;

public sealed class HotkeySetting
{
    public int Id { get; }
    public string Action { get; }
    public string Modifiers { get; set; }
    public string Key { get; set; }
    public bool Enabled { get; set; }

    public HotkeySetting(int id, string action, string modifiers, string key, bool enabled)
    {
        Id = id;
        Action = action;
        Modifiers = modifiers;
        Key = key;
        Enabled = enabled;
    }

    public HotkeySetting Clone() => new(Id, Action, Modifiers, Key, Enabled);
}

using BepInEx.Configuration;

namespace DearImGuiInjectionPlus.BepInEx;

public class BepInExConfigEntry<T> : IConfigEntry<T>
{
    private readonly ConfigEntry<T> _configEntry;

    public BepInExConfigEntry(ConfigEntry<T> configEntry)
    {
        _configEntry = configEntry;
    }

    public T Get() => _configEntry != null ? _configEntry.Value : default;
    public void Set(T value)
    {
        if (_configEntry != null)
        {
            _configEntry.Value = value;
        }
    }
}
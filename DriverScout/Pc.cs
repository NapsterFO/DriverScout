namespace DriverScout;

/// <summary>Сведения об этом компьютере — читаются из WMI один раз на запуск и нужны всем вкладкам.</summary>
public static class Pc
{
    private static readonly Lazy<Task<SystemProfile?>> Profile = new(() => Task.Run(() =>
    {
        try { return SystemProfile.Load(); }
        catch { return (SystemProfile?)null; } // WMI сломан — вкладки работают без сведений о компьютере
    }));

    public static Task<SystemProfile?> ProfileAsync() => Profile.Value;
}

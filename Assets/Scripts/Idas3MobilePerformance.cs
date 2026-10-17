internal static class Idas3MobilePerformance
{
    internal static bool Enabled {get;private set;}
    internal static void Apply(bool enabled)
    {
        if(Enabled==enabled)return;
        Enabled=enabled;
        Idas3MenuLocalization.ClearFitCache();
    }
}

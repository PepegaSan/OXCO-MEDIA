using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HailMary.Services;

internal static class LocalizationNotify
{
    private static readonly MethodInfo? NotifyMethod =
        typeof(ObservableObject).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)]);

    public static void Description(object? host)
    {
        if (host is ObservableObject ob)
        {
            NotifyMethod?.Invoke(ob, ["Description"]);
        }
    }
}
